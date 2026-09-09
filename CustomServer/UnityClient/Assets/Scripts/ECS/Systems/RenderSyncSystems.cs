using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;

namespace CustomClient.Dots
{
    /// <summary>
    /// 원본 ApplyShipRotation을 포함해, RenderTransform2D(순수 시뮬레이션 결과)를 실제
    /// Entities Graphics가 그리는 LocalTransform으로 옮기는 System. 모든 시뮬레이션/보간
    /// System 이후, 렌더링 직전에 실행되어야 한다.
    ///
    /// [중요] 회전 변환 규칙은 원본과 동일하게 유지한다:
    ///   Quaternion.Euler(0, 0, -rotationDeg)  (0도=+Y, 시계방향 증가 -> Unity Z축 음수 회전)
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderLast = true)]
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
    /// 원본 UpdateDeathVisual + CreatePlayerObject의 사망 표시 로직을 옮긴 System.
    /// HealthState.IsDead가 바뀐 프레임에만 다시 처리한다 (원본처럼 매 틱 무조건 처리하지 않음).
    ///
    /// [중요] 색상 상수(DeadColor/LocalAliveColor/RemoteAliveColor)는 여전히 public으로 두어
    /// ServerStateApplySystem.FindOrCreatePlayerEntity에서도 재사용한다. 신규 생성된 플레이어
    /// 엔티티가 "이미 죽은 상태로 처음 관측되는" 경우(예: 다른 플레이어가 사망 중일 때 재접속),
    /// IsDead와 WasDead가 똑같이 초기화되어 이 System의 "전환 시에만 처리한다"는 조건에 걸리지
    /// 않아 사망 표시가 누락되는 문제가 있었다 — 그래서 생성 시점에 초기 IsDead 값에 맞는
    /// 색/머티리얼을 미리 정확하게 적용해 두면, 이후 이 System은 "전환 시에만" 갱신해도 항상
    /// 정확하다. 값을 두 곳에 따로 두면 나중에 바꿀 때 한쪽만 고치는 실수가 생기므로,
    /// 반드시 이 상수/헬퍼 메서드를 통해서만 참조해야 한다.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderLast = true)]
    [UpdateBefore(typeof(SyncRenderTransformSystem))]
    public partial class PlayerVisualColorSystem : SystemBase
    {
        public static readonly float4 DeadColor = new float4(0.4f, 0.4f, 0.4f, 1f);
        public static readonly float4 LocalAliveColor = new float4(0f, 1f, 0f, 1f);
        public static readonly float4 RemoteAliveColor = new float4(1f, 0f, 0f, 1f);

        protected override void OnUpdate()
        {
            var graphicsSystem = World.GetExistingSystemManaged<Unity.Rendering.EntitiesGraphicsSystem>();

            // 1) 아직 머티리얼 등록이 안 된 엔티티(RuntimeMaterialIds가 없는)를 찾아 최초 1회
            //    등록한다. DeathMaterialRef는 매니지드 컴포넌트라 RefRO/RefRW로 쿼리 튜플에
            //    섞을 수 없으므로(이 프로젝트의 다른 매니지드 컴포넌트 NetworkConnectionData도
            //    항상 GetComponentObject로 개별 조회하지, 쿼리 튜플에 넣지 않는다 — 그 관례를
            //    그대로 따른다), 쿼리는 구조체 컴포넌트만으로 걸러내고 DeathMaterialRef는
            //    엔티티별로 EntityManager를 통해 따로 확인한다.
            //
            //    [중요] 등록 직후 곧바로 현재 HealthState.IsDead에 맞는 머티리얼을 적용한다.
            //    아래 2)번 루프는 "WasDead와 IsDead가 달라진 프레임에만" 갱신하는데, 방금 막
            //    등록된 엔티티가 "이미 죽은 상태로 처음 관측된" 경우(예: 다른 플레이어가 사망
            //    중일 때 재접속) ServerStateApplySystem.FindOrCreatePlayerEntity가 WasDead를
            //    IsDead와 동일하게 초기화해두므로(리스폰 오탐 방지 목적) 2)번 루프 기준
            //    "전환이 없었다"고 판단되어 영원히 갱신되지 않는다 — 색상 처리에서 이미 한 번
            //    겪은 것과 정확히 같은 함정이라, 여기서도 등록 시점에 바로 한 번 발라 두는
            //    방식으로 막는다.
            foreach (var (materialMeshInfoRW, entity) in
                     SystemAPI.Query<RefRW<MaterialMeshInfo>>()
                         .WithAll<PlayerId>()
                         .WithNone<RuntimeMaterialIds>()
                         .WithEntityAccess())
            {
                if (graphicsSystem == null) continue;
                if (!EntityManager.HasComponent<DeathMaterialRef>(entity)) continue; // 하위 호환 프리팹: 등록할 대상이 없다.

                var deathMatRef = EntityManager.GetComponentObject<DeathMaterialRef>(entity);
                if (deathMatRef.DeadMaterial == null) continue;

                // 현재(베이킹된) 머티리얼을 "생존용"으로 그대로 등록한다. MaterialMeshInfo.Material이
                // 음수(정적 RenderMeshArray 인덱스)로 시작하므로, 그 인덱스가 가리키는 실제 Material
                // 에셋을 RenderMeshArray에서 가져와 등록해야 한다.
                var renderMeshArray = EntityManager.GetSharedComponentManaged<RenderMeshArray>(entity);
                UnityEngine.Material aliveMaterial = renderMeshArray.GetMaterial(materialMeshInfoRW.ValueRO);

                if (aliveMaterial == null) continue; // 방어적: 예상치 못한 베이킹 상태면 등록을 건너뛴다.

                var aliveId = graphicsSystem.RegisterMaterial(aliveMaterial);
                var deadId = graphicsSystem.RegisterMaterial(deathMatRef.DeadMaterial);

                EntityManager.AddComponentData(entity, new RuntimeMaterialIds
                {
                    AliveMaterialID = aliveId,
                    DeadMaterialID = deadId,
                    Initialized = true
                });

                // 등록 직후 현재 사망 상태에 맞는 머티리얼을 바로 적용 (위 주석 참고).
                bool isDeadNow = EntityManager.HasComponent<HealthState>(entity)
                    && EntityManager.GetComponentData<HealthState>(entity).IsDead;
                materialMeshInfoRW.ValueRW.MaterialID = isDeadNow ? deadId : aliveId;
            }

            // 2) 사망 상태가 실제로 바뀐 프레임에만 색/머티리얼을 갱신한다 (원본과 동일한 최적화).
            foreach (var (health, colorRW, materialMeshInfoRW, entity) in
                     SystemAPI.Query<RefRO<HealthState>, RefRW<URPMaterialPropertyBaseColor>, RefRW<MaterialMeshInfo>>()
                         .WithAll<PlayerId>()
                         .WithEntityAccess())
            {
                if (health.ValueRO.IsDead == health.ValueRO.WasDead) continue;

                bool isLocal = EntityManager.HasComponent<LocalPlayerTag>(entity);

                colorRW.ValueRW.Value = health.ValueRO.IsDead
                    ? DeadColor
                    : (isLocal ? LocalAliveColor : RemoteAliveColor);

                // 머티리얼(스프라이트) 자체 교체는 RuntimeMaterialIds가 준비된 엔티티에서만 한다.
                if (EntityManager.HasComponent<RuntimeMaterialIds>(entity))
                {
                    var ids = EntityManager.GetComponentData<RuntimeMaterialIds>(entity);
                    if (ids.Initialized)
                    {
                        materialMeshInfoRW.ValueRW.MaterialID = health.ValueRO.IsDead
                            ? ids.DeadMaterialID
                            : ids.AliveMaterialID;
                    }
                }
            }
        }
    }

    /// <summary>
    /// 원본 SpawnPredictedMissile / ApplyMissileServerState의 색상 규칙을 옮긴 System.
    /// 본인이 쏜 미사일은 노란색(예측 중이든 확정이든 동일), 원격 미사일은 흰색.
    /// 미사일은 사망처럼 상태가 바뀌지 않으므로 생성 시 1회만 칠하면 충분하지만,
    /// 안전하게 매 프레임 재계산해도 비용이 크지 않다.
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
