using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;

namespace DeterministicSample.Dots
{
    /// <summary>
    /// RenderTransform2D를 실제 Entities Graphics가 그리는 LocalTransform으로 옮기는 System.
    /// RenderTransform2D 값은 PhysicsToRenderSyncSystem이 채워 넣는다.
    ///
    /// PhysicsToRenderSyncSystem(물리 -> RenderTransform2D) 이후, 색상 System들과 마찬가지로
    /// 프레임의 가장 마지막에 실행되어야 한다.
    ///
    /// 회전 변환 규칙: Quaternion.Euler(0, 0, -rotationDeg) (0도=+Y, 시계방향 증가 -> Unity
    /// Z축 음수 회전)
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderLast = true)]
    [UpdateAfter(typeof(PhysicsToRenderSyncSystem))]
    public partial class SyncRenderTransformSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            foreach (var (renderTransform2D, localTransform) in
                     SystemAPI.Query<RefRO<RenderTransform2D>, RefRW<LocalTransform>>())
            {
                var rt2d = renderTransform2D.ValueRO;
                localTransform.ValueRW.Position = new float3(rt2d.Position.x, rt2d.Position.y, 0f);
                localTransform.ValueRW.Rotation = quaternion.Euler(0f, 0f, math.radians(-rt2d.RotationDeg));
            }
        }
    }

    /// <summary>
    /// 플레이어 사망/생존 색상을 관리하는 System. HealthState.IsDead가 SimulationTickSystem에
    /// 의해 바뀐 프레임에만 색을 다시 칠한다.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderLast = true)]
    [UpdateBefore(typeof(SyncRenderTransformSystem))]
    public partial class PlayerVisualColorSystem : SystemBase
    {
        private static readonly float4 DeadColor = new float4(0.4f, 0.4f, 0.4f, 1f);
        private static readonly float4 LocalAliveColor = new float4(0f, 1f, 0f, 1f);
        private static readonly float4 RemoteAliveColor = new float4(1f, 0f, 0f, 1f);

        protected override void OnUpdate()
        {
            foreach (var (health, colorRW, entity) in
                     SystemAPI.Query<RefRO<HealthState>, RefRW<URPMaterialPropertyBaseColor>>()
                         .WithAll<PlayerId>()
                         .WithEntityAccess())
            {
                // 이전 프레임과 사망 상태가 같으면 다시 칠하지 않는다.
                if (health.ValueRO.IsDead == health.ValueRO.WasDead) continue;

                bool isLocal = EntityManager.HasComponent<LocalPlayerTag>(entity);

                colorRW.ValueRW.Value = health.ValueRO.IsDead
                    ? DeadColor
                    : (isLocal ? LocalAliveColor : RemoteAliveColor);
            }
        }
    }

    /// <summary>
    /// 미사일 색상 규칙: 본인이 쏜 미사일은 노란색, 원격 미사일은 흰색.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderLast = true)]
    [UpdateBefore(typeof(SyncRenderTransformSystem))]
    public partial class MissileVisualColorSystem : SystemBase
    {
        private static readonly float4 OwnMissileColor = new float4(1f, 1f, 0f, 1f); // Color.yellow
        private static readonly float4 OtherMissileColor = new float4(1f, 1f, 1f, 1f); // Color.white

        protected override void OnCreate()
        {
            RequireForUpdate<SimulationConfig>();
            RequireForUpdate<ClientStateSingleton>();
        }

        protected override void OnUpdate()
        {
            byte myPlayerId = SystemAPI.GetSingleton<ClientStateSingleton>().MyPlayerId;

            foreach (var (owner, colorRW) in
                     SystemAPI.Query<RefRO<MissileOwner>, RefRW<URPMaterialPropertyBaseColor>>().WithAll<MissileTag>())
            {
                colorRW.ValueRW.Value = (owner.ValueRO.PlayerId == myPlayerId) ? OwnMissileColor : OtherMissileColor;
            }
        }
    }
}
