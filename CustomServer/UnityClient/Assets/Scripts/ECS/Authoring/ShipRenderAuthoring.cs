using Unity.Entities;
using Unity.Entities.Graphics;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;

namespace CustomClient.Dots
{
    /// <summary>
    /// 탱크(플레이어) 프리팹 Authoring.
    /// 일반 GameObject 프리팹에 이 컴포넌트를 추가하고 SubScene에 넣으면, 베이킹 시
    /// RenderMeshArray가 함께 Entities Graphics로 렌더링된다.
    ///
    /// 색상은 URPMaterialPropertyBaseColor로 매 프레임 갱신한다 (PlayerVisualColorSystem 참고).
    /// MeshRenderer/MeshFilter는 베이킹 후 필요 없으므로, 프리팹에는 RenderMeshUtility가
    /// 요구하는 최소 구성(MeshFilter+MeshRenderer)만 있으면 된다.
    /// </summary>
    [RequireComponent(typeof(MeshFilter))]
    [RequireComponent(typeof(MeshRenderer))]
    public class ShipRenderAuthoring : MonoBehaviour
    {
        public bool IsLocalPlayerPrefab;

        public class Baker : Baker<ShipRenderAuthoring>
        {
            public override void Bake(ShipRenderAuthoring authoring)
            {
                Entity entity = GetEntity(TransformUsageFlags.Dynamic);

                AddComponent(entity, new RenderTransform2D());
                AddComponent(entity, new URPMaterialPropertyBaseColor
                {
                    Value = authoring.IsLocalPlayerPrefab
                        ? new float4(0f, 1f, 0f, 1f)   // Color.green
                        : new float4(1f, 0f, 0f, 1f)   // Color.red
                });
            }
        }
    }
}
