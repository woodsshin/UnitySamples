using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace CustomClient.Dots
{
    /// <summary>
    /// 기존 MonoBehaviour의 [SerializeField] 인스펙터 필드들을 그대로 옮긴 Authoring 컴포넌트.
    /// SubScene 안에 빈 GameObject를 하나 만들고 이 컴포넌트를 붙이면, 베이킹 시
    /// SimulationConfig 싱글톤 엔티티가 생성된다.
    /// </summary>
    public class SimulationConfigAuthoring : MonoBehaviour
    {
        [Header("Connection")]
        public string ServerIp = "127.0.0.1";
        public int ServerPort = 9050;
        public float ConnectionTimeoutSeconds = 3.0f;
        public float ReconnectIntervalSeconds = 1.5f;

        [Header("Tank Movement (must mirror server constants)")]
        public float MaxForwardSpeed = 3.0f;
        public float MaxReverseSpeed = 3.0f;
        public float Acceleration = 4.0f;
        public float TurnSpeedDeg = 160f;

        [Header("World Bounds (movement wraps at ServerWorldHalfExtentX/Y, must exactly equal server)")]
        // 탱크 이동은 이 값(X/Y 각각)을 기준으로 wrap(반대편에서 재등장)한다.
        // 화면 해상도가 1280x720(16:9)으로 고정되어 있으므로, 두 값은 정사각형이 아니라
        // 16:9 비율을 반영한 서버 WORLD_HALF_EXTENT_X/Y와 정확히 같아야 한다:
        //   ServerWorldHalfExtentY = 4.0 (Orthographic Size 기준)
        //   ServerWorldHalfExtentX = ServerWorldHalfExtentY * (1280/720) = 7.1111111...
        // 카메라 화면비에 따라 달라지는 시각적 계산(UpdateBoundsSystem.BoundsHalfExtent)은
        // wrap에 관여하지 않으므로, 실제 화면비와 무관하게 이 두 값만 서버와 정확히 같으면 된다.
        public float ServerWorldHalfExtentX = 7.1111111f; // 서버 WORLD_HALF_EXTENT_X와 반드시 동일해야 함
        public float ServerWorldHalfExtentY = 4.0f;       // 서버 WORLD_HALF_EXTENT_Y와 반드시 동일해야 함
        public float FallbackHalfExtentX = 7.1111111f;
        public float FallbackHalfExtentY = 4.0f;
        public float BoundsMargin = 0.5f;
        public bool UseCameraForBounds = true;

        [Header("Reconciliation Thresholds")]
        public float ErrorThreshold = 0.05f;
        public float RotationErrorThresholdDeg = 3.0f;
        public float SnapThreshold = 2.0f;
        public float RotationSnapThresholdDeg = 45f;

        [Header("Render Smoothing")]
        public float SmoothingSpeed = 15.0f;

        [Header("Remote Interpolation")]
        public float InterpolationDelay = 0.1f;

        [Header("Missile (must mirror server MISSILE_SPEED / FIRE_COOLDOWN_SECONDS / MISSILE_MAX_DISTANCE)")]
        public float MissileSpeed = 12.0f;
        public float FireCooldownSeconds = 0.3f;
        // 서버 MISSILE_MAX_DISTANCE와 정확히 같은 값이어야 한다. 미사일은 화면 경계에서 wrap하지
        // 않고 발사 방향으로 계속 직진하므로, 오직 이 사거리로만 수명이 제한된다. 화면이 16:9로
        // 고정되며 가로(ServerWorldHalfExtentX*2 ≈ 14.2222)가 세로보다 훨씬 넓어졌으므로, 사거리는
        // 더 긴 축인 가로 기준으로 잡는다(서버 주석 참고).
        public float MissileMaxDistance = 14.2222222f;
        // [중요] (MissileMaxDistance / MissileSpeed)보다 넉넉히 길어야 한다(기본값 기준 약 1.185초
        // → 1.8초로 여유를 둠). 그보다 짧으면 정상 비행 중인 예측 미사일이 사거리에 닿기도 전에
        // 이 시간 기반 안전장치가 먼저 지워버려, 서버는 아직 살아있다고 보내는 미사일이 화면에서만
        // 먼저 사라지는 깜빡임이 생긴다.
        public float PredictedMissileTimeoutSeconds = 1.8f;

        public class Baker : Baker<SimulationConfigAuthoring>
        {
            public override void Bake(SimulationConfigAuthoring authoring)
            {
                Entity entity = GetEntity(TransformUsageFlags.None);

                AddComponent(entity, new SimulationConfig
                {
                    ServerIp = authoring.ServerIp,
                    ServerPort = authoring.ServerPort,
                    ConnectionTimeoutSeconds = authoring.ConnectionTimeoutSeconds,
                    ReconnectIntervalSeconds = authoring.ReconnectIntervalSeconds,

                    MaxForwardSpeed = authoring.MaxForwardSpeed,
                    MaxReverseSpeed = authoring.MaxReverseSpeed,
                    Acceleration = authoring.Acceleration,
                    TurnSpeedDeg = authoring.TurnSpeedDeg,

                    ServerWorldHalfExtent = new float2(authoring.ServerWorldHalfExtentX, authoring.ServerWorldHalfExtentY),
                    FallbackHalfExtent = new float2(authoring.FallbackHalfExtentX, authoring.FallbackHalfExtentY),
                    BoundsMargin = authoring.BoundsMargin,
                    UseCameraForBounds = authoring.UseCameraForBounds,

                    ErrorThreshold = authoring.ErrorThreshold,
                    RotationErrorThresholdDeg = authoring.RotationErrorThresholdDeg,
                    SnapThreshold = authoring.SnapThreshold,
                    RotationSnapThresholdDeg = authoring.RotationSnapThresholdDeg,

                    SmoothingSpeed = authoring.SmoothingSpeed,
                    InterpolationDelay = authoring.InterpolationDelay,

                    MissileSpeed = authoring.MissileSpeed,
                    FireCooldownSeconds = authoring.FireCooldownSeconds,
                    MissileMaxDistance = authoring.MissileMaxDistance,
                    PredictedMissileTimeoutSeconds = authoring.PredictedMissileTimeoutSeconds,

                    // 초기값 — UpdateBoundsSystem이 첫 프레임에 바로 재계산한다.
                    BoundsHalfExtent = new float2(authoring.FallbackHalfExtentX, authoring.FallbackHalfExtentY)
                });
            }
        }
    }
}
