// CustomServer.cpp
//
// CustomServer.cs와의 동일성(parity)에 관한 구현 노트:
//
// - HandleClientInput의 틱별 연산(회전 -> 가속 -> 이동 -> 랩핑 -> 발사)은
//   C# 버전과 정확히 동일한 순서를 따름. 이 단계들 중 하나라도 순서를
//   바꾸면 클라이언트 예측이 어긋남(desync).
// - WrapCoordinate는 C# 및 Unity 클라이언트 버전과 동일한 이중 모듈로
//   (double-modulo) 공식을 사용함(음수 값에 단일 '%'가 왜 불충분한지에
//   대한 자세한 설명은 C# 측의 긴 주석 참고).
// - UpdateMissiles는 각 미사일을 이동시킨 후, 사거리 소진 여부를
//   체크하고, 그다음 소유자가 아니고 죽지 않은 모든 플레이어와의 충돌을
//   체크함 -- C# 버전과 동일한 순서, 동일한 "첫 충돌이 우선, 미사일 제거"
//   시맨틱.
// - 스레딩 모델은 C# 버전을 그대로 미러링함: 백그라운드 수신 스레드 1개,
//   고정 주기 게임 루프 스레드 1개. C#의 ConcurrentDictionary는 모든 공유
//   맵을 보호하는 단일 뮤텍스로 대체됨; players_/missiles_/scoreboard_를
//   건드리는 모든 메서드는 자체적으로 락을 잡거나, _Locked 접미사가 붙어
//   호출자가 이미 락을 보유하고 있어야 함을 문서화함.
#include "CustomServer.h"
#include "BinaryStream.h"

#include <algorithm>
#include <cerrno>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <ctime>
#include <iostream>

#ifndef _WIN32
#include <fcntl.h>
#include <unistd.h>
#else
#include <mstcpip.h>
using ssize_t = int;
#endif

namespace server
{
    namespace
    {
        constexpr float PI = 3.14159265358979323846f;

        // C# Log() 헬퍼의 타임스탬프 형식(DateTime.Now, 로컬 타임)과 정확히
        // 일치하도록 "[YYYY-MM-DD HH:MM:SS.mmm] message" 형태로 포맷함.
        void Log(const std::string& message)
        {
            using namespace std::chrono;
            auto now = system_clock::now();
            auto nowTimeT = system_clock::to_time_t(now);
            auto ms = duration_cast<milliseconds>(now.time_since_epoch()) % 1000;

            std::tm localTm{};
#ifdef _WIN32
            localtime_s(&localTm, &nowTimeT);
#else
            localtime_r(&nowTimeT, &localTm);
#endif
            char buf[32];
            std::strftime(buf, sizeof(buf), "%Y-%m-%d %H:%M:%S", &localTm);
            std::printf("[%s.%03d] %s\n", buf, static_cast<int>(ms.count()), message.c_str());
            std::fflush(stdout);
        }

        double SecondsSince(const TimePoint& t)
        {
            return std::chrono::duration<double>(Clock::now() - t).count();
        }
    }

    CustomServer::~CustomServer()
    {
        Stop();
    }

    void CustomServer::Start()
    {
#ifdef _WIN32
        WSADATA wsaData;
        WSAStartup(MAKEWORD(2, 2), &wsaData);
#endif
        udpSocket_ = static_cast<socket_t>(socket(AF_INET, SOCK_DGRAM, 0));

        sockaddr_in bindAddr{};
        bindAddr.sin_family = AF_INET;
        bindAddr.sin_addr.s_addr = INADDR_ANY;
        bindAddr.sin_port = htons(static_cast<uint16_t>(proto::PORT));

        if (bind(udpSocket_, reinterpret_cast<sockaddr*>(&bindAddr), sizeof(bindAddr)) != 0)
        {
            Log("[Server] FATAL: bind() failed on port " + std::to_string(proto::PORT) +
                " (errno=" + std::to_string(errno) + ")");
            return;
        }

        // C# 서버의 SIO_UDP_CONNRESET 우회 처리에 대응하는 Windows 전용
        // 코드. Windows에서는 바인딩된(비연결) UDP 소켓이라도 상대방으로
        // 부터 ICMP 포트 도달 불가 응답을 받으면 recv 호출이 에러로
        // 실패하는 문제가 있어 이를 우회해야 하지만, Linux는 애초에 이런
        // 동작이 없어 우회할 대상 자체가 없음. C# 소스와 나란히 대조하기
        // 쉽도록 자리만 남겨두는 no-op임.
#ifdef _WIN32
        {
            DWORD bytesReturned = 0;
            BOOL enable = FALSE;
            WSAIoctl(udpSocket_, _WSAIOW(IOC_VENDOR, 12), &enable, sizeof(enable),
                nullptr, 0, &bytesReturned, nullptr, nullptr);
        }
#endif

        isRunning_ = true;
        Log("[Server] UDP Server started on port " + std::to_string(proto::PORT) + "...");

        rxThread_ = std::thread(&CustomServer::ReceiveLoop, this);
        loopThread_ = std::thread(&CustomServer::ServerLoop, this);
    }

