using System.Collections.Generic;
using Game.Networking;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace DeterministicSample.Dots
{
    /// <summary>
    /// 클라이언트 시뮬레이션의 핵심 System. 로컬/원격 구분 없이, 모든 플레이어(PlayerId
    /// 오름차순)가 동일한 코드경로를 거쳐 이동/발사/충돌/사망/리스폰을 계산한다. 이것이
    /// 성립하려면 모든 클라이언트가 정확히 같은 입력을 정확히 같은 순서로 재생해야 하며, 그
    /// 순서 규약은 다음과 같다:
    ///
    ///   한 TickCommit 처리 순서: Join(JoinSequence 오름차순) -> Leave(PlayerId 오름차순)
    ///                          -> 이동/발사 판정(PlayerId 오름차순) -> 미사일 이동/충돌 판정
    ///                          (OwnerId 오름차순, 동률이면 FireTick 오름차순)
    ///
    /// 신규 클라이언트는 JoinResponse.ExistingPlayers로 이미 접속해 있던 플레이어 전원의
    /// (PlayerId, JoinSequence) 목록을 받아, TickCommit 처리를 시작하기 전에 먼저 이들을
    /// 스폰한다(SpawnExistingPlayersOnConnect). 이 절차가 없으면 늦게 접속한 클라이언트가
    /// 기존 플레이어를 보지 못하거나, 같은 플레이어를 서로 다른 클라이언트가 서로 다른 스폰
    /// 위치로 계산하게 된다.
    ///
    /// 이 System은 독자적인 고정 틱 타이머를 갖지 않는다. SimulationSystemGroup(가변
    /// 프레임)에서 매 프레임 실행되며, 이번 프레임에 새로 도착한 TickCommit들을 모아 그
    /// 개수만큼 순서대로 반복 실행한다. 서버가 TickCommit을 보내주지 않으면 이 System은
    /// 아무 틱도 진행시키지 않는다.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial class SimulationTickSystem : SystemBase
    {
        // 스폰 슬롯 배정 상수.
        private const float SPAWN_RADIUS = 1.2f;
        private const int SPAWN_SLOTS = 16;

        // 미사일/탱크 충돌 판정 반지름.
        private const float MISSILE_RADIUS = 0.06f;
        private const float TANK_COLLISION_RADIUS = 0.175f;

        // 최대 체력. GameHudBridge.MAX_HEALTH와 반드시 같은 값이어야 한다.
        private const int MAX_HEALTH = 10;

        protected override void OnCreate()
        {
            RequireForUpdate<SimulationConfig>();
            RequireForUpdate<ClientStateSingleton>();
            RequireForUpdate<RenderPrefabs>();
            RequireForUpdate<NetworkConnectionData>();
        }

        protected override void OnUpdate()
        {
            var configEntity = SystemAPI.GetSingletonEntity<SimulationConfig>();
            var config = EntityManager.GetComponentData<SimulationConfig>(configEntity);
            var netData = EntityManager.GetComponentObject<NetworkConnectionData>(configEntity);
            var clientState = EntityManager.GetComponentData<ClientStateSingleton>(configEntity);

            var uiEventSingleton = GetOrCreateUiEventSingleton();

            bool anyTickProcessed = false;

            // TickCommit 큐 처리보다 반드시 먼저 실행되어야 한다. NetworkConnectionSystem이
            // JoinResponse를 받아 여기 넘겨준 "이미 접속해 있던 플레이어 목록"을 스폰한다.
            // 이를 먼저 끝내지 않으면, 뒤이어 처리할 TickCommit.Inputs에 그 플레이어들의
            // 입력이 섞여 와도 TryFindPlayerEntity가 실패해 무시된다.
            if (netData.UnprocessedExistingPlayers != null)
            {
                SpawnExistingPlayersOnConnect(netData.UnprocessedExistingPlayers, clientState.MyPlayerId, uiEventSingleton);
                netData.UnprocessedExistingPlayers = null;
            }

            // 이번 프레임에 새로 도착한 TickCommit을 전부 소비한다. 서버는 틱 오름차순으로
            // 순서대로 보내므로(ServerLoop이 순차 실행), 큐에서 꺼내는 순서가 곧 처리 순서다.
            while (netData.TickCommitQueue.TryDequeue(out TickCommitData commit))
            {
                // 이번 틱의 물리 계산(Join/Leave/Input/Missile/Respawn)을 시작하기 전에
                // 먼저 TotalElapsedSimTime에 이번 틱의 DeltaTimeSeconds를 더한다 — 발사
                // 쿨다운/리스폰 판정은 이 틱 시점의 누적 경과 시간을 기준으로 하므로, 이번
                // 틱에 흐른 시간이 반영된 이후에 판정이 이루어져야 한다. 모든 클라이언트가
                // 매 틱 동일한 순서로 누적하므로 결정론이 유지된다.
                clientState.TotalElapsedSimTime += commit.DeltaTimeSeconds;

                // GameStarted는 엣지 신호(그 틱에서만 true)이므로, 관측한 즉시 영속적인
                // IsGameStarted로 래치한다. 반드시 ProcessOneTick 호출보다 먼저 해야 한다 —
                // 그래야 게임이 시작된 바로 그 틱에 실린 이동/발사 입력이 같은 틱 안에서
                // 곧바로 반영된다.
                if (commit.GameStarted)
                {
                    clientState.IsGameStarted = true;
                }

                ProcessOneTick(commit, config, uiEventSingleton, ref clientState);
                clientState.LastConfirmedTick = commit.Tick;
                anyTickProcessed = true;
            }

            if (anyTickProcessed)
            {
                EntityManager.SetComponentData(configEntity, clientState);
            }
        }

        /// <summary>
        /// JoinResponse.ExistingPlayers를 처리한다. JoinSequence 오름차순으로 순회하며
        /// SpawnPlayer를 그대로 호출한다 — TickCommit.JoinedPlayers를 처리하는 것과 완전히
        /// 동일한 경로다. 같은 함수(SpawnPlayer -> ComputeSpawnTransform)를 재사용해야만,
        /// 먼저 접속해 있던 다른 클라이언트가 이 플레이어들을 스폰했을 때와 정확히 동일한
        /// 슬롯 배정 결과가 보장된다.
        /// </summary>
        private void SpawnExistingPlayersOnConnect(JoinedPlayerInfo[] existingPlayers, byte myPlayerId, Entity uiEventSingleton)
        {
            // 서버가 이미 JoinSequence 오름차순으로 정렬해서 보내지만, 프로토콜 계약에 의존하지
            // 않고 이 System 스스로도 재확인한다(다른 Join 처리 지점과 동일한 방어적 습관).
            var sorted = new List<JoinedPlayerInfo>(existingPlayers);
            sorted.Sort((a, b) => a.JoinSequence.CompareTo(b.JoinSequence));

            foreach (var info in sorted)
            {
                SpawnPlayer(info.PlayerId, info.JoinSequence, myPlayerId, uiEventSingleton);
            }
        }

        /// <summary>
        /// TickCommit 하나를 처리 순서 규약대로 실행한다: Join -> Leave -> Input(이동/발사)
        /// -> 미사일 이동/충돌. 이 순서를 절대 바꾸지 않는다 — 순서 자체가 결정론의 일부다.
        /// </summary>
        private void ProcessOneTick(TickCommitData commit, SimulationConfig config, Entity uiEventSingleton,
            ref ClientStateSingleton clientState)
        {
            int tick = commit.Tick;

            // 1) Join (JoinSequence 오름차순 — 서버가 이미 정렬해서 보내지만, 프로토콜 계약에
            //    의존하지 않고 이 System 스스로도 재확인한다. 재정렬 비용은 무시할 수 있는
            //    수준이고, 그 대가로 서버 구현이 바뀌어도 클라이언트가 안전하다. PlayerId
            //    오름차순은 실제 접속 순서와 항상 일치한다는 보장이 없으므로(재접속 등)
            //    정렬 기준으로 사용하지 않는다.)
            var sortedJoins = new List<JoinedPlayerInfo>(commit.JoinedPlayers);
            sortedJoins.Sort((a, b) => a.JoinSequence.CompareTo(b.JoinSequence));
            foreach (var info in sortedJoins)
            {
                SpawnPlayer(info.PlayerId, info.JoinSequence, clientState.MyPlayerId, uiEventSingleton);
            }

            // 2) Leave (PlayerId 오름차순)
            var sortedLeaves = new List<byte>(commit.LeftPlayerIds);
            sortedLeaves.Sort();
            foreach (byte playerId in sortedLeaves)
            {
                RemovePlayer(playerId, uiEventSingleton);
            }

            // 게임이 아직 시작되지 않았으면(로비) 여기서 멈춘다. Join/Leave는 로비에서도
            // 항상 반영되어야 하므로(플레이어는 로비에서도 스폰되어 서로를 볼 수 있어야
            // 한다) 이미 위에서 처리했다 — 하지만 이동/발사/미사일/리스폰(3~5단계)은
            // 호스트가 StartGameRequest를 보내 서버가 TickCommit.GameStarted를 방송하기
            // 전까지 실행하지 않는다. 플레이어는 로비에서 스폰된 자리에 가만히 서서
            // 대기한다. clientState.IsGameStarted는 이 함수 호출 직전(OnUpdate)에 이미
            // 이번 틱의 commit.GameStarted 여부까지 반영된 최신 값이므로, 게임이 시작되는
            // 바로 그 틱의 입력도 여기서 즉시 통과한다.
            if (!clientState.IsGameStarted) return;

            // 3) 이동/발사 판정 (PlayerId 오름차순)
            //    dt(이동 계산용)는 서버가 실측해 보낸 commit.DeltaTimeSeconds를 그대로 쓴다.
            //    elapsedTime(쿨다운 판정용)은 방금 OnUpdate에서 갱신을 마친
            //    clientState.TotalElapsedSimTime — 이 틱의 DeltaTimeSeconds가 이미 반영된 값이다.
            var sortedInputs = new List<TickPlayerInput>(commit.Inputs);
            sortedInputs.Sort((a, b) => a.PlayerId.CompareTo(b.PlayerId));
            foreach (var input in sortedInputs)
            {
                ProcessPlayerTick(input, tick, commit.DeltaTimeSeconds, clientState.TotalElapsedSimTime, config, uiEventSingleton);
            }

            // 4) 미사일 이동/사거리 소진/충돌 판정 (OwnerId 오름차순, 동률이면 FireTick 오름차순)
            ProcessMissiles(tick, commit.DeltaTimeSeconds, clientState.TotalElapsedSimTime, config, uiEventSingleton);

            // 5) 리스폰 판정 — 사망 중인 모든 플레이어를 PlayerId 오름차순으로 검사.
            //    [중요] 반드시 미사일 충돌 판정(4번) 이후에 실행해야 한다. 이번 틱에 막
            //    사망한 플레이어가 같은 틱에 리스폰 조건(RespawnDelaySeconds <= 0)을 만족하는
            //    극단적인 설정이 아니라면 순서가 결과에 영향을 주지 않지만, 원칙적으로
            //    "이번 틱에 확정된 사망 상태"를 기준으로 판정해야 하므로 사망 판정 다음에 둔다.
            ProcessRespawns(clientState.TotalElapsedSimTime, config, uiEventSingleton);
        }

        // -------------------------------------------------------------------
        // Join / Leave
        // -------------------------------------------------------------------

        /// <summary>
        /// 신규 플레이어를 스폰한다. 스폰 위치는 ComputeSpawnTransform으로 계산하며, 현재
        /// 살아있는(이미 스폰된) 플레이어 집합의 SpawnSlotState를 근거로 빈 슬롯을 찾는다.
        ///
        /// 서버 구현상 Join이 확정된 바로 그 틱부터 InputCount 목록에도 이 플레이어가
        /// 포함되며 값은 중립(Throttle=0, Turn=0, Fire=false)이다 — 그래서 이 함수(Join 처리,
        /// 순서 규약 1번)는 반드시 이동/발사 판정(3번)보다 먼저 끝나야 한다. 그래야 3번에서
        /// 이 PlayerId의 입력을 만났을 때 대응하는 엔티티가 이미 존재한다. 결과적으로는 이동
        /// 없이 스폰 위치 그대로 첫 틱을 마친다.
        ///
        /// joinSequence는 JoinedPlayerInfo.JoinSequence를 그대로 받아 JoinSequenceState에
        /// 영속시킨다 — 현재 방장이 누구인지 계산하려면 각 플레이어 엔티티가 자신의 참가
        /// 순서를 들고 있어야 한다(NetworkComponents.cs의 JoinSequenceState 참고).
        /// </summary>
        private void SpawnPlayer(byte playerId, long joinSequence, byte myPlayerId, Entity uiEventSingleton)
        {
            // 이미 존재하면(재전송 등으로 중복 도착) 건너뛴다.
            foreach (var (existingId, _) in SystemAPI.Query<RefRO<PlayerId>>().WithEntityAccess())
            {
                if (existingId.ValueRO.Value == playerId) return;
            }

            (float2 spawnPos, float spawnRot, int slot) = ComputeSpawnTransform();

            bool isLocal = (playerId == myPlayerId);
            var prefabs = SystemAPI.GetSingleton<RenderPrefabs>();
            Entity prefab = isLocal ? prefabs.LocalShipPrefab : prefabs.RemoteShipPrefab;

            Entity entity = EntityManager.Instantiate(prefab);

            EntityManager.AddComponentData(entity, new PlayerId { Value = playerId });
            if (isLocal)
            {
                EntityManager.AddComponentData(entity, new LocalPlayerTag());
            }
            else
            {
                EntityManager.AddComponentData(entity, new RemotePlayerTag());
            }

            EntityManager.AddComponentData(entity, new TankPhysicsState
            {
                Position = spawnPos,
                RotationDeg = spawnRot,
                Speed = 0f
            });
            // [주의] 여기만 SetComponentData다 — AddComponentData가 아니다. RenderTransform2D는
            // ShipRenderAuthoring.Baker가 프리팹 베이킹 시점에 이미 AddComponent로 붙여두므로,
            // EntityManager.Instantiate(prefab)로 만든 이 엔티티는 이미 그 컴포넌트를 갖고
            // 있다 — 값만 스폰 위치로 덮어써야 하므로 Set을 쓴다. 실수로 Add로 바꾸면 "이미
            // 존재하는 컴포넌트를 다시 추가하려 함" 런타임 예외가 난다.
            EntityManager.SetComponentData(entity, new RenderTransform2D { Position = spawnPos, RotationDeg = spawnRot });
            EntityManager.AddComponentData(entity, new HealthState { Health = MAX_HEALTH, IsDead = false, WasDead = false });
            EntityManager.AddComponentData(entity, new ScoreboardEntry { Kills = 0, Deaths = 0 });
            EntityManager.AddComponentData(entity, new SpawnSlotState { Value = slot });
            EntityManager.AddComponentData(entity, new JoinSequenceState { Value = joinSequence });
            // 아직 발사한 적 없음/사망한 적 없음을 표현하는 충분히 작은 값. 감산 결과가 항상
            // 큰 양수(=쿨다운 없음)가 되도록 float.MinValue를 그대로 쓰지 않고 절반 정도의
            // 여유를 둔다.
            EntityManager.AddComponentData(entity, new LastFireElapsedTime { Value = float.MinValue / 2f });
            EntityManager.AddComponentData(entity, new DeathElapsedTime { Value = float.MinValue / 2f });
            EntityManager.AddBuffer<TickInputHistoryElement>(entity);

            AppendUiEvent(uiEventSingleton, new PlayerJoinedElement { PlayerId = playerId, IsLocal = isLocal });
        }

        private void RemovePlayer(byte playerId, Entity uiEventSingleton)
        {
            // SystemAPI.Query(...).WithEntityAccess() foreach로 순회하는 동안에는
            // EntityManager.DestroyEntity 같은 구조적 변경을 호출할 수 없다 — Unity Entities가
            // "Structural changes are not allowed while iterating over entities" 예외를 던진다.
            // 반드시 먼저 순회를 완전히 끝내 대상 엔티티를 찾아두고(TryFindPlayerEntity), 그
            // 결과를 갖고 루프 밖에서 삭제해야 한다 — SpawnPlayer의 중복 체크 등 다른
            // 헬퍼들과 동일한 "먼저 찾고, 나중에 바꾼다" 패턴이다.
            if (!TryFindPlayerEntity(playerId, out Entity entity)) return;

            AppendUiEvent(uiEventSingleton, new PlayerRemovedElement { PlayerId = playerId });
            EntityManager.DestroyEntity(entity);
        }

        /// <summary>
        /// 현재 존재하는 모든 플레이어 엔티티의 SpawnSlotState를 점유 슬롯 집합으로 삼아 빈
        /// 슬롯을 찾는다. HashSet.Contains 검사 + 슬롯 번호 오름차순 탐색이므로, 호출 시점의
        /// 점유 슬롯 집합만 같다면 순회 순서와 무관하게 항상 같은 결과를 낸다. 다만 누가 새로
        /// 스폰되어 이 함수를 호출하는가의 순서(Join 처리 순서)는 SpawnPlayer 호출부에서
        /// JoinSequence 오름차순으로 고정되어 있어야 한다.
        ///
        /// 이 전제가 클라이언트 간에 일치하려면, 모든 클라이언트가 지금까지 참가한 모든
        /// 플레이어를 정확히 같은 순서로 재생해 정확히 같은 엔티티 집합을 갖고 있어야 한다.
        /// 신규 접속자는 JoinResponse.ExistingPlayers(SpawnExistingPlayersOnConnect)로 자신보다
        /// 먼저 참가한 플레이어 전원을 TickCommit 처리 시작 전에 먼저 스폰하므로, 그 이후로는
        /// 모든 클라이언트가 동일한 Join 이력을 공유한다. 이 초기 동기화 절차가 없으면 늦게
        /// 접속한 클라이언트의 점유 슬롯 집합이 다른 클라이언트보다 작아, 이 함수가
        /// 클라이언트마다 다른 결과를 내게 된다.
        /// </summary>
        private (float2 position, float rotationDeg, int slot) ComputeSpawnTransform()
        {
            var occupiedSlots = new HashSet<int>();
            foreach (var (slot, _) in SystemAPI.Query<RefRO<SpawnSlotState>>().WithEntityAccess())
            {
                occupiedSlots.Add(slot.ValueRO.Value);
            }

            int chosenSlot = 0;
            for (int i = 0; i < SPAWN_SLOTS; i++)
            {
                if (!occupiedSlots.Contains(i))
                {
                    chosenSlot = i;
                    break;
                }
                chosenSlot = i;
            }

            float angleDeg = chosenSlot * (360f / SPAWN_SLOTS);
            float angleRad = angleDeg * Mathf.Deg2Rad;

            float x = Mathf.Sin(angleRad) * SPAWN_RADIUS;
            float y = Mathf.Cos(angleRad) * SPAWN_RADIUS;

            float rotationDeg = angleDeg;

            return (new float2(x, y), rotationDeg, chosenSlot);
        }

        // -------------------------------------------------------------------
        // 이동 / 발사
        // -------------------------------------------------------------------

        private void ProcessPlayerTick(TickPlayerInput input, int tick, float dt, float elapsedTime,
            SimulationConfig config, Entity uiEventSingleton)
        {
            if (!TryFindPlayerEntity(input.PlayerId, out Entity entity)) return;

            // 입력 이력 버퍼에 원본 기록 (디버깅/사후 검증용). 보관 기간을 넘은 오래된 항목은
            // 여기서 함께 정리한다.
            AppendInputHistory(entity, tick, input, config.InputHistoryRetentionTicks);

            var health = EntityManager.GetComponentData<HealthState>(entity);

            // 사망한 플레이어는 이동/회전/발사를 전혀 반영하지 않는다. 별도의
            // LastProcessedTick 필드는 필요 없다 — 틱 진행 자체가 TickCommit 도착에
            // 종속되기 때문이다.
            if (health.IsDead) return;

            var physics = EntityManager.GetComponentData<TankPhysicsState>(entity);

            float throttle = math.clamp(input.Throttle, -1f, 1f);
            float turn = math.clamp(input.Turn, -1f, 1f);

            SimulateTankStep(config, ref physics.Position, ref physics.RotationDeg, ref physics.Speed, throttle, turn, dt);
            EntityManager.SetComponentData(entity, physics);

            // 발사 쿨다운 판정 — 반드시 서버 실측 누적 시간(elapsedTime) 기반이다. 서버
            // 실측 dt가 매 틱 달라질 수 있어 "N틱 지남"은 일정한 시간을 보장하지 않으므로,
            // "마지막 발사 이후 누적 경과 시간 >= FireCooldownSeconds"로 판정한다.
            var lastFire = EntityManager.GetComponentData<LastFireElapsedTime>(entity);

            if (input.Fire && (elapsedTime - lastFire.Value) >= config.FireCooldownSeconds)
            {
                EntityManager.SetComponentData(entity, new LastFireElapsedTime { Value = elapsedTime });
                SpawnMissile(input.PlayerId, tick, physics.Position, physics.RotationDeg);
            }
        }

        /// <summary>
        /// 이동 계산 순서: 1) 회전 2) 목표 속도로 가감속 3) 이동 4) 위치만 경계 wrap. 모든
        /// 클라이언트가 동일한 공식과 순서로 계산해야 결정론이 유지된다.
        /// </summary>
        private static void SimulateTankStep(SimulationConfig config, ref float2 pos, ref float rotDeg, ref float speed,
            float throttle, float turn, float dt)
        {
            rotDeg += turn * config.TurnSpeedDeg * dt;
            rotDeg %= 360f;
            if (rotDeg < 0f) rotDeg += 360f;

            float targetSpeed = throttle >= 0f ? throttle * config.MaxForwardSpeed : throttle * config.MaxReverseSpeed;
            float speedDelta = config.Acceleration * dt;
            if (speed < targetSpeed)
            {
                speed = math.min(speed + speedDelta, targetSpeed);
            }
            else if (speed > targetSpeed)
            {
                speed = math.max(speed - speedDelta, targetSpeed);
            }

            float rotRad = rotDeg * Mathf.Deg2Rad;
            float2 dir = new float2(Mathf.Sin(rotRad), Mathf.Cos(rotRad));
            pos += dir * speed * dt;

            pos.x = WrapCoordinate(pos.x, config.ServerWorldHalfExtent.x);
            pos.y = WrapCoordinate(pos.y, config.ServerWorldHalfExtent.y);
        }

        /// <summary>
        /// 좌표 하나를 [-halfExtent, +halfExtent) 범위로 순환(wrap)시킨다. 이 System이 유일한
        /// 구현이며, 모든 클라이언트가 이 공식을 동일하게 사용해야 결정론이 유지된다.
        /// </summary>
        public static float WrapCoordinate(float value, float halfExtent)
        {
            float range = halfExtent * 2f;
            if (range <= 0f) return 0f;

            float shifted = value + halfExtent;
            float wrapped = shifted % range;
            if (wrapped < 0f) wrapped += range;
            return wrapped - halfExtent;
        }

        private void AppendInputHistory(Entity entity, int tick, TickPlayerInput input, int retentionTicks)
        {
            var buffer = EntityManager.GetBuffer<TickInputHistoryElement>(entity);
            buffer.Add(new TickInputHistoryElement { Tick = tick, Throttle = input.Throttle, Turn = input.Turn, Fire = input.Fire });

            // 보관 기간(retentionTicks)보다 오래된 앞부분을 잘라낸다. 버퍼는 항상 Tick 오름차순으로
            // 추가되므로 앞에서부터 지우면 된다.
            int cutoff = 0;
            while (cutoff < buffer.Length && (tick - buffer[cutoff].Tick) > retentionTicks)
            {
                cutoff++;
            }
            if (cutoff > 0)
            {
                buffer.RemoveRange(0, cutoff);
            }
        }

        // -------------------------------------------------------------------
        // 미사일
        // -------------------------------------------------------------------

        private void SpawnMissile(byte ownerId, int fireTick, float2 position, float rotationDeg)
        {
            var prefabs = SystemAPI.GetSingleton<RenderPrefabs>();
            Entity missileEntity = EntityManager.Instantiate(prefabs.MissilePrefab);

            EntityManager.AddComponentData(missileEntity, new MissileTag());
            EntityManager.AddComponentData(missileEntity, new MissileId { OwnerId = ownerId, FireTick = fireTick });
            EntityManager.AddComponentData(missileEntity, new MissileOwner { PlayerId = ownerId });
            EntityManager.AddComponentData(missileEntity, new MissileMotion { Position = position, RotationDeg = rotationDeg });
            EntityManager.AddComponentData(missileEntity, new MissileDistanceTraveled { Value = 0f });
            EntityManager.SetComponentData(missileEntity, new RenderTransform2D { Position = position, RotationDeg = rotationDeg });
        }

        /// <summary>
        /// 미사일 이동/사거리 소진/탱크 충돌을 처리한다. 여러 클라이언트가 각자 독립적으로
        /// 이 로직을 실행하므로 순회 순서 자체가 결정론의 일부다 — 순회 순서를 명시적으로
        /// (OwnerId 오름차순, 동률이면 FireTick 오름차순)으로 고정한다. 한 틱에 같은 타겟이
        /// 여러 미사일에 동시에 맞을 수 있는 경우(체력이 정확히 0으로 떨어지는 순간의 킬
        /// 크레딧), 이 정렬 순서가 어느 미사일의 명중이 먼저 처리되는지를 결정한다.
        /// </summary>
        private void ProcessMissiles(int tick, float dt, float elapsedTime, SimulationConfig config, Entity uiEventSingleton)
        {
            // 이 함수는 반드시 3단계(이동/발사 판정)가 전부 끝난 뒤에 호출되어야 한다. 아래에서
            // 읽는 TankPhysicsState.Position은 이번 틱의 최종 확정 위치여야 충돌 판정이 정확하다.
            //
            // dt는 이번 틱 이동 계산과 동일하게 서버 실측값(commit.DeltaTimeSeconds)을 그대로
            // 쓴다 — 탱크와 미사일이 서로 다른 dt를 쓰면 같은 틱 안에서 서로 다른 만큼의
            // 시간이 흐른 것처럼 계산되어 위치 관계가 어긋난다.
            float collisionDistSqr = (MISSILE_RADIUS + TANK_COLLISION_RADIUS) * (MISSILE_RADIUS + TANK_COLLISION_RADIUS);
            float stepDistance = config.MissileSpeed * dt;

            // 정렬된 순서로 순회하기 위해 먼저 목록을 모은다.
            var missiles = new List<(Entity Entity, MissileId Id)>();
            foreach (var (missileId, entity) in SystemAPI.Query<RefRO<MissileId>>().WithAll<MissileTag>().WithEntityAccess())
            {
                missiles.Add((entity, missileId.ValueRO));
            }
            missiles.Sort((a, b) =>
            {
                int ownerCompare = a.Id.OwnerId.CompareTo(b.Id.OwnerId);
                return ownerCompare != 0 ? ownerCompare : a.Id.FireTick.CompareTo(b.Id.FireTick);
            });

            // 살아있는 플레이어 목록도 PlayerId 오름차순으로 미리 모아둔다(충돌 판정 대상
            // 순회용). 이 목록 자체는 이번 틱 시작 시점의 스냅샷이지만, 아래 루프 안에서
            // 미사일이 명중해 사망 처리되면 같은 틱 안의 뒤따르는 판정에서 그 플레이어가
            // 자동으로 제외되어야 한다. 이를 위해 "죽은 플레이어의 Entity 집합"을 별도로
            // 추적하고 매 미사일 판정 시 함께 확인한다(목록을 매번 다시 만들지 않고
            // HashSet으로 배제하는 방식이 더 저렴하다).
            var alivePlayers = new List<(Entity Entity, byte PlayerId, float2 Position)>();
            foreach (var (id, physics, health, entity) in
                     SystemAPI.Query<RefRO<PlayerId>, RefRO<TankPhysicsState>, RefRO<HealthState>>().WithEntityAccess())
            {
                if (!health.ValueRO.IsDead)
                {
                    alivePlayers.Add((entity, id.ValueRO.Value, physics.ValueRO.Position));
                }
            }
            alivePlayers.Sort((a, b) => a.PlayerId.CompareTo(b.PlayerId));

            // 이번 틱 안에서 이미 사망 처리된 플레이어를 추적한다. alivePlayers 스냅샷 자체는
            // 갱신하지 않고, 판정 시 이 집합에 포함된 엔티티는 건너뛴다.
            var justDiedThisTick = new HashSet<Entity>();

            foreach (var (missileEntity, missileId) in missiles)
            {
                var motion = EntityManager.GetComponentData<MissileMotion>(missileEntity);
                var distance = EntityManager.GetComponentData<MissileDistanceTraveled>(missileEntity);

                float rotRad = motion.RotationDeg * Mathf.Deg2Rad;
                motion.Position += new float2(Mathf.Sin(rotRad), Mathf.Cos(rotRad)) * stepDistance;
                distance.Value += stepDistance;

                // 1) 사거리 소진
                if (distance.Value >= config.MissileMaxDistance)
                {
                    EntityManager.DestroyEntity(missileEntity);
                    continue;
                }

                EntityManager.SetComponentData(missileEntity, motion);
                EntityManager.SetComponentData(missileEntity, distance);

                // 2) 탱크 충돌: 발사자 자신, 이미 사망한 플레이어(스냅샷 시점 기준), 그리고
                //    이번 틱에 다른 미사일에 이미 죽은 플레이어(justDiedThisTick)를 모두
                //    제외하고 PlayerId 오름차순으로 검사한다. 가장 먼저 반지름 조건을
                //    만족하는 타겟 하나에만 명중 처리한다 — 한 미사일은 한 틱에 최대 하나의
                //    타겟에만 명중한다.
                foreach (var (targetEntity, targetPlayerId, targetPos) in alivePlayers)
                {
                    if (targetPlayerId == missileId.OwnerId) continue;
                    if (justDiedThisTick.Contains(targetEntity)) continue;

                    float2 delta = motion.Position - targetPos;
                    if (math.lengthsq(delta) <= collisionDistSqr)
                    {
                        EntityManager.DestroyEntity(missileEntity);
                        bool died = ApplyDamage(targetEntity, missileId.OwnerId, elapsedTime, uiEventSingleton);
                        if (died)
                        {
                            justDiedThisTick.Add(targetEntity);
                        }
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// 체력을 1 깎고, 0 이하로 떨어지면 사망 처리 + 킬/데스 집계를 수행한다. 이번 호출로
        /// 사망이 실제로 발생했는지를 반환한다 — 호출부(ProcessMissiles)가 같은 틱 안에서 이
        /// 타겟을 다시 명중 판정 대상에서 제외하기 위해 필요하다.
        /// </summary>
        private bool ApplyDamage(Entity targetEntity, byte killerId, float elapsedTime, Entity uiEventSingleton)
        {
            var health = EntityManager.GetComponentData<HealthState>(targetEntity);
            health.Health = (byte)math.max(0, health.Health - 1);

            if (health.Health == 0 && !health.IsDead)
            {
                health.WasDead = health.IsDead; // 다음 프레임 색상 갱신 트리거용
                health.IsDead = true;
                EntityManager.SetComponentData(targetEntity, health);
                EntityManager.SetComponentData(targetEntity, new DeathElapsedTime { Value = elapsedTime });

                var physics = EntityManager.GetComponentData<TankPhysicsState>(targetEntity);
                physics.Speed = 0f;
                EntityManager.SetComponentData(targetEntity, physics);

                var victimId = EntityManager.GetComponentData<PlayerId>(targetEntity).Value;
                AppendUiEvent(uiEventSingleton, new DeathVisualChangedElement { PlayerId = victimId, IsDead = true });

                // 킬/데스 집계. 가해자가 이미 존재하지 않으면(방금 접속 끊김 등) 무시.
                if (TryFindPlayerEntity(killerId, out Entity killerEntity))
                {
                    var killerScore = EntityManager.GetComponentData<ScoreboardEntry>(killerEntity);
                    killerScore.Kills += 1;
                    EntityManager.SetComponentData(killerEntity, killerScore);
                }

                var victimScore = EntityManager.GetComponentData<ScoreboardEntry>(targetEntity);
                victimScore.Deaths += 1;
                EntityManager.SetComponentData(targetEntity, victimScore);

                return true;
            }

            EntityManager.SetComponentData(targetEntity, health);
            return false;
        }

        // -------------------------------------------------------------------
        // 리스폰
        // -------------------------------------------------------------------

        /// <summary>
        /// 사망한 플레이어 중 (TotalElapsedSimTime - DeathElapsedTime) >= RespawnDelaySeconds인
        /// 대상을 새 스폰 슬롯으로 되살린다. PlayerId 오름차순으로 검사 — 여러 명이 같은 틱에
        /// 동시에 리스폰 조건을 만족하면, 이 순서가 곧 ComputeSpawnTransform 호출 순서이자
        /// 슬롯 배정 순서다.
        /// </summary>
        private void ProcessRespawns(float elapsedTime, SimulationConfig config, Entity uiEventSingleton)
        {
            var candidates = new List<(Entity Entity, byte PlayerId)>();
            foreach (var (id, health, deathElapsed, entity) in
                     SystemAPI.Query<RefRO<PlayerId>, RefRO<HealthState>, RefRO<DeathElapsedTime>>().WithEntityAccess())
            {
                if (health.ValueRO.IsDead && (elapsedTime - deathElapsed.ValueRO.Value) >= config.RespawnDelaySeconds)
                {
                    candidates.Add((entity, id.ValueRO.Value));
                }
            }
            candidates.Sort((a, b) => a.PlayerId.CompareTo(b.PlayerId));

            foreach (var (entity, playerId) in candidates)
            {
                (float2 spawnPos, float spawnRot, int slot) = ComputeSpawnTransform();

                EntityManager.SetComponentData(entity, new TankPhysicsState { Position = spawnPos, RotationDeg = spawnRot, Speed = 0f });
                EntityManager.SetComponentData(entity, new SpawnSlotState { Value = slot });

                var health = EntityManager.GetComponentData<HealthState>(entity);
                health.WasDead = health.IsDead;
                health.IsDead = false;
                health.Health = MAX_HEALTH;
                EntityManager.SetComponentData(entity, health);

                AppendUiEvent(uiEventSingleton, new DeathVisualChangedElement { PlayerId = playerId, IsDead = false });
            }
        }

        // -------------------------------------------------------------------
        // 헬퍼
        // -------------------------------------------------------------------

        private bool TryFindPlayerEntity(byte playerId, out Entity result)
        {
            foreach (var (id, entity) in SystemAPI.Query<RefRO<PlayerId>>().WithEntityAccess())
            {
                if (id.ValueRO.Value == playerId)
                {
                    result = entity;
                    return true;
                }
            }
            result = Entity.Null;
            return false;
        }

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
