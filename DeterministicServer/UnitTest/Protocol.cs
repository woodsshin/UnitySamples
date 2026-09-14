using System.Collections.Generic;
using System.IO;

namespace Game.Networking
{
    /// <summary>
    /// Game.Networking 네임스페이스로 통일된 프로토콜 정의 및 직렬화 규칙의 원본 파일이다.
    /// 서버(NetworkServer), DOTS 클라이언트(Unity), 봇/부하테스트(LoadTestBot) 세 프로젝트가
    /// 각자의 경로에 동일한 내용으로 물리적 복사본을 두고 사용한다 — 프로젝트 참조나 공유
    /// 라이브러리가 아니므로 컴파일러 차원의 동기화 강제력이 없다. 프로토콜을 변경할 때는
    /// 세 복사본을 반드시 수동으로 동일하게 갱신해야 한다. 한 곳만 갱신하면 패킷 필드 순서/
    /// 타입이 어긋나 송수신 측이 서로의 패킷을 잘못 해석하는 문제가 발생한다.
    ///
    /// 서버는 물리(위치/회전/체력/사망/미사일/스코어)를 전혀 계산하지 않는 순수 입력 우편함 +
    /// 참가/퇴장 중재자다. 모든 물리 값은 클라이언트가 TickCommit으로 전달받는 입력 시퀀스를
    /// 전원 동일한 순서로 재생해 각자 독립적으로 계산한다.
    /// </summary>
    public enum PacketType : byte
    {
        JoinRequest = 1,
        JoinResponse = 2,
        ClientInput = 3,
        TickCommit = 4,
        Ping = 5,
        Pong = 6,
        // 호스트(가장 먼저 접속한 플레이어, 최저 JoinSequence 보유자)가 게임 시작을 요청할 때
        // 보내는 패킷. 서버는 발신자가 현재 호스트인지 검증한 뒤, 성공하면 다음에 확정할
        // 틱에 TickCommit.GameStarted로 방송을 예약한다. 클라이언트는 빌드만 하며 파싱은
        // 서버(TryReadStartGameRequest)만 수행한다.
        StartGameRequest = 7
    }

    /// <summary>
    /// TickCommit 하나에 담긴 입력 한 명분. 물리 값(위치 등)은 전혀 포함하지 않는다.
    /// </summary>
    public struct TickPlayerInput
    {
        public byte PlayerId;
        public float Throttle;
        public float Turn;
        public bool Fire;
    }

    /// <summary>
    /// PlayerId와 함께 전역 단조 증가 카운터 JoinSequence를 싣는다. PlayerId 오름차순은 실제
    /// 접속 순서와 일치한다는 보장이 없으므로(재접속 시 재발급), Join 처리 순서 규약은 반드시
    /// JoinSequence 오름차순을 따른다 — 그래야 모든 클라이언트의 ComputeSpawnTransform 결과가
    /// 일치한다.
    /// </summary>
    public struct JoinedPlayerInfo
    {
        public byte PlayerId;
        public long JoinSequence;
    }

    /// <summary>
    /// JoinResponse에 함께 실리는, 신규 접속 시점에 이미 존재하는 플레이어 전원의 목록. 신규
    /// 클라이언트는 이 목록을 JoinSequence 오름차순으로 순회하며 ComputeSpawnTransform을
    /// 재계산해, 기존 플레이어들의 스폰 위치를 다른 클라이언트와 동일하게 재현한다.
    /// </summary>
    public struct JoinResponseData
    {
        public byte AssignedPlayerId;
        public long AssignedJoinSequence;
        public JoinedPlayerInfo[] ExistingPlayers;

        // 접속 시도 시점에 게임이 이미 시작되어 있었으면 true다 — 참가 자체가 거부된 것이며,
        // AssignedPlayerId/AssignedJoinSequence/ExistingPlayers는 의미 없는 sentinel 값
        // (0/0/빈 배열)이다. 수신 측은 true일 경우 어떤 필드도 사용하지 말고, 접속을 정리한
        // 뒤 재시도하지 않아야 한다. false면 정상 참가이며 나머지 필드가 모두 정상적으로
        // 채워진다.
        public bool GameAlreadyStarted;
    }

    /// <summary>
    /// 파싱된 TickCommit 전체. 이 틱에 처리해야 할 Join/Leave/Input을 모두 담는다. 소비하는
    /// 쪽(DOTS 시스템 등)은 반드시 Join -> Leave -> Input 순서로 처리해야 한다 — 이 구조체
    /// 자체는 순서를 강제하지 않고 세 목록만 담는다.
    ///
    /// DeltaTimeSeconds는 서버가 실측한, 이번 틱을 확정하기까지 실제로 경과한 시간(초)이다.
    /// 서버의 목표 틱레이트(예: 60Hz)는 스레드 스케줄링 오차로 실제 간격과 다를 수 있으므로,
    /// 클라이언트는 고정값(1/60) 대신 이 실측값을 물리 dt로 사용한다. 이 값이 전원에게
    /// 동일하게 방송되므로 가변 dt를 쓰더라도 결정론은 유지된다.
    /// </summary>
    public struct TickCommitData
    {
        public int Tick;
        public float DeltaTimeSeconds;