    void CustomServer::Stop()
    {
        if (!isRunning_) return;
        isRunning_ = false;

#ifdef _WIN32
        closesocket(udpSocket_);
#else
        shutdown(udpSocket_, SHUT_RDWR);
        close(udpSocket_);
#endif
        udpSocket_ = -1;

        if (rxThread_.joinable()) rxThread_.join();
        if (loopThread_.joinable()) loopThread_.join();

        Log("[Server] UDP Server stopped.");
    }

    void CustomServer::SendTo(const EndPoint& ep, const uint8_t* data, size_t len)
    {
        sendto(udpSocket_, reinterpret_cast<const char*>(data), static_cast<int>(len), 0,
            reinterpret_cast<const sockaddr*>(&ep.addr), sizeof(ep.addr));
    }

    // ---------------------------------------------------------------------
    // 수신 루프
    // ---------------------------------------------------------------------

    void CustomServer::ReceiveLoop()
    {
        std::vector<uint8_t> buf(2048);

        while (isRunning_)
        {
            sockaddr_in fromAddr{};
            socklen_t fromLen = sizeof(fromAddr);

            ssize_t n = recvfrom(udpSocket_, reinterpret_cast<char*>(buf.data()),
                static_cast<int>(buf.size()), 0,
                reinterpret_cast<sockaddr*>(&fromAddr), &fromLen);

            if (n < 0)
            {
                if (!isRunning_) break;
                // C#의 SocketException catch와 동일함: 무시하고 계속 진행;
                // stale-client 타임아웃 처리는 CheckTimeouts에 위임함.
                continue;
            }
            if (n == 0) continue;

            EndPoint remoteEp;
            remoteEp.addr = fromAddr;

            try
            {
                proto::BinaryReader br(buf.data(), static_cast<size_t>(n));
                auto type = static_cast<proto::PacketType>(br.ReadByte());

                switch (type)
                {
                case proto::PacketType::JoinRequest:
                    HandleJoinRequest(remoteEp);
                    break;

                case proto::PacketType::ClientInput:
                    HandleClientInput(br);
                    break;

                case proto::PacketType::Ping:
                {
                    uint8_t playerId = br.ReadByte();
                    int64_t clientTicks = br.ReadInt64();
                    SendPongResponse(remoteEp, playerId, clientTicks);
                    break;
                }

                default:
                    // 알 수 없는/레거시 패킷 타입; 무시함. C#의 switch가
                    // 일치하는 case 없이 통과하는 것과 동일함.
                    break;
                }
            }
            catch (const std::exception& ex)
            {
                if (!isRunning_) break;
                Log(std::string("[Server Error] ") + ex.what());
            }
        }
    }

    void CustomServer::HandleJoinRequest(const EndPoint& clientEp)
    {
        std::lock_guard<std::mutex> lock(stateMutex_);

        RemoveClientByEndPoint_Locked(clientEp);

        uint8_t assignedId = nextPlayerId_++;
        SpawnTransform spawn = ComputeSpawnTransform_Locked();

        PlayerState newPlayer;
        newPlayer.Id = assignedId;
        newPlayer.Endpoint = clientEp;
        newPlayer.X = spawn.x;
        newPlayer.Y = spawn.y;
        newPlayer.Rotation = spawn.rotationDeg;
        newPlayer.CurrentSpeed = 0.0f;
        newPlayer.SpawnSlot = spawn.slot;
        newPlayer.LastPingTime = Clock::now();
        newPlayer.Health = proto::MAX_HEALTH;
        newPlayer.IsDead = false;
        // newPlayer.LastFireTime은 클래스 내 기본 멤버 초기화자를 통해
        // 이미 FarPast()로 설정됨. 따라서 최초 발사 쿨다운 체크
        // ((now - LastFireTime) >= FIRE_COOLDOWN_SECONDS)는 항상 통과함 --
        // C#의 DateTime.MinValue와 동일함.

        players_[assignedId] = newPlayer;
        scoreboard_[assignedId] = ScoreEntry{};

        proto::BinaryWriter bw;
        bw.WriteByte(static_cast<uint8_t>(proto::PacketType::JoinResponse));
        bw.WriteByte(assignedId);
        SendTo(clientEp, bw.Data(), bw.Size());

        Log("[Server] Player " + std::to_string(assignedId) + " joined (Total: " +
            std::to_string(players_.size()) + ")");
    }

