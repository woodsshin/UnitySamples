using System.Threading;

namespace LoadTestBot
{
    /// <summary>
    /// 모든 봇이 공유하는 스레드 안전 카운터 모음. 봇 개수가 많을 수 있으므로
    /// lock 대신 Interlocked만 사용해 경합 비용을 최소화한다.
    /// </summary>
    public class LoadTestStats
    {
        private long _packetsSent;
        private long _bytesSent;
        private long _packetsReceived;
        private long _bytesReceived;
        private long _botsJoined;
        private long _botsJoinFailed;
        private long _botsJoinRaceRetried;
        private long _pongsReceived;

        public void RecordSent(int bytes)
        {
            Interlocked.Increment(ref _packetsSent);
            Interlocked.Add(ref _bytesSent, bytes);
        }

        public void RecordReceived(int bytes)
        {
            Interlocked.Increment(ref _packetsReceived);
            Interlocked.Add(ref _bytesReceived, bytes);
        }

        public void RecordJoined() => Interlocked.Increment(ref _botsJoined);
        public void RecordJoinFailed() => Interlocked.Increment(ref _botsJoinFailed);

        /// <summary>
        /// 게임 시작 직후 좁은 레이스 윈도우에서 GameAlreadyStarted 거부를 받고 1회 재시도할
        /// 때만 호출된다. 재시도 성공 여부와 무관하게 "재시도가 발생했다"는 사실만 집계하며,
        /// 뒤이은 RecordJoined/RecordJoinFailed로 그 재시도가 구제에 성공했는지 판단할 수 있다.
        /// </summary>
        public void RecordJoinRaceRetried() => Interlocked.Increment(ref _botsJoinRaceRetried);
        public void RecordPongReceived() => Interlocked.Increment(ref _pongsReceived);

        public StatsSnapshot Snapshot() => new StatsSnapshot
        {
            PacketsSent = Interlocked.Read(ref _packetsSent),
            BytesSent = Interlocked.Read(ref _bytesSent),
            PacketsReceived = Interlocked.Read(ref _packetsReceived),
            BytesReceived = Interlocked.Read(ref _bytesReceived),
            BotsJoined = Interlocked.Read(ref _botsJoined),
            BotsJoinFailed = Interlocked.Read(ref _botsJoinFailed),
            BotsJoinRaceRetried = Interlocked.Read(ref _botsJoinRaceRetried),
            PongsReceived = Interlocked.Read(ref _pongsReceived)
        };
    }

    public struct StatsSnapshot
    {
        public long PacketsSent;
        public long BytesSent;
        public long PacketsReceived;
        public long BytesReceived;
        public long BotsJoined;
        public long BotsJoinFailed;
        public long BotsJoinRaceRetried;
        public long PongsReceived;
    }
}
