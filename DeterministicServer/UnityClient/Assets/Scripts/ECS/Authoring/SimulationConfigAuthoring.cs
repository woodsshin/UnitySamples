using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace DeterministicSample.Dots
{
    /// <summary>
    /// SubScene 안에 빈 GameObject를 하나 만들고 이 컴포넌트를 붙이면, 베이킹 시
    /// SimulationConfig 싱글톤 엔티티가 생성된다. 인스펙터에 노출된 필드들이 그대로 컴포넌트
    /// 필드로 베이킹된다.
    ///
    /// 고정 틱레이트(TickRateHz) 필드는 존재하지 않는다 — 서버가 매 틱 실제로 경과한 시간을
    /// 실측해 TickCommit.DeltaTimeSeconds로 방송하고, 클라이언트는 그 값을 물리 dt로 그대로
    /// 쓴다. 서버 실제 틱레이트가 목표치(60Hz)와 미세하게 어긋나도 클라이언트는 영향받지
    /// 않는다. 발사 쿨다운/리스폰 판정도 "틱 개수"가 아니라, 서버가 방송한 DeltaTimeSeconds를
    /// 클라이언트가 누적한 실측 경과 시간(ClientStateSingleton.TotalElapsedSimTime) 기준으로
    /// 이루어진다.
    /// </summary>
    public class SimulationConfigAuthoring : MonoBehaviour
    {
        [Header("Connection")]
        public string ServerIp = "127.0.0.1";
        public int ServerPort = 9050;
        // 연결이 끊기면 재시도 없이 EntryScene으로 이동한다(NetworkConnectionSystem 참고).
        public float ConnectionTimeoutSeconds = 3.0f;

        [Header("Input Delay & History")]
        [Tooltip("클라이언트가 로컬에서 마지막으로 확정된 틱보다 이만큼 미래 틱에 적용될 입력을 " +
                 "미리 서버로 보낸다. 로컬 PC 환경(지연/유실 거의 없음) 가정이라 작은 값으로도 " +
                 "충분하지만, 구조 검증을 위해 노출해둔다.")]
        public int InputDelayTicks = 2;
        [Tooltip("최근 이 개수만큼 원본 입력 이력을 각 플레이어 엔티티에 누적 보관한다(디버깅/사후 " +
                 "검증용). 발사 쿨다운/리스폰 판정 자체는 이 버퍼에 의존하지 않는다 — 그 판정은 " +
                 "TotalElapsedSimTime 기반 파생값(LastFireElapsedTime/DeathElapsedTime)만으로 " +
                 "이루어진다. 기본 180은 대략 60Hz 기준 3초에 해당하는 틱 개수(실측 dt 환경에서는 " +
                 "근사치일 뿐이다).")]
        public int InputHistoryRetentionTicks = 180;

        [Header("Tank Movement (must mirror the physics constants this project used to run on the server)")]
        public float MaxForwardSpeed = 3.0f;
        public float MaxReverseSpeed = 3.0f;
        public float Acceleration = 4.0f;
        public float TurnSpeedDeg = 160f;

        [Header("World Bounds (movement wraps at ServerWorldHalfExtentX/Y)")]
        // 우주선 이동은 이 값(X/Y 각각)을 기준으로 wrap(반대편에서 재등장)한다. 화면 해상도가
        // 1280x720(16:9)으로 고정되어 있으므로 16:9 비율을 반영한 값을 사용한다:
        //   ServerWorldHalfExtentY = 4.0 (Orthographic Size 기준)
        //   ServerWorldHalfExtentX = ServerWorldHalfExtentY * (1280/720) = 7.1111111...
        // 이 값은 서버가 아니라 모든 클라이언트가 동일하게 설정해야 하는 공유 상수다.
        public float ServerWorldHalfExtentX = 7.1111111f;
        public float ServerWorldHalfExtentY = 4.0f;
        public float FallbackHalfExtentX = 7.1111111f;
        public float FallbackHalfExtentY = 4.0f;
        public float BoundsMargin = 0.5f;
        public bool UseCameraForBounds = true;

        [Header("Missile (must be identical across all clients for determinism)")]
        public float MissileSpeed = 12.0f;
        public float FireCooldownSeconds = 0.3f;
        // 사거리(유닛). 화면이 16:9로 고정되며 가로(ServerWorldHalfExtentX*2 ≈ 14.2222)가
        // 세로보다 훨씬 넓어졌으므로, 사거리는 더 긴 축인 가로 기준으로 잡는다.
        public float MissileMaxDistance = 14.2222222f;

        [Header("Respawn")]
        [Tooltip("사망 후 리스폰까지 대기하는 시간(초). 발사 쿨다운과 마찬가지로 틱 개수가 아니라 " +
                 "초 단위 실측 누적 시간(TotalElapsedSimTime) 기준으로 판정한다.")]
        public float RespawnDelaySeconds = 3.0f;

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

                    InputDelayTicks = authoring.InputDelayTicks,
                    InputHistoryRetentionTicks = authoring.InputHistoryRetentionTicks,

                    MaxForwardSpeed = authoring.MaxForwardSpeed,
                    MaxReverseSpeed = authoring.MaxReverseSpeed,
                    Acceleration = authoring.Acceleration,
                    TurnSpeedDeg = authoring.TurnSpeedDeg,

                    ServerWorldHalfExtent = new float2(authoring.ServerWorldHalfExtentX, authoring.ServerWorldHalfExtentY),
                    FallbackHalfExtent = new float2(authoring.FallbackHalfExtentX, authoring.FallbackHalfExtentY),
                    BoundsMargin = authoring.BoundsMargin,
                    UseCameraForBounds = authoring.UseCameraForBounds,

                    MissileSpeed = authoring.MissileSpeed,
                    FireCooldownSeconds = authoring.FireCooldownSeconds,
                    MissileMaxDistance = authoring.MissileMaxDistance,

                    RespawnDelaySeconds = authoring.RespawnDelaySeconds,

                    // 초기값 — UpdateBoundsSystem이 첫 프레임에 바로 재계산한다.
                    BoundsHalfExtent = new float2(authoring.FallbackHalfExtentX, authoring.FallbackHalfExtentY)
                });
            }
        }
    }
}