    CustomServer::SpawnTransform CustomServer::ComputeSpawnTransform_Locked()
    {
        std::unordered_set<int> occupiedSlots;
        for (auto& kvp : players_)
            occupiedSlots.insert(kvp.second.SpawnSlot);

        int chosenSlot = 0;
        for (int i = 0; i < proto::SPAWN_SLOTS; ++i)
        {
            if (occupiedSlots.find(i) == occupiedSlots.end())
            {
                chosenSlot = i;
                break;
            }
            // 모든 슬롯이 사용 중인 경우(SPAWN_SLOTS보다 동시 접속자가 많음):
            // C# 버전과 동일하게 마지막으로 확인한 슬롯으로 폴백함.
            chosenSlot = i;
        }

        float angleDeg = chosenSlot * (360.0f / proto::SPAWN_SLOTS);
        float angleRad = angleDeg * (PI / 180.0f);

        float x = std::sin(angleRad) * proto::SPAWN_RADIUS;
        float y = std::cos(angleRad) * proto::SPAWN_RADIUS;
        float rotationDeg = angleDeg;

        return { x, y, rotationDeg, chosenSlot };
    }

    // ---------------------------------------------------------------------
    // 좌표 랩핑
    // ---------------------------------------------------------------------

    float CustomServer::WrapCoordinate(float value, float halfExtent)
    {
        float range = halfExtent * 2.0f;
        if (range <= 0.0f) return 0.0f;

        float shifted = value + halfExtent;
        float wrapped = std::fmod(shifted, range);
        if (wrapped < 0.0f) wrapped += range; // fmod는 피제수의 부호를 유지함, C#의 %와 동일
        return wrapped - halfExtent;
    }

    // ---------------------------------------------------------------------
    // 클라이언트 입력 처리
    // ---------------------------------------------------------------------

    void CustomServer::HandleClientInput(proto::BinaryReader& br)
    {
        uint8_t playerId = br.ReadByte();
        int32_t clientTick = br.ReadInt32();
        float throttle = br.ReadSingle();
        float turn = br.ReadSingle();
        bool fire = br.ReadBool();

        std::lock_guard<std::mutex> lock(stateMutex_);

        auto it = players_.find(playerId);
        if (it == players_.end()) return;
        PlayerState& player = it->second;

        // 사망한 플레이어: 이동/회전/발사는 적용하지 않지만, 클라이언트 측
        // 보정이 계속 진행되도록 LastProcessedTick은 계속 기록함
        // (리스폰 스냅이 깔끔한 위치에 안착하도록 하기 위함).
        if (player.IsDead)
        {
            player.LastProcessedTick = clientTick;
            return;
        }

        float dt = 1.0f / proto::TICK_RATE;

        throttle = std::clamp(throttle, -1.0f, 1.0f);
        turn = std::clamp(turn, -1.0f, 1.0f);

        // 1) 회전을 먼저 처리 (탱크 조작: 회전은 즉시 적용됨).
        player.Rotation += turn * proto::TURN_SPEED_DEG * dt;
        player.Rotation = std::fmod(player.Rotation, 360.0f);
        if (player.Rotation < 0.0f) player.Rotation += 360.0f;

        // 2) 목표 속도를 향해 가속/감속.
        float targetSpeed = throttle >= 0.0f
            ? throttle * proto::MAX_FORWARD_SPEED
            : throttle * proto::MAX_REVERSE_SPEED;

        float speedDelta = proto::ACCELERATION * dt;
        if (player.CurrentSpeed < targetSpeed)
        {
            player.CurrentSpeed = std::min(player.CurrentSpeed + speedDelta, targetSpeed);
        }
        else if (player.CurrentSpeed > targetSpeed)
        {
            player.CurrentSpeed = std::max(player.CurrentSpeed - speedDelta, targetSpeed);
        }

        // 3) 현재 진행 방향(heading)을 따라 전진/후진. 0도 = +Y, 시계 방향+.
        float rotRad = player.Rotation * (PI / 180.0f);
        float dirX = std::sin(rotRad);
        float dirY = std::cos(rotRad);

        player.X += dirX * player.CurrentSpeed * dt;
        player.Y += dirY * player.CurrentSpeed * dt;

        // 클램핑 대신 플레이 영역 경계에서 랩핑함. 월드가 16:9이므로 각
        // 축은 자신의 절반 범위(half-extent)를 기준으로 개별 랩핑됨.
        player.X = WrapCoordinate(player.X, proto::WORLD_HALF_EXTENT_X);
        player.Y = WrapCoordinate(player.Y, proto::WORLD_HALF_EXTENT_Y);

        // 4) 발사: 키를 누르고 있는 동안 자동 반복됨, 쿨다운으로만 제한됨
        // (엣지 감지 없음).
        if (fire && SecondsSince(player.LastFireTime) >= proto::FIRE_COOLDOWN_SECONDS)
        {
            player.LastFireTime = Clock::now();
            SpawnMissile_Locked(player);
        }

        player.LastProcessedTick = clientTick;
    }

