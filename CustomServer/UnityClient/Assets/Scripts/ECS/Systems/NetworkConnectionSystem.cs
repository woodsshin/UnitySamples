using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace CustomClient.Dots
{
    /// <summary>
    /// 서버 연결/재연결/수신 스레드를 관리하는 System.
    /// 기존 MonoBehaviour의 ConnectToServer / DisconnectInternal / ReceiveLoop /
    /// UpdateReconnectionWatchdog를 그대로 옮긴 것이다.
    ///
    /// [중요] UdpClient.Receive는 블로킹 호출이라 여기서도 여전히 별도 스레드(ReceiveLoop)를
    /// 사용한다. ECS System.OnUpdate 자체는 항상 메인 스레드에서 실행되므로, 수신 스레드가
    /// 채워 넣는 ConcurrentQueue를 거쳐야만 메인 스레드 System들이 안전하게 데이터를 읽을 수 있다.
    /// </summary>
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial class NetworkConnectionSystem : SystemBase
    {
        private Entity _networkEntity;

        protected override void OnCreate()
        {
            RequireForUpdate<SimulationConfig>();
        }

        protected override void OnStartRunning()
        {
            // 싱글톤 엔티티 준비 (SimulationConfig는 Authoring/Bootstrap에서 미리 생성해 둔다).
            _networkEntity = SystemAPI.GetSingletonEntity<SimulationConfig>();

            if (!EntityManager.HasComponent<NetworkConnectionData>(_networkEntity))
            {
                EntityManager.AddComponentData(_networkEntity, new NetworkConnectionData
                {
                    StateQueue = new ConcurrentQueue<ServerStateFrame>(),
                    MissileStateQueue = new ConcurrentQueue<MissileStateFrame>(),
                    ScoreboardQueue = new ConcurrentQueue<ScoreboardFrame>()
                });
            }

            if (!EntityManager.HasComponent<ClientStateSingleton>(_networkEntity))
            {
                EntityManager.AddComponentData(_networkEntity, new ClientStateSingleton());
            }

            ConnectToServer();
        }

        protected override void OnUpdate()
        {
            var clientState = EntityManager.GetComponentData<ClientStateSingleton>(_networkEntity);
            var config = EntityManager.GetComponentData<SimulationConfig>(_networkEntity);
            var netData = EntityManager.GetComponentObject<NetworkConnectionData>(_networkEntity);

            UpdateTrafficMetrics(ref clientState, netData);
            UpdateReconnectionWatchdog(ref clientState, config, netData);

            EntityManager.SetComponentData(_networkEntity, clientState);
        }

        /// <summary>
        /// 서버에 연결(또는 재연결)한다. 기존 연결이 있다면 먼저 정리한다.
        /// </summary>
        public void ConnectToServer()
        {
            var netData = EntityManager.GetComponentObject<NetworkConnectionData>(_networkEntity);
            var config = EntityManager.GetComponentData<SimulationConfig>(_networkEntity);

            DisconnectInternal(netData);

            // 재연결 시도 타이머를 리셋
            Interlocked.Exchange(ref netData.TimeSinceLastServerMessageMs,
                DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond);

            netData.ServerEndPoint = new IPEndPoint(IPAddress.Parse(config.ServerIp.ToString()), config.ServerPort);
            netData.UdpClient = new UdpClient();
            netData.UdpClient.Connect(netData.ServerEndPoint);

            UdpClient socketForThisConnection = netData.UdpClient;
            netData.ReceiveThread = new Thread(() => ReceiveLoop(socketForThisConnection, netData)) { IsBackground = true };
            netData.ReceiveThread.Start();

            byte[] joinPacket = { (byte)PacketType.JoinRequest };
            SendPacket(netData, joinPacket);

            Debug.Log($"[Client-DOTS] Connecting to {config.ServerIp}:{config.ServerPort}...");

            var clientState = EntityManager.GetComponentData<ClientStateSingleton>(_networkEntity);
            clientState.ReconnectTimer = 0f;
            EntityManager.SetComponentData(_networkEntity, clientState);
        }

        private void DisconnectInternal(NetworkConnectionData netData)
        {
            if (netData.UdpClient != null)
            {
                try { netData.UdpClient.Close(); } catch { }
                netData.UdpClient = null;
            }

            var clientState = EntityManager.GetComponentData<ClientStateSingleton>(_networkEntity);
            clientState.MyPlayerId = 0;
            clientState.ClientTick = 0;
            clientState.IsConnected = false;
            clientState.IsLocalPlayerDead = false;
            EntityManager.SetComponentData(_networkEntity, clientState);

            // 원본 DisconnectInternal: _pendingInputs/_snapshotBuffer/_scoreboard Clear +
            // ClearAllPlayerViews + ClearAllMissileViews. ECS에서는 엔터티를 제거하기 전에 UI 이벤트를 먼저
            // 처리해서 브리지가 네임플레이트를 정리할 수 있도록 한 다음 엔티티를 일괄 제거한다.
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

            using (var snapshotQuery = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<SnapshotBufferSingletonTag>()))
            {
                if (snapshotQuery.CalculateEntityCount() > 0)
                {
                    Entity snapshotEntity = snapshotQuery.GetSingletonEntity();
                    EntityManager.GetBuffer<SnapshotFrameHeader>(snapshotEntity).Clear();
                    EntityManager.GetBuffer<PlayerSnapshotSample>(snapshotEntity).Clear();
                }
            }

            using (var uiEventQuery = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<UiEventSingletonTag>()))
            {
                if (uiEventQuery.CalculateEntityCount() > 0)
                {
                    Entity uiEventEntity = uiEventQuery.GetSingletonEntity();
                    EntityManager.GetBuffer<DeathVisualChangedElement>(uiEventEntity).Clear();
                    EntityManager.GetBuffer<PlayerJoinedElement>(uiEventEntity).Clear();
                }
            }
        }

        public void SendPacket(NetworkConnectionData netData, byte[] bytes)
        {
            if (netData.UdpClient == null) return;

            netData.UdpClient.Send(bytes, bytes.Length);
            Interlocked.Add(ref netData.TotalBytesSent, bytes.Length);
            Interlocked.Add(ref netData.BytesSentThisSec, bytes.Length);
        }

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

                    using var ms = new MemoryStream(data);
                    using var br = new BinaryReader(ms);
                    PacketType type = (PacketType)br.ReadByte();

                    Interlocked.Exchange(ref netData.TimeSinceLastServerMessageMs,
                        DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond);

                    switch (type)
                    {
                        case PacketType.JoinResponse:
                        {
                            byte myId = br.ReadByte();
                            // ClientStateSingleton 갱신은 메인 스레드 전용이 아니라 구조 변경이 없는
                            // 단순 값 대입이므로 여기서도 안전하지만, EntityManager 접근은 메인
                            // 스레드에서만 허용되므로 추가 큐를 생성.
                            netData.StateQueue.Enqueue(new ServerStateFrame
                            {
                                ServerTick = -1, // -1을 JoinResponse 마커로 사용
                                States = new[] { new PlayerStateWire { Id = myId } }
                            });
                            break;
                        }
                        case PacketType.ServerState:
                        {
                            int tick = br.ReadInt32();
                            int count = br.ReadInt32();
                            var states = new PlayerStateWire[count];
                            for (int i = 0; i < count; i++)
                            {
                                states[i] = new PlayerStateWire
                                {
                                    Id = br.ReadByte(),
                                    LastProcessedTick = br.ReadInt32(),
                                    X = br.ReadSingle(),
                                    Y = br.ReadSingle(),
                                    Rotation = br.ReadSingle(),
                                    CurrentSpeed = br.ReadSingle(),
                                    Health = br.ReadByte(),
                                    IsDead = br.ReadBoolean()
                                };
                            }
                            netData.StateQueue.Enqueue(new ServerStateFrame { ServerTick = tick, States = states });
                            break;
                        }
                        case PacketType.Pong:
                        {
                            long sendTimeTicks = br.ReadInt64();
                            float rttMs = (float)(DateTime.UtcNow.Ticks - sendTimeTicks) / TimeSpan.TicksPerMillisecond;
                            netData.PendingPingResult = rttMs;
                            netData.HasPendingPingResult = true;
                            break;
                        }
                        case PacketType.MissileState:
                        {
                            int tick = br.ReadInt32();
                            int count = br.ReadInt32();
                            var missiles = new MissileStateWire[count];
                            for (int i = 0; i < count; i++)
                            {
                                missiles[i] = new MissileStateWire
                                {
                                    Id = br.ReadUInt16(),
                                    OwnerId = br.ReadByte(),
                                    X = br.ReadSingle(),
                                    Y = br.ReadSingle(),
                                    Rotation = br.ReadSingle()
                                };
                            }
                            netData.MissileStateQueue.Enqueue(new MissileStateFrame { ServerTick = tick, Missiles = missiles });
                            break;
                        }
                        case PacketType.ScoreboardState:
                        {
                            int count = br.ReadInt32();
                            var entries = new ScoreEntryWire[count];
                            for (int i = 0; i < count; i++)
                            {
                                entries[i] = new ScoreEntryWire
                                {
                                    PlayerId = br.ReadByte(),
                                    Kills = br.ReadInt32(),
                                    Deaths = br.ReadInt32()
                                };
                            }
                            netData.ScoreboardQueue.Enqueue(new ScoreboardFrame { Entries = entries });
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

        private void UpdateReconnectionWatchdog(ref ClientStateSingleton clientState, SimulationConfig config, NetworkConnectionData netData)
        {
            if (netData.UdpClient == null) return;

            long lastMsgMs = Interlocked.Read(ref netData.TimeSinceLastServerMessageMs);
            long nowMs = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
            float secondsSinceLastMessage = (nowMs - lastMsgMs) / 1000f;

            if (secondsSinceLastMessage < config.ConnectionTimeoutSeconds)
            {
                clientState.ReconnectTimer = 0f;
                return;
            }

            clientState.ReconnectTimer += UnityEngine.Time.deltaTime;
            if (clientState.ReconnectTimer >= config.ReconnectIntervalSeconds)
            {
                clientState.ReconnectTimer = 0f;
                clientState.HasReconnectedAtLeastOnce = true;
                Debug.Log("[Client-DOTS] Server unresponsive, attempting to reconnect...");
                EntityManager.SetComponentData(_networkEntity, clientState);
                ConnectToServer();
                return;
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
