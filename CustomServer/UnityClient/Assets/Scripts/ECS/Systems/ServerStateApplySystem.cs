using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using UnityEngine;

namespace CustomClient.Dots
{
    /// <summary>
    /// 원본 Update()의 "while (_stateQueue.TryDequeue(...))" 블록을 그대로 옮긴 System.
    /// Update()는 매 프레임 실행되므로 이 System도 SimulationSystemGroup(가변 프레임)에서 실행한다.
    /// (FixedStepSimulationSystemGroup이 아님에 주의 — 원본도 재조정은 FixedUpdate가 아니라 Update에서 처리.)
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial class ServerStateApplySystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<SimulationConfig>();
            RequireForUpdate<ClientStateSingleton>();
            RequireForUpdate<RenderPrefabs>();
        }

        protected override void OnUpdate()
        {
            var configEntity = SystemAPI.GetSingletonEntity<SimulationConfig>();
            var config = EntityManager.GetComponentData<SimulationConfig>(configEntity);
            var netData = EntityManager.GetComponentObject<NetworkConnectionData>(configEntity);
            var clientState = EntityManager.GetComponentData<ClientStateSingleton>(configEntity);

            var snapshotSingleton = GetOrCreateSnapshotSingleton();
            var uiEventSingleton = GetOrCreateUiEventSingleton();

            bool stateChanged = false;

            while (netData.StateQueue.TryDequeue(out var frame))
            {
                stateChanged = true;

                // JoinResponse는 ServerTick == -1인 특수 프레임으로 인코딩해 전달 (NetworkConnectionSystem 참고).
                if (frame.ServerTick == -1)
                {
                    byte myId = frame.States[0].Id;
                    clientState.MyPlayerId = myId;
                    clientState.IsConnected = true;
                    Debug.Log($"[Client-DOTS] Joined server as Player {myId}");

                    AppendUiEvent(uiEventSingleton, new PlayerJoinedElement { PlayerId = myId, IsLocal = true });
                    continue;
                }

                int serverTick = frame.ServerTick;
                var states = frame.States;

                var activeIds = new NativeHashSet<byte>(states.Length, Allocator.Temp);
                var sampleStartIndex = GetSnapshotSampleCount(snapshotSingleton);

                foreach (var s in states)
                {
                    activeIds.Add(s.Id);
                    float2 serverPos = new float2(s.X, s.Y);

                    Entity playerEntity = FindOrCreatePlayerEntity(s.Id, clientState.MyPlayerId, serverPos, s.Rotation, s.Health, s.IsDead, uiEventSingleton);

                    // 사망 상태 변화 감지 및 UI 이벤트 통지 (기존 UpdateDeathVisual 대체)
                    var health = EntityManager.GetComponentData<HealthState>(playerEntity);
                    bool deathChanged = health.IsDead != s.IsDead;
                    bool justRespawned = health.WasDead && !s.IsDead;
                    health.WasDead = health.IsDead;
                    health.IsDead = s.IsDead;
                    health.Health = s.Health;
                    EntityManager.SetComponentData(playerEntity, health);
                    if (deathChanged)
                    {
                        AppendUiEvent(uiEventSingleton, new DeathVisualChangedElement { PlayerId = s.Id, IsDead = s.IsDead });
                    }

                    if (justRespawned)
                    {
                        // 로컬이면 ReconcileLocalPlayer가, 원격이면 RemotePlayerInterpolationSystem이
                        // 이 태그를 보고 각자의 경로에서 즉시 점프 처리한 뒤 태그를 제거한다.
                        // 여기서는 무조건 붙이기만 하고 대상별 분기는 하지 않는다 — 태그 하나로
                        // 로컬/원격 양쪽 소비자를 모두 지원하는 편이, 대상마다 다른 신호를
                        // 따로 만드는 것보다 단순하다.
                        if (!EntityManager.HasComponent<JustRespawnedTag>(playerEntity))
                        {
                            EntityManager.AddComponent<JustRespawnedTag>(playerEntity);
                        }
                    }

                    // 스냅샷 버퍼에 추가할 샘플 기록 (보간용, 로컬 포함 — 로컬은 InterpolationSystem에서 건너뜀)
                    AppendSnapshotSample(snapshotSingleton, s.Id, serverPos, s.Rotation);

                    if (s.Id == clientState.MyPlayerId)
                    {
                        clientState.IsLocalPlayerDead = s.IsDead;
                        ReconcileLocalPlayer(playerEntity, config, serverTick, s.LastProcessedTick, serverPos, s.Rotation, s.CurrentSpeed, justRespawned);
                    }
                    else
                    {
                        var confirmed = new ServerConfirmedState
                        {
                            ServerTick = serverTick,
                            Position = serverPos,
                            RotationDeg = s.Rotation,
                            CurrentSpeed = s.CurrentSpeed,
                            Health = s.Health,
                            IsDead = s.IsDead
                        };
                        EntityManager.SetComponentData(playerEntity, confirmed);
                    }
                }

                int sampleCount = GetSnapshotSampleCount(snapshotSingleton) - sampleStartIndex;
                AppendSnapshotHeader(snapshotSingleton, UnityEngine.Time.time, sampleStartIndex, sampleCount);
                TrimOldSnapshots(snapshotSingleton);

                RemoveDisconnectedPlayers(activeIds, uiEventSingleton);
                activeIds.Dispose();
            }

            if (stateChanged)
            {
                EntityManager.SetComponentData(configEntity, clientState);
            }
        }

        // -------------------------------------------------------------------
        // 플레이어 생성/조회
        // -------------------------------------------------------------------
        private Entity FindOrCreatePlayerEntity(byte id, byte myPlayerId, float2 initialPos, float initialRot,
            byte initialHealth, bool initialIsDead, Entity uiEventSingleton)
        {
            foreach (var (playerId, entity) in SystemAPI.Query<RefRO<PlayerId>>().WithEntityAccess())
            {
                if (playerId.ValueRO.Value == id) return entity;
            }

            bool isLocal = (id == myPlayerId);
            var prefabs = SystemAPI.GetSingleton<RenderPrefabs>();
            Entity prefab = isLocal ? prefabs.LocalShipPrefab : prefabs.RemoteShipPrefab;

            Entity entity2 = EntityManager.Instantiate(prefab);

            EntityManager.AddComponentData(entity2, new PlayerId { Value = id });
            if (isLocal)
            {
                EntityManager.AddComponentData(entity2, new LocalPlayerTag());
            }
            else
            {
                EntityManager.AddComponentData(entity2, new RemotePlayerTag());
            }

            EntityManager.AddComponentData(entity2, new HealthState { Health = initialHealth, IsDead = initialIsDead, WasDead = initialIsDead });
            EntityManager.AddComponentData(entity2, new ScoreboardEntry { Kills = 0, Deaths = 0 });
            EntityManager.SetComponentData(entity2, new RenderTransform2D { Position = initialPos, RotationDeg = initialRot });

            if (EntityManager.HasComponent<URPMaterialPropertyBaseColor>(entity2))
            {
                float4 initialColor = initialIsDead
                    ? PlayerVisualColorSystem.DeadColor
                    : (isLocal ? PlayerVisualColorSystem.LocalAliveColor : PlayerVisualColorSystem.RemoteAliveColor);
                EntityManager.SetComponentData(entity2, new URPMaterialPropertyBaseColor { Value = initialColor });
            }

            EntityManager.AddComponentData(entity2, new ServerConfirmedState
            {
                ServerTick = 0,
                Position = initialPos,
                RotationDeg = initialRot,
                CurrentSpeed = 0f,
                Health = initialHealth,
                IsDead = initialIsDead
            });

            if (isLocal)
            {
                EntityManager.AddComponentData(entity2, new PredictedTankState
                {
                    Position = initialPos,
                    RotationDeg = initialRot,
                    Speed = 0f
                });
                EntityManager.AddComponentData(entity2, new RenderErrorOffset { Position = float2.zero, RotationDeg = 0f });
                EntityManager.AddBuffer<PendingInputElement>(entity2);
            }

            AppendUiEvent(uiEventSingleton, new PlayerJoinedElement { PlayerId = id, IsLocal = isLocal });

            return entity2;
        }

        // -------------------------------------------------------------------
        // 재조정 (원본 Update()의 "if (s.Id == _myPlayerId)" 블록과 정확히 동일한 3단 분기)
        // -------------------------------------------------------------------
        private void ReconcileLocalPlayer(Entity localEntity, SimulationConfig config, int serverTick, int lastProcessedTick,
            float2 serverPos, float serverRot, float serverSpeed, bool justRespawned)
        {
            var pendingBuffer = EntityManager.GetBuffer<PendingInputElement>(localEntity);

            if (justRespawned)
            {
                // pending 입력을 재생하지 않는 이유: 그 입력들은 죽기 직전(리스폰 전 위치
                // 기준)에 쌓인 것들이라, 리스폰된 새 위치에 그대로 재생하면 의미 없는 방향으로
                // 몇 프레임 밀리는 잘못된 예측이 나온다. 서버도 사망 중에는 HandleClientInput에서
                // 이동을 전혀 반영하지 않으므로(LastProcessedTick만 갱신), 재생해도 서버 결과와
                // 맞을 리가 없다.
                pendingBuffer.Clear();

                EntityManager.SetComponentData(localEntity, new PredictedTankState
                {
                    Position = serverPos,
                    RotationDeg = serverRot,
                    Speed = serverSpeed
                });
                EntityManager.SetComponentData(localEntity, new RenderErrorOffset
                {
                    Position = float2.zero,
                    RotationDeg = 0f
                });

                var health = EntityManager.GetComponentData<HealthState>(localEntity);
                EntityManager.SetComponentData(localEntity, new ServerConfirmedState
                {
                    ServerTick = serverTick,
                    Position = serverPos,
                    RotationDeg = serverRot,
                    CurrentSpeed = serverSpeed,
                    Health = health.Health,
                    IsDead = health.IsDead
                });

                // 이 프레임의 리스폰 점프 처리가 끝났으므로 태그를 제거한다. 남겨두면 다음
                // 프레임에도 계속 이 분기를 타서 정상적인 예측/재조정이 재개되지 않는다.
                if (EntityManager.HasComponent<JustRespawnedTag>(localEntity))
                {
                    EntityManager.RemoveComponent<JustRespawnedTag>(localEntity);
                }
                return;
            }

            // 서버가 이 플레이어에게서 실제로 처리한 마지막 입력 틱인 lastProcessedTick
            // (CustomServer.HandleClientInput이 그 플레이어가 보낸 clientTick을 그대로 기록한
            // 값, PlayerStateWire.LastProcessedTick으로 전송됨)과 비교한다. 이 값은 ServerLoop의
            // 실행 속도나 접속 시점과 완전히 무관하게, 항상 "서버가 실제로 반영한 내 마지막
            // 입력이 몇 번째 틱이었는가"만을 정확히 가리킨다. CustomClient.cs(MonoBehaviour)의
            // 동일 수정과 정확히 같은 근거.
            int removeCount = 0;
            for (int i = 0; i < pendingBuffer.Length; i++)
            {
                if (pendingBuffer[i].Tick <= lastProcessedTick) removeCount++;
                else break;
            }
            if (removeCount > 0) pendingBuffer.RemoveRange(0, removeCount);

            float2 reconciledPos = serverPos;
            float reconciledRot = serverRot;
            float reconciledSpeed = serverSpeed;
            float dt = UnityEngine.Time.fixedDeltaTime;

            for (int i = 0; i < pendingBuffer.Length; i++)
            {
                var pending = pendingBuffer[i];
                LocalPlayerFixedStepSystem.SimulateTankStep(config, ref reconciledPos, ref reconciledRot, ref reconciledSpeed,
                    pending.Throttle, pending.Turn, dt);
            }

            var predicted = EntityManager.GetComponentData<PredictedTankState>(localEntity);
            var offset = EntityManager.GetComponentData<RenderErrorOffset>(localEntity);

            // [중요] 단순 뺄셈(reconciledPos - predicted.Position) 대신 wrap 경계를 최단 경로로
            // 넘는 차이를 구한다. 예측과 재조정이 wrap 경계를 사이에 두고 한쪽만 이미 wrap된
            // 상태(예: predicted=-3.99, reconciled=+3.99, 실제로는 0.02만큼만 떨어진 이웃 좌표)
            // 라면, 단순 뺄셈은 이를 거의 range(2*ServerWorldHalfExtent)만큼의 거대한 오차로
            // 오인해 불필요한 스냅을 일으킨다. WrapCoordinate로 최단 차이를 구하면 이런 경우
            // 실제 물리적 거리에 가까운 값이 나와 오차 판정(스냅/보정/유지)이 항상 "실제로
            // 얼마나 떨어져 있는가" 기준으로 정확하게 이루어진다. CustomClient
            // (MonoBehaviour)의 동일 지점과 정확히 같은 처리. 화면이 16:9로 고정되어 X/Y
            // half-extent가 다르므로, 각 축은 반드시 자신의 값(.x / .y)으로 wrap해야 한다.
            float2 posError = new float2(
                LocalPlayerFixedStepSystem.WrapCoordinate(reconciledPos.x - predicted.Position.x, config.ServerWorldHalfExtent.x),
                LocalPlayerFixedStepSystem.WrapCoordinate(reconciledPos.y - predicted.Position.y, config.ServerWorldHalfExtent.y));
            float rotError = Mathf.DeltaAngle(predicted.RotationDeg, reconciledRot);
            float posErrorSqrMag = math.lengthsq(posError);
            float rotErrorAbs = Mathf.Abs(rotError);

            // 위치: 스냅 / 보정(오프셋 흡수) / 유지 3단 처리 (원본과 동일 순서: snap 조건 먼저 검사)
            if (posErrorSqrMag > config.SnapThreshold * config.SnapThreshold)
            {
                predicted.Position = reconciledPos;
                offset.Position = float2.zero;
            }
            else if (posErrorSqrMag > config.ErrorThreshold * config.ErrorThreshold)
            {
                predicted.Position = reconciledPos;
                offset.Position -= posError;
            }
            // else: 오차가 작음 — predicted, offset 둘 다 유지 (원본과 동일하게 아무것도 하지 않음)

            if (rotErrorAbs > config.RotationSnapThresholdDeg)
            {
                predicted.RotationDeg = reconciledRot;
                offset.RotationDeg = 0f;
            }
            else if (rotErrorAbs > config.RotationErrorThresholdDeg)
            {
                predicted.RotationDeg = reconciledRot;
                offset.RotationDeg -= rotError;
            }
            // else: 유지

            predicted.Speed = reconciledSpeed;

            EntityManager.SetComponentData(localEntity, predicted);
            EntityManager.SetComponentData(localEntity, offset);

            // ServerConfirmedState도 갱신해둔다 (디버깅/다른 System 참조용. 재조정 자체는 위에서 완료됨)
            var currentHealth = EntityManager.GetComponentData<HealthState>(localEntity);
            EntityManager.SetComponentData(localEntity, new ServerConfirmedState
            {
                ServerTick = serverTick,
                Position = serverPos,
                RotationDeg = serverRot,
                CurrentSpeed = serverSpeed,
                Health = currentHealth.Health,
                IsDead = currentHealth.IsDead
            });
        }

        // -------------------------------------------------------------------
        // 연결 종료 플레이어 정리 (원본 RemoveDisconnectedPlayers)
        // -------------------------------------------------------------------
        private void RemoveDisconnectedPlayers(NativeHashSet<byte> activeIds, Entity uiEventSingleton)
        {
            var toRemove = new NativeList<Entity>(Allocator.Temp);
            var toRemoveIds = new NativeList<byte>(Allocator.Temp);

            foreach (var (playerId, entity) in SystemAPI.Query<RefRO<PlayerId>>().WithEntityAccess())
            {
                if (!activeIds.Contains(playerId.ValueRO.Value))
                {
                    toRemove.Add(entity);
                    toRemoveIds.Add(playerId.ValueRO.Value);
                }
            }

            for (int i = 0; i < toRemove.Length; i++)
            {
                AppendUiEvent(uiEventSingleton, new PlayerRemovedElement { PlayerId = toRemoveIds[i] });
                EntityManager.DestroyEntity(toRemove[i]);
            }

            toRemove.Dispose();
            toRemoveIds.Dispose();
        }

        // -------------------------------------------------------------------
        // 전역 스냅샷 버퍼 helper (원본 List<Snapshot> _snapshotBuffer 대체)
        // -------------------------------------------------------------------
        private Entity GetOrCreateSnapshotSingleton()
        {
            foreach (var (tag, entity) in SystemAPI.Query<RefRO<SnapshotBufferSingletonTag>>().WithEntityAccess())
            {
                return entity;
            }

            Entity e = EntityManager.CreateEntity(typeof(SnapshotBufferSingletonTag));
            EntityManager.AddBuffer<SnapshotFrameHeader>(e);
            EntityManager.AddBuffer<PlayerSnapshotSample>(e);
            return e;
        }

        private int GetSnapshotSampleCount(Entity snapshotSingleton)
        {
            return EntityManager.GetBuffer<PlayerSnapshotSample>(snapshotSingleton).Length;
        }

        private void AppendSnapshotSample(Entity snapshotSingleton, byte playerId, float2 pos, float rot)
        {
            var buffer = EntityManager.GetBuffer<PlayerSnapshotSample>(snapshotSingleton);
            buffer.Add(new PlayerSnapshotSample { PlayerId = playerId, Position = pos, RotationDeg = rot });
        }

        private void AppendSnapshotHeader(Entity snapshotSingleton, float timestamp, int startIndex, int count)
        {
            var headers = EntityManager.GetBuffer<SnapshotFrameHeader>(snapshotSingleton);
            headers.Add(new SnapshotFrameHeader { LocalTimeStamp = timestamp, SampleStartIndex = startIndex, SampleCount = count });
        }

        /// <summary>
        /// 원본: _snapshotBuffer.RemoveAll(sn => Time.time - sn.LocalTimeStamp > 2.0f);
        /// 헤더와 샘플을 함께 앞에서부터 잘라내고 인덱스를 재정렬한다.
        /// </summary>
        private void TrimOldSnapshots(Entity snapshotSingleton)
        {
            var headers = EntityManager.GetBuffer<SnapshotFrameHeader>(snapshotSingleton);
            float now = UnityEngine.Time.time;

            int firstValid = 0;
            while (firstValid < headers.Length && now - headers[firstValid].LocalTimeStamp > 2.0f)
            {
                firstValid++;
            }

            if (firstValid == 0) return;

            int sampleCutoff = headers[firstValid - 1].SampleStartIndex + headers[firstValid - 1].SampleCount;
            var samples = EntityManager.GetBuffer<PlayerSnapshotSample>(snapshotSingleton);
            samples.RemoveRange(0, sampleCutoff);
            headers.RemoveRange(0, firstValid);

            // 남은 헤더들의 SampleStartIndex를 sampleCutoff만큼 당겨준다.
            for (int i = 0; i < headers.Length; i++)
            {
                var h = headers[i];
                h.SampleStartIndex -= sampleCutoff;
                headers[i] = h;
            }
        }

        // -------------------------------------------------------------------
        // UI 이벤트 싱글톤 helper
        // -------------------------------------------------------------------
        private Entity GetOrCreateUiEventSingleton()
        {
            foreach (var (tag, entity) in SystemAPI.Query<RefRO<UiEventSingletonTag>>().WithEntityAccess())
            {
                return entity;
            }

            Entity e = EntityManager.CreateEntity(typeof(UiEventSingletonTag));
            EntityManager.AddBuffer<DeathVisualChangedElement>(e);
            EntityManager.AddBuffer<PlayerRemovedElement>(e);
            EntityManager.AddBuffer<PlayerJoinedElement>(e);
            return e;
        }

        private void AppendUiEvent(Entity uiEventSingleton, DeathVisualChangedElement ev)
        {
            EntityManager.GetBuffer<DeathVisualChangedElement>(uiEventSingleton).Add(ev);
        }

        private void AppendUiEvent(Entity uiEventSingleton, PlayerRemovedElement ev)
        {
            EntityManager.GetBuffer<PlayerRemovedElement>(uiEventSingleton).Add(ev);
        }

        private void AppendUiEvent(Entity uiEventSingleton, PlayerJoinedElement ev)
        {
            EntityManager.GetBuffer<PlayerJoinedElement>(uiEventSingleton).Add(ev);
        }
    }
}
