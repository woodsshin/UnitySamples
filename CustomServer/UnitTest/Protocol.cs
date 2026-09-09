using System.IO;

namespace LoadTestBot
{
    /// <summary>
    /// CustomServer.cs와 동일한 패킷 정의 및 직렬화 규칙.
    /// 필드 순서/타입이 서버와 한 바이트라도 어긋나면 서버가 패킷을 잘못 읽거나
    /// 조용히 무시하므로, 서버 소스가 바뀌면 이 파일도 함께 맞춰야 한다.
    /// </summary>
    public enum PacketType : byte
    {
        JoinRequest = 1,
        JoinResponse = 2,
        ClientInput = 3,
        ServerState = 4,
        Ping = 5,
        Pong = 6,
        MissileState = 7,
        ScoreboardState = 8
    }

    public struct PlayerSnapshot
    {
        public byte Id;
        public int LastProcessedTick;
        public float X;
        public float Y;
        public float Rotation;
        public float CurrentSpeed;
        public byte Health;
        public bool IsDead;
    }

    public static class Protocol
    {
        public static byte[] BuildJoinRequest()
        {
            return new byte[] { (byte)PacketType.JoinRequest };
        }

        public static byte[] BuildClientInput(byte playerId, int clientTick, float throttle, float turn, bool fire)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write((byte)PacketType.ClientInput);
            bw.Write(playerId);
            bw.Write(clientTick);
            bw.Write(throttle);
            bw.Write(turn);
            bw.Write(fire);
            return ms.ToArray();
        }

        public static byte[] BuildPing(byte playerId, long clientTicks)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write((byte)PacketType.Ping);
            bw.Write(playerId);
            bw.Write(clientTicks);
            return ms.ToArray();
        }

        public static PacketType PeekType(byte[] data)
        {
            return data.Length == 0 ? default : (PacketType)data[0];
        }

        public static bool TryReadJoinResponse(byte[] data, out byte assignedId)
        {
            assignedId = 0;
            if (data.Length < 2) return false;

            using var ms = new MemoryStream(data);
            using var br = new BinaryReader(ms);
            if ((PacketType)br.ReadByte() != PacketType.JoinResponse) return false;

            assignedId = br.ReadByte();
            return true;
        }

        /// <summary>
        /// ServerState 브로드캐스트에서 targetPlayerId 한 명의 행만 뽑아낸다.
        /// 봇은 자기 자신의 위치/생존 여부만 필요하므로 전체 목록을 매 틱 할당하지 않는다.
        /// </summary>
        public static bool TryReadServerStateForPlayer(byte[] data, byte targetPlayerId, out PlayerSnapshot snapshot)
        {
            snapshot = default;

            using var ms = new MemoryStream(data);
            using var br = new BinaryReader(ms);

            if ((PacketType)br.ReadByte() != PacketType.ServerState) return false;

            br.ReadInt32(); // serverTick - 봇 의사결정에는 사용하지 않음
            int count = br.ReadInt32();

            for (int i = 0; i < count; i++)
            {
                byte id = br.ReadByte();
                int lastProcessedTick = br.ReadInt32();
                float x = br.ReadSingle();
                float y = br.ReadSingle();
                float rotation = br.ReadSingle();
                float speed = br.ReadSingle();
                byte health = br.ReadByte();
                bool isDead = br.ReadBoolean();

                if (id == targetPlayerId)
                {
                    snapshot = new PlayerSnapshot
                    {
                        Id = id,
                        LastProcessedTick = lastProcessedTick,
                        X = x,
                        Y = y,
                        Rotation = rotation,
                        CurrentSpeed = speed,
                        Health = health,
                        IsDead = isDead
                    };
                    return true;
                }
            }

            return false;
        }
    }
}
