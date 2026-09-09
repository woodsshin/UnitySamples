using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace CustomClient.Dots
{
    /// <summary>
    /// 원본 InterpolateRemotePlayers()를 그대로 옮긴 System.
    /// renderTime = Time.time - _interpolationDelay 시점에 해당하는 두 스냅샷(프레임 헤더) 사이를
    /// 찾아 그 사이를 Lerp/LerpAngle로 보간한다. 로컬 플레이어는 건너뛴다.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(ServerStateApplySystem))]
    public partial class RemotePlayerInterpolationSystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<SimulationConfig>();
            RequireForUpdate<ClientStateSingleton>();
        }

        protected override void OnUpdate()
        {
            Entity snapshotSingleton = Entity.Null;
            foreach (var (tag, entity) in SystemAPI.Query<RefRO<SnapshotBufferSingletonTag>>().WithEntityAccess())
            {
                snapshotSingleton = entity;
                break;
            }
            if (snapshotSingleton == Entity.Null)
            {
                ApplyRespawnJumps();
                return;
            }

            var headers = EntityManager.GetBuffer<SnapshotFrameHeader>(snapshotSingleton);
            if (headers.Length == 0)
            {
                ApplyRespawnJumps();
                return;
            }

            var config = SystemAPI.GetSingleton<SimulationConfig>();
            var samples = EntityManager.GetBuffer<PlayerSnapshotSample>(snapshotSingleton);

            float renderTime = UnityEngine.Time.time - config.InterpolationDelay;

            int index = 0;
            while (index < headers.Length && headers[index].LocalTimeStamp < renderTime)
            {
                index++;
            }

            if (index == 0)
            {
                ApplySnapshotFrame(headers[0], samples);
            }
            else if (index >= headers.Length)
            {
                ApplySnapshotFrame(headers[headers.Length - 1], samples);
            }
            else
            {
                var h1 = headers[index - 1];
                var h2 = headers[index];

                float t = (renderTime - h1.LocalTimeStamp) / (h2.LocalTimeStamp - h1.LocalTimeStamp);
                t = Mathf.Clamp01(t);

                InterpolateBetweenFrames(h1, h2, samples, t, config);
            }

            ApplyRespawnJumps();
        }

        /// <summary>
        /// JustRespawnedTag가 붙은 원격 플레이어 엔티티를 interpolation 없이 최신
        /// ServerConfirmedState(서버가 보낸 리스폰 후 위치)로 즉시 스냅(점프)시키고 태그를
        /// 제거한다. OnUpdate()의 모든 분기(단일 스냅샷 적용, 두 스냅샷 사이 보간, 스냅샷
        /// 버퍼가 아직 없거나 비어있는 초기 상태) 뒤에 공통으로 호출되어, 어느 경로를 타든
        /// 리스폰한 플레이어는 항상 이 함수가 최종적으로 위치를 확정한다.
        ///
        /// 로컬 플레이어는 여기서 다루지 않는다 — 로컬은 ServerStateApplySystem.ReconcileLocalPlayer가
        /// 같은 프레임에 이미 처리하고 태그도 그쪽에서 제거하므로, 이 System이 실행되는 시점에는
        /// 로컬 엔티티에 이 태그가 남아있지 않다. 그래도 원격 전용 컴포넌트
        /// RenderTransform2D를 원격이 실제로 읽는 값만 수정하도록 RemotePlayerTag로 한 번 더 필터링한다.
        /// </summary>
        private void ApplyRespawnJumps()
        {
            var toClear = new NativeList<Entity>(Allocator.Temp);

            foreach (var (confirmed, renderTransform, entity) in
                     SystemAPI.Query<RefRO<ServerConfirmedState>, RefRW<RenderTransform2D>>()
                         .WithAll<JustRespawnedTag, RemotePlayerTag>()
                         .WithEntityAccess())
            {
                renderTransform.ValueRW.Position = confirmed.ValueRO.Position;
                renderTransform.ValueRW.RotationDeg = confirmed.ValueRO.RotationDeg;
                toClear.Add(entity);
            }

            for (int i = 0; i < toClear.Length; i++)
            {
                EntityManager.RemoveComponent<JustRespawnedTag>(toClear[i]);
            }
            toClear.Dispose();
        }

        /// <summary>
        /// [-halfExtent, +halfExtent) 범위로 순환(wrap)하는 두 축(X/Y, 각각 다른 half-extent를
        /// 가질 수 있음)에서, a에서 b로 가는 "최단 경로" 기준으로 t만큼 보간한다. Mathf.LerpAngle이
        /// 0/360도 경계를 최단 경로로 보간하는 것과 정확히 같은 원리를 위치 좌표에 적용한 것이다.
        /// </summary>
        private static float2 LerpWrapped(float2 a, float2 b, float t, float2 halfExtent)
        {
            float2 delta = new float2(
                LocalPlayerFixedStepSystem.WrapCoordinate(b.x - a.x, halfExtent.x),
                LocalPlayerFixedStepSystem.WrapCoordinate(b.y - a.y, halfExtent.y));
            float2 result = a + delta * t;
            return new float2(
                LocalPlayerFixedStepSystem.WrapCoordinate(result.x, halfExtent.x),
                LocalPlayerFixedStepSystem.WrapCoordinate(result.y, halfExtent.y));
        }

        /// <summary>단일 스냅샷 프레임 값을 그대로 적용 (버퍼 시작/끝 구간, 보간 불가 시 사용).</summary>
        private void ApplySnapshotFrame(SnapshotFrameHeader header, DynamicBuffer<PlayerSnapshotSample> samples)
        {
            var myIdEntity = SystemAPI.GetSingletonEntity<SimulationConfig>();
            byte myPlayerId = EntityManager.GetComponentData<ClientStateSingleton>(myIdEntity).MyPlayerId;

            for (int i = 0; i < header.SampleCount; i++)
            {
                var sample = samples[header.SampleStartIndex + i];
                if (sample.PlayerId == myPlayerId) continue;

                if (TryFindPlayerEntity(sample.PlayerId, out Entity playerEntity))
                {
                    var rt = EntityManager.GetComponentData<RenderTransform2D>(playerEntity);
                    rt.Position = sample.Position;
                    rt.RotationDeg = sample.RotationDeg;
                    EntityManager.SetComponentData(playerEntity, rt);
                }
            }
        }

        private void InterpolateBetweenFrames(SnapshotFrameHeader h1, SnapshotFrameHeader h2,
            DynamicBuffer<PlayerSnapshotSample> samples, float t, SimulationConfig config)
        {
            var myIdEntity = SystemAPI.GetSingletonEntity<SimulationConfig>();
            byte myPlayerId = EntityManager.GetComponentData<ClientStateSingleton>(myIdEntity).MyPlayerId;

            for (int i = 0; i < h1.SampleCount; i++)
            {
                var s1 = samples[h1.SampleStartIndex + i];
                if (s1.PlayerId == myPlayerId) continue;

                // h2에서 같은 PlayerId를 가진 샘플 탐색 (원본은 Dictionary라 O(1)이지만,
                // 프레임당 플레이어 수가 적으므로 선형 탐색으로도 충분하다).
                for (int j = 0; j < h2.SampleCount; j++)
                {
                    var s2 = samples[h2.SampleStartIndex + j];
                    if (s2.PlayerId != s1.PlayerId) continue;

                    if (TryFindPlayerEntity(s1.PlayerId, out Entity playerEntity))
                    {
                        // 이번 프레임에 막 리스폰한 원격 플레이어는 여기서 보간하지 않는다 —
                        // 아래 ApplyRespawnJumps()가 OnUpdate() 마지막에 최신 ServerConfirmedState로
                        // 강제 스냅하므로, 여기서 계산해봤자 곧바로 덮어써진다. 애초에 s1(죽은
                        // 위치)과 s2(새 스폰 위치) 사이를 보간하면 화면을 가로질러 미끄러지는
                        // 시각적 오류가 생기므로 계산 자체를 건너뛴다.
                        if (EntityManager.HasComponent<JustRespawnedTag>(playerEntity))
                        {
                            break;
                        }

                        var rt = EntityManager.GetComponentData<RenderTransform2D>(playerEntity);
                        // 단순 math.lerp 대신 wrap 경계를 최단 경로로 넘어가는 보간을 쓴다.
                        // 그렇지 않으면 상대방이 벽에서 wrap한 프레임에 화면을 가로질러
                        // 순간이동하는 것처럼 미끄러져 보인다.
                        rt.Position = LerpWrapped(s1.Position, s2.Position, t, config.ServerWorldHalfExtent);
                        rt.RotationDeg = Mathf.LerpAngle(s1.RotationDeg, s2.RotationDeg, t);
                        EntityManager.SetComponentData(playerEntity, rt);
                    }
                    break;
                }
                // found == false: h2 시점엔 사라진 플레이어 — 원본도 이 경우 아무것도 하지 않고 건너뛴다.
            }
        }

        private bool TryFindPlayerEntity(byte id, out Entity result)
        {
            foreach (var (playerId, entity) in SystemAPI.Query<RefRO<PlayerId>>().WithEntityAccess())
            {
                if (playerId.ValueRO.Value == id)
                {
                    result = entity;
                    return true;
                }
            }
            result = Entity.Null;
            return false;
        }
    }
}
