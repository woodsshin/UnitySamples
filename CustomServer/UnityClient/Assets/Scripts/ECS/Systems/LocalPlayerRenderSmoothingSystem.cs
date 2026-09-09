using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace CustomClient.Dots
{
    /// <summary>
    /// 원본 Update() 맨 아래의 블록을 옮긴 System:
    ///   _renderErrorOffset = Vector2.Lerp(_renderErrorOffset, Vector2.zero, dt * _smoothingSpeed);
    ///   _renderRotErrorOffset = Mathf.Lerp(_renderRotErrorOffset, 0f, dt * _smoothingSpeed);
    ///   displayPos = _predictedPos + _renderErrorOffset;
    ///   displayRot = _predictedRot + _renderRotErrorOffset;
    /// 재조정(ServerStateApplySystem)이 오프셋 값 자체를 바꾸고, 이 System은 매 프레임
    /// 무조건 실행되어 그 오프셋을 서서히 0으로 감쇠시키며 최종 렌더 좌표를 만든다.
    /// 반드시 ServerStateApplySystem 이후, 렌더링 관련 System 이전에 실행되어야 한다.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(ServerStateApplySystem))]
    public partial class LocalPlayerRenderSmoothingSystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<SimulationConfig>();
        }

        protected override void OnUpdate()
        {
            var config = SystemAPI.GetSingleton<SimulationConfig>();
            float dt = UnityEngine.Time.deltaTime;
            float lerpT = dt * config.SmoothingSpeed;

            foreach (var (predicted, offset, renderTransform) in
                     SystemAPI.Query<RefRO<PredictedTankState>, RefRW<RenderErrorOffset>, RefRW<RenderTransform2D>>()
                         .WithAll<LocalPlayerTag>())
            {
                offset.ValueRW.Position = math.lerp(offset.ValueRO.Position, float2.zero, lerpT);
                offset.ValueRW.RotationDeg = Mathf.Lerp(offset.ValueRO.RotationDeg, 0f, lerpT);

                renderTransform.ValueRW.Position = predicted.ValueRO.Position + offset.ValueRO.Position;
                renderTransform.ValueRW.RotationDeg = predicted.ValueRO.RotationDeg + offset.ValueRO.RotationDeg;
            }
        }
    }
}
