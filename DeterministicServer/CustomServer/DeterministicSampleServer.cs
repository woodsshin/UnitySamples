using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Game.Networking;

namespace NetworkServer
{
    /// <summary>
    /// 입력 릴레이 전용 UDP 서버. 물리(위치/회전/체력/사망/미사일/스코어)는 전혀 계산하지 않는다.
    ///
    /// 서버의 책임은 두 가지뿐이다:
    ///   1) 각 플레이어의 (TargetTick, Throttle, Turn, Fire) 입력을 목표 틱별로 수집한다.
    ///   2) 매 틱마다 그 틱에 참가 중인 모든 플레이어의 입력을 모아 TickCommit으로 방송한다.
    ///      아직 도착하지 않은 입력은 해당 플레이어의 마지막 확정 입력으로 대체한다(fallback).
    ///      이는 한 플레이어의 입력 지연이 전체 틱 진행을 막지 않도록 하기 위함이다.
    ///
    /// 이동, 미사일 이동/충돌/사거리 판정, 체력/사망/리스폰, 스폰 위치 계산, 좌표 wrap 등 모든
    /// 물리 계산은 클라이언트의 결정론적 시뮬레이션 System이 전담한다. 모든 클라이언트는 서버가
    /// 보내는 동일한 입력 시퀀스를 동일한 순서로 재생해 각자 독립적으로 물리를 계산한다.
    ///
    /// Join/Leave 이벤트는 도착 즉시가 아니라 항상 다음 틱에 방송 예약된다 — 이번 틱의 입력
    /// 수집이 진행 중인 상태에서 참가자 집합이 바뀌는 레이스를 방지하기 위함이며, 모든
    /// 클라이언트가 동일한 틱에서 동일한 Join/Leave를 관찰해야 스폰 슬롯 계산 등 "현재 접속자
    /// 집합"에 의존하는 로직이 전원 동일한 결과를 낸다.
    ///
    /// PacketType 및 와이어 직렬화 규칙은 공유 Protocol.cs(Game.Networking 네임스페이스)에
    /// 정의되어 있다. 이 파일은 그 파일의 물리적 복사본이며, 프로토콜 변경 시 서버/DOTS
    /// 클라이언트/봇 세 프로젝트의 Protocol.cs를 모두 동일하게 갱신해야 한다.
    ///
    /// 게임이 이미 시작된 뒤의 신규 접속은 거부된다(JoinResponse.GameAlreadyStarted=true). 이
    /// 경우 서버 상태는 전혀 변경되지 않으며, 거부된 클라이언트는 재시도하지 않고 접속을
    /// 정리해야 한다.
    ///
    /// 실제 플레이어 전원이 접속을 끊어 _players가 완전히 비면, 다음 게임을 처음부터 다시 시작할
    /// 수 있도록 _isGameStarted와 PlayerId/JoinSequence 카운터를 초기 상태로 되돌린다.
    /// </summary>
    public class DeterministicSampleServer
    {
        /// <summary>서버가 플레이어 하나에 대해 유지하는 순수 네트워크 상태. 위치/회전/체력 등
        /// 물리 필드는 포함하지 않는다.</summary>
        public class PlayerConnection
        {
            public byte Id;
            public IPEndPoint EndPoint;
            public DateTime LastPingTime = DateTime.UtcNow;

            // 이 플레이어의 전역 참가 순서. PlayerId(byte, 재접속 시 재발급되며 오름차순 보장
            // 없음)와 별개로 관리한다. ComputeSpawnTransform은 참가 순서대로 슬롯을 채워야
            // 모든 클라이언트가 동일한 결과를 재현하므로, 순서를 명시적으로 보장하는 이
            // 카운터가 필요하다.
            public long JoinSequence;

            // fallback 캐시: 마지막으로 확정에 사용한 입력. 아직 한 번도 확정한 적 없으면
            // 중립값(0,0,false).
            public float LastThrottle;
            public float LastTurn;
            public bool LastFire;

            // 마지막으로 확정에 사용된 입력의 목표 틱. 디버깅/로그 용도이며 물리에는 관여하지
            // 않는다.
            public int LastAppliedTick = -1;
        }

        /// <summary>
        /// 플레이어별 미확정 목표 틱 입력 우편함. (PlayerId, TargetTick) 쌍을 키로 사용한다.
        /// 틱 T가 확정되면 TargetTick &lt;= T인 항목은 더 이상 필요 없으므로 정리한다.
        /// </summary>
        private readonly ConcurrentDictionary<(byte PlayerId, int TargetTick), (float Throttle, float Turn, bool Fire)> _pendingInputs = new();

