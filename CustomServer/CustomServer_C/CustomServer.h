// CustomServer.h
//
// CustomServer.cs의 C++ 포팅 버전. 원본 C# 서버와 동일하게 동작해야 함:
// 동일한 상수(Protocol.h 참고), 동일한 틱별 연산 순서, 동일한 랩핑/충돌/
// 리스폰 규칙, 동일한 와이어 포맷. Unity 클라이언트가 동일한 공식으로
// 클라이언트 측 예측을 수행하므로, 여기서 발생하는 동작 차이는 클라이언트
// 에서 지속적인 보정(reconciliation) 스냅으로 나타남.
#pragma once

#include <atomic>
#include <chrono>
#include <cstdint>
#include <mutex>
#include <string>
#include <thread>
#include <unordered_map>
#include <unordered_set>
#include <vector>

#ifdef _WIN32
#define NOMINMAX
#include <winsock2.h>
#include <ws2tcpip.h>
using socket_t = SOCKET;
#else
#include <arpa/inet.h>
#include <netinet/in.h>
#include <sys/socket.h>
using socket_t = int;
#endif

#include "Protocol.h"
#include "BinaryStream.h"

namespace server
{
    using Clock = std::chrono::steady_clock;
    using TimePoint = Clock::time_point;

    // C#의 DateTime.MinValue는 "무한히 먼 과거"를 뜻하는 센티널 값으로
    // 사용됨(예: PlayerState.LastFireTime — 최초 발사 쿨다운 체크가
    // 항상 통과하도록). 기본 생성된 std::chrono::steady_clock::time_point
    // 는 먼 과거를 보장하지 않음 -- 일부 플랫폼에서 epoch가 부팅 시점이라,
    // 프로세스 초기에는 "기본값 - 현재"가 큰 음수가 아닌 작은 값일 수 있음.
    // FarPast()는 명시적 센티널 오프셋을 반환함: TimePoint::min() 자체는
    // 피함 -- 거기서 값을 빼면 오버플로우가 발생할 수 있어, min()으로부터
    // 크지만 안전한 오프셋을 사용함.
    inline TimePoint FarPast()
    {
        return TimePoint::min() + std::chrono::hours(24 * 365 * 10); // min()으로부터 약 10년, 안전 여유분
    }

    // C#의 IPEndPoint와 동일한 방식으로 사용되는 UDP 피어 주소:
    // 라우팅 대상이자 동등성 비교가 가능한 클라이언트 식별자로 사용됨.
    struct EndPoint
    {
        sockaddr_in addr{};

        bool operator==(const EndPoint& other) const
        {
            return addr.sin_family == other.addr.sin_family &&
                addr.sin_port == other.addr.sin_port &&
                addr.sin_addr.s_addr == other.addr.sin_addr.s_addr;
        }
    };

    // CustomServer.PlayerState를 미러링.
    struct PlayerState
    {
        uint8_t Id = 0;
        EndPoint Endpoint{};
        float X = 0.0f;
        float Y = 0.0f;
        float Rotation = 0.0f;     // 도(degree) 단위, 0 = +Y, 시계 방향 증가
        float CurrentSpeed = 0.0f; // 부호 있음: + 전진, - 후진
        int SpawnSlot = 0;
        int LastProcessedTick = 0;
        TimePoint LastPingTime{};

        TimePoint LastFireTime = FarPast(); // 센티널: 최초 발사가 항상 쿨다운을 통과하도록 보장

        int Health = proto::MAX_HEALTH;
        bool IsDead = false;
        TimePoint DeathTime{}; // IsDead가 true일 때만 유효
    };

    // CustomServer.MissileState를 미러링.
    struct MissileState
    {
        uint16_t Id = 0;
        uint8_t OwnerId = 0;
        float X = 0.0f;
        float Y = 0.0f;
        float Rotation = 0.0f;
        float DistanceTraveled = 0.0f;
    };

    // CustomServer.ScoreEntry를 미러링.
    struct ScoreEntry
    {
        int Kills = 0;
        int Deaths = 0;
    };

    class CustomServer
    {
    public:
        CustomServer() = default;
        ~CustomServer();

        void Start();
        void Stop();

    private:
        // --- 네트워킹 ---
        socket_t udpSocket_ = -1;
        std::atomic<bool> isRunning_{ false };
        std::thread rxThread_;
        std::thread loopThread_;

        // --- 공유 상태, stateMutex_로 보호됨 (ConcurrentDictionary 대체재:
        // 매 틱마다 모든 플레이어/미사일에 대해 일관된 스냅샷이 필요하므로
        // 단일 뮤텍스로 거친 단위(coarse-grained) 잠금이면 충분하며, 이
        // 규모의 패킷 처리량에서는 단일 뮤텍스가 병목이 되지 않음) ---
        std::mutex stateMutex_;
        std::unordered_map<uint8_t, PlayerState> players_;
        std::unordered_map<uint16_t, MissileState> missiles_;
        std::unordered_map<uint8_t, ScoreEntry> scoreboard_;
        uint16_t nextMissileId_ = 1;
        uint8_t nextPlayerId_ = 1;

        // --- 수신 루프 ---
        void ReceiveLoop();
        void HandleJoinRequest(const EndPoint& clientEp);
        void HandleClientInput(proto::BinaryReader& br);
        void SendPongResponse(const EndPoint& clientEp, uint8_t playerId, int64_t clientTicks);

        // --- 스폰 ---
        // (x, y, rotationDeg, slot)을 반환. 호출자는 stateMutex_를 이미 보유하고 있어야 함.
        struct SpawnTransform { float x, y, rotationDeg; int slot; };
        SpawnTransform ComputeSpawnTransform_Locked();

        // --- 시뮬레이션 ---
        static float WrapCoordinate(float value, float halfExtent);
        void SpawnMissile_Locked(const PlayerState& owner);

        // --- 서버 틱 루프 ---
        void ServerLoop();
        void CheckTimeouts_Locked();
        void CheckRespawns_Locked();
        void UpdateMissiles_Locked(float dt);
        void ApplyDamage_Locked(PlayerState& target, int amount, uint8_t killerId);
        void HandlePlayerDeath_Locked(PlayerState& target, uint8_t killerId);

        void RemoveClientByEndPoint_Locked(const EndPoint& ep);

        // --- 브로드캐스트 ---
        void BroadcastServerState_Locked(int serverTick);
        void BroadcastMissileState_Locked(int serverTick);
        void BroadcastScoreboardState_Locked();

        // --- 원시 전송 헬퍼 ---
        void SendTo(const EndPoint& ep, const uint8_t* data, size_t len);
    };
}