    void CustomServer::SpawnMissile_Locked(const PlayerState& owner)
    {
        uint16_t missileId = nextMissileId_++;
        MissileState missile;
        missile.Id = missileId;
        missile.OwnerId = owner.Id;
        missile.X = owner.X;
        missile.Y = owner.Y;
        missile.Rotation = owner.Rotation;
        missile.DistanceTraveled = 0.0f;
        missiles_[missileId] = missile;
    }

    void CustomServer::SendPongResponse(const EndPoint& clientEp, uint8_t playerId, int64_t clientTicks)
    {
        {
            std::lock_guard<std::mutex> lock(stateMutex_);
            auto it = players_.find(playerId);
            if (it != players_.end())
                it->second.LastPingTime = Clock::now();
        }

        proto::BinaryWriter bw;
        bw.WriteByte(static_cast<uint8_t>(proto::PacketType::Pong));
        bw.WriteInt64(clientTicks);
        SendTo(clientEp, bw.Data(), bw.Size());
    }

    // ---------------------------------------------------------------------
    // 서버 틱 루프
    // ---------------------------------------------------------------------

    void CustomServer::ServerLoop()
    {
        int serverTick = 0;
        auto intervalMs = std::chrono::milliseconds(static_cast<int>(1000.0f / proto::TICK_RATE));
        float dt = 1.0f / proto::TICK_RATE;

        while (isRunning_)
        {
            serverTick++;

            {
                std::lock_guard<std::mutex> lock(stateMutex_);
                CheckTimeouts_Locked();
                CheckRespawns_Locked();
                UpdateMissiles_Locked(dt);
                BroadcastServerState_Locked(serverTick);
                BroadcastMissileState_Locked(serverTick);
                BroadcastScoreboardState_Locked();
            }

            std::this_thread::sleep_for(intervalMs);
        }
    }

    void CustomServer::UpdateMissiles_Locked(float dt)
    {
        if (missiles_.empty()) return;

        float collisionRadiusSum = proto::MISSILE_RADIUS + proto::TANK_COLLISION_RADIUS;
        float collisionDistSqr = collisionRadiusSum * collisionRadiusSum;
        float stepDistance = proto::MISSILE_SPEED * dt;

        std::vector<uint16_t> toRemove;

        for (auto& kvp : missiles_)
        {
            MissileState& missile = kvp.second;

            float rotRad = missile.Rotation * (PI / 180.0f);
            missile.X += std::sin(rotRad) * stepDistance;
            missile.Y += std::cos(rotRad) * stepDistance;
            missile.DistanceTraveled += stepDistance;

            // 1) 사거리 소진.
            if (missile.DistanceTraveled >= proto::MISSILE_MAX_DISTANCE)
            {
                toRemove.push_back(kvp.first);
                continue;
            }

            // 2) 탱크 충돌: 소유자와 사망한 플레이어는 건너뜀.
            for (auto& playerKvp : players_)
            {
                PlayerState& target = playerKvp.second;
                if (target.Id == missile.OwnerId) continue;
                if (target.IsDead) continue;

                float dx = missile.X - target.X;
                float dy = missile.Y - target.Y;
                if ((dx * dx + dy * dy) <= collisionDistSqr)
                {
                    toRemove.push_back(kvp.first);
                    ApplyDamage_Locked(target, 1, missile.OwnerId);
                    break;
                }
            }
        }

        for (uint16_t id : toRemove)
            missiles_.erase(id);
    }

    void CustomServer::ApplyDamage_Locked(PlayerState& target, int amount, uint8_t killerId)
    {
        target.Health -= amount;
        if (target.Health <= 0)
        {
            target.Health = 0;
            HandlePlayerDeath_Locked(target, killerId);
        }
    }