        private readonly ConcurrentDictionary<byte, PlayerConnection> _players = new();

        // Join 요청 도착 순서를 보존하는 큐. 수신 스레드가 Enqueue하고, ServerLoop 전용
        // 스레드가 매 틱 시작 시 전부 Dequeue해 PlayerId를 배정한 뒤 다음 틱 JoinEvent
        // 예약 목록으로 옮긴다.
        private readonly ConcurrentQueue<IPEndPoint> _pendingJoinRequests = new();

        // 호스트의 StartGameRequest 접수 순서를 보존하는 큐. 수신 스레드는 파싱만 해
        // Enqueue하고, ServerLoop 전용 스레드가 매 틱 시작 시 Dequeue해 발신자가 현재
        // 호스트인지, 게임이 아직 미시작 상태인지 검증한다. 판단 로직은 전부 ServerLoop
        // 스레드에서 수행한다(수신 스레드는 파싱만 담당).
        private readonly ConcurrentQueue<byte> _pendingStartGameRequests = new();

        // 이번 틱에 실어 보낼 Join/Leave 예약분. ServerLoop 스레드에서만 접근하므로 잠금이
        // 필요 없다. Join 목록은 PlayerId와 JoinSequence를 함께 담아, 클라이언트가 정확한
        // 참가 순서로 ComputeSpawnTransform을 재현할 수 있게 한다.
        private readonly List<JoinedPlayerInfo> _scheduledJoins = new();
        private readonly List<byte> _scheduledLeaves = new();
        // 퇴장 확정 시점의 EndPoint 스냅샷. _players에서 제거된 뒤에도 본인에게 LeaveEvent를
        // 전달해야 하므로 별도로 보관한다.
        private readonly List<IPEndPoint> _scheduledLeaveEndPoints = new();

        // 참가 거부 응답(GameAlreadyStarted=true)에 실어 보내는 빈 ExistingPlayers 목록.
        // 수신 측이 사용하지 않는 필드이므로 매번 새로 할당하지 않고 재사용한다.
        private static readonly List<JoinedPlayerInfo> EmptyExistingPlayers = new();

        private UdpClient _udpServer;
        private byte _nextPlayerId = 1;
        // JoinSequence 발급용 전역 카운터. long이라 byte(PlayerId)와 달리 wraparound 걱정이
        // 없다. 모든 플레이어가 나가 _players가 비면 다음 게임을 위해 0으로 초기화된다
        // (ServerLoop 참고).
        private long _nextJoinSequence = 0;

        private bool _isRunning = false;

        // 게임 시작 여부. ServerLoop 스레드만 읽고 쓴다(ReceiveLoop는 이 필드를 직접 건드리지
        // 않고 _pendingStartGameRequests에 넣기만 한다). 실제 플레이어가 모두 나가면 다음
        // 게임을 위해 false로 초기화된다(ServerLoop 참고).
        private bool _isGameStarted = false;

        private const int PORT = 9050;
        private const float TICK_RATE = 60f; // 서버 루프의 목표 주기(Thread.Sleep 간격)일 뿐이다.
                                              // 클라이언트는 이 값을 알 필요가 없다 — 물리 dt는
                                              // 서버가 실측해 TickCommit.DeltaTimeSeconds로
                                              // 방송하는 값을 그대로 쓰므로, 실제 틱레이트가
                                              // 목표치와 어긋나도 결정론에 영향을 주지 않는다.
        private const float TIMEOUT_SECONDS = 3.0f;

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
            Log($"[Server] UDP Server started on port {PORT}... (input-relay only, no physics)");

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
                    PacketType type = Protocol.PeekType(data);

