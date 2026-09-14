using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Game.Networking;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace DeterministicSample.Dots
{
    /// <summary>
    /// 서버 연결/수신 스레드를 관리하는 System.
    ///
    /// 서버가 보내는 패킷은 JoinResponse / TickCommit / Pong 세 종류이며, ReceiveLoop의
    /// switch가 이를 처리한다. MyPlayerId 배정은 순수 연결 메타데이터이므로
    /// ClientStateSingleton에 직접 기록하지만, EntityManager 접근이 메인 스레드 전용이라
    /// 별도의 스레드-안전 필드(PendingMyPlayerId)를 거쳐 메인 스레드 System이 소비한다.
    /// Join/Leave/Input 목록은 TickCommitQueue 하나로 전달된다(NetworkComponents.cs 참고).
    ///
    /// 게임이 이미 시작된 뒤의 접속 시도는 거부된다. 거부 응답을 받으면 이 System은
    /// (1) ClientStateSingleton.IsJoinRejected를 true로 래치하고
    /// SceneTransitionCountdownSeconds를 3초로 채운 뒤, (2) 소켓을 닫고, (3) 매 프레임
    /// 카운트다운을 감산하다가 0 이하가 되면 EntryScene으로 전환한다. GameHudBridge는 이
    /// 두 필드를 보고 "게임이 이미 시작되었습니다" 메시지와 남은 시간을 화면에 보여주기만
    /// 하면 된다.
    ///
    /// 서버 응답이 끊기면 재시도 없이 즉시 EntryScene으로 전환한다 — 판단 기준은
    /// TimeSinceLastServerMessageMs(서버로부터 실제로 무언가 수신했는지)뿐이다.
    ///
    /// UdpClient.Receive는 블로킹 호출이므로 별도 스레드(ReceiveLoop)를 사용한다. ECS
    /// System.OnUpdate는 항상 메인 스레드에서 실행되므로, 수신 스레드가 채워 넣는
    /// ConcurrentQueue를 거쳐야만 메인 스레드 System들이 안전하게 데이터를 읽을 수 있다.
    /// </summary>
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial class NetworkConnectionSystem : SystemBase
    {
        private Entity _networkEntity;

        // EntryScene 전환은 한 번만 트리거되어야 한다 — SceneManager.LoadScene 호출 후
        // 씬이 실제로 언로드되기까지 몇 프레임이 걸릴 수 있으므로, 그 사이에 OnUpdate가
        // 다시 실행되어 조건을 재확인하고 LoadScene을 중복 호출하는 것을 막는다.
        private bool _hasTriggeredSceneTransition;

        protected override void OnCreate()
        {
            RequireForUpdate<SimulationConfig>();
        }

        protected override void OnStartRunning()
        {
            // _hasTriggeredSceneTransition은 이 System 인스턴스 자체에 붙은 필드다. Unity
            // DOTS의 기본 World는 씬 전환(EntryScene <-> 게임 씬) 시에도 파괴되지 않으므로,
            // 이 System 인스턴스도 계속 같은 객체로 살아있다 — 한 번 true가 되면 그 값이
            // 남아 이후 재접속의 OnUpdate가 매번 조기 return될 수 있다. OnStartRunning은
            // 새로운 연결 세션이 시작되는 시점이므로 여기서 반드시 false로 되돌려야 한다.
            // 다른 연결 상태(IsJoinRejected/IsConnectionLost/SceneTransitionCountdownSeconds)는
            // ClientStateSingleton 컴포넌트에 있어 새 엔티티와 함께 자동으로 초기화되지만,
            // 이 필드만 System 인스턴스 스코프이므로 별도로 리셋이 필요하다.
            _hasTriggeredSceneTransition = false;

            _networkEntity = SystemAPI.GetSingletonEntity<SimulationConfig>();

            if (!EntityManager.HasComponent<NetworkConnectionData>(_networkEntity))
            {
                EntityManager.AddComponentData(_networkEntity, new NetworkConnectionData
                {
                    TickCommitQueue = new ConcurrentQueue<TickCommitData>()
                });
            }

            if (!EntityManager.HasComponent<ClientStateSingleton>(_networkEntity))
            {
                EntityManager.AddComponentData(_networkEntity, new ClientStateSingleton
                {
                    LastConfirmedTick = -1
                });
            }

            ConnectToServer();
        }

        protected override void OnUpdate()
        {
            var clientState = EntityManager.GetComponentData<ClientStateSingleton>(_networkEntity);
            var config = EntityManager.GetComponentData<SimulationConfig>(_networkEntity);
            var netData = EntityManager.GetComponentObject<NetworkConnectionData>(_networkEntity);

            // 씬 전환이 이미 예약되었으면(거부됨 또는 연결 끊김) 그 외 어떤 상태 갱신도 더 이상
            // 의미가 없다 — 카운트다운만 진행시키고 나머지는 건너뛴다.
            if (_hasTriggeredSceneTransition)
            {
                return;
            }

            // 서버가 거부 응답(GameAlreadyStarted=true)을 보낸 경우. 정상 참가 처리보다
            // 먼저 확인한다 — 거부된 접속 시도는 HasPendingJoinResponse를 세팅하지
            // 않으므로 순서 자체는 무관하지만, 의도를 명확히 하기 위해 먼저 둔다.
            if (netData.PendingGameAlreadyStarted)
            {
                netData.PendingGameAlreadyStarted = false;
                clientState.IsJoinRejected = true;
                clientState.SceneTransitionCountdownSeconds = 3f;
                Debug.LogWarning("[Client-DOTS] 접속이 거부되었습니다: 게임이 이미 시작되었습니다. 3초 후 EntryScene으로 이동합니다.");

                // 서버가 이미 거부했으므로 이 소켓으로 더 할 일이 없다 — 곧바로 닫고
                // 재시도하지 않는다(거부되면 메시지 표시 후 EntryScene 이동뿐).
                if (netData.UdpClient != null)
                {
                    try { netData.UdpClient.Close(); } catch { }
                    netData.UdpClient = null;
                }

                EntityManager.SetComponentData(_networkEntity, clientState);
                return;
            }

            // JoinResponse로 배정받은 PlayerId를 메인 스레드에서 반영한다. 아직 미배정(0)인
            // 상태에서 값이 들어왔을 때만 반영.
            if (netData.HasPendingJoinResponse)
            {
                clientState.MyPlayerId = netData.PendingMyPlayerId;
                clientState.IsConnected = true;
                // ExistingPlayers 소유권을 SimulationTickSystem이 소비할 필드로 넘긴다. 이
                // System은 물리를 계산하지 않으므로(순수 네트워크 계층) 스폰 절차는 직접
                // 수행하지 않고, 넘겨주는 역할만 한다.
                netData.UnprocessedExistingPlayers = netData.PendingExistingPlayers;
                netData.PendingExistingPlayers = null;
                netData.HasPendingJoinResponse = false;
                Debug.Log($"[Client-DOTS] Joined server as Player {clientState.MyPlayerId}, " +
                    $"existing players: {netData.UnprocessedExistingPlayers?.Length ?? 0}");
            }

            UpdateTrafficMetrics(ref clientState, netData);
            CheckConnectionLost(ref clientState, config, netData);

            // 거부/연결끊김으로 카운트다운이 진행 중이면 감산하고, 다 되면 씬을 전환한다.
            if (clientState.IsJoinRejected || clientState.IsConnectionLost)
            {
                clientState.SceneTransitionCountdownSeconds -= UnityEngine.Time.deltaTime;
                if (clientState.SceneTransitionCountdownSeconds <= 0f)
                {
                    _hasTriggeredSceneTransition = true;
                    EntityManager.SetComponentData(_networkEntity, clientState);
                    UnityEngine.SceneManagement.SceneManager.LoadScene("EntryScene");
                    return;
                }
            }

            EntityManager.SetComponentData(_networkEntity, clientState);
        }

        /// <summary>
        /// 서버에 최초 연결한다. OnStartRunning에서 딱 한 번만 호출되며, 이후 연결이
        /// 끊기면 CheckConnectionLost가 재시도 없이 곧바로 EntryScene 전환을 예약한다.
        /// </summary>
        public void ConnectToServer()
        {
            var netData = EntityManager.GetComponentObject<NetworkConnectionData>(_networkEntity);
            var config = EntityManager.GetComponentData<SimulationConfig>(_networkEntity);

            DisconnectInternal(netData);

            Interlocked.Exchange(ref netData.TimeSinceLastServerMessageMs,
                DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond);

            netData.ServerEndPoint = new IPEndPoint(IPAddress.Parse(config.ServerIp.ToString()), config.ServerPort);
            netData.UdpClient = new UdpClient();

            // Windows에서 UDP 소켓이 ICMP Port Unreachable을 받으면 다음 Send()/Receive()
            // 호출에서 SocketException(WSAECONNRESET)이 발생할 수 있다(서버와 동일한 조치).
            // 클라이언트는 UdpClient.Connect()로 연결된 소켓을 쓰므로 이 문제에 더
            // 취약해 동일한 억제 조치를 둔다.
            try
            {
                const int SIO_UDP_CONNRESET = -1744830452;
                netData.UdpClient.Client.IOControl((IOControlCode)SIO_UDP_CONNRESET, new byte[] { 0 }, null);
            }
            catch { }

            netData.UdpClient.Connect(netData.ServerEndPoint);

            UdpClient socketForThisConnection = netData.UdpClient;
            netData.ReceiveThread = new Thread(() => ReceiveLoop(socketForThisConnection, netData)) { IsBackground = true };
            netData.ReceiveThread.Start();

            SendPacket(netData, Protocol.BuildJoinRequest());

            Debug.Log($"[Client-DOTS] Connecting to {config.ServerIp}:{config.ServerPort}...");
        }

        private void DisconnectInternal(NetworkConnectionData netData)
        {
            if (netData.UdpClient != null)
            {
                try { netData.UdpClient.Close(); } catch { }
                netData.UdpClient = null;
            }

            netData.HasPendingJoinResponse = false;
            netData.PendingMyPlayerId = 0;

            var clientState = EntityManager.GetComponentData<ClientStateSingleton>(_networkEntity);
            clientState.MyPlayerId = 0;
            clientState.IsConnected = false;
            clientState.LastConfirmedTick = -1;
            // TotalElapsedSimTime도 함께 0으로 리셋해야 한다. LastConfirmedTick만 리셋하고
            // 이 값을 그대로 두면, 새로 스폰되는 플레이어의 발사 쿨다운/리스폰 판정
            // 기준선(LastFireElapsedTime/DeathElapsedTime 초기값과의 비교)이 이전 연결에서
            // 누적된 값을 기준으로 계산되어 어긋난다.
            clientState.TotalElapsedSimTime = 0f;
            clientState.IsGameStarted = false;
            EntityManager.SetComponentData(_networkEntity, clientState);

            // 남아있는 미처리 TickCommit도 전부 비운다 — 재연결 이후에는 새 틱 시퀀스가 처음부터
            // (서버가 이번 연결에서 새로 보내주는 틱 번호부터) 다시 시작되므로, 옛 연결에서
            // 도착한 낡은 TickCommit이 섞여 처리되면 안 된다.
            while (netData.TickCommitQueue.TryDequeue(out _)) { }

            // 플레이어/미사일 엔티티를 전부 파괴하기 전에 UI 이벤트를 먼저 채워(브리지가
            // 네임플레이트를 정리할 수 있도록) 그 다음 엔티티를 일괄 파괴한다.
            using (var uiEventQuery0 = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<UiEventSingletonTag>()))
            {
                if (uiEventQuery0.CalculateEntityCount() > 0)
                {
                    Entity uiEventEntity0 = uiEventQuery0.GetSingletonEntity();
                    var removedBuffer = EntityManager.GetBuffer<PlayerRemovedElement>(uiEventEntity0);
                    using var playerIdQuery = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PlayerId>());
                    using var playerIds = playerIdQuery.ToComponentDataArray<PlayerId>(Allocator.Temp);
                    foreach (var pid in playerIds)
                    {
                        removedBuffer.Add(new PlayerRemovedElement { PlayerId = pid.Value });
                    }
                }
            }

            using (var playerQuery = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PlayerId>()))
            {
                EntityManager.DestroyEntity(playerQuery);
            }
            using (var missileQuery = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<MissileTag>()))
            {
                EntityManager.DestroyEntity(missileQuery);
            }

            using (var uiEventQuery = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<UiEventSingletonTag>()))
            {
                if (uiEventQuery.CalculateEntityCount() > 0)
                {
                    Entity uiEventEntity = uiEventQuery.GetSingletonEntity();
                    EntityManager.GetBuffer<DeathVisualChangedElement>(uiEventEntity).Clear();
                    EntityManager.GetBuffer<PlayerJoinedElement>(uiEventEntity).Clear();
                    // PlayerRemovedElement는 위에서 이미 채워 넣었으므로 여기서는 비우지 않는다.
                    // 브리지(Update)가 다음 프레임에 소비한 뒤 스스로 Clear()한다.
                }
            }
        }

        /// <summary>
        /// Send() 호출을 try/catch로 감싼다 — SIO_UDP_CONNRESET 억제가 플랫폼/드라이버에
        /// 따라 완벽하지 않을 수 있으므로, 억제가 실패하는 드문 경우에도 이 한 번의 Send
        /// 실패가 호출부(PingSystem.OnUpdate, InputCaptureSystem.OnUpdate, GameHudBridge의
        /// Start Game 버튼 핸들러, ConnectToServer의 JoinRequest 전송)를 처리되지 않은
        /// 예외로 죽이면 안 된다. 처리하지 않으면 SystemBase.OnUpdate가 그 프레임에
        /// 예외를 던져, 같은 프레임의 다른 System 갱신에도 영향을 줄 수 있다.
        ///
        /// 실패했다고 해서 여기서 직접 EntryScene 전환을 트리거하지 않는다 — 그 판단은
        /// 전적으로 CheckConnectionLost가 TimeSinceLastServerMessageMs(서버로부터 실제로
        /// 무언가 수신했는지)를 기준으로 내린다. 한 번의 Send 실패가 곧 서버 다운을
        /// 의미하지 않으므로(일시적 ICMP 노이즈일 수 있음), 이 함수가 그 판단을 대신
        /// 내리면 불필요하게 이른 씬 전환을 유발할 수 있다.
        /// </summary>
        public void SendPacket(NetworkConnectionData netData, byte[] bytes)
        {
            if (netData.UdpClient == null) return;

            try
            {
                netData.UdpClient.Send(bytes, bytes.Length);
            }
            catch (ObjectDisposedException)
            {
                // 이 소켓은 이미 닫혔다. 호출부는 다음 프레임에 최신 netData.UdpClient를 다시
                // 읽어 자연스럽게 처리를 이어간다.
                return;
            }
            catch (SocketException)
            {
                // WSAECONNRESET을 포함한 일시적 소켓 오류. 이 한 번의 전송은 유실되지만
                // 치명적이지 않다 — 연결 끊김 여부는 CheckConnectionLost가 서버로부터의 수신
                // 여부만으로 독립적으로 판단하므로, 여기서는 넘어간다.
                return;
            }

            Interlocked.Add(ref netData.TotalBytesSent, bytes.Length);
            Interlocked.Add(ref netData.BytesSentThisSec, bytes.Length);
        }

        /// <summary>
        /// 서버가 보내는 패킷은 JoinResponse/TickCommit/Pong 세 가지다. 파싱은 전부 공유
        /// Protocol.cs의 TryRead* 헬퍼에 위임한다.
        /// </summary>
        private void ReceiveLoop(UdpClient socket, NetworkConnectionData netData)
        {
            IPEndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
            while (socket == netData.UdpClient)
            {
                try
                {
                    byte[] data = socket.Receive(ref remoteEP);

                    Interlocked.Add(ref netData.TotalBytesReceived, data.Length);
                    Interlocked.Add(ref netData.BytesReceivedThisSec, data.Length);

                    PacketType type = Protocol.PeekType(data);

                    Interlocked.Exchange(ref netData.TimeSinceLastServerMessageMs,
                        DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond);

                    switch (type)
                    {
                        case PacketType.JoinResponse:
                        {
                            if (Protocol.TryReadJoinResponse(data, out JoinResponseData joinData))
                            {
                                // [스레드 안전성] 배열 참조와 값 필드를 먼저 전부 써넣고, 맨
                                // 마지막에 HasPendingJoinResponse(volatile)를 쓴다 — 이
                                // 순서가 NetworkComponents.cs에서 설명한 release/acquire
                                // 보장의 전제다.
                                //
                                // 거부 응답(GameAlreadyStarted=true)이면 AssignedPlayerId
                                // 등은 sentinel 값이므로 정상 참가 필드에 채워 넣지 않는다 —
                                // 대신 PendingGameAlreadyStarted만 세팅해 메인 스레드가
                                // 거부 처리(메시지 표시 후 EntryScene 전환)로 들어가도록
                                // 한다.
                                if (joinData.GameAlreadyStarted)
                                {
                                    netData.PendingGameAlreadyStarted = true;
                                }
                                else
                                {
                                    netData.PendingMyPlayerId = joinData.AssignedPlayerId;
                                    netData.PendingMyJoinSequence = joinData.AssignedJoinSequence;
                                    netData.PendingExistingPlayers = joinData.ExistingPlayers;
                                    netData.HasPendingJoinResponse = true;
                                }
                            }
                            break;
                        }
                        case PacketType.TickCommit:
                        {
                            if (Protocol.TryReadTickCommit(data, out TickCommitData commit))
                            {
                                netData.TickCommitQueue.Enqueue(commit);
                            }
                            break;
                        }
                        case PacketType.Pong:
                        {
                            // Pong 와이어 포맷: PacketType(1) + echoedClientTicks(long, 8) 그대로.
                            // Protocol.cs는 빌드 헬퍼만 제공하고 Pong 파싱 헬퍼는 별도로 없으므로
                            // (RTT 계산은 클라이언트 로컬 시각과의 차이만 필요해 이 자리에서 직접
                            // 읽는 것이 자연스럽다) 여기서 바로 읽는다.
                            using var ms = new System.IO.MemoryStream(data);
                            using var br = new System.IO.BinaryReader(ms);
                            br.ReadByte(); // PacketType (이미 PeekType으로 확인함)
                            long sendTimeTicks = br.ReadInt64();
                            float rttMs = (float)(DateTime.UtcNow.Ticks - sendTimeTicks) / TimeSpan.TicksPerMillisecond;
                            netData.PendingPingResult = rttMs;
                            netData.HasPendingPingResult = true;
                            break;
                        }
                    }
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException)
                {
                    // socket == netData.UdpClient 검사로 다음 루프에서 자연 종료된다.
                    // ConnectToServer가 SIO_UDP_CONNRESET을 억제해두었지만 플랫폼/드라이버에
                    // 따라 무효화될 수 있다 — 억제가 우회되어 SocketException이 연달아
                    // 발생하면, 즉시 재시도하는 루프가 백그라운드 스레드의 CPU를 독점해
                    // TimeSinceLastServerMessageMs 갱신이 지연될 위험이 있으므로 짧게
                    // 양보한다. 정상 상황(초당 몇 회 이하)에서는 이 대기가 지연을 유발하지
                    // 않는다.
                    Thread.Sleep(10);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Client-DOTS] ReceiveLoop error: {ex.Message}");
                }
            }
        }

        private void UpdateTrafficMetrics(ref ClientStateSingleton clientState, NetworkConnectionData netData)
        {
            clientState.BandwidthTimer += UnityEngine.Time.deltaTime;
            if (clientState.BandwidthTimer >= 1.0f)
            {
                int sent = Interlocked.Exchange(ref netData.BytesSentThisSec, 0);
                int recv = Interlocked.Exchange(ref netData.BytesReceivedThisSec, 0);

                clientState.KbSentPerSec = sent / 1024f;
                clientState.KbReceivedPerSec = recv / 1024f;
                clientState.BandwidthTimer = 0f;
            }

            if (netData.HasPendingPingResult)
            {
                clientState.CurrentPingMs = netData.PendingPingResult;
                netData.HasPendingPingResult = false;
            }
        }

        /// <summary>
        /// 서버 응답이 ConnectionTimeoutSeconds 이상 끊기면 재연결을 시도하지 않고 곧바로
        /// EntryScene 전환을 예약한다. SceneTransitionCountdownSeconds를 0으로 두어
        /// OnUpdate가 이번 프레임에 곧바로 LoadScene을 호출하게 한다(거부 응답의 3초
        /// 대기와 달리 즉시 전환). 이미 IsConnectionLost가 true면 다시 트리거하지 않는다.
        /// </summary>
        private void CheckConnectionLost(ref ClientStateSingleton clientState, SimulationConfig config, NetworkConnectionData netData)
        {
            if (netData.UdpClient == null) return;
            if (clientState.IsConnectionLost) return;

            long lastMsgMs = Interlocked.Read(ref netData.TimeSinceLastServerMessageMs);
            long nowMs = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
            float secondsSinceLastMessage = (nowMs - lastMsgMs) / 1000f;

            if (secondsSinceLastMessage < config.ConnectionTimeoutSeconds) return;

            Debug.LogWarning("[Client-DOTS] 서버 응답이 끊겼습니다. EntryScene으로 이동합니다.");
            clientState.IsConnectionLost = true;
            clientState.SceneTransitionCountdownSeconds = 0f;

            if (netData.UdpClient != null)
            {
                try { netData.UdpClient.Close(); } catch { }
                netData.UdpClient = null;
            }
        }

        protected override void OnStopRunning()
        {
            if (EntityManager.Exists(_networkEntity) && EntityManager.HasComponent<NetworkConnectionData>(_networkEntity))
            {
                var netData = EntityManager.GetComponentObject<NetworkConnectionData>(_networkEntity);
                DisconnectInternal(netData);
            }
        }
    }
}