    void CustomServer::HandlePlayerDeath_Locked(PlayerState& target, uint8_t killerId)
    {
        target.IsDead = true;
        target.DeathTime = Clock::now();
        target.CurrentSpeed = 0.0f;

        auto killerIt = scoreboard_.find(killerId);
        if (killerIt != scoreboard_.end())
            killerIt->second.Kills++;

        auto victimIt = scoreboard_.find(target.Id);
        if (victimIt != scoreboard_.end())
            victimIt->second.Deaths++;
    }

    void CustomServer::CheckRespawns_Locked()
    {
        auto now = Clock::now();
        for (auto& kvp : players_)
        {
            PlayerState& player = kvp.second;
            if (!player.IsDead) continue;
            if (std::chrono::duration<double>(now - player.DeathTime).count() < proto::RESPAWN_DELAY_SECONDS)
                continue;

            player.CurrentSpeed = 0.0f;
            player.Health = proto::MAX_HEALTH;
            player.IsDead = false;
        }
    }

    void CustomServer::CheckTimeouts_Locked()
    {
        auto now = Clock::now();
        std::vector<uint8_t> toRemove;

        for (auto& kvp : players_)
        {
            if (std::chrono::duration<double>(now - kvp.second.LastPingTime).count() > proto::TIMEOUT_SECONDS)
                toRemove.push_back(kvp.first);
        }

        for (uint8_t id : toRemove)
        {
            players_.erase(id);
            scoreboard_.erase(id);
            Log("[Server] Player " + std::to_string(id) + " timed out (Total: " +
                std::to_string(players_.size()) + ").");
        }
    }

    void CustomServer::RemoveClientByEndPoint_Locked(const EndPoint& ep)
    {
        for (auto it = players_.begin(); it != players_.end(); ++it)
        {
            if (it->second.Endpoint == ep)
            {
                uint8_t id = it->first;
                players_.erase(it);
                scoreboard_.erase(id);
                Log("[Server] Player " + std::to_string(id) + " replaced/removed by endpoint match.");
                break;
            }
        }
    }

    // ---------------------------------------------------------------------
    // 브로드캐스트
    // ---------------------------------------------------------------------

    void CustomServer::BroadcastServerState_Locked(int serverTick)
    {
        if (players_.empty()) return;

        proto::BinaryWriter bw;
        bw.WriteByte(static_cast<uint8_t>(proto::PacketType::ServerState));
        bw.WriteInt32(serverTick);
        bw.WriteInt32(static_cast<int32_t>(players_.size()));

        for (auto& kvp : players_)
        {
            PlayerState& p = kvp.second;
            bw.WriteByte(p.Id);
            bw.WriteInt32(p.LastProcessedTick);
            bw.WriteSingle(p.X);
            bw.WriteSingle(p.Y);
            bw.WriteSingle(p.Rotation);
            bw.WriteSingle(p.CurrentSpeed);
            bw.WriteByte(static_cast<uint8_t>(p.Health));
            bw.WriteBool(p.IsDead);
        }

        for (auto& kvp : players_)
            SendTo(kvp.second.Endpoint, bw.Data(), bw.Size());
    }

    void CustomServer::BroadcastMissileState_Locked(int serverTick)
    {
        if (players_.empty()) return;

        proto::BinaryWriter bw;
        bw.WriteByte(static_cast<uint8_t>(proto::PacketType::MissileState));
        bw.WriteInt32(serverTick);
        bw.WriteInt32(static_cast<int32_t>(missiles_.size()));

        for (auto& kvp : missiles_)
        {
            MissileState& m = kvp.second;
            bw.WriteUInt16(m.Id);
            bw.WriteByte(m.OwnerId);
            bw.WriteSingle(m.X);
            bw.WriteSingle(m.Y);
            bw.WriteSingle(m.Rotation);
        }

        for (auto& kvp : players_)
            SendTo(kvp.second.Endpoint, bw.Data(), bw.Size());
    }

    void CustomServer::BroadcastScoreboardState_Locked()
    {
        if (players_.empty()) return;

        proto::BinaryWriter bw;
        bw.WriteByte(static_cast<uint8_t>(proto::PacketType::ScoreboardState));
        bw.WriteInt32(static_cast<int32_t>(scoreboard_.size()));

        for (auto& kvp : scoreboard_)
        {
            bw.WriteByte(kvp.first);
            bw.WriteInt32(kvp.second.Kills);
            bw.WriteInt32(kvp.second.Deaths);
        }

        for (auto& kvp : players_)
            SendTo(kvp.second.Endpoint, bw.Data(), bw.Size());
    }
}