        // true면 게임이 바로 이 틱에 시작되었다는 엣지(edge) 신호다 — Join/Leave와 마찬가지로
        // 상태가 아니라 사건이며, 전환이 일어난 딱 한 틱에서만 true다. 소비하는 쪽은 이 값을
        // 관찰한 즉시 영속적인 IsGameStarted 상태로 래치해, 이후 모든 틱에서 "게임 중"으로
        // 취급해야 한다. 서버는 이 상태를 매 틱 재전송하지 않는다.
        public bool GameStarted;

        public JoinedPlayerInfo[] JoinedPlayers;
        public byte[] LeftPlayerIds;
        public TickPlayerInput[] Inputs;
    }

    public static class Protocol
    {
        // -------------------------------------------------------------
        // 빌드 (송신 측에서 패킷 바이트를 만들 때 사용)
        // -------------------------------------------------------------

        public static byte[] BuildJoinRequest()
        {
            return new byte[] { (byte)PacketType.JoinRequest };
        }

        /// <summary>
        /// JoinResponse 와이어 포맷:
        ///   byte PacketType, byte AssignedPlayerId, long AssignedJoinSequence,
        ///   byte ExistingPlayerCount, x ExistingPlayerCount: (byte PlayerId, long JoinSequence),
        ///   bool GameAlreadyStarted
        ///
        /// existingPlayers는 신규 접속자보다 먼저 참가한 플레이어 전원의 (PlayerId,
        /// JoinSequence) 목록이다. 신규 클라이언트는 이를 JoinSequence 오름차순으로 순회하며
        /// ComputeSpawnTransform을 재계산해 기존 플레이어들의 스폰 위치를 재현한다. 호출부
        /// (서버)가 이미 정렬해서 넘겨줄 것을 전제하며, 이 함수는 재정렬하지 않는다.
        ///
        /// gameAlreadyStarted가 true면 거부 응답이다 — 이 경우 호출부는 관례상
        /// assignedPlayerId=0, assignedJoinSequence=0, existingPlayers=빈 목록을 넘긴다. 이
        /// 함수는 값을 그대로 실어 보낼 뿐 그 관례를 강제하지 않는다.
        /// </summary>
        public static byte[] BuildJoinResponse(byte assignedPlayerId, long assignedJoinSequence,
            IReadOnlyList<JoinedPlayerInfo> existingPlayers, bool gameAlreadyStarted)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write((byte)PacketType.JoinResponse);
            bw.Write(assignedPlayerId);
            bw.Write(assignedJoinSequence);

            bw.Write((byte)existingPlayers.Count);
            for (int i = 0; i < existingPlayers.Count; i++)
            {
                bw.Write(existingPlayers[i].PlayerId);
                bw.Write(existingPlayers[i].JoinSequence);
            }

            bw.Write(gameAlreadyStarted);

            return ms.ToArray();
        }

        /// <summary>
        /// targetTick: 이 입력이 적용될 목표 틱. 계산 공식은
        /// (ClientStateSingleton.LastConfirmedTick + 1 + InputDelayTicks) — 서버가 다음으로
        /// 확정할 틱에 delay를 더한 값이다(InputCaptureSystem 참고). 클라이언트가 스스로
        /// 진행시키는 로컬 틱 개념은 존재하지 않는다 — 틱 진행은 전적으로 서버 확정에
        /// 종속된다.
        /// </summary>
        public static byte[] BuildClientInput(byte playerId, int targetTick, float throttle, float turn, bool fire)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write((byte)PacketType.ClientInput);
            bw.Write(playerId);
            bw.Write(targetTick);
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