                    switch (type)
                    {
                        case PacketType.JoinRequest:
                            // PlayerId 배정은 여기서 하지 않는다. ServerLoop이 틱 경계에서
                            // 순서대로 처리해야, 입력 확정 도중 새 플레이어가 끼어드는 레이스를
                            // 방지할 수 있다.
                            _pendingJoinRequests.Enqueue(remoteEP);
                            break;

                        case PacketType.ClientInput:
                            HandleClientInput(data);
                            break;

                        case PacketType.StartGameRequest:
                            // Join과 동일한 이유로 여기서 검증하지 않는다. 발신자가 현재
                            // 호스트인지 확인하려면 ServerLoop의 최신 _players 스냅샷이
                            // 필요하며, 판정은 틱 경계에서 한 번만 일어나야 같은 틱에 동시
                            // 도착한 여러 요청이 뒤섞이지 않는다.
                            if (Protocol.TryReadStartGameRequest(data, out byte requesterId))
                            {
                                _pendingStartGameRequests.Enqueue(requesterId);
                            }
                            break;

                        case PacketType.Ping:
                        {
                            using var ms = new MemoryStream(data);
                            using var br = new BinaryReader(ms);
                            br.ReadByte(); // PacketType (이미 PeekType으로 확인함)
                            byte playerId = br.ReadByte();
                            long clientTicks = br.ReadInt64();
                            SendPongResponse(remoteEP, playerId, clientTicks);
                            break;
                        }
                    }
                }
                catch (SocketException)
                {
                    if (!_isRunning) break;
                    // SocketException 발생 시 remoteEP는 직전 정상 패킷 주소를 가리키므로
                    // 여기서 타임아웃 처리를 하면 안 된다. 타임아웃은 CheckTimeouts()에 맡긴다.
                }
                catch (Exception ex)
                {
                    if (!_isRunning) break;
                    Log($"[Server Error] {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 순수 입력 우편함에 (PlayerId, TargetTick) 키로 값을 적재한다. 물리 판단은 전혀 하지
        /// 않으며, 같은 키로 다시 도착하면(재전송 등) 마지막 값으로 덮어쓴다.
        /// </summary>
        private void HandleClientInput(byte[] data)
        {
            if (!Protocol.TryReadClientInput(data, out byte playerId, out int targetTick,
                    out float throttle, out float turn, out bool fire))
            {
                return;
            }

            // 아직 JoinEvent로 방송되지 않은 플레이어의 입력도 우편함에 적재해둔다 — 참가가
            // 확정되는 즉시 사용할 수 있도록 한다.
            _pendingInputs[(playerId, targetTick)] = (throttle, turn, fire);
        }

        private void ServerLoop()
        {
            int tick = 0;
            int intervalMs = (int)(1000f / TICK_RATE);

            // 실측 dt 계산용. 이전 틱 확정부터 이번 틱 확정까지의 실제 경과 시간을 측정해
            // TickCommit에 실어 보낸다. 클라이언트는 고정값(1/TICK_RATE) 대신 이 실측값을
            // 물리 dt로 사용한다. Stopwatch는 스레드 슬립/스케줄링 오차를 그대로 반영하는
            // 고해상도 타이머다.
            var tickStopwatch = System.Diagnostics.Stopwatch.StartNew();

            while (_isRunning)
            {
                try
                {
                    // 1) 대기 중인 JoinRequest를 접수 순서대로 처리해 PlayerId를 배정하고,
                    //    다음에 확정할 틱에 JoinEvent로 방송하도록 예약한다.
                    _scheduledJoins.Clear();
                    _scheduledLeaves.Clear();
                    _scheduledLeaveEndPoints.Clear();

                    // 이번 틱에 새로 접속하는 플레이어들에게 JoinResponse로 돌려줄 기존
                    // 플레이어 스냅샷. 이번 반복에서 아무도 추가되기 전 시점의 _players를
                    // 인스턴스화해야 할, 신규 접속자 자신이나 동시 접속한 다른 신규 플레이어가 중복으로
                    // 섞이지 않는다. 둘 다 결국 클라이언트의 SpawnPlayer에서 JoinSequence
                    // 오름차순으로 처리되므로 결과는 동일하다.
                    var existingPlayersSnapshot = _players.Values
                        .OrderBy(c => c.JoinSequence)
                        .Select(c => new JoinedPlayerInfo { PlayerId = c.Id, JoinSequence = c.JoinSequence })
                        .ToList();

                    while (_pendingJoinRequests.TryDequeue(out IPEndPoint clientEP))
                    {
                        if (_isGameStarted)
                        {
                            // 게임이 이미 시작된 뒤의 신규 접속은 거부한다. 서버 상태는
                            // 전혀 변경하지 않고, sentinel 값(0/0/빈 목록)과 함께
                            // GameAlreadyStarted=true만 응답한다.
                            SendJoinResponse(clientEP, assignedId: 0, assignedSequence: 0,
                                existingPlayers: EmptyExistingPlayers, gameAlreadyStarted: true);
                            Log($"[Server] {clientEP} join REJECTED: game already in progress.");
                            continue;
                        }

                        // 동일 EndPoint로 기존 접속이 남아있다면 정리 (재접속 시나리오).
                        RemoveClientByEndPoint(clientEP, out byte? removedId);
                        if (removedId.HasValue)
                        {
                            _scheduledLeaves.Add(removedId.Value);
                            _scheduledLeaveEndPoints.Add(clientEP);
                        }

                        byte assignedId = _nextPlayerId++;
                        long assignedSequence = _nextJoinSequence++;
                        var connection = new PlayerConnection
                        {
                            Id = assignedId,
                            JoinSequence = assignedSequence,
                            EndPoint = clientEP,
                            LastPingTime = DateTime.UtcNow
                        };
                        _players[assignedId] = connection;
                        _scheduledJoins.Add(new JoinedPlayerInfo { PlayerId = assignedId, JoinSequence = assignedSequence });

                        SendJoinResponse(clientEP, assignedId, assignedSequence, existingPlayersSnapshot, gameAlreadyStarted: false);
                        Log($"[Server] Player {assignedId} (seq={assignedSequence}) join scheduled for tick {tick} (Total: {_players.Count})");
                    }

                    // 2) 타임아웃된 플레이어도 이번 틱의 LeaveEvent 예약분에 합류시킨다.
                    CollectTimeouts(_scheduledLeaves, _scheduledLeaveEndPoints);

                    // 3) 이번에 퇴장 예약된 플레이어는 입력 수집 대상에서 제외한다.
                    foreach (byte leftId in _scheduledLeaves)
                    {
                        _players.TryRemove(leftId, out _);
                    }

                    // 3.5) 이번 틱의 Leave 반영으로 실제 플레이어가 전부 사라졌다면 게임 상태를
                    //      초기화한다: _isGameStarted, _nextPlayerId, _nextJoinSequence를 전부
                    //      되돌려 다음 게임이 PlayerId 1부터 새로 시작하도록 한다.
                    //
                    //      조건은 반드시 "_scheduledLeaves.Count > 0"(이번 틱에 실제로 누군가
                    //      나갔음)과 함께 확인한다 — 단순히 _players.IsEmpty만 보면, 리셋
                    //      이후 아무도 참가하지 않아 계속 비어 있는 매 틱마다 초기화가 반복
                    //      실행된다.
                    if (_players.IsEmpty && _scheduledLeaves.Count > 0)
                    {
                        _isGameStarted = false;
                        _nextPlayerId = 1;
                        _nextJoinSequence = 0;
                        Log($"[Server] All real players disconnected at tick {tick} — game state reset for the next session.");
                    }

                    // 3.6) 호스트의 StartGameRequest를 처리한다. Join(1)과 Leave(3) 반영
                    //      이후에 검증해야, 이번 틱의 최신 _players 스냅샷 기준으로 호스트
                    //      여부를 정확히 판정할 수 있다. 판정에 성공하면 이 틱의 TickCommit에
                    //      바로 GameStarted=true를 실어 보낸다.
                    //
                    //      3.5에서 이번 틱에 리셋이 일어났다면 _players가 비어 있어
                    //      GetCurrentHostId가 null을 반환하므로, 옛 호스트의 대기 요청은
                    //      자연스럽게 무시된다.
                    bool scheduledGameStartThisTick = false;
                    while (_pendingStartGameRequests.TryDequeue(out byte requesterId))
                    {
                        if (_isGameStarted) continue; // 이미 시작됨 — 이후 도착하는 요청은 전부 무시.

                        byte? currentHostId = GetCurrentHostId();
                        if (currentHostId.HasValue && currentHostId.Value == requesterId)
                        {
                            _isGameStarted = true;
                            scheduledGameStartThisTick = true;
                            Log($"[Server] Game started by host (Player {requesterId}) at tick {tick}.");
                        }
                        else
                        {
                            Log($"[Server] Ignored StartGameRequest from Player {requesterId} (not current host, or host not found).");
                        }
                    }

                    // 4) 참가 완료된 각 플레이어의 이번 틱 입력을 확정한다. PlayerId 오름차순으로
                    //    정렬해 모든 클라이언트가 동일한 순서로 InputCount 목록을 해석하게 한다.
                    var confirmedInputs = new List<(byte PlayerId, float Throttle, float Turn, bool Fire)>();
                    foreach (byte playerId in _players.Keys.OrderBy(id => id))
                    {
                        var connection = _players[playerId];
                        if (_pendingInputs.TryRemove((playerId, tick), out var input))
                        {
                            connection.LastThrottle = input.Throttle;
                            connection.LastTurn = input.Turn;
                            connection.LastFire = input.Fire;
                            connection.LastAppliedTick = tick;
                        }
                        // else: 아직 도착하지 않음 -> 마지막 확정값(LastThrottle/Turn/Fire)을
                        // 그대로 재사용.

                        confirmedInputs.Add((playerId, connection.LastThrottle, connection.LastTurn, connection.LastFire));
                    }

                    // 5) 이번 틱보다 오래된(더 이상 쓸 일 없는) 우편함 항목을 정리한다. 무한정
                    //    쌓이는 것을 방지.
                    PrunePendingInputsOlderThan(tick);

                    // dt는 이번 틱 확정 직전에 측정해 1~5단계(Join/Leave 접수, 입력 확정)의
                    // 처리 시간까지 반영한다. 첫 틱(tick == 0)은 비교할 이전 틱이 없으므로
                    // 목표 간격(1/TICK_RATE)을 그대로 사용한다.
                    float deltaTimeSeconds = tick == 0
                        ? (1f / TICK_RATE)
                        : (float)tickStopwatch.Elapsed.TotalSeconds;
                    tickStopwatch.Restart();

                    // 6) TickCommit 브로드캐스트. 현재 접속 중인 플레이어 전원 + 이번 틱 막
                    //    참가/퇴장한 플레이어에게 전달된다(BroadcastTickCommit 참고).
                    BroadcastTickCommit(tick, deltaTimeSeconds, scheduledGameStartThisTick,
                        _scheduledJoins, _scheduledLeaves, confirmedInputs, _scheduledLeaveEndPoints);

                    tick++;
                }
                catch (SocketException ex)
                {
                    // Windows에서는 Send()가 ICMP Port Unreachable을 유발하면 해당 소켓의
                    // 다음 Send()/Receive() 호출에서 SocketException(WSAECONNRESET)이 발생할
                    // 수 있다. SIO_UDP_CONNRESET로 억제하지만 플랫폼에 따라 무효화될 수
                    // 있으므로 방어가 필요하다. 처리하지 않으면 백그라운드 스레드의 미처리
                    // 예외로 프로세스 전체가 종료되므로, 로그만 남기고 다음 틱으로 진행한다.
                    if (!_isRunning) break;
                    Log($"[Server Error] Socket exception in ServerLoop: {ex.Message}");
                }
                catch (Exception ex)
                {
                    if (!_isRunning) break;
                    Log($"[Server Error] Unexpected exception in ServerLoop: {ex.Message}");
                }

                Thread.Sleep(intervalMs);
            }
        }

        /// <summary>TargetTick이 현재 확정 중인 tick보다 작은 우편함 항목을 제거한다.</summary>
        private void PrunePendingInputsOlderThan(int tick)
        {
            foreach (var key in _pendingInputs.Keys)
            {
                if (key.TargetTick < tick)
                {
                    _pendingInputs.TryRemove(key, out _);
                }
            }
        }

        private void CollectTimeouts(List<byte> scheduledLeaves, List<IPEndPoint> scheduledLeaveEndPoints)
        {
            var now = DateTime.UtcNow;
            foreach (var kvp in _players)
            {
                if ((now - kvp.Value.LastPingTime).TotalSeconds > TIMEOUT_SECONDS)
                {
                    scheduledLeaves.Add(kvp.Key);
                    scheduledLeaveEndPoints.Add(kvp.Value.EndPoint);
                    Log($"[Server] Player {kvp.Key} timed out, leave scheduled.");
                }
            }
        }

        private void RemoveClientByEndPoint(IPEndPoint ep, out byte? removedId)
        {
            removedId = null;
            foreach (var kvp in _players)
            {
                if (kvp.Value.EndPoint.Equals(ep))
                {
                    if (_players.TryRemove(kvp.Key, out _))
                    {
                        removedId = kvp.Key;
                        Log($"[Server] Player {kvp.Key} replaced/removed by endpoint match.");
                    }
                    break;
                }
            }
        }

        /// <summary>현재 접속 중인 플레이어 중 JoinSequence가 가장 낮은 플레이어의 PlayerId를
        /// 반환한다(현재 호스트). 별도 필드로 관리하지 않고 매번 계산하므로, 호스트가 퇴장하면
        /// 다음 최저 JoinSequence 보유자가 자동으로 승계된다. 접속자가 없으면 null.</summary>
        private byte? GetCurrentHostId()
        {
            PlayerConnection host = null;
            foreach (var kvp in _players)
            {
                if (host == null || kvp.Value.JoinSequence < host.JoinSequence)
                {
                    host = kvp.Value;
                }
            }
            return host?.Id;
        }

        private void SendJoinResponse(IPEndPoint clientEP, byte assignedId, long assignedSequence,
            List<JoinedPlayerInfo> existingPlayers, bool gameAlreadyStarted)
        {
            byte[] bytes = Protocol.BuildJoinResponse(assignedId, assignedSequence, existingPlayers, gameAlreadyStarted);
            // 이 Send() 하나의 실패가 같은 틱에 동시 접속한 다른 플레이어의 Join 처리까지
            // 막지 않도록 개별적으로 방어한다(BroadcastTickCommit의 수신자별 Send 방어와
            // 동일한 패턴).
            try
            {
                _udpServer.Send(bytes, bytes.Length, clientEP);
            }
            catch { }
        }

        private void SendPongResponse(IPEndPoint clientEP, byte playerId, long clientTicks)
        {
            if (_players.TryGetValue(playerId, out var connection))
            {
                connection.LastPingTime = DateTime.UtcNow;
            }

            byte[] bytes = Protocol.BuildPong(clientTicks);
            _udpServer.Send(bytes, bytes.Length, clientEP);
        }

        /// <summary>
        /// Protocol.BuildTickCommit으로 TickCommit을 직렬화해 방송한다. Join 목록은 JoinSequence
        /// 오름차순으로 정렬한다(PlayerId 오름차순은 실제 접속 순서를 보장하지 않으므로
        /// ComputeSpawnTransform 재현 기준으로 사용하지 않는다).
        ///
        /// 수신 대상은 현재 _players뿐 아니라 이번 틱에 막 퇴장한 플레이어도 포함한다 —
        /// 본인의 LeaveEvent를 받아야 서버가 자신을 퇴장 처리했음을 인지할 수 있다. 퇴장한
        /// 플레이어의 EndPoint는 이미 _players에서 제거되었으므로 별도로 스냅샷해 전달한다.
        /// </summary>
        private void BroadcastTickCommit(int tick, float deltaTimeSeconds, bool gameStarted,
            List<JoinedPlayerInfo> joins, List<byte> leaves,
            List<(byte PlayerId, float Throttle, float Turn, bool Fire)> inputs,
            List<IPEndPoint> leftEndPoints)
        {
            // 게임 시작 틱은 접속자가 없어 보이는 극단적 상황에서도 생략되면 안 되므로
            // 방어적으로 조건에 포함한다.
            if (_players.IsEmpty && joins.Count == 0 && leaves.Count == 0 && !gameStarted) return;

            // Join은 JoinSequence 오름차순, Leave/Input은 PlayerId 오름차순 — 서버가 보내는
            // 순서가 곧 모든 클라이언트가 지켜야 할 처리 순서다. inputs는 ServerLoop에서 이미
            // 정렬되어 들어온다.
            var sortedJoins = joins.OrderBy(j => j.JoinSequence).ToList();
            var sortedLeaves = leaves.OrderBy(id => id).ToList();
            var tickInputs = inputs
                .Select(i => new TickPlayerInput { PlayerId = i.PlayerId, Throttle = i.Throttle, Turn = i.Turn, Fire = i.Fire })
                .ToList();

            byte[] packet = Protocol.BuildTickCommit(tick, deltaTimeSeconds, gameStarted, sortedJoins, sortedLeaves, tickInputs);

            // 현재 접속 중인(참가 완료) 플레이어 전원 + 이번 틱 막 참가한 플레이어에게 전달.
            foreach (var kvp in _players)
            {
                try
                {
                    _udpServer.Send(packet, packet.Length, kvp.Value.EndPoint);
                }
                catch { }
            }

            // 이번 틱에 막 퇴장한 플레이어에게도 자신의 LeaveEvent가 담긴 이 패킷을 한 번 더 보낸다.
            foreach (var ep in leftEndPoints)
            {
                try
                {
                    _udpServer.Send(packet, packet.Length, ep);
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
            DeterministicSampleServer server = new DeterministicSampleServer();
            server.Start();

            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Press Enter to stop server...");
            Console.ReadLine();

            server.Stop();
        }
    }
}
