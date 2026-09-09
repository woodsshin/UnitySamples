using System;
using System.IO;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace CustomClient.Dots
{
    /// <summary>
    /// 원본 UpdateBounds()를 옮긴 System. 매 프레임 Camera.main을 읽어 SimulationConfig.BoundsHalfExtent를
    /// 갱신한다. 이동/예측/재조정 관련 모든 System보다 먼저 실행되어야 이번 프레임의 경계값이 최신 상태로 적용 가능.
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
                // 화면 해상도가 1280x720(16:9)으로 고정되므로 FallbackHalfExtent도 더 이상
                // 정사각형이 아니라 X/Y가 다른 float2다.
                computed = config.FallbackHalfExtent;
            }

            // 서버가 허용하는 영역보다 넓어지지 않도록 최종 clamp (예측 안정성을 위해 필수).
            // X/Y 각각 자신의 half-extent로 clamp해야 한다 — 화면이 16:9로 고정되어 X가 Y보다
            // 훨씬 넓으므로, 두 축을 같은 값으로 clamp하면(예전 정사각형 시절 코드) 한쪽이
            // 지나치게 좁아지거나 넓어진다.
            config.BoundsHalfExtent = new float2(
                Mathf.Min(computed.x, config.ServerWorldHalfExtent.x),
                Mathf.Min(computed.y, config.ServerWorldHalfExtent.y));

            EntityManager.SetComponentData(configEntity, config);
        }
    }

    /// <summary>
    /// 원본 SendPingCheck()를 옮긴 System. 1초 간격으로 서버에 Ping을 보내고,
    /// 응답(Pong)은 NetworkConnectionSystem의 ReceiveLoop가 CurrentPingMs에 채워 넣는다.
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

                using var ms = new MemoryStream();
                using var bw = new BinaryWriter(ms);
                bw.Write((byte)PacketType.Ping);
                bw.Write(clientState.MyPlayerId);
                bw.Write(DateTime.UtcNow.Ticks);

                var netSystem = World.GetExistingSystemManaged<NetworkConnectionSystem>();
                netSystem.SendPacket(netData, ms.ToArray());
            }

            EntityManager.SetComponentData(configEntity, clientState);
        }
    }
}
