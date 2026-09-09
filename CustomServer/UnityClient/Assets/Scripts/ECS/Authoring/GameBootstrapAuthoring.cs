using Unity.Entities;
using UnityEngine;

namespace CustomClient.Dots
{
    /// <summary>
    /// SubScene에 하나만 배치한다. 로컬/원격 탱크, 미사일 프리팹(모두 Entities Graphics로 베이킹된
    /// GameObject 프리팹)을 등록해 런타임 System들이 EntityManager.Instantiate로 생성할 수 있게 한다.
    /// </summary>
    public class GameBootstrapAuthoring : MonoBehaviour
    {
        [Tooltip("ShipRenderAuthoring(IsLocalPlayerPrefab=true)이 붙은 프리팹")]
        public GameObject LocalShipPrefab;

        [Tooltip("ShipRenderAuthoring(IsLocalPlayerPrefab=false)이 붙은 프리팹")]
        public GameObject RemoteShipPrefab;

        [Tooltip("MissileRenderAuthoring이 붙은 프리팹")]
        public GameObject MissilePrefab;

        public class Baker : Baker<GameBootstrapAuthoring>
        {
            public override void Bake(GameBootstrapAuthoring authoring)
            {
                Entity entity = GetEntity(TransformUsageFlags.None);
                AddComponent(entity, new RenderPrefabs
                {
                    LocalShipPrefab = GetEntity(authoring.LocalShipPrefab, TransformUsageFlags.Dynamic),
                    RemoteShipPrefab = GetEntity(authoring.RemoteShipPrefab, TransformUsageFlags.Dynamic),
                    MissilePrefab = GetEntity(authoring.MissilePrefab, TransformUsageFlags.Dynamic)
                });
            }
        }
    }
}
