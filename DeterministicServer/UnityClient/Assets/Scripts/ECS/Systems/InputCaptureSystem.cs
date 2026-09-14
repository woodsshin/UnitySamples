using Game.Networking;
using Unity.Entities;
using UnityEngine;

namespace DeterministicSample.Dots
{
    /// <summary>
    /// 로컬 키보드 입력을 읽어 서버로 전송하는 System. 물리는 전혀 계산하지 않는다 — 현재
    /// 키보드 상태를 읽어 이 입력이 적용되어야 할 목표 틱(TargetTick)과 함께 서버로 보내는
    /// 것이 유일한 역할이다.
    ///
    /// 사망 여부에 따라 입력을 0으로 강제하는 로직은 없다 — 입력 전송 시점에는 input delay
    /// 때문에 그 입력이 적용될 미래 틱에서 자신이 살아있을지 알 수 없으므로, 원본 입력을
    /// 있는 그대로 보내고 판정은 전적으로 SimulationTickSystem이 그 틱 시점의 HealthState를
    /// 보고 내린다.
    ///
    /// 이 System은 SimulationSystemGroup(가변 프레임, 모니터 주사율만큼 실행)에서 돌지만
    /// 서버 틱은 고정 60Hz다. 같은 TargetTick에 대해 매 프레임 중복 전송하지 않도록, 마지막
    /// 전송한 TargetTick을 기억해두고 값이 바뀌었을 때만 새로 전송한다.
    ///
    /// 반드시 SimulationTickSystem 이후에 실행되어야 한다. TargetTick 계산은
    /// ClientStateSingleton.LastConfirmedTick을 기준으로 하는데, 이 값은 SimulationTickSystem이
    /// 이번 프레임에 도착한 TickCommit을 처리하면서 갱신한다. 순서가 바뀌면 이번 프레임에 막
    /// 도착한 TickCommit을 반영하기 전의 오래된 LastConfirmedTick으로 입력을 계산하게 되어,
    /// 매 프레임 한 틱씩 뒤처진 TargetTick을 계산할 수 있다(다음 프레임에 다시 계산되므로
    /// 치명적이지 않지만 불필요한 지연이다).
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(SimulationTickSystem))]
    public partial class InputCaptureSystem : SystemBase
    {
        // 마지막으로 서버에 전송한 TargetTick. 같은 값에 대해서는 재전송하지 않는다.
        // int.MinValue를 "아직 한 번도 보낸 적 없음" 마커로 사용 — 실제 틱 번호는 항상
        // 0 이상이므로 절대 충돌하지 않는다.
        private int _lastSentTargetTick = int.MinValue;

        protected override void OnCreate()
        {
            RequireForUpdate<SimulationConfig>();
            RequireForUpdate<ClientStateSingleton>();
        }

        protected override void OnUpdate()
        {
            var configEntity = SystemAPI.GetSingletonEntity<SimulationConfig>();
            var config = EntityManager.GetComponentData<SimulationConfig>(configEntity);
            var clientState = EntityManager.GetComponentData<ClientStateSingleton>(configEntity);
            var netData = EntityManager.GetComponentObject<NetworkConnectionData>(configEntity);

            // 1) 소켓/PlayerId 유효성 체크.
            if (netData.UdpClient == null || clientState.MyPlayerId == 0) return;

            // LastConfirmedTick == -1은 NetworkConnectionSystem.DisconnectInternal이 방금
            // 연결을 리셋했다는 신호다. 이 상태에서는 마지막으로 보낸 TargetTick 기억도
            // 함께 리셋해야 한다 — 그러지 않으면 새 연결의 낮은 틱 번호를 이미 보냈다고
            // 착각해 첫 입력 전송을 건너뛸 수 있다.
            if (clientState.LastConfirmedTick == -1)
            {
                _lastSentTargetTick = int.MinValue;
            }

            // 2) 입력 읽기.
            float throttle = 0f;
            if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) throttle += 1f;
            if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) throttle -= 1f;
            throttle = Mathf.Clamp(throttle, -1f, 1f);

            float turn = 0f;
            if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) turn -= 1f;
            if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) turn += 1f;
            turn = Mathf.Clamp(turn, -1f, 1f);

            bool fireHeld = Input.GetKey(KeyCode.Space);

            // TargetTick 계산: 서버가 다음으로 확정할 틱은 (LastConfirmedTick + 1)이다. 이
            // 입력이 그 틱에 늦지 않게 도착하려면 InputDelayTicks만큼 더 미래를 향해 보내야
            // 한다. LastConfirmedTick의 초기값이 -1이면 다음 틱은 0이므로 targetTick = 0 +
            // InputDelayTicks가 되어, 서버의 첫 틱부터 delay만큼 미리 보낸 값이 자연스럽게
            // 나온다 — 별도의 분기 처리가 필요 없다.
            int targetTick = clientState.LastConfirmedTick + 1 + config.InputDelayTicks;

            if (targetTick == _lastSentTargetTick) return;

            byte[] packet = Protocol.BuildClientInput(clientState.MyPlayerId, targetTick, throttle, turn, fireHeld);
            var netSystem = World.GetExistingSystemManaged<NetworkConnectionSystem>();
            netSystem.SendPacket(netData, packet);

            _lastSentTargetTick = targetTick;
        }
    }
}
