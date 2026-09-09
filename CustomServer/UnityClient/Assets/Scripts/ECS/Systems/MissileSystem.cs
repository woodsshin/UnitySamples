using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace CustomClient.Dots
{
    /// <summary>
    /// 원본 ApplyMissileServerState + UpdatePredictedMissiles를 옮긴 System.
    /// 원본 순서(Update() 내부): ApplyMissileServerState(큐 소비) → ... → UpdatePredictedMissiles(dt).
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(ServerStateApplySystem))]
    public partial class MissileSystem : SystemBase
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
            byte myPlayerId = EntityManager.GetComponentData<ClientStateSingleton>(configEntity).MyPlayerId;

            while (netData.MissileStateQueue.TryDequeue(out var frame))
            {
                ApplyMissileServerState(frame.Missiles, myPlayerId);
            }

            UpdatePredictedMissiles(config, UnityEngine.Time.deltaTime);

            // MissileMotion(논리 위치/회전)을 RenderTransform2D(렌더링이 실제로 읽는 값)로 반영한다.
            // 미사일은 탱크와 달리 렌더 오프셋 스무딩이 없으므로 그대로 복사하면 된다.
            foreach (var (motion, renderTransform) in SystemAPI.Query<RefRO<MissileMotion>, RefRW<RenderTransform2D>>().WithAll<MissileTag>())
            {
                renderTransform.ValueRW.Position = motion.ValueRO.Position;
                renderTransform.ValueRW.RotationDeg = motion.ValueRO.RotationDeg;
            }
        }

        // ---------------------------------------------------------------
        // 원본 ApplyMissileServerState
        // ---------------------------------------------------------------
        private void ApplyMissileServerState(MissileStateWire[] serverMissiles, byte myPlayerId)
        {
            var activeServerIds = new NativeHashSet<ushort>(serverMissiles.Length, Allocator.Temp);

            foreach (var m in serverMissiles)
            {
                activeServerIds.Add(m.Id);

                if (TryFindMissileByServerId(m.Id, out Entity existing))
                {
                    var motion = EntityManager.GetComponentData<MissileMotion>(existing);
                    motion.Position = new float2(m.X, m.Y);
                    EntityManager.SetComponentData(existing, motion);
                    continue;
                }

                // 아직 이 서버 ID에 대응하는 엔티티가 없다 -> 신규.
                // 본인이 쏜 미사일이면 아직 서버 매칭 안 된 예측 미사일 중 최근접을 찾아 재사용한다.
                Entity matched = Entity.Null;
                if (m.OwnerId == myPlayerId)
                {
                    matched = FindClosestPendingLocalMissile(new float2(m.X, m.Y));
                }

                if (matched != Entity.Null)
                {
                    EntityManager.RemoveComponent<PredictedMissileTag>(matched);
                    EntityManager.RemoveComponent<PredictedMissileSpawnTime>(matched);
                    EntityManager.RemoveComponent<PredictedMissileDistance>(matched);
                    EntityManager.SetComponentData(matched, new MissileId { Value = m.Id, HasServerId = true });
                    var motion = EntityManager.GetComponentData<MissileMotion>(matched);
                    motion.Position = new float2(m.X, m.Y);
                    EntityManager.SetComponentData(matched, motion);
                }
                else
                {
                    // 원격 플레이어의 미사일이거나, 예측을 만들지 못한 본인 미사일 -> 신규 생성.
                    var prefabs = SystemAPI.GetSingleton<RenderPrefabs>();
                    Entity newMissile = EntityManager.Instantiate(prefabs.MissilePrefab);

                    EntityManager.AddComponentData(newMissile, new MissileTag());
                    EntityManager.AddComponentData(newMissile, new MissileId { Value = m.Id, HasServerId = true });
                    EntityManager.AddComponentData(newMissile, new MissileOwner { PlayerId = m.OwnerId });
                    EntityManager.AddComponentData(newMissile, new MissileMotion { Position = new float2(m.X, m.Y), RotationDeg = m.Rotation });
                    EntityManager.SetComponentData(newMissile, new RenderTransform2D { Position = new float2(m.X, m.Y), RotationDeg = m.Rotation });
                }
            }

            // 서버 목록에서 사라진 미사일(사거리 소진 또는 탱크 충돌로 삭제) 제거. 단, 아직 서버
            // 매칭이 안 된 예측 미사일(HasServerId == false)은 이 목록과 무관하므로 절대 여기서
            // 지우면 안 된다 (원본도 _missileViews만 순회하고 _pendingLocalMissiles는 건드리지 않는다).
            var toDestroy = new NativeList<Entity>(Allocator.Temp);
            foreach (var (missileId, entity) in SystemAPI.Query<RefRO<MissileId>>().WithAll<MissileTag>().WithEntityAccess())
            {
                if (missileId.ValueRO.HasServerId && !activeServerIds.Contains(missileId.ValueRO.Value))
                {
                    toDestroy.Add(entity);
                }
            }
            for (int i = 0; i < toDestroy.Length; i++)
            {
                EntityManager.DestroyEntity(toDestroy[i]);
            }
            toDestroy.Dispose();
            activeServerIds.Dispose();
        }

        private bool TryFindMissileByServerId(ushort serverId, out Entity result)
        {
            foreach (var (missileId, entity) in SystemAPI.Query<RefRO<MissileId>>().WithAll<MissileTag>().WithEntityAccess())
            {
                if (missileId.ValueRO.HasServerId && missileId.ValueRO.Value == serverId)
                {
                    result = entity;
                    return true;
                }
            }
            result = Entity.Null;
            return false;
        }

        /// <summary>원본 FindClosestPendingLocalMissile: 아직 서버 미확정인 예측 미사일 중 최근접.</summary>
        private Entity FindClosestPendingLocalMissile(float2 serverPos)
        {
            Entity best = Entity.Null;
            float bestDistSqr = float.MaxValue;

            foreach (var (motion, entity) in SystemAPI.Query<RefRO<MissileMotion>>().WithAll<PredictedMissileTag>().WithEntityAccess())
            {
                float distSqr = math.lengthsq(motion.ValueRO.Position - serverPos);
                if (distSqr < bestDistSqr)
                {
                    bestDistSqr = distSqr;
                    best = entity;
                }
            }

            return best;
        }

        /// <summary>
        /// 원본 UpdatePredictedMissiles: 아직 서버 확정 안 된 예측 미사일을 직접 전진시키고,
        /// 다음 두 조건 중 하나라도 만족하면 스스로 제거한다:
        /// 1) 누적 이동 거리가 config.MissileMaxDistance에 도달 — 서버도 같은 조건으로 이 미사일을
        ///    이미(또는 곧) 지울 것이므로, 사거리 끝에서 예측을 미리 정리해 서버 확정을 기다리는
        ///    동안 미사일이 화면 밖으로 계속 날아가 보이는 것을 막는다.
        /// 2) 타임아웃(config.PredictedMissileTimeoutSeconds) 초과 — 패킷 유실 등으로 서버가
        ///    스폰 자체를 못 받았거나 즉시 삭제한 경우의 안전장치. 정상 케이스에서는 조건 1이
        ///    먼저 발동하므로 이 타임아웃은 항상 (MissileMaxDistance / MissileSpeed)보다 넉넉히
        ///    길게 설정되어 있어야 한다(SimulationConfigAuthoring 주석 참고).
        /// </summary>
        private void UpdatePredictedMissiles(SimulationConfig config, float dt)
        {
            var toDestroy = new NativeList<Entity>(Allocator.Temp);
            float stepDistance = config.MissileSpeed * dt;

            foreach (var (motion, spawnTime, distance, entity) in
                     SystemAPI.Query<RefRW<MissileMotion>, RefRO<PredictedMissileSpawnTime>, RefRW<PredictedMissileDistance>>()
                         .WithAll<PredictedMissileTag>()
                         .WithEntityAccess())
            {
                if (UnityEngine.Time.time - spawnTime.ValueRO.Value > config.PredictedMissileTimeoutSeconds)
                {
                    toDestroy.Add(entity);
                    continue;
                }

                if (distance.ValueRO.Value >= config.MissileMaxDistance)
                {
                    toDestroy.Add(entity);
                    continue;
                }

                float rotRad = motion.ValueRO.RotationDeg * Mathf.Deg2Rad;
                float2 dir = new float2(Mathf.Sin(rotRad), Mathf.Cos(rotRad));
                motion.ValueRW.Position += dir * stepDistance;
                distance.ValueRW.Value += stepDistance;
            }

            for (int i = 0; i < toDestroy.Length; i++)
            {
                EntityManager.DestroyEntity(toDestroy[i]);
            }
            toDestroy.Dispose();
        }
    }
}
