using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Game.Networking;

namespace LoadTestBot
{
    /// <summary>
    /// 서버에 접속해 하나의 플레이어처럼 행동하는 봇 하나.
    ///
    /// 이동은 전진 위주에 가끔 정지/후진/제자리 회전을 섞은 복합 패턴이며, 발사는 스페이스바를
    /// 누르고 있는 것처럼 구간(burst) 단위로 on/off를 반복한다. 참가에 성공해
    /// MovementLoopAsync에 진입한 뒤로는 재접속을 시도하지 않는다 — 연결이 끊기면 봇은 그대로
    /// 종료된다. 예외적으로 참가 단계에서만, GameAlreadyStarted 거부에 대해 아주 좁은 범위의
    /// 1회 재시도를 한다(TryJoinOnceAsync 참고) — 이는 끊어진 연결의 재접속이 아니라, 성립한
    /// 적 없는 참가 시도가 서버 쪽 레이스로 억울하게 거부됐을 가능성을 한 번 더 확인하는
    /// 것이다.
    ///
    /// 서버는 위치/회전/사망여부 등 물리 상태를 전혀 보내지 않는다 — 서버가 아는 것은 순수
    /// 입력 우편함과 참가/퇴장뿐이다. 물리는 DOTS 클라이언트가 각자 결정론적으로 계산하므로,
    /// 이 봇은 자신의 사망 여부를 스스로 판정하지 않는다: 실제로 죽어 있어도 신경 쓰지 않고
    /// 자신의 행동 패턴대로 입력을 계속 보내며, 그 틱에 죽어 있다면 각 DOTS 클라이언트의
    /// SimulationTickSystem이 그 입력을 무시한다. TickCommit을 받으면 Tick 번호만
    /// 추출해 LastConfirmedTick을 갱신하며(TargetTick 계산용), Join/Leave/Input 목록은 파싱만
    /// 하고 별도로 처리하지 않는다.
    /// </summary>
    public class BotClient
    {
        // 서버 목표 틱레이트에 맞춰두면 트래픽 패턴이 실제 클라이언트와 비슷해져 부하 테스트
        // 의미가 커진다. 이 dt는 (1) MovementLoopAsync의 PeriodicTimer 간격, (2)
        // UpdateBehavior의 행동 상태 타이머 감산에 쓰인다 — 봇 자신의 AI 타이밍일 뿐
        // 결정론 대상이 아니므로 다른 클라이언트와 값이 일치할 필요는 없다. 서버가 방송하는
        // 실측 dt(TickCommit.DeltaTimeSeconds)는 봇이 물리를 계산하지 않으므로 사용하지 않는다.
        private const float TickIntervalSeconds = 1f / 60f;
        private const float PingIntervalSeconds = 1.0f;      // 실제 클라이언트의 PING_INTERVAL과 동일.
                                                               // 서버는 Ping을 받아야만 LastPingTime을 갱신하므로,
                                                               // 이걸 안 보내면 TIMEOUT_SECONDS(3초) 후 강제 제거된다.
        private const float JoinTimeoutSeconds = 5.0f;

        // GameAlreadyStarted 거부에 대한 1회 레이스 구제 재시도 전 대기 시간. 서버 목표 틱
        // 간격(1000/60 ≈ 16.7ms)의 몇 배로 잡아, 호스트의 StartGameRequest 처리 틱과 두 번째
        // JoinRequest 도착 시점 사이에 최소 한두 틱의 여유를 둔다. 게임이 실제로 이미 진행
        // 중인 경우라면 얼마를 기다리든 재시도는 다시 거부되므로 길게 잡을 필요는 없다.
        private const float JoinRaceRetryDelaySeconds = 0.05f;

        // Input delay: DOTS 클라이언트(SimulationConfigAuthoring.InputDelayTicks)와 같은
        // 값으로 맞춘다. 값이 다르면 봇의 입력 도착 타이밍이 클라이언트와 어긋나
        // fallback(마지막 입력 재사용)이 자주 발동할 수 있으므로, 실제 트래픽 패턴을 흉내
        // 내려면 일치시키는 편이 낫다.
        private const int InputDelayTicks = 2;

