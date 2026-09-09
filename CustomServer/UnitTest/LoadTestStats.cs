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
        public void RecordPongReceived() => Interlocked.Increment(ref _pongsReceived);

        public StatsSnapshot Snapshot() => new StatsSnapshot
        {
            PacketsSent = Interlocked.Read(ref _packetsSent),
            BytesSent = Interlocked.Read(ref _bytesSent),
            PacketsReceived = Interlocked.Read(ref _packetsReceived),
            BytesReceived = Interlocked.Read(ref _bytesReceived),
            BotsJoined = Interlocked.Read(ref _botsJoined),
            BotsJoinFailed = Interlocked.Read(ref _botsJoinFailed),
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
        public long PongsReceived;
    }
}
