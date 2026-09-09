using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace LoadTestBot
{
    /// <summary>
    /// 서버에 접속해 하나의 플레이어처럼 행동하는 봇 하나.
    /// 이동은 "전진 위주 + 가끔 정지/후진/제자리 회전"을 섞은 복합 패턴이고,
    /// 발사는 스페이스바를 누르고 있는 것처럼 구간(burst) 단위로 on/off를 반복한다.
    /// 재접속 로직은 의도적으로 넣지 않았다 — 연결이 끊기면 그 봇은 그대로 종료된다.
    /// </summary>
    public class BotClient
    {
        // 서버 상수와 반드시 일치해야 하는 값들 (CustomServer.cs 참고)
        // [참고] 이전에는 여기 WorldHalfExtent(월드 경계 절반 크기) 상수가 있었고, "가장자리
        // 근처면 중앙으로 조향"하는 드로짐 로직에 쓰였다. 서버가 화면 경계에서 clamp(벽에
        // 막힘) 대신 wrap(반대편에서 재등장)으로 바뀌면서, 벽에 박혀 낭비되는 이동을 피하려던
        // 그 드로짐 자체의 존재 이유가 사라졌다(wrap은 박힐 위험이 없다) — 그래서 로직과
        // 상수를 함께 제거했다. 참고로 지금 서버는 WORLD_HALF_EXTENT_X(7.1111...)/
        // WORLD_HALF_EXTENT_Y(4.0)로 X/Y 경계 크기가 다르다(16:9 고정 해상도 적용).
        // 봇은 이동을 직접 계산하지 않고 서버가 보내주는 ServerState를 그대로 신뢰하므로,
        // 이 값이 봇 코드 어디에도 필요하지 않다.
        private const float TickIntervalSeconds = 1f / 60f; // 서버 TICK_RATE(60Hz)에 맞춘 입력 전송 주기
        private const float PingIntervalSeconds = 1.0f;      // 실제 클라이언트의 PING_INTERVAL과 동일.
                                                               // 서버는 Ping을 받아야만 LastPingTime을 갱신하므로,
                                                               // 이걸 안 보내면 TIMEOUT_SECONDS(3초) 후 강제 제거된다.
        private const float JoinTimeoutSeconds = 5.0f;

        private enum DriveState { Forward, Reverse, Turn, Idle }

        private sealed class SelfSnapshot
        {
            public readonly float X;
            public readonly float Y;
            public readonly float RotationDeg;
            public readonly bool IsDead;

            public SelfSnapshot(float x, float y, float rotationDeg, bool isDead)
            {
                X = x;
                Y = y;
                RotationDeg = rotationDeg;
                IsDead = isDead;
            }
        }

        private readonly IPEndPoint _serverEndPoint;
        private readonly LoadTestStats _stats;
        private readonly Random _random = new();

        private UdpClient _udp;
        private volatile int _playerId = -1; // -1 = 아직 JoinResponse를 못 받음
        private int _clientTick;
        private TaskCompletionSource<byte> _joinTcs;

        // 이동/발사 행동 상태 (봇 자신의 루프에서만 읽고 쓰므로 동기화 불필요)
        private DriveState _driveState = DriveState.Forward;
        private float _driveStateTimeLeft;
        private float _turnBias;
        private bool _isFiring;
        private float _fireStateTimeLeft;
        private float _pingTimer;

        // 수신 루프가 쓰고 행동 루프가 읽는 값. 개별 필드를 따로 volatile로 두는 대신
        // 불변 스냅샷을 통째로 교체해서, X/Y/회전/사망여부가 항상 같은 시점의 값으로
        // 함께 관찰되도록 한다.
        private volatile SelfSnapshot _selfSnapshot;

        public bool JoinSucceeded { get; private set; }

        public BotClient(IPEndPoint serverEndPoint, LoadTestStats stats)
        {
            _serverEndPoint = serverEndPoint;
            _stats = stats;
        }

        public async Task RunAsync(CancellationToken ct)
        {
            _joinTcs = new TaskCompletionSource<byte>(TaskCreationOptions.RunContinuationsAsynchronously);

            using var udp = new UdpClient();
            _udp = udp;
            _udp.Connect(_serverEndPoint);

            Task receiveTask = ReceiveLoopAsync(ct);

            try
            {
                SendPacket(Protocol.BuildJoinRequest());

                byte assignedId;
                using (var joinCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    joinCts.CancelAfter(TimeSpan.FromSeconds(JoinTimeoutSeconds));
                    try
                    {
                        assignedId = await _joinTcs.Task.WaitAsync(joinCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // 전체 테스트가 취소된 게 아니라 이 봇만 접속 확인에 실패한 경우
                        // (JoinResponse 유실 또는 서버 무응답).
                        _stats.RecordJoinFailed();
                        return;
                    }
                }

                _playerId = assignedId;
                JoinSucceeded = true;
                _stats.RecordJoined();

                PickNewDriveState();
                PickNewFireState();

                await MovementLoopAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 정상 종료 경로 (테스트 시간 만료 또는 Ctrl+C).
            }
            finally
            {
                try { _udp.Close(); } catch { /* 이미 닫힌 소켓은 무시 */ }
                try { await receiveTask.ConfigureAwait(false); } catch { /* 종료 경로의 예외는 무시 */ }
            }
        }

        private async Task MovementLoopAsync(CancellationToken ct)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(TickIntervalSeconds));
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                Tick(TickIntervalSeconds);
            }
        }

        private void Tick(float dt)
        {
            var (throttle, turn, fire) = UpdateBehavior(dt);

            _clientTick++;
            SendPacket(Protocol.BuildClientInput((byte)_playerId, _clientTick, throttle, turn, fire));

            _pingTimer += dt;
            if (_pingTimer >= PingIntervalSeconds)
            {
                _pingTimer = 0f;
                SendPacket(Protocol.BuildPing((byte)_playerId, DateTime.UtcNow.Ticks));
            }
        }

        private (float throttle, float turn, bool fire) UpdateBehavior(float dt)
        {
            _driveStateTimeLeft -= dt;
            if (_driveStateTimeLeft <= 0f) PickNewDriveState();

            _fireStateTimeLeft -= dt;
            if (_fireStateTimeLeft <= 0f) PickNewFireState();

            float throttle = _driveState switch
            {
                DriveState.Forward => 1f,
                DriveState.Reverse => -1f,
                _ => 0f
            };

            float turn = _driveState == DriveState.Turn ? _turnBias : _turnBias * 0.4f;

            var self = _selfSnapshot;
            bool isDead = false;

            if (self != null)
            {
                isDead = self.IsDead;
            }

            bool fire = _isFiring;

            if (isDead)
            {
                // 서버는 사망한 플레이어의 입력을 전부 무시하지만, 실제 클라이언트와 동일하게
                // 리스폰 대기 중에도 패킷 자체는 계속 보낸다 (0 입력으로).
                throttle = 0f;
                turn = 0f;
                fire = false;
            }

            return (throttle, turn, fire);
        }

        private void PickNewDriveState()
        {
            double roll = _random.NextDouble();
            _driveState = roll switch
            {
                < 0.55 => DriveState.Forward,
                < 0.70 => DriveState.Turn,
                < 0.85 => DriveState.Idle,
                _ => DriveState.Reverse
            };

            _driveStateTimeLeft = _driveState switch
            {
                DriveState.Forward => RandomRange(1.5f, 4.5f),
                DriveState.Turn => RandomRange(0.4f, 1.5f),
                DriveState.Idle => RandomRange(0.5f, 2.0f),
                DriveState.Reverse => RandomRange(0.5f, 1.5f),
                _ => 1f
            };

            // 상태를 바꿀 때마다 새로운 회전 성향을 다시 뽑아 "랜덤 방향으로 전진"을 만든다.
            _turnBias = RandomRange(-1f, 1f);
        }

        private void PickNewFireState()
        {
            _isFiring = !_isFiring;
            _fireStateTimeLeft = _isFiring
                ? RandomRange(0.4f, 2.5f)   // 발사 지속(스페이스바를 누르고 있는 구간)
                : RandomRange(0.5f, 3.0f);  // 다음 발사까지 쉬는 시간
        }

        private float RandomRange(float min, float max) => min + (float)_random.NextDouble() * (max - min);

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try
                {
                    result = await _udp.ReceiveAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException)
                {
                    // 일시적 수신 오류. 재접속을 시도하지 않고 다음 패킷을 계속 기다린다.
                    continue;
                }

                _stats.RecordReceived(result.Buffer.Length);
                ProcessPacket(result.Buffer);
            }
        }

        private void ProcessPacket(byte[] data)
        {
            if (data.Length == 0) return;

            switch (Protocol.PeekType(data))
            {
                case PacketType.JoinResponse:
                    if (Protocol.TryReadJoinResponse(data, out byte assignedId))
                    {
                        _joinTcs.TrySetResult(assignedId);
                    }
                    break;

                case PacketType.ServerState:
                    int myId = _playerId;
                    if (myId >= 0 && Protocol.TryReadServerStateForPlayer(data, (byte)myId, out var snap))
                    {
                        _selfSnapshot = new SelfSnapshot(snap.X, snap.Y, snap.Rotation, snap.IsDead);
                    }
                    break;

                case PacketType.Pong:
                    _stats.RecordPongReceived();
                    break;
            }
        }

        private void SendPacket(byte[] bytes)
        {
            try
            {
                _udp.Send(bytes, bytes.Length);
                _stats.RecordSent(bytes.Length);
            }
            catch (ObjectDisposedException)
            {
                // 종료 과정에서 소켓이 이미 닫힌 경우.
            }
            catch (SocketException)
            {
                // 일시적 전송 오류. 재접속을 시도하지 않으므로 다음 틱에서 다시 시도될 뿐이다.
            }
        }
    }
}