        private enum DriveState { Forward, Reverse, Turn, Idle }

        /// <summary>
        /// TryJoinOnceAsync 한 번의 결과를 세 가지로 구분한다. RunAsync는 결과에 따라 재시도
        /// 여부를 다르게 결정한다 — GameAlreadyStarted만 재시도하고 TimedOut은 재시도하지
        /// 않는다.
        /// </summary>
        private enum JoinOutcome { Success, TimedOut, RejectedGameAlreadyStarted }

        private readonly struct JoinAttemptResult
        {
            public readonly JoinOutcome Outcome;
            public readonly byte AssignedPlayerId;

            public JoinAttemptResult(JoinOutcome outcome, byte assignedPlayerId = 0)
            {
                Outcome = outcome;
                AssignedPlayerId = assignedPlayerId;
            }
        }

        private readonly IPEndPoint _serverEndPoint;
        private readonly LoadTestStats _stats;
        private readonly Random _random = new();

        private UdpClient _udp;
        private volatile int _playerId = -1; // -1 = 아직 JoinResponse를 못 받음
        private TaskCompletionSource<byte> _joinTcs;

        // 서버가 확정한 가장 최근 틱 번호. TickCommit이 도착할 때마다 수신 루프가 갱신한다.
        // DOTS 클라이언트의 ClientStateSingleton.LastConfirmedTick과 동일한 역할이며, 초기값도
        // 동일하게 -1(아직 확정된 틱 없음)이다. TargetTick 계산에만 쓰이고 물리에는 전혀
        // 관여하지 않는다 — volatile인 이유는 수신 스레드가 쓰고 행동 루프(다른 스레드)가
        // 읽기 때문이다.
        private volatile int _lastConfirmedTick = -1;

        // 이동/발사 행동 상태 (봇 자신의 루프에서만 읽고 쓰므로 동기화 불필요)
        private DriveState _driveState = DriveState.Forward;
        private float _driveStateTimeLeft;
        private float _turnBias;
        private bool _isFiring;
        private float _fireStateTimeLeft;
        private float _pingTimer;

        // 같은 TargetTick에 대해 중복 전송하지 않기 위한 기억값. DOTS 클라이언트의
        // InputCaptureSystem._lastSentTargetTick과 동일한 목적.
        private int _lastSentTargetTick = int.MinValue;

        public bool JoinSucceeded { get; private set; }

        public BotClient(IPEndPoint serverEndPoint, LoadTestStats stats)
        {
            _serverEndPoint = serverEndPoint;
            _stats = stats;
        }

