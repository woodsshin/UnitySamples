using System;
using System.IO;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace CustomClient.Dots
{
    /// <summary>
    /// 기존 MonoBehaviour의 FixedUpdate() 전체를 그대로 옮긴 System.
    /// 반드시 FixedStepSimulationSystemGroup에서 실행되어야 한다 (원본이 FixedUpdate였으므로
    /// Time.fixedDeltaTime 기준 고정 주기 실행이 동일하게 유지되어야 서버와의 예측이 어긋나지 않는다).
    ///
    /// 처리 순서(원본과 완전히 동일하게 유지할 것):
    ///   1) 소켓/PlayerId 유효성 체크
    ///   2) 입력 읽기
    ///   3) 사망 중이면 throttle/turn/fire를 0으로 강제 (패킷 전송 자체는 유지)
    ///   4) clientTick 증가
    ///   5) 사망이 아니면 SimulateTankStep으로 예측 이동
    ///   6) pendingInputs(버퍼)에 이번 입력 기록
    ///   7) 발사 쿨다운 처리 + 예측 미사일 스폰
    ///   8) 서버로 ClientInput 패킷 전송
    /// </summary>
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup))]
    public partial class LocalPlayerFixedStepSystem : SystemBase
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
            var clientState = EntityManager.GetComponentData<ClientStateSingleton>(configEntity);
            var netData = EntityManager.GetComponentObject<NetworkConnectionData>(configEntity);

            // 1) 원본: if (_udpClient == null || _myPlayerId == 0) return;
            if (netData.UdpClient == null || clientState.MyPlayerId == 0) return;

            // 2) 입력 읽기 (원본 GetTankInput)
            float throttle = 0f;
            if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) throttle += 1f;
            if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) throttle -= 1f;
            throttle = Mathf.Clamp(throttle, -1f, 1f);

            float turn = 0f;
            if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) turn -= 1f;
            if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) turn += 1f;
            turn = Mathf.Clamp(turn, -1f, 1f);

            bool fireHeld = Input.GetKey(KeyCode.Space);

            // 3) 사망 중 입력 무시 (패킷은 그대로 전송해 LastProcessedTick 갱신 유지)
            if (clientState.IsLocalPlayerDead)
            {
                throttle = 0f;
                turn = 0f;
                fireHeld = false;
            }

            // 4) clientTick 증가 (입력이 0이어도 매 FixedUpdate마다 증가)
            clientState.ClientTick++;

            float dt = UnityEngine.Time.fixedDeltaTime;

            Entity localEntity = Entity.Null;
            foreach (var (_, entity) in SystemAPI.Query<RefRO<PredictedTankState>>().WithAll<LocalPlayerTag>().WithEntityAccess())
            {
                localEntity = entity;
                break;
            }

            if (localEntity != Entity.Null)
            {
                var predicted = EntityManager.GetComponentData<PredictedTankState>(localEntity);

                // 5) 사망이 아니면 예측 이동 (원본: if (!_isLocalPlayerDead) SimulateTankStep)
                if (!clientState.IsLocalPlayerDead)
                {
                    SimulateTankStep(config, ref predicted.Position, ref predicted.RotationDeg, ref predicted.Speed, throttle, turn, dt);
                }

                EntityManager.SetComponentData(localEntity, predicted);

                // 6) pendingInputs 버퍼에 기록 (원본: _pendingInputs.Add)
                var pendingBuffer = EntityManager.GetBuffer<PendingInputElement>(localEntity);
                pendingBuffer.Add(new PendingInputElement
                {
                    Tick = clientState.ClientTick,
                    Throttle = throttle,
                    Turn = turn
                });

                // 7) 발사 쿨다운 처리 + 예측 미사일 스폰
                //    원본: if (_fireCooldownTimer > 0f) _fireCooldownTimer -= dt;
                //          if (fireHeld && _fireCooldownTimer <= 0f) { 리셋; SpawnPredictedMissile(); }
                if (clientState.FireCooldownTimer > 0f)
                {
                    clientState.FireCooldownTimer -= dt;
                }

                if (fireHeld && clientState.FireCooldownTimer <= 0f)
                {
                    clientState.FireCooldownTimer = config.FireCooldownSeconds;
                    SpawnPredictedMissile(config, clientState.MyPlayerId, predicted.Position, predicted.RotationDeg);
                }
            }

            // 8) 서버로 입력 패킷 전송 (원본 FixedUpdate 마지막 부분)
            //    필드 순서: PacketType, PlayerId, Tick, Throttle, Turn, Fire
            //    -- 서버 HandleClientInput과 정확히 동일한 순서여야 한다.
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write((byte)PacketType.ClientInput);
                bw.Write(clientState.MyPlayerId);
                bw.Write(clientState.ClientTick);
                bw.Write(throttle);
                bw.Write(turn);
                bw.Write(fireHeld);

                var netSystem = World.GetExistingSystemManaged<NetworkConnectionSystem>();
                netSystem.SendPacket(netData, ms.ToArray());
            }

            EntityManager.SetComponentData(configEntity, clientState);
        }

        /// <summary>
        /// 좌표 하나를 [-halfExtent, +halfExtent) 범위로 순환(wrap)시킨다.
        ///
        /// [중요] CustomServer.WrapCoordinate, CustomClient(MonoBehaviour)의
        /// WrapCoordinate와 정확히 동일한 공식이어야 한다. 세 곳 중 하나만 달라도 wrap이 일어나는
        /// 정확한 좌표가 미세하게 어긋나 재조정이 매 틱 실패하기 시작한다. public static으로 두어
        /// ServerStateApplySystem(재조정 오차 계산)과 RemotePlayerInterpolationSystem(원격 보간)
        /// 양쪽에서도 동일한 공식을 재사용한다.
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

        /// <summary>
        /// 서버(HandleClientInput)와 정확히 동일한 순서/공식. 절대로 임의로 변형하지 말 것.
        /// 순서: 1) 회전  2) 목표 속도로 가감속  3) 이동  4) 위치만 경계 wrap.
        /// </summary>
        public static void SimulateTankStep(SimulationConfig config, ref float2 pos, ref float rotDeg, ref float speed,
            float throttle, float turn, float dt)
        {
            throttle = Mathf.Clamp(throttle, -1f, 1f);
            turn = Mathf.Clamp(turn, -1f, 1f);

            // 1) 회전
            rotDeg += turn * config.TurnSpeedDeg * dt;
            rotDeg %= 360f;
            if (rotDeg < 0f) rotDeg += 360f;

            // 2) 목표 속도로 가속/감속
            float targetSpeed = throttle >= 0f ? throttle * config.MaxForwardSpeed : throttle * config.MaxReverseSpeed;
            float speedDelta = config.Acceleration * dt;
            if (speed < targetSpeed)
            {
                speed = Mathf.Min(speed + speedDelta, targetSpeed);
            }
            else if (speed > targetSpeed)
            {
                speed = Mathf.Max(speed - speedDelta, targetSpeed);
            }

            // 3) 현재 방향으로 전진/후진 (0도 = +Y, 시계방향 증가 — 서버와 동일 기준)
            float rotRad = rotDeg * Mathf.Deg2Rad;
            float2 dir = new float2(Mathf.Sin(rotRad), Mathf.Cos(rotRad));
            pos += dir * speed * dt;

            // 4) 화면(플레이 가능 영역) 경계에서 반대편으로 순환(wrap)한다. 속도/회전은 그대로
            //    둔다 — wrap은 위치 좌표만 바꾸는 순간이동이다.
            //    [중요] 반드시 config.ServerWorldHalfExtent(X/Y 각각)를 기준으로 wrap해야 한다.
            //    config.BoundsHalfExtent(카메라 화면비에 따라 달라지는, 서버보다 좁을 수 있는 값)를
            //    쓰면 ECS 클라이언트가 서버/MonoBehaviour 클라이언트와 다른 좌표에서 wrap하게 되어
            //    매 틱 재조정이 실패한다 — clamp였던 예전 코드는 BoundsHalfExtent를 썼지만
            //    ("좁게 잡는 것은 안전하다"는 clamp 고유의 성질 덕분에 문제없었다), wrap에는 그
            //    성질이 없으므로 반드시 서버와 정확히 같은 값으로 바꿔야 한다. 화면이 16:9로
            //    고정되어 X와 Y의 half-extent가 서로 다르므로, 각 축은 반드시 자신의 값으로만
            //    wrap해야 한다 — X를 Y의 값으로(혹은 그 반대로) wrap하면 좌우/상하 벽 위치가
            //    화면 경계와 어긋난다.
            pos.x = WrapCoordinate(pos.x, config.ServerWorldHalfExtent.x);
            pos.y = WrapCoordinate(pos.y, config.ServerWorldHalfExtent.y);
        }

        private void SpawnPredictedMissile(SimulationConfig config, byte ownerId, float2 position, float rotationDeg)
        {
            var prefabs = SystemAPI.GetSingleton<RenderPrefabs>();
            Entity missileEntity = EntityManager.Instantiate(prefabs.MissilePrefab);

            EntityManager.AddComponentData(missileEntity, new MissileTag());
            EntityManager.AddComponentData(missileEntity, new PredictedMissileTag());
            EntityManager.AddComponentData(missileEntity, new MissileId { Value = 0, HasServerId = false });
            EntityManager.AddComponentData(missileEntity, new MissileOwner { PlayerId = ownerId });
            EntityManager.AddComponentData(missileEntity, new MissileMotion { Position = position, RotationDeg = rotationDeg });
            EntityManager.AddComponentData(missileEntity, new PredictedMissileSpawnTime { Value = UnityEngine.Time.time });
            EntityManager.AddComponentData(missileEntity, new PredictedMissileDistance { Value = 0f });
            EntityManager.SetComponentData(missileEntity, new RenderTransform2D { Position = position, RotationDeg = rotationDeg });
        }
    }
}
