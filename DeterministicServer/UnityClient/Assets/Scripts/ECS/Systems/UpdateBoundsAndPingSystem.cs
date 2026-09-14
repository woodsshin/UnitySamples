using Game.Networking;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace DeterministicSample.Dots
{
    /// <summary>
    /// 매 프레임 Camera.main을 읽어 SimulationConfig.BoundsHalfExtent를 갱신한다. 순수 시각적
    /// 계산(카메라 화면비에 따른 여유 공간 표시용)이며 물리 wrap 기준으로는 절대 쓰이지
    /// 않는다 — 그 기준은 항상 config.ServerWorldHalfExtent다.
    ///
    /// SimulationTickSystem은 서버 TickCommit 도착 여부에 종속되어 프레임마다 실행되지 않을
    /// 수 있으므로, 이 System은 OrderFirst = true로 물리 시뮬레이션 실행 전 프레임 시작
    /// 시점에 항상 최신값을 보장한다.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst = true)]
    public partial class UpdateBoundsSystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<SimulationConfig>();
        }

        protected override void OnUpdate()
        {
            var configEntity = SystemAPI.GetSingletonEntity<SimulationConfig>();
            var config = EntityManager.GetComponentData<SimulationConfig>(configEntity);

            Camera mainCamera = Camera.main;
            float2 computed;

            if (config.UseCameraForBounds && mainCamera != null && mainCamera.orthographic)
            {
                float halfHeight = mainCamera.orthographicSize;
                float halfWidth = halfHeight * mainCamera.aspect;

                computed = new float2(
                    Mathf.Max(0.1f, halfWidth - config.BoundsMargin),
                    Mathf.Max(0.1f, halfHeight - config.BoundsMargin));
            }
            else
            {
                computed = config.FallbackHalfExtent;
            }

            config.BoundsHalfExtent = new float2(
                Mathf.Min(computed.x, config.ServerWorldHalfExtent.x),
                Mathf.Min(computed.y, config.ServerWorldHalfExtent.y));

            EntityManager.SetComponentData(configEntity, config);
        }
    }

    /// <summary>
    /// 1초 간격으로 서버에 Ping을 보내고, 응답(Pong)은 NetworkConnectionSystem의 ReceiveLoop가
    /// CurrentPingMs에 채워 넣는다. 직렬화는 Protocol.BuildPing을 사용한다 — 다른 System들과
    /// 마찬가지로 공유 Protocol.cs가 유일한 직렬화 구현이다.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial class PingSystem : SystemBase
    {
        private const float PingInterval = 1.0f;

        protected override void OnCreate()
        {
            RequireForUpdate<SimulationConfig>();
            RequireForUpdate<ClientStateSingleton>();
        }

        protected override void OnUpdate()
        {
            var configEntity = SystemAPI.GetSingletonEntity<SimulationConfig>();
            var clientState = EntityManager.GetComponentData<ClientStateSingleton>(configEntity);
            var netData = EntityManager.GetComponentObject<NetworkConnectionData>(configEntity);

            if (netData.UdpClient == null || clientState.MyPlayerId == 0) return;

            clientState.PingTimer += UnityEngine.Time.deltaTime;
            if (clientState.PingTimer >= PingInterval)
            {
                clientState.PingTimer = 0f;

                byte[] packet = Protocol.BuildPing(clientState.MyPlayerId, System.DateTime.UtcNow.Ticks);
                var netSystem = World.GetExistingSystemManaged<NetworkConnectionSystem>();
                netSystem.SendPacket(netData, packet);
            }

            EntityManager.SetComponentData(configEntity, clientState);
        }
    }
}