        public async Task RunAsync(CancellationToken ct)
        {
            // _joinTcs는 TryJoinOnceAsync 호출마다 새로 생성된다 — GameAlreadyStarted 레이스
            // 재시도가 두 번째 JoinRequest를 보낼 때 새 TaskCompletionSource가 필요하기
            // 때문이다(TryJoinOnceAsync 참고).
            using var udp = new UdpClient();
            _udp = udp;

            // Windows에서 UDP 소켓이 ICMP Port Unreachable을 받으면 다음 Send()/Receive()
            // 호출에서 SocketException(WSAECONNRESET)이 발생할 수 있다(서버, DOTS
            // 클라이언트와 동일한 조치). 다수의 소켓이 짧은 시간에 접속/해제를 반복하는 부하
            // 테스트 봇이 이 노이즈를 가장 자주 겪는다.
            try
            {
                const int SIO_UDP_CONNRESET = -1744830452;
                udp.Client.IOControl((IOControlCode)SIO_UDP_CONNRESET, new byte[] { 0 }, null);
            }
            catch { }

            _udp.Connect(_serverEndPoint);

            Task receiveTask = ReceiveLoopAsync(ct);

            try
            {
                byte assignedId;
                JoinAttemptResult firstAttempt = await TryJoinOnceAsync(ct).ConfigureAwait(false);

                if (firstAttempt.Outcome == JoinOutcome.RejectedGameAlreadyStarted)
                {
                    // 이 거부가 게임이 한참 전에 시작된 것인지, 호스트의 StartGameRequest
                    // 반영 시점에 이 봇의 JoinRequest가 우연히 겹쳐 억울하게 거부된 것인지는
                    // 구분할 수 없다. 서버 틱 1~2개 분량만큼 짧게 대기한 뒤 한 번만
                    // 재시도한다 — 진짜로 게임이 시작된 상태라면 재시도도 동일하게
                    // 거부되므로 무한정 매달리지 않는다. 재시도 발생 자체를
                    // RecordJoinRaceRetried로 집계한다.
                    _stats.RecordJoinRaceRetried();
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(JoinRaceRetryDelaySeconds), ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        _stats.RecordJoinFailed();
                        return;
                    }

                    firstAttempt = await TryJoinOnceAsync(ct).ConfigureAwait(false);
                }

                switch (firstAttempt.Outcome)
                {
                    case JoinOutcome.Success:
                        assignedId = firstAttempt.AssignedPlayerId;
                        break;

                    case JoinOutcome.TimedOut:
                        // JoinResponse 유실 또는 서버 무응답. 응답 자체가 오지 않았으므로
                        // 재시도해도 같은 결과를 반복할 가능성이 높아 즉시 실패 처리한다.
                        _stats.RecordJoinFailed();
                        return;

                    case JoinOutcome.RejectedGameAlreadyStarted:
                        // 재시도까지 거쳤는데도 거부됐다 — 게임이 실제로 이미 진행 중인
                        // 것으로 확정하고 더 이상 재시도하지 않는다.
                        _stats.RecordJoinFailed();
                        return;

                    default:
                        // JoinOutcome의 세 값을 전부 위에서 처리했으므로 도달하지 않는다.
                        // assignedId가 모든 코드 경로에서 할당됨을 컴파일러가 확인할 수
                        // 있도록 남겨둔다.
                        _stats.RecordJoinFailed();
                        return;
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

        /// <summary>
        /// JoinRequest를 한 번 보내고 JoinResponse(또는 타임아웃)를 기다린다. RunAsync가 최초
        /// 1회, 그리고 GameAlreadyStarted 거부 시 레이스 구제용으로 1회, 최대 2번 호출한다.
        ///
        /// 매 호출마다 _joinTcs를 새로 할당한다 — 이전 호출에서 예외로 완료된
        /// TaskCompletionSource를 재사용하면 새 await가 즉시 그 예외를 다시 던져 새 응답을
        /// 기다릴 수 없기 때문이다. ReceiveLoopAsync는 별도로 같은 소켓을 계속 수신하므로,
        /// 두 번째 JoinRequest에 대한 JoinResponse는 새 _joinTcs로 정상 전달된다.
        /// </summary>
        private async Task<JoinAttemptResult> TryJoinOnceAsync(CancellationToken ct)
        {
            _joinTcs = new TaskCompletionSource<byte>(TaskCreationOptions.RunContinuationsAsynchronously);

            SendPacket(Protocol.BuildJoinRequest());

            using var joinCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            joinCts.CancelAfter(TimeSpan.FromSeconds(JoinTimeoutSeconds));
            try
            {
                byte assignedId = await _joinTcs.Task.WaitAsync(joinCts.Token).ConfigureAwait(false);
                return new JoinAttemptResult(JoinOutcome.Success, assignedId);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 전체 테스트가 취소된 게 아니라 이 시도만 접속 확인에 실패한 경우
                // (JoinResponse 유실 또는 서버 무응답).
                return new JoinAttemptResult(JoinOutcome.TimedOut);
            }
            catch (InvalidOperationException)
            {
                // 서버가 GameAlreadyStarted=true로 거부한 경우
                // (ProcessPacket에서 _joinTcs.TrySetException으로 던진 예외).
                return new JoinAttemptResult(JoinOutcome.RejectedGameAlreadyStarted);
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

            // TargetTick 계산: DOTS 클라이언트의 InputCaptureSystem과 동일한 공식. 서버가
            // 다음으로 확정할 틱(_lastConfirmedTick + 1)에 InputDelayTicks를 더한다.
            int targetTick = _lastConfirmedTick + 1 + InputDelayTicks;

            // 같은 TargetTick에 대해서는 재전송하지 않는다 — 이 가드가 없으면 서버가 아직
            // 그 틱을 확정하지 못한 사이 같은 입력을 여러 번 보내게 된다(기능상 무해하지만
            // 불필요한 트래픽).
            if (targetTick != _lastSentTargetTick)
            {
                SendPacket(Protocol.BuildClientInput((byte)_playerId, targetTick, throttle, turn, fire));
                _lastSentTargetTick = targetTick;
            }

            _pingTimer += dt;
            if (_pingTimer >= PingIntervalSeconds)
            {
                _pingTimer = 0f;
                SendPacket(Protocol.BuildPing((byte)_playerId, DateTime.UtcNow.Ticks));
            }
        }

        /// <summary>
        /// 봇은 물리를 계산하지 않으므로 자신이 죽었는지 알 방법이 없다 — 항상 자신의 행동
        /// 패턴대로 입력을 만든다. 실제로 죽어 있는 틱이라면 각 DOTS 클라이언트가 그 입력을 무시한다.
        /// </summary>
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

            bool fire = _isFiring;

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
                    // SIO_UDP_CONNRESET 억제가 우회되어 SocketException이 연달아 발생하는
                    // 경우에 대비해 짧게 양보한다. Thread.Sleep 대신 Task.Delay를 쓴다 —
                    // 이 메서드는 스레드 풀 스레드에서 비동기로 돌므로, 여러 봇이 동시에
                    // 이 경로를 타면 Thread.Sleep이 다른 봇들의 처리까지 지연시킬 수 있다.
                    try
                    {
                        await Task.Delay(10, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    continue;
                }

                _stats.RecordReceived(result.Buffer.Length);
                ProcessPacket(result.Buffer);
            }
        }

        /// <summary>
        /// TickCommit 케이스는 Tick 번호만 추출해 _lastConfirmedTick을 갱신한다.
        /// Protocol.TryReadTickCommit이 전체 구조를 파싱하지만, Tick 필드 외에는 사용하지 않는다.
        /// </summary>
        private void ProcessPacket(byte[] data)
        {
            if (data.Length == 0) return;

            switch (Protocol.PeekType(data))
            {
                case PacketType.JoinResponse:
                    // 봇은 물리를 계산하지 않으므로 ExistingPlayers는 무시하고
                    // AssignedPlayerId만 사용한다.
                    //
                    // GameAlreadyStarted가 true면 거부 응답이다(AssignedPlayerId는 의미
                    // 없는 sentinel 0) — _joinTcs를 성공(0)으로 착각해 완료시키면 이후
                    // 모든 패킷이 PlayerId 0으로 나가므로, 반드시 실패 경로(예외)로
                    // 완료시켜 RunAsync가 접속 실패로 집계하고 종료하게 한다.
                    //
                    // TryJoinOnceAsync가 레이스 구제 재시도로 _joinTcs를 새
                    // TaskCompletionSource로 교체할 수 있다. 이 case는 그 시점의
                    // _joinTcs를 그대로 완료시킨다 — 재시도가 최대 1회로 제한되어 있어,
                    // 옛 응답이 새 시도의 슬롯을 오염시킬 확률은 무시할 수 있는 수준이다.
                    if (Protocol.TryReadJoinResponse(data, out JoinResponseData joinData))
                    {
                        if (joinData.GameAlreadyStarted)
                        {
                            _joinTcs.TrySetException(
                                new InvalidOperationException("Join rejected: game already started."));
                        }
                        else
                        {
                            _joinTcs.TrySetResult(joinData.AssignedPlayerId);
                        }
                    }
                    break;

                case PacketType.TickCommit:
                    if (Protocol.TryReadTickCommit(data, out TickCommitData commit))
                    {
                        _lastConfirmedTick = commit.Tick;
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
