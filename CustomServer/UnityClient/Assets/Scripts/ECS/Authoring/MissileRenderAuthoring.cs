using Unity.Entities;
using Unity.Entities.Graphics;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;

namespace CustomClient.Dots
{
    /// <summary>미사일 프리팹 Authoring. 탱크와 동일한 방식이며 색상은 트리거에 따라 결정된다.</summary>
    [RequireComponent(typeof(MeshFilter))]
    [RequireComponent(typeof(MeshRenderer))]
    public class MissileRenderAuthoring : MonoBehaviour
    {
        public class Baker : Baker<MissileRenderAuthoring>
        {
            public override void Bake(MissileRenderAuthoring authoring)
            {
                Entity entity = GetEntity(TransformUsageFlags.Dynamic);
                AddComponent(entity, new RenderTransform2D());
                AddComponent(entity, new URPMaterialPropertyBaseColor { Value = new float4(1f, 1f, 1f, 1f) });
            }
        }
    }
}