        public static byte[] BuildPong(long echoedClientTicks)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write((byte)PacketType.Pong);
            bw.Write(echoedClientTicks);
            return ms.ToArray();
        }

        /// <summary>
        /// StartGameRequest 와이어 포맷: byte PacketType, byte PlayerId. 페이로드가 단순해
        /// MemoryStream/BinaryWriter 없이 직접 구성한다. playerId는 발신자가 주장하는 호스트
        /// ID일 뿐이며, 실제 호스트 여부 검증은 서버(GetCurrentHostId)가 수행한다.
        /// </summary>
        public static byte[] BuildStartGameRequest(byte playerId)
        {
            return new byte[] { (byte)PacketType.StartGameRequest, playerId };
        }

        /// <summary>
        /// TickCommit 와이어 포맷:
        ///   byte PacketType, int Tick, float DeltaTimeSeconds, bool GameStarted,
        ///   byte JoinCount,  x JoinCount:  byte PlayerId, long JoinSequence
        ///   byte LeaveCount, x LeaveCount: byte PlayerId
        ///   byte InputCount, x InputCount: byte PlayerId, float Throttle, float Turn, bool Fire
        ///
        /// JoinCount 목록은 PlayerId와 JoinSequence를 함께 싣는다 — Join 처리 순서 규약이
        /// JoinSequence 오름차순이기 때문이며, JoinResponse.ExistingPlayers와 동일한 재계산
        /// 로직(ComputeSpawnTransform)을 타므로 순서 기준이 일치해야 한다.
        ///
        /// joinedPlayers/leftPlayerIds/inputs는 호출부(서버)가 각자의 정렬 기준(Join은
        /// JoinSequence 오름차순, Leave/Input은 PlayerId 오름차순)으로 이미 정렬해서 넘겨줄
        /// 것을 전제로 한다 — 이 함수는 재정렬하지 않는다. 서버가 보내는 순서가 곧 모든
        /// 클라이언트의 처리 순서이므로 정렬 책임은 호출부에 있다.
        ///
        /// deltaTimeSeconds: 서버가 실측한 이번 틱의 실제 경과 시간(초).
        ///
        /// gameStarted: TickCommitData.GameStarted와 동일한 엣지 신호 규약 — 게임 시작이
        /// 확정된 바로 그 틱 호출에서만 true여야 한다. 호출부(ServerLoop)가 이 규약을 지킬
        /// 책임이 있으며, 이 함수는 값을 검증 없이 그대로 실어 보낸다.
        /// </summary>
        public static byte[] BuildTickCommit(int tick, float deltaTimeSeconds, bool gameStarted,
            IReadOnlyList<JoinedPlayerInfo> joinedPlayers, IReadOnlyList<byte> leftPlayerIds,
            IReadOnlyList<TickPlayerInput> inputs)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write((byte)PacketType.TickCommit);
            bw.Write(tick);
            bw.Write(deltaTimeSeconds);
            bw.Write(gameStarted);

            bw.Write((byte)joinedPlayers.Count);
            for (int i = 0; i < joinedPlayers.Count; i++)
            {
                bw.Write(joinedPlayers[i].PlayerId);
                bw.Write(joinedPlayers[i].JoinSequence);
            }

            bw.Write((byte)leftPlayerIds.Count);
            for (int i = 0; i < leftPlayerIds.Count; i++) bw.Write(leftPlayerIds[i]);

            bw.Write((byte)inputs.Count);
            for (int i = 0; i < inputs.Count; i++)
            {
                bw.Write(inputs[i].PlayerId);
                bw.Write(inputs[i].Throttle);
                bw.Write(inputs[i].Turn);
                bw.Write(inputs[i].Fire);
            }

            return ms.ToArray();
        }

        // -------------------------------------------------------------
        // 파싱 (수신 측에서 도착한 바이트를 해석할 때 사용)
        // -------------------------------------------------------------

        public static PacketType PeekType(byte[] data)
        {
            return data.Length == 0 ? default : (PacketType)data[0];
        }

        /// <summary>
        /// JoinResponse를 파싱한다. ExistingPlayers는 서버가 보낸 순서(JoinSequence 오름차순)를
        /// 그대로 보존한다. 봇처럼 물리를 계산하지 않는 소비자는 result.ExistingPlayers를
        /// 무시해도 안전하다 — 파싱 자체는 항상 전체를 읽어야 다음 패킷과의 바이트 경계가
        /// 맞는다.
        /// </summary>
        public static bool TryReadJoinResponse(byte[] data, out JoinResponseData result)
        {
            result = default;
            if (data.Length < 10) return false; // PacketType(1) + PlayerId(1) + JoinSequence(8) 최소 길이

            using var ms = new MemoryStream(data);
            using var br = new BinaryReader(ms);
            if ((PacketType)br.ReadByte() != PacketType.JoinResponse) return false;

            byte assignedPlayerId = br.ReadByte();
            long assignedJoinSequence = br.ReadInt64();

            int existingCount = br.ReadByte();
            var existingPlayers = new JoinedPlayerInfo[existingCount];
            for (int i = 0; i < existingCount; i++)
            {
                existingPlayers[i] = new JoinedPlayerInfo
                {
                    PlayerId = br.ReadByte(),
                    JoinSequence = br.ReadInt64()
                };
            }

            bool gameAlreadyStarted = br.ReadBoolean();

            result = new JoinResponseData
            {
                AssignedPlayerId = assignedPlayerId,
                AssignedJoinSequence = assignedJoinSequence,
                ExistingPlayers = existingPlayers,
                GameAlreadyStarted = gameAlreadyStarted
            };
            return true;
        }

        /// <summary>
        /// 서버 쪽에서 도착한 StartGameRequest 패킷을 읽는다. 페이로드가 (PacketType, PlayerId)
        /// 2바이트뿐이므로 BinaryReader 없이 인덱스로 직접 읽는다.
        /// </summary>
        public static bool TryReadStartGameRequest(byte[] data, out byte playerId)
        {
            playerId = 0;
            if (data.Length < 2) return false;
            if ((PacketType)data[0] != PacketType.StartGameRequest) return false;
            playerId = data[1];
            return true;
        }

        /// <summary>
        /// 서버 쪽에서 도착한 ClientInput 패킷을 읽는다. (playerId, targetTick, throttle, turn, fire)를 그대로 반환.
        /// </summary>
        public static bool TryReadClientInput(byte[] data, out byte playerId, out int targetTick,
            out float throttle, out float turn, out bool fire)
        {
            playerId = 0;
            targetTick = 0;
            throttle = 0f;
            turn = 0f;
            fire = false;

            using var ms = new MemoryStream(data);
            using var br = new BinaryReader(ms);
            if ((PacketType)br.ReadByte() != PacketType.ClientInput) return false;

            playerId = br.ReadByte();
            targetTick = br.ReadInt32();
            throttle = br.ReadSingle();
            turn = br.ReadSingle();
            fire = br.ReadBoolean();
            return true;
        }

        /// <summary>
        /// TickCommit 패킷 전체를 파싱한다. 반환된 TickCommitData의 각 목록은 서버가 보낸 순서를
        /// 그대로 보존한다(Join은 JoinSequence 오름차순, Leave/Input은 PlayerId 오름차순) — 이
        /// 함수는 재정렬하지 않는다. 소비하는 쪽이 Join -> Leave -> Input 순서로 처리해야 한다.
        /// </summary>
        public static bool TryReadTickCommit(byte[] data, out TickCommitData result)
        {
            result = default;

            using var ms = new MemoryStream(data);
            using var br = new BinaryReader(ms);
            if ((PacketType)br.ReadByte() != PacketType.TickCommit) return false;

            int tick = br.ReadInt32();
            float deltaTimeSeconds = br.ReadSingle();
            bool gameStarted = br.ReadBoolean();

            int joinCount = br.ReadByte();
            var joined = new JoinedPlayerInfo[joinCount];
            for (int i = 0; i < joinCount; i++)
            {
                joined[i] = new JoinedPlayerInfo
                {
                    PlayerId = br.ReadByte(),
                    JoinSequence = br.ReadInt64()
                };
            }

            int leaveCount = br.ReadByte();
            var left = new byte[leaveCount];
            for (int i = 0; i < leaveCount; i++) left[i] = br.ReadByte();

            int inputCount = br.ReadByte();
            var inputs = new TickPlayerInput[inputCount];
            for (int i = 0; i < inputCount; i++)
            {
                inputs[i] = new TickPlayerInput
                {
                    PlayerId = br.ReadByte(),
                    Throttle = br.ReadSingle(),
                    Turn = br.ReadSingle(),
                    Fire = br.ReadBoolean()
                };
            }

            result = new TickCommitData
            {
                Tick = tick,
                DeltaTimeSeconds = deltaTimeSeconds,
                GameStarted = gameStarted,
                JoinedPlayers = joined,
                LeftPlayerIds = left,
                Inputs = inputs
            };
            return true;
        }

        /// <summary>
        /// TickCommit에서 targetPlayerId 한 명의 입력 행만 뽑아낸다. 봇처럼 "내 입력만 알면 되는"
        /// 소비자가 전체 목록을 매 틱 할당하지 않도록 돕는 편의 함수. Join/Leave 여부 판단이
        /// 필요 없는 단순 소비자용이며, 물리를 직접 재현해야 하는 소비자(DOTS 클라이언트)는
        /// TryReadTickCommit으로 전체를 받아 처리 순서 규약을 지켜야 한다.
        /// </summary>
        public static bool TryFindInputForPlayer(TickCommitData commit, byte targetPlayerId, out TickPlayerInput input)
        {
            for (int i = 0; i < commit.Inputs.Length; i++)
            {
                if (commit.Inputs[i].PlayerId == targetPlayerId)
                {
                    input = commit.Inputs[i];
                    return true;
                }
            }
            input = default;
            return false;
        }
    }
}
