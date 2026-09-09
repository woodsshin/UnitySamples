using Unity.Entities;

namespace CustomClient.Dots
{
    /// <summary>
    /// 원본 ApplyScoreboardState를 옮긴 System. 서버가 매 틱 전체 스냅샷을 보내므로
    /// 로컬 값을 덮어써도 된다. 존재하지 않는 PlayerId 항목 무시한다.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(ServerStateApplySystem))]
    public partial class ScoreboardApplySystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<SimulationConfig>();

            // NetworkConnectionData가 존재할 때만 시스템이 업데이트되도록 조건 추가
            RequireForUpdate<NetworkConnectionData>();
        }

        protected override void OnUpdate()
        {
            var configEntity = SystemAPI.GetSingletonEntity<SimulationConfig>();
            var netData = EntityManager.GetComponentObject<NetworkConnectionData>(configEntity);

            while (netData.ScoreboardQueue.TryDequeue(out var frame))
            {
                foreach (var e in frame.Entries)
                {
                    foreach (var (playerId, scoreboard, entity) in
                             SystemAPI.Query<RefRO<PlayerId>, RefRW<ScoreboardEntry>>().WithEntityAccess())
                    {
                        if (playerId.ValueRO.Value == e.PlayerId)
                        {
                            scoreboard.ValueRW.Kills = e.Kills;
                            scoreboard.ValueRW.Deaths = e.Deaths;
                            break;
                        }
                    }
                }
            }
        }
    }
}
