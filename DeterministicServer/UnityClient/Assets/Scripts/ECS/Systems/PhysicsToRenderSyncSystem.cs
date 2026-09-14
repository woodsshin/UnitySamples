using Unity.Entities;

namespace DeterministicSample.Dots
{
    /// <summary>
    /// TankPhysicsState(탱크의 유일한 물리 진실)와 MissileMotion(미사일의 유일한 물리 진실)을
    /// RenderTransform2D(렌더링이 최종적으로 읽는 값)로 동기화한다.
    ///
    /// SimulationTickSystem은 서버 TickCommit이 도착했을 때만 실행되고, 물리 진실
    /// (TankPhysicsState/MissileMotion)도 그때만 갱신된다. 이 System은 매 프레임
    /// (SimulationSystemGroup, 가변 프레임)마다 실행되어 가장 최근에 확정된 물리 값을
    /// RenderTransform2D로 그대로 복사하는 별도 동기화 단계다.
    ///
    /// 보간은 하지 않는다. 서버가 확정한 여러 틱이 한 렌더 프레임 사이에 몰려 도착하면
    /// SimulationTickSystem이 그 틱들을 순서대로 전부 처리하고, 이 System은 그 프레임
    /// 시점의 최종 물리 값만 그대로 복사한다 — 중간 틱의 위치는 화면에 그려지지 않고
    /// 건너뛰어진다. 로컬 PC 환경(지연/병목 거의 없음) 전제상 이 현상은 드물고, 발생해도
    /// 한 프레임 정도의 미미한 위치 점프이므로 보간 없이 무시한다.
    ///
    /// 반드시 SimulationTickSystem 이후, PlayerVisualColorSystem/MissileVisualColorSystem/
    /// SyncRenderTransformSystem(RenderTransform2D -> LocalTransform) 이전에 실행되어야 한다.
    ///
    /// [정렬 안전장치] SyncRenderTransformSystem 등은 [UpdateInGroup(..., OrderLast = true)]로
    /// OrderLast 묶음에 속한다. 이 System은 OrderLast가 아니므로 일반적으로 그 묶음보다 먼저
    /// 실행되지만, 이는 Unity 정렬 알고리즘의 묶음 배치 규칙에 기대는 것이지 명시적 보장이
    /// 아니다. RenderTransform2D를 쓰는 쪽이 이 System보다 먼저 실행되면 그 프레임은 한 틱
    /// 오래된 값을 렌더링하게 되므로, UpdateBefore로 관계를 명시해 정렬 규칙 변경에도 안전
    /// 하게 고정한다.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(SimulationTickSystem))]
    [UpdateBefore(typeof(SyncRenderTransformSystem))]
    [UpdateBefore(typeof(PlayerVisualColorSystem))]
    [UpdateBefore(typeof(MissileVisualColorSystem))]
    public partial class PhysicsToRenderSyncSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            foreach (var (physics, renderTransform) in
                     SystemAPI.Query<RefRO<TankPhysicsState>, RefRW<RenderTransform2D>>().WithAll<PlayerId>())
            {
                renderTransform.ValueRW.Position = physics.ValueRO.Position;
                renderTransform.ValueRW.RotationDeg = physics.ValueRO.RotationDeg;
            }

            foreach (var (motion, renderTransform) in
                     SystemAPI.Query<RefRO<MissileMotion>, RefRW<RenderTransform2D>>().WithAll<MissileTag>())
            {
                renderTransform.ValueRW.Position = motion.ValueRO.Position;
                renderTransform.ValueRW.RotationDeg = motion.ValueRO.RotationDeg;
            }
        }
    }
}
