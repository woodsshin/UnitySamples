using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace NetworkServer
{
    public class CustomServer
    {
        public enum PacketType : byte
        {
            JoinRequest = 1,
            JoinResponse = 2,
            ClientInput = 3,
            ServerState = 4,
            Ping = 5,
            Pong = 6,
            MissileState = 7,
            ScoreboardState = 8
        }

        public class PlayerState
        {
            public byte Id;
            public IPEndPoint EndPoint;
            public float X;
            public float Y;
            public float Rotation; // degrees, 0 = +Y(위쪽) 기준, 시계방향 증가
            public float CurrentSpeed; // 현재 전진(+) / 후진(-) 속도
            public int SpawnSlot; // 배정받은 스폰 슬롯 (0 ~ SPAWN_SLOTS-1). 겹치지 않는 위치 배정을 위해 추적.
            // 이 플레이어가 보낸 ClientInput 중 서버가 실제로 처리한 마지막 clientTick.
            // HandleClientInput에서 매 패킷마다 갱신되며, BroadcastServerState에서 그대로
            // 클라이언트에 전송 및 재조정(reconciliation)의 pending 입력 정리 기준으로 쓰인다.
            public int LastProcessedTick;
            public DateTime LastPingTime = DateTime.UtcNow;

            // 미사일 연사 제한용. 스페이스바를 계속 누르고 있어도 이 시각 기준
            // FIRE_COOLDOWN_SECONDS가 지나야 다음 발사가 허용된다 (자동 연사, 엣지 검출 없음).
            public DateTime LastFireTime = DateTime.MinValue;

            // 체력/사망 관련 상태.
            public int Health = MAX_HEALTH;
            public bool IsDead;
            public DateTime DeathTime = DateTime.MinValue; // IsDead일 때만 의미 있음. 리스폰 타이머 기준.
        }

        public class MissileState
        {
            public ushort Id;
            public byte OwnerId;
            public float X;
            public float Y;
            public float Rotation; // degrees, 0 = +Y 기준. 발사 시점 방향으로 고정, 이후 회전하지 않는다.
            public float DistanceTraveled; // 발사 이후 누적 이동 거리(유닛). MISSILE_MAX_DISTANCE 초과 시 자동 삭제.
        }

        /// <summary>
        /// 플레이어 하나의 킬/데스 집계. PlayerId(byte) 기준으로 관리하며, 지금은 재접속 시
        /// 새 PlayerId가 발급되므로 사실상 리셋된다(나중에 닉네임/로그인 같은 영속 식별자가
        /// 생기면 그 키로 교체할 것을 전제로 한 단순화된 1단계 구현).
        /// </summary>
        public class ScoreEntry
        {
            public int Kills;
            public int Deaths;
        }

        private UdpClient _udpServer;
        private readonly ConcurrentDictionary<byte, PlayerState> _players = new();
        private readonly ConcurrentDictionary<ushort, MissileState> _missiles = new();
        private readonly ConcurrentDictionary<byte, ScoreEntry> _scoreboard = new();
        private ushort _nextMissileId = 1;
        private byte _nextPlayerId = 1;
        private bool _isRunning = false;

        private const int PORT = 9050;
        private const float TICK_RATE = 60f; // 60Hz
        private const float TIMEOUT_SECONDS = 3.0f;

        // 탱크 컨트롤 이동 파라미터
        // [중요] 이 4개 상수는 CustomClient.cs의
        // "Tank Movement (must mirror server constants)" 인스펙터 필드와 정확히 같은 값이어야 한다.
        // 클라이언트 예측(SimulateTankStep)이 이 서버 계산(HandleClientInput)과 동일한 결과를
        // 나와야만 재조정(reconciliation)이 매 틱 어긋나지 않는다. 한쪽만 바꾸면 클라이언트가
        // 계속 예측 오차를 내며 위치/회전에 jittering 증상으로 나타난다.
        private const float MAX_FORWARD_SPEED = 3.0f;   // 전진 최대 속도
        private const float MAX_REVERSE_SPEED = 3.0f;   // 후진 최대 속도(전진과 동일)
        private const float ACCELERATION = 4.0f;        // 초당 속도 변화량
        private const float TURN_SPEED_DEG = 160f;      // 초당 회전 각도(도)

        // 미사일 파라미터
        // [중요] MISSILE_SPEED와 FIRE_COOLDOWN_SECONDS는 클라이언트의 로컬 예측(발사 순간
        // 클라이언트가 먼저 그리는 미사일)과 오차범위 이상으로 안 된다. 클라이언트 쪽 값은
        // CustomClient.cs의 "Missile" 인스펙터 필드를 참고.
        private const float MISSILE_SPEED = 12.0f;         // 초당 이동 거리(유닛). 탱크 최대 속도(3.0)의 4배.
        private const float FIRE_COOLDOWN_SECONDS = 0.3f;  // 연사 제한. 이보다 짧은 간격의 발사 입력은 무시한다.
        private const float MISSILE_RADIUS = 0.06f;        // 충돌 판정용 미사일 반지름
        private const float TANK_COLLISION_RADIUS = 0.175f; // 충돌 판정용 탱크 반지름. 클라이언트 SHIP_WORLD_DIAMETER(0.35)의 절반과 일치시킨다.
        // 탱크가 화면 경계에서 반대편으로 순간이동(wrap)하도록 바뀌면서, 미사일은
        // 더 이상 "월드 경계를 벗어나면 삭제"라는 자연스러운 소멸 조건이 없어졌다(경계라는
        // 개념 자체가 탱크에만 적용되고 미사일은 계속 직진하므로, wrap하지 않으면 영원히
        // 날아간다). 대신 발사 지점으로부터의 누적 이동 거리로 수명을 제한한다.
        // [중요] 클라이언트(CustomClient.cs의 "Missile" 인스펙터 필드, 그리고
        // ECS SimulationConfig.MissileMaxDistance)와 정확히 같은 값이어야 한다.
        // [중요] 화면이 1280x720(16:9)으로 고정되며 WORLD_HALF_EXTENT_X(7.1111)가 Y(4.0)보다
        // 훨씬 넓어졌으므로, 사거리도 더 긴 축(가로) 기준으로 다시 잡는다. 화면을 대각선
        // 없이 가로지르는 최대 거리는 WORLD_HALF_EXTENT_X*2(약 14.2222)이며, 이 값을 사거리로
        // 쓴다 — 이전(정사각형 시절, 8.0)을 그대로 두면 미사일이 화면 가로 폭을 채 절반도
        // 못 가서 사라지는 부자연스러운 결과가 나온다.
        private const float MISSILE_MAX_DISTANCE = 14.2222222f;   // 사거리(유닛). WORLD_HALF_EXTENT_X*2와 맞춤.

        // 체력/사망/리스폰 파라미터
        private const int MAX_HEALTH = 10;                 // 미사일 10발에 사망
        private const float RESPAWN_DELAY_SECONDS = 3.0f;  // 사망 후 리스폰까지 대기 시간

        // 스폰 배치 파라미터
        // [중요] 화면 해상도가 1280x720(16:9)으로 고정되었으므로, 더 이상 다양한 화면비를
        // 방어적으로 고려할 필요가 없다. 클라이언트 카메라는 Orthographic Size 5, 화면비
        // 16:9(1280x720)로 고정 설정되어 있다는 전제 하에 WORLD_HALF_EXTENT_X/Y를 정확히
        // 계산해 상수로 설정한다:
        //   half_y = Orthographic Size = 4.0 (기존과 동일하게 유지, 카메라 자체는 5지만
        //            world 경계는 지금까지처럼 그보다 살짝 좁게 잡는 기존 관례를 유지)
        //   half_x = half_y * (1280/720) = 4.0 * 16/9 = 7.1111111...
        // 클라이언트(CustomClient.cs, ECS SimulationConfig)의 대응 값과 정확히
        // 같아야 한다 — 하나라도 다르면 wrap이 일어나는 좌표가 어긋나 재조정이 매 틱 실패한다.
        private const float WORLD_HALF_EXTENT_X = 7.1111111f; // 서버가 강제하는 플레이 가능 영역의 절반 가로 크기 (16:9)
        private const float WORLD_HALF_EXTENT_Y = 4.0f;       // 서버가 강제하는 플레이 가능 영역의 절반 세로 크기
        private const float SPAWN_RADIUS = 1.2f;        // 스폰 원 반지름. WORLD_HALF_EXTENT_Y(더 좁은 축)보다
                                                        // 충분히 작아야 스폰 지점이 항상 플레이 영역 안에 들어온다.
        private const int SPAWN_SLOTS = 16;             // 원 위의 스폰 자리 개수. ComputeSpawnTransform이 이 중 비어있는 자리를 찾아 배정한다.

        private static void Log(string message)
        {
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}");
        }

        public void Start()
        {
            _udpServer = new UdpClient(PORT);

            // Windows ICMP Connection Reset 무시 설정
            try
            {
                const int SIO_UDP_CONNRESET = -1744830452;
                _udpServer.Client.IOControl((IOControlCode)SIO_UDP_CONNRESET, new byte[] { 0 }, null);
            }
            catch { }

            _isRunning = true;
            Log($"[Server] UDP Server started on port {PORT}...");

            Thread rxThread = new Thread(ReceiveLoop) { IsBackground = true };
            rxThread.Start();

            Thread loopThread = new Thread(ServerLoop) { IsBackground = true };
            loopThread.Start();
        }

        private void ReceiveLoop()
        {
            IPEndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);

            while (_isRunning)
            {
                try
                {
                    byte[] data = _udpServer.Receive(ref remoteEP);
                    using var ms = new MemoryStream(data);
                    using var br = new BinaryReader(ms);
                    PacketType type = (PacketType)br.ReadByte();

                    switch (type)
                    {
                        case PacketType.JoinRequest:
                            HandleJoinRequest(remoteEP);
                            break;

                        case PacketType.ClientInput:
                            HandleClientInput(br);
                            break;

                        case PacketType.Ping:
                            byte playerId = br.ReadByte();
                            long clientTicks = br.ReadInt64();
                            SendPongResponse(remoteEP, playerId, clientTicks);
                            break;
                    }
                }
                catch (SocketException)
                {
                    if (!_isRunning) break;
                    // SocketException 발생 시 remoteEP는 직전 정상 패킷 주소를 가리키므로
                    // 타임아웃 처리는 CheckTimeouts()에 위임.
                }
                catch (Exception ex)
                {
                    if (!_isRunning) break;
                    Log($"[Server Error] {ex.Message}");
                }
            }
        }

        private void HandleJoinRequest(IPEndPoint clientEP)
        {
            // 동일한 EndPoint로 기존 접속이 있다면 사전 정리
            RemoveClientByEndPoint(clientEP);

            byte assignedId = _nextPlayerId++;
            (float spawnX, float spawnY, float spawnRot, int spawnSlot) = ComputeSpawnTransform();

            var newPlayer = new PlayerState
            {
                Id = assignedId,
                EndPoint = clientEP,
                X = spawnX,
                Y = spawnY,
                Rotation = spawnRot,
                CurrentSpeed = 0f,
                SpawnSlot = spawnSlot,
                LastPingTime = DateTime.UtcNow,
                Health = MAX_HEALTH,
                IsDead = false
            };

            _players[assignedId] = newPlayer;
            _scoreboard[assignedId] = new ScoreEntry();

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write((byte)PacketType.JoinResponse);
            bw.Write(assignedId);

            byte[] bytes = ms.ToArray();
            _udpServer.Send(bytes, bytes.Length, clientEP);

            Log($"[Server] Player {assignedId} joined from {clientEP} (Total: {_players.Count})");
        }

        /// <summary>
        /// 현재 접속해 있는 다른 플레이어들이 점유하지 않은 스폰 슬롯을 찾아 원형으로 배치하고,
        /// 원 중심에서 바깥쪽을 향하는 초기 회전각을 함께 계산합니다.
        /// (누적 발급 ID가 아니라 "지금 실제로 사용 중인 슬롯"을 기준으로 찾기 때문에,
        /// 플레이어가 들어왔다 나가도 슬롯이 자연스럽게 재사용되어 SPAWN_SLOTS 인원 이하로
        /// 동시 접속해 있는 한 항상 서로 겹치지 않습니다.)
        /// </summary>
        private (float x, float y, float rotationDeg, int slot) ComputeSpawnTransform()
        {
            var occupiedSlots = new HashSet<int>();
            foreach (var kvp in _players)
            {
                occupiedSlots.Add(kvp.Value.SpawnSlot);
            }

            int chosenSlot = 0;
            for (int i = 0; i < SPAWN_SLOTS; i++)
            {
                if (!occupiedSlots.Contains(i))
                {
                    chosenSlot = i;
                    break;
                }
                // 모든 슬롯이 이미 점유 중이라면(동시 접속자가 SPAWN_SLOTS를 초과한 드문 경우)
                // 마지막으로 확인한 슬롯을 그대로 사용한다. 중복이 발생하지만 로직에는 문제가 되지 않는다.
                chosenSlot = i;
            }

            float angleDeg = chosenSlot * (360f / SPAWN_SLOTS);
            float angleRad = angleDeg * (MathF.PI / 180f);

            float x = MathF.Sin(angleRad) * SPAWN_RADIUS;
            float y = MathF.Cos(angleRad) * SPAWN_RADIUS;

            // 회전 기준: 0도 = +Y(위쪽)을 바라봄, 시계방향 증가.
            // 원 중심에서 바깥쪽을 향하도록 스폰 각도와 동일하게 맞춘다.
            float rotationDeg = angleDeg;

            return (x, y, rotationDeg, chosenSlot);
        }

        /// <summary>
        /// 좌표 하나를 [-halfExtent, +halfExtent) 범위로 순환(wrap)시킨다. 경계를 넘어가면
        /// clamp처럼 멈추는 대신 반대편 경계에서 다시 나타나게 하기 위한 공식이다.
        ///
        /// [중요] 이 함수는 CustomClient.cs(MonoBehaviour, SimulateTankStep)와
        /// LocalPlayerFixedStepSystem.cs(ECS, SimulateTankStep) 양쪽에 정확히 동일한 공식으로
        /// 이식되어 있어야 한다. 세 곳 중 하나만 다르면(예: 경계 처리 순서, 반개구간 방향 등)
        /// wrap이 일어나는 정확한 좌표가 미세하게 달라져 재조정이 매 틱 어긋나기 시작한다.
        ///
        /// C#의 %는 피연산자가 음수면 결과도 음수 부호를 유지하므로(예: -1 % 8 == -1, 0이 아님),
        /// 그냥 한 번 %를 적용하는 것만으로는 음수 쪽 wrap이 틀린다. "% range 후 다시 +range,
        /// 다시 % range"로 두 번 적용해 항상 [0, range) 안으로 넣은 뒤 half를 빼서 [-half, +half)로
        /// 옮기는 방식을 쓴다.
        ///
        /// 한 틱 동안 이동 거리가 range(2*halfExtent)를 초과해 한 번의 wrap으로 부족한 극단적인
        /// 경우(예: 매우 낮은 틱레이트 + 매우 빠른 속도)에도 이 modulo 기반 공식은 여러 바퀴를
        /// 순환한 것과 동일하게 정확히 처리되며, "밖으로 나간 만큼 다시 안쪽에서" 식의 단순
        /// if-분기(한 번만 wrap)보다 안전하다.
        /// </summary>
        private static float WrapCoordinate(float value, float halfExtent)
        {
            float range = halfExtent * 2f;
            if (range <= 0f) return 0f;

            float shifted = value + halfExtent; // [-half, +half) -> [0, range) 기준으로 이동
            float wrapped = shifted % range;
            if (wrapped < 0f) wrapped += range; // C#의 음수 % 결과 보정
            return wrapped - halfExtent; // 다시 [-half, +half)로 복귀
        }

        /// <summary>
        /// [중요] 이 함수의 계산 순서(회전 → 가속/감속 → 이동 → 화면 경계 wrap)와 공식은
        /// 클라이언트의 CustomClient.SimulateTankStep과 반드시 동일해야 한다.
        /// 둘 중 하나만 수정하면 클라이언트 예측이 서버 결과와 어긋나기 시작한다.
        /// </summary>
        private void HandleClientInput(BinaryReader br)
        {
            byte playerId = br.ReadByte();
            int clientTick = br.ReadInt32();
            float throttle = br.ReadSingle(); // -1(후진) ~ +1(전진)
            float turn = br.ReadSingle();     // -1(좌회전) ~ +1(우회전)
            bool fire = br.ReadBoolean();     // 스페이스바가 눌려있는 동안 true (자동 연사, 쿨다운은 서버가 강제)

            if (_players.TryGetValue(playerId, out var player))
            {
                // 사망한 플레이어는 리스폰 대기 중이므로 이동/회전/발사를 전혀 반영하지 않는다.
                // LastProcessedTick만 갱신해 클라이언트의 pending input 정리(reconciliation)가
                // 계속 정상적으로 진행되게 한다 (그래야 리스폰 순간 위치가 깨끗하게 맞아떨어진다).
                if (player.IsDead)
                {
                    player.LastProcessedTick = clientTick;
                    return;
                }

                float dt = 1.0f / TICK_RATE;

                throttle = Math.Clamp(throttle, -1f, 1f);
                turn = Math.Clamp(turn, -1f, 1f);

                // 1) 회전 먼저 적용 (탱크 컨트롤: 좌우 입력은 즉시 방향 전환)
                player.Rotation += turn * TURN_SPEED_DEG * dt;
                player.Rotation %= 360f;
                if (player.Rotation < 0f) player.Rotation += 360f;

                // 2) 목표 속도로 가속/감속 (급발진/급정거 대신 부드러운 가감속)
                float targetSpeed = throttle >= 0f
                    ? throttle * MAX_FORWARD_SPEED
                    : throttle * MAX_REVERSE_SPEED;

                float speedDelta = ACCELERATION * dt;
                if (player.CurrentSpeed < targetSpeed)
                {
                    player.CurrentSpeed = Math.Min(player.CurrentSpeed + speedDelta, targetSpeed);
                }
                else if (player.CurrentSpeed > targetSpeed)
                {
                    player.CurrentSpeed = Math.Max(player.CurrentSpeed - speedDelta, targetSpeed);
                }

                // 3) 현재 방향으로 전진/후진. 회전 0도 = +Y(위쪽), 시계방향 증가 기준.
                float rotRad = player.Rotation * (MathF.PI / 180f);
                float dirX = MathF.Sin(rotRad);
                float dirY = MathF.Cos(rotRad);

                player.X += dirX * player.CurrentSpeed * dt;
                player.Y += dirY * player.CurrentSpeed * dt;

                // 화면(플레이 가능 영역) 경계를 벽처럼 막는 대신, 경계를 넘어가면 반대편에서
                // 다시 나타나도록 순환(wrap)시킨다. 속도/회전은 그대로 둔다 — wrap은 오직
                // 위치 좌표만 바꾸는 순간이동이며, 진행 방향이나 속력에는 영향을 주지 않는다.
                // 화면이 16:9로 고정되어 X/Y 경계 크기가 다르므로, 각 축은 반드시 자신의
                // half-extent(WORLD_HALF_EXTENT_X / _Y)로 wrap해야 한다 — X를 Y 기준으로
                // wrap하면(또는 그 반대) 좌우 벽보다 훨씬 안쪽에서 잘못 순간이동하게 된다.
                player.X = WrapCoordinate(player.X, WORLD_HALF_EXTENT_X);
                player.Y = WrapCoordinate(player.Y, WORLD_HALF_EXTENT_Y);

                // 4) 미사일 발사: 스페이스바를 누르고 있는 동안 FIRE_COOLDOWN_SECONDS 간격으로
                //    자동 연사한다. 엣지 검출(눌리는 순간만) 없이 fire가 true이기만 하면 되고,
                //    실제 발사 빈도 제한은 오직 쿨다운(LastFireTime)만으로 강제한다.
                if (fire && (DateTime.UtcNow - player.LastFireTime).TotalSeconds >= FIRE_COOLDOWN_SECONDS)
                {
                    player.LastFireTime = DateTime.UtcNow;
                    SpawnMissile(player);
                }

                player.LastProcessedTick = clientTick;
            }
        }

        /// <summary>
        /// 발사한 플레이어의 현재 위치/방향에서 미사일 하나를 생성해 활성 미사일 목록에 추가한다.
        /// 미사일은 발사 시점의 회전각으로 고정되어 직진하며(이후 방향이 바뀌지 않음), 이동/충돌
        /// 판정은 ServerLoop -> UpdateMissiles에서 매 틱 처리한다.
        /// </summary>
        private void SpawnMissile(PlayerState owner)
        {
            ushort missileId = _nextMissileId++;
            var missile = new MissileState
            {
                Id = missileId,
                OwnerId = owner.Id,
                X = owner.X,
                Y = owner.Y,
                Rotation = owner.Rotation,
                DistanceTraveled = 0f
            };
            _missiles[missileId] = missile;
        }

        private void SendPongResponse(IPEndPoint clientEP, byte playerId, long clientTicks)
        {
            // PlayerId를 직접 수신하여 해당 플레이어의 Ping 시각 갱신
            if (_players.TryGetValue(playerId, out var player))
            {
                player.LastPingTime = DateTime.UtcNow;
            }

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write((byte)PacketType.Pong);
            bw.Write(clientTicks);

            byte[] bytes = ms.ToArray();
            _udpServer.Send(bytes, bytes.Length, clientEP);
        }

        private void ServerLoop()
        {
            int serverTick = 0;
            int intervalMs = (int)(1000f / TICK_RATE);
            float dt = 1.0f / TICK_RATE;

            while (_isRunning)
            {
                serverTick++;

                CheckTimeouts();
                CheckRespawns();
                UpdateMissiles(dt);
                BroadcastServerState(serverTick);
                BroadcastMissileState(serverTick);
                BroadcastScoreboardState();

                Thread.Sleep(intervalMs);
            }
        }

        /// <summary>
        /// 활성 미사일을 한 틱만큼 직진 이동시키고, 두 가지 삭제 조건을 매 틱 검사한다:
        /// 1) 발사 지점으로부터의 누적 이동 거리가 MISSILE_MAX_DISTANCE를 초과한 경우 (사거리 소진)
        /// 2) 발사자 본인을 제외한, 살아있는 다른 플레이어의 탱크와 반지름이 겹친 경우 (탱크 충돌)
        /// 탱크 충돌 시 미사일은 삭제되고 맞은 플레이어의 체력이 1 감소한다. 체력이 0 이하가
        /// 되면 즉시 사망 처리(HandlePlayerDeath)한다. 이미 사망한 플레이어는 판정에서 제외되어
        /// 리스폰 대기 중에는 미사일이 그냥 통과한다.
        ///
        /// [중요] 탱크가 화면 경계에서 반대편으로 wrap하지만 미사일은 wrap하지 않고 발사 방향
        /// 그대로 계속 직진한다(발사 시점 방향으로 고정되어 이후 회전하지 않는다는 기존 설계와
        /// 일관성을 유지). 따라서 더 이상 "월드 경계를 벗어났는지"로는 삭제 여부를 판단할 수
        /// 없고(경계 밖으로 나가는 것 자체가 정상 동작), 오직 사거리(DistanceTraveled)만으로
        /// 수명을 제한한다.
        /// </summary>
        private void UpdateMissiles(float dt)
        {
            if (_missiles.IsEmpty) return;

            float collisionDistSqr = (MISSILE_RADIUS + TANK_COLLISION_RADIUS) * (MISSILE_RADIUS + TANK_COLLISION_RADIUS);
            float stepDistance = MISSILE_SPEED * dt;

            foreach (var kvp in _missiles)
            {
                var missile = kvp.Value;

                float rotRad = missile.Rotation * (MathF.PI / 180f);
                missile.X += MathF.Sin(rotRad) * stepDistance;
                missile.Y += MathF.Cos(rotRad) * stepDistance;
                missile.DistanceTraveled += stepDistance;

                // 1) 사거리 소진: 발사 이후 누적 이동 거리가 최대 사거리를 넘으면 즉시 삭제
                if (missile.DistanceTraveled >= MISSILE_MAX_DISTANCE)
                {
                    _missiles.TryRemove(kvp.Key, out _);
                    continue;
                }

                // 2) 탱크 충돌: 발사자 자신과 이미 사망한 플레이어는 제외하고 검사
                foreach (var playerKvp in _players)
                {
                    var target = playerKvp.Value;
                    if (target.Id == missile.OwnerId) continue;
                    if (target.IsDead) continue;

                    float dx = missile.X - target.X;
                    float dy = missile.Y - target.Y;
                    if ((dx * dx + dy * dy) <= collisionDistSqr)
                    {
                        _missiles.TryRemove(kvp.Key, out _);
                        ApplyDamage(target, 1, missile.OwnerId);
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// 대상 플레이어의 체력을 amount만큼 깎는다. 체력이 0 이하로 떨어지면 사망 처리한다.
        /// 이미 사망한 플레이어에게는 호출되지 않는다(UpdateMissiles에서 IsDead를 미리 걸러냄).
        /// killerId는 스코어보드 집계용으로, 사망이 실제로 발생했을 때만 사용된다.
        /// </summary>
        private void ApplyDamage(PlayerState target, int amount, byte killerId)
        {
            target.Health -= amount;
            if (target.Health <= 0)
            {
                target.Health = 0;
                HandlePlayerDeath(target, killerId);
            }
        }

        /// <summary>
        /// 플레이어를 사망 상태로 전환한다. 위치는 그대로 두어(화면에는 클라이언트가 회색으로
        /// 계속 표시) 죽은 자리를 유지하고, 속도만 0으로 만들어 관성으로 미끄러지지 않게 한다.
        /// 리스폰 자체는 ServerLoop -> CheckRespawns에서 RESPAWN_DELAY_SECONDS 경과 후 처리한다.
        /// 스코어보드에는 가해자의 Kills와 피해자(target)의 Deaths를 각각 1씩 더한다. 두 항목이
        /// 이 시점에 이미 존재하지 않는(방금 접속을 끊은 등) 예외적인 경우는 무시한다.
        /// </summary>
        private void HandlePlayerDeath(PlayerState target, byte killerId)
        {
            target.IsDead = true;
            target.DeathTime = DateTime.UtcNow;
            target.CurrentSpeed = 0f;

            if (_scoreboard.TryGetValue(killerId, out var killerScore))
            {
                Interlocked.Increment(ref killerScore.Kills);
            }
            if (_scoreboard.TryGetValue(target.Id, out var victimScore))
            {
                Interlocked.Increment(ref victimScore.Deaths);
            }
        }

        /// <summary>
        /// 사망한 플레이어 중 RESPAWN_DELAY_SECONDS가 지난 플레이어를 리스폰 처리한다.
        /// 위치를 그대로 유지한 채 체력만 MAX_HEALTH로 되돌리고 Input을 다시 받기 시작한다("제자리
        ///
        /// 이 변경은 클라이언트가 리스폰 순간 겪던 "점프처럼 보이는 랙"과도 연결된다 —
        /// 클라이언트는 리스폰 감지 시 보간/재조정 없이 서버가 보낸 위치로 즉시 스냅하는데
        /// (ServerStateApplySystem.ReconcileLocalPlayer의 justRespawned 분기, 원격은
        /// RemotePlayerInterpolationSystem.ApplyRespawnJumps), 그 "즉시 스냅"이 죽은 자리와
        /// 정확히 같은 좌표로 향하면 시각적으로 스냅 자체가 티가 나지 않는다. 좌표가 그대로면
        /// 스냅해도 이동이 0이기 때문이다.
        /// </summary>
        private void CheckRespawns()
        {
            var now = DateTime.UtcNow;
            foreach (var kvp in _players)
            {
                var player = kvp.Value;
                if (!player.IsDead) continue;
                if ((now - player.DeathTime).TotalSeconds < RESPAWN_DELAY_SECONDS) continue;

                player.CurrentSpeed = 0f;
                player.Health = MAX_HEALTH;
                player.IsDead = false;
            }
        }

        private void BroadcastMissileState(int serverTick)
        {
            if (_players.IsEmpty) return;

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write((byte)PacketType.MissileState);
            bw.Write(serverTick);
            bw.Write(_missiles.Count);

            foreach (var kvp in _missiles)
            {
                var m = kvp.Value;
                bw.Write(m.Id);
                bw.Write(m.OwnerId);
                bw.Write(m.X);
                bw.Write(m.Y);
                bw.Write(m.Rotation);
            }

            byte[] packet = ms.ToArray();

            foreach (var kvp in _players)
            {
                try
                {
                    _udpServer.Send(packet, packet.Length, kvp.Value.EndPoint);
                }
                catch { }
            }
        }

        /// <summary>
        /// 현재 접속 중인 모든 플레이어의 킬/데스를 매 틱 브로드캐스트한다. 스코어보드는
        /// 이동/미사일처럼 매 프레임 변하지 않으므로 상태 자체는 가볍지만, 별도 이벤트
        /// 채널을 두는 대신 기존 브로드캐스트 루프에 얹어 구현을 단순하게 유지한다.
        /// </summary>
        private void BroadcastScoreboardState()
        {
            if (_players.IsEmpty) return;

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write((byte)PacketType.ScoreboardState);
            bw.Write(_scoreboard.Count);

            foreach (var kvp in _scoreboard)
            {
                bw.Write(kvp.Key);           // PlayerId
                bw.Write(kvp.Value.Kills);
                bw.Write(kvp.Value.Deaths);
            }

            byte[] packet = ms.ToArray();

            foreach (var kvp in _players)
            {
                try
                {
                    _udpServer.Send(packet, packet.Length, kvp.Value.EndPoint);
                }
                catch { }
            }
        }

        private void CheckTimeouts()
        {
            var now = DateTime.UtcNow;
            foreach (var kvp in _players)
            {
                if ((now - kvp.Value.LastPingTime).TotalSeconds > TIMEOUT_SECONDS)
                {
                    if (_players.TryRemove(kvp.Key, out _))
                    {
                        _scoreboard.TryRemove(kvp.Key, out _);
                        Log($"[Server] Player {kvp.Key} timed out (Total: {_players.Count}).");
                    }
                }
            }
        }

        private void RemoveClientByEndPoint(IPEndPoint ep)
        {
            foreach (var kvp in _players)
            {
                if (kvp.Value.EndPoint.Equals(ep))
                {
                    if (_players.TryRemove(kvp.Key, out _))
                    {
                        _scoreboard.TryRemove(kvp.Key, out _);
                        Log($"[Server] Player {kvp.Key} replaced/removed by endpoint match.");
                    }
                    break;
                }
            }
        }

        private void BroadcastServerState(int serverTick)
        {
            if (_players.IsEmpty) return;

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write((byte)PacketType.ServerState);
            bw.Write(serverTick);
            bw.Write(_players.Count);

            foreach (var kvp in _players)
            {
                var p = kvp.Value;
                bw.Write(p.Id);
                // [추가] 클라이언트가 재조정(reconciliation)에서 pending 입력을 정리할 기준으로
                // 쓴다. 예전에는 클라이언트가 이 패킷의 serverTick(=ServerLoop가 도는 횟수를
                // 세는, 플레이어 입력 처리와 아무 인과관계 없는 전역 카운터)과 자신의 로컬
                // _clientTick을 직접 비교했다. 이 둘은 서로 다른 시계에서 흘러가는 값이라
                // 접속 시점부터 이미 어긋나 있고, 시간이 지날수록 서로 다른 속도로 더 벌어져
                // "연결 후 시간이 지나면 움직임이 이상해지는" 문제의 근본 원인이었다. 이제는
                // 이 값(그 플레이어의 입력을 서버가 실제로 어디까지 처리했는지)을 그대로 전송해서
                // 정확한 기준으로 쓸 수 있게 한다.
                bw.Write(p.LastProcessedTick);
                bw.Write(p.X);
                bw.Write(p.Y);
                bw.Write(p.Rotation);
                bw.Write(p.CurrentSpeed);
                bw.Write((byte)p.Health);
                bw.Write(p.IsDead);
            }

            byte[] packet = ms.ToArray();

            foreach (var kvp in _players)
            {
                try
                {
                    _udpServer.Send(packet, packet.Length, kvp.Value.EndPoint);
                }
                catch { }
            }
        }

        public void Stop()
        {
            _isRunning = false;
            _udpServer?.Close();
            Log("[Server] UDP Server stopped.");
        }
    }

    class Program
    {
        static void Main(string[] args)
        {
            CustomServer server = new CustomServer();
            server.Start();

            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Press Enter to stop server...");
            Console.ReadLine();

            server.Stop();
        }
    }
}