using System.Net;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace DeterministicSample.Dots
{
    // ---------------------------------------------------------------------
    // 전원(로컬+원격 구분 없음)이 서버로부터 받은 동일한 입력 시퀀스를 동일한 순서로 재생해
    // 각자 독립적으로 물리를 계산하는 구조다. 예측/재조정 인프라는 존재하지 않는다. 서버는
    // 위치/회전/체력/사망/미사일/스코어 값을 보내지 않으므로, 그런 값을 담는 컴포넌트도
    // 존재하지 않는다. 프로토콜 정의(PacketType, TickCommitData)는 공유 Protocol.cs
    // (Game.Networking 네임스페이스)에 있다.
    // ---------------------------------------------------------------------

    // ---------------------------------------------------------------------
    // 싱글톤: 네트워크 연결 상태 및 소켓을 들고 있는 컴포넌트.
    // UdpClient/IPEndPoint는 매니지드 타입이므로 IComponentData가 아니라
    // class 기반 IComponentData(매니지드 컴포넌트)로 선언한다.
    // ---------------------------------------------------------------------
    public class NetworkConnectionData : IComponentData
    {
        public System.Net.Sockets.UdpClient UdpClient;
        public IPEndPoint ServerEndPoint;
        public System.Threading.Thread ReceiveThread;

        // ReceiveLoop(백그라운드 스레드)와 메인 스레드 System 사이의 유일한 통로. 반드시
        // 스레드-안전 큐만 사용하고, 그 외 필드는 메인 스레드에서만 접근한다. 서버가
        // TickCommit 패킷 하나로 Join/Leave/Input을 전부 실어 보내므로, 클라이언트도 이
        // 큐 하나로 소비한다.
        public System.Collections.Concurrent.ConcurrentQueue<Game.Networking.TickCommitData> TickCommitQueue;

        // JoinResponse 수신 시 수신 스레드가 채워 넣고, 메인 스레드 System
        // (NetworkConnectionSystem.OnUpdate)이 소비 후 false로 되돌린다. 물리 큐
        // (TickCommitQueue)와는 완전히 분리된 별도 필드로 전달한다.
        // PendingExistingPlayers/PendingMyJoinSequence는 신규 접속자가 이미 존재하는
        // 플레이어들을 스폰할 때 필요한 (PlayerId, JoinSequence) 목록이다.
        //
        // [스레드 안전성] HasPendingJoinResponse(volatile bool)는 반드시 가장 마지막에
        // 쓰고 소비 시 가장 먼저 읽어야 한다 — volatile 쓰기는 release, volatile 읽기는
        // acquire 의미를 가지므로, 이 순서를 지키면 앞뒤의 일반 필드(PendingMyPlayerId
        // 등 배열 참조 포함)도 함께 안전하게 가시성이 보장된다.
        public volatile bool HasPendingJoinResponse;
        public byte PendingMyPlayerId;
        public long PendingMyJoinSequence;
        public Game.Networking.JoinedPlayerInfo[] PendingExistingPlayers;

        // 수신 스레드가 거부 응답(GameAlreadyStarted=true)을 받으면 여기에만 표시한다.
        // HasPendingJoinResponse는 세팅하지 않으므로 메인 스레드가 정상 참가 처리로 잘못
        // 들어갈 일이 없다. 메인 스레드(OnUpdate)가 소비 후 false로 되돌린다. 이 필드에
        // 딸린 배열 참조 등 별도로 가시성을 보장해야 할 필드 그룹이 없으므로 volatile이
        // 필요 없다.
        public bool PendingGameAlreadyStarted;

        // NetworkConnectionSystem.OnUpdate가 HasPendingJoinResponse를 소비하면서 여기로
        // 소유권을 넘긴다. SimulationTickSystem이 매 프레임 이 필드를 확인해 null이 아니면
        // ExistingPlayers 스폰 절차를 실행한 뒤 다시 null로 되돌린다. 두 System 모두 메인
        // 스레드에서만 이 필드를 건드리므로 volatile이 필요 없다.
        public Game.Networking.JoinedPlayerInfo[] UnprocessedExistingPlayers;

        public long TimeSinceLastServerMessageMs; // Interlocked로 접근
        public long TotalBytesSent;
        public long TotalBytesReceived;
        public int BytesSentThisSec;
        public int BytesReceivedThisSec;

        // Pong 수신 시 수신 스레드가 채워 넣고, 메인 스레드 System이 소비 후 false로 되돌린다.
        public volatile bool HasPendingPingResult;
        public float PendingPingResult;
    }

    // ---------------------------------------------------------------------
    // 싱글톤: 클라이언트 전역 상태 (연결 여부, 내 플레이어 ID 등)
    //
    // 틱 진행은 서버 TickCommit 도착에 종속되므로 클라이언트가 스스로 세는 로컬 틱 개념은
    // 존재하지 않는다:
    //   - LastConfirmedTick: 지금까지 실제로 시뮬레이션을 실행 완료한 가장 최근 틱 번호.
    //     SimulationTickSystem이 TickCommit을 하나 처리할 때마다 갱신한다. InputCaptureSystem이
    //     TargetTick을 계산할 때, 그리고 NetworkConnectionSystem이 재연결 감지에 사용한다.
    //   - TotalElapsedSimTime: 서버가 매 틱 실측해 보내주는 DeltaTimeSeconds를 누적한 값(초).
    //     발사 쿨다운/리스폰 대기시간 판정은 틱 개수가 아니라 이 누적 시간을 기준으로 한다 —
    //     서버 실측 dt가 매 틱 달라질 수 있어 "N틱 지남"이 "N * 고정간격만큼의 시간이 지남"을
    //     보장하지 않기 때문이다. 모든 클라이언트가 서버가 방송하는 동일한 DeltaTimeSeconds
    //     시퀀스를 동일한 순서로 누적하므로, 부동소수점 덧셈의 결정성에 의해 클라이언트 간에
    //     완전히 동일한 값이 유지된다.
    // ---------------------------------------------------------------------
    public struct ClientStateSingleton : IComponentData
    {
        public byte MyPlayerId;
        public bool IsConnected;         // 소켓이 열려 있고 최초 Join까지 마쳤는지

        // 로컬 플레이어의 사망 여부는 이 싱글톤에 캐시하지 않고 HealthState 컴포넌트에서
        // 직접 읽는다 — 전원 공통 컴포넌트이므로 별도 캐시가 필요 없고, 캐시와 실제 값이
        // 어긋나는 문제도 원천적으로 방지된다.

        public int LastConfirmedTick;    // 마지막으로 시뮬레이션을 완료한 틱. 초기값은 -1(아직 확정된 틱 없음)이어야 하므로 생성 시 명시적으로 설정할 것.
        public float TotalElapsedSimTime; // 누적 실측 시뮬레이션 시간(초). 초기값 0.

        // 방장이 StartGameRequest로 게임을 시작시켰는지 여부. TickCommitData.GameStarted는
        // 그 틱에 시작되었다는 엣지 신호일 뿐이므로, SimulationTickSystem이 그 엣지를
        // 관측한 순간 이 필드에 영속적으로 래치해 이후 모든 틱에서 "게임 중"임을 판단하는
        // 근거로 쓴다.
        public bool IsGameStarted;

        // 서버가 "게임이 이미 시작되었다"며 참가를 거부했는지 여부. true가 되는 순간
        // NetworkConnectionSystem이 SceneTransitionCountdownSeconds를 3초로 채워 넣고 매
        // 프레임 감산하다가 0 이하가 되면 EntryScene으로 전환한다. GameHudBridge는 이 값을
        // 보고 "게임이 이미 시작되었습니다" 메시지와 남은 시간을 표시하기만 하면 된다.
        public bool IsJoinRejected;

        // 서버 응답이 끊겼다고 판단되었는지 여부(연결 이후 ConnectionTimeoutSeconds 동안
        // 아무 패킷도 받지 못함). true가 되는 즉시 재시도 없이 EntryScene으로 전환한다.
        // GameHudBridge가 짧게라도 "연결이 끊겼습니다" 문구를 보여줄 수 있도록 상태만
        // 남겨둔다.
        public bool IsConnectionLost;

        // IsJoinRejected가 true인 동안에만 의미가 있는, EntryScene 전환까지 남은 시간(초).
        // NetworkConnectionSystem이 매 프레임 Time.deltaTime만큼 감산한다.
        public float SceneTransitionCountdownSeconds;

        public float CurrentPingMs;
        public float PingTimer;

        public float KbSentPerSec;
        public float KbReceivedPerSec;
        public float BandwidthTimer;
    }

    // 설정값(서버와 반드시 동일해야 하는 상수들)을 담는 싱글톤.
    public struct SimulationConfig : IComponentData
    {
        public FixedString64Bytes ServerIp;
        public int ServerPort;

        public float ConnectionTimeoutSeconds;

        public float MaxForwardSpeed;
        public float MaxReverseSpeed;
        public float Acceleration;
        public float TurnSpeedDeg;

        // 이동 wrap 기준은 항상 이 ServerWorldHalfExtent다. 서버와 정확히 같은 값이어야
        // 한다. 화면이 1280x720(16:9)으로 고정되어 X/Y 크기가 다르므로 float2로 관리한다.
        public float2 ServerWorldHalfExtent;
        public float2 FallbackHalfExtent;
        public float BoundsMargin;
        public bool UseCameraForBounds;

        public float MissileSpeed;
        public float FireCooldownSeconds;
        public float MissileMaxDistance;

        // Input delay: 클라이언트가 로컬에서 확정된 틱보다 이만큼 미래 틱에 적용될 입력을
        // 미리 서버로 보낸다. 로컬 PC 환경 가정이라 작은 값으로도 충분하지만, 구조 검증을
        // 위해 설정값으로 노출한다.
        public int InputDelayTicks;

        // 사망 후 리스폰까지 대기하는 시간(초). 서버 실측 dt가 매 틱 달라질 수 있어 "틱
        // 개수"가 일정한 시간을 보장하지 않으므로, 발사 쿨다운과 마찬가지로 누적 경과 시간
        // (ClientStateSingleton.TotalElapsedSimTime) 기준으로 판정한다.
        public float RespawnDelaySeconds;

        // 고정 틱레이트 필드(TickRateHz)는 존재하지 않는다 — 실측 DeltaTimeSeconds(서버가
        // 매 틱 보내주는 값)를 물리 dt로 직접 쓰므로, 고정 틱레이트를 가정한 계산이 어디에도
        // 필요 없다.

        // 입력 이력 버퍼(TickInputHistoryElement)를 몇 틱까지 누적 보관할지. 디버깅 및 사후
        // 검증용 원본 근거 자료로만 사용한다 — 발사 쿨다운/리스폰 판정은
        // TotalElapsedSimTime 기반 파생값만으로 이루어지며 이 버퍼에 의존하지 않는다.
        // 기본 180틱(대략 60Hz 기준 3초에 해당하는 개수).
        public int InputHistoryRetentionTicks;

        // 카메라 화면비 기반 "지금 실제로 보이는 화면" 절반 크기. 순수 시각적 용도로만 쓰이며
        // wrap 기준으로는 절대 쓰지 않는다.
        public float2 BoundsHalfExtent;
    }

    // ---------------------------------------------------------------------
    // 플레이어 엔티티에 붙는 컴포넌트들 (로컬/원격 구분 없이 전원 동일 세트)
    // ---------------------------------------------------------------------

    // 이 엔티티가 "내가 조작하는" 플레이어인지 표시하는 태그.
    // [중요] 이 태그는 물리 계산에 전혀 관여하지 않는다 (SimulationTickSystem은 이 태그를
    // 확인하지 않고 모든 PlayerId를 동일한 코드경로로 처리한다). 오직 두 곳에서만 쓰인다:
    //   1) InputCaptureSystem이 "어느 엔티티가 로컬 키보드 입력을 받아야 하는가"를 판단할 때
    //   2) 렌더링/카메라 시스템이 "어느 엔티티를 카메라가 따라가야 하는가"를 판단할 때
    public struct LocalPlayerTag : IComponentData { }

    // 원격(다른 접속자) 플레이어 태그. 마찬가지로 물리에는 관여하지 않으며 렌더링 색상 등
    // 순수 표시 목적으로만 쓰인다.
    public struct RemotePlayerTag : IComponentData { }

    public struct PlayerId : IComponentData
    {
        public byte Value;
    }

    /// <summary>
    /// 탱크의 유일한 물리 진실. 전원이 같은 입력을 같은 방식으로 재생한 결과이므로 예측값이
    /// 아니다. SimulationTickSystem이 매 확정 틱마다 이 값을 갱신하고,
    /// PhysicsToRenderSyncSystem이 이 값을 그대로 읽어 RenderTransform2D로 복사한다 —
    /// 별도의 렌더 오프셋/스무딩은 없다.
    /// </summary>
    public struct TankPhysicsState : IComponentData
    {
        public float2 Position;
        public float RotationDeg;
        public float Speed;
    }

    // 화면에 실제로 그려지는 최종 위치/회전. PhysicsToRenderSyncSystem이 TankPhysicsState를
    // 매 프레임 그대로 복사한다 — 보간은 하지 않는다. 여러 틱이 한 프레임에 몰려 도착해도
    // 중간 위치를 보간하지 않고 최종 확정값만 반영한다.
    public struct RenderTransform2D : IComponentData
    {
        public float2 Position;
        public float RotationDeg;
    }

    public struct HealthState : IComponentData
    {
        public byte Health;
        public bool IsDead;
        public bool WasDead; // 사망 전환 시점(색상 갱신 트리거)만 감지하기 위한 이전 프레임 값
    }

    /// <summary>
    /// 발사 쿨다운 판정용. 이 플레이어가 마지막으로 미사일을 발사한 시점의
    /// ClientStateSingleton.TotalElapsedSimTime 값을 그대로 기록한다.
    /// 판정 공식: (TotalElapsedSimTime - LastFireElapsedTime) >= FireCooldownSeconds 이면
    /// 발사 허용. 반드시 서버가 방송하는 누적 실측 시간 기반으로만 판정해야 한다 — 로컬
    /// 실시간 DateTime/Time.time을 쓰면 프레임 타이밍에 따라 클라이언트마다 다른 결과가
    /// 나와 결정론이 깨진다. 아직 한 번도 발사한 적이 없으면 float.MinValue/2 정도의 충분히
    /// 작은 값으로 초기화해 감산 결과가 항상 큰 양수("쿨다운 없음")가 되도록 한다.
    /// </summary>
    public struct LastFireElapsedTime : IComponentData
    {
        public float Value;
    }

    /// <summary>
    /// 리스폰 판정용. 이 플레이어가 마지막으로 사망 처리된 시점의 TotalElapsedSimTime 값을
    /// 기록한다. 사망 중이 아닐 때는 의미가 없으므로 HealthState.IsDead와 함께 확인해야 한다.
    /// 판정 공식: HealthState.IsDead && (TotalElapsedSimTime - DeathElapsedTime) >=
    ///           config.RespawnDelaySeconds 이면 리스폰.
    /// </summary>
    public struct DeathElapsedTime : IComponentData
    {
        public float Value;
    }

    /// <summary>
    /// 스폰 슬롯 배정 결과. 리스폰 시 새 슬롯을 다시 계산하므로, 클라이언트가 직접 계산해
    /// 보관한다.
    /// </summary>
    public struct SpawnSlotState : IComponentData
    {
        public int Value;
    }

    /// <summary>
    /// 이 플레이어가 서버에 참가한 전역 순서(JoinedPlayerInfo.JoinSequence를 스폰 시점에
    /// 그대로 복사해 엔티티에 영속시킨 값). "현재 방장이 누구인가"를 클라이언트가 스스로
    /// 계산하기 위해서만 필요하다 — 서버는 방장을 별도 필드로 방송하지 않는다. 모든
    /// 클라이언트가 동일한 Join/Leave 이력을 동일한 순서로 재생하므로, 현재 존재하는
    /// 플레이어 엔티티 중 이 값이 가장 작은 사람을 구하면 전 클라이언트가 항상 동일한
    /// 결과를 얻는다 — 방장이 퇴장하면 다음 최소값 보유자가 자동으로 방장이 되므로 별도의
    /// 승계 로직이 필요 없다.
    /// </summary>
    public struct JoinSequenceState : IComponentData
    {
        public long Value;
    }

    /// <summary>
    /// 최근 InputHistoryRetentionTicks 틱만큼 누적 보관하는 원본 입력 이력 버퍼. 판정
    /// 자체는 파생값(LastFireElapsedTime, DeathElapsedTime)만으로 충분하지만, 결정론이
    /// 깨졌을 때 사후 재현/디버깅을 위해 원본 입력을 일정 기간 보관한다.
    /// SimulationTickSystem이 매 확정 틱마다 추가하고, config.InputHistoryRetentionTicks보다
    /// 오래된 항목은 같은 System이 앞에서부터 잘라낸다.
    /// </summary>
    [InternalBufferCapacity(180)]
    public struct TickInputHistoryElement : IBufferElementData
    {
        public int Tick;
        public float Throttle;
        public float Turn;
        public bool Fire;
    }

    // 스코어보드 표시 정보. 서버는 이 값을 집계해 보내주지 않는다 — SimulationTickSystem이
    // 미사일 충돌로 사망이 발생할 때마다 가해자/피해자 엔티티의 이 컴포넌트를 직접
    // 갱신한다. 이 값 자체가 클라이언트 시뮬레이션의 산출물이다.
    public struct ScoreboardEntry : IComponentData
    {
        public int Kills;
        public int Deaths;
    }

    // ---------------------------------------------------------------------
    // 미사일 엔티티에 붙는 컴포넌트들
    // ---------------------------------------------------------------------

    public struct MissileTag : IComponentData { }

    // 미사일도 탱크와 마찬가지로 전원이 같은 입력으로부터 같은 스폰/이동/소멸을 같은 틱에
    // 계산하므로, 서버가 아직 모르는 예측 미사일이라는 상태는 존재하지 않는다.

    /// <summary>
    /// (OwnerId, FireTick) 튜플로 전역 유일성을 보장한다. 발사 쿨다운 판정이 결정론적이라
    /// 한 플레이어가 같은 틱에 두 번 쏠 수 없으므로, 이 튜플만으로 충돌 없이 유일하다 —
    /// 별도 ID 발급자가 필요 없다.
    /// </summary>
    public struct MissileId : IComponentData
    {
        public byte OwnerId;
        public int FireTick;
    }

    public struct MissileOwner : IComponentData
    {
        public byte PlayerId;
    }

    public struct MissileMotion : IComponentData
    {
        public float2 Position;
        public float RotationDeg;
    }

    /// <summary>
    /// 미사일이 살아있는 동안 항상 갱신되는 이동 거리 누적값 — 사거리 소진 판정
    /// (DistanceTraveled >= config.MissileMaxDistance)에 사용한다.
    /// </summary>
    public struct MissileDistanceTraveled : IComponentData
    {
        public float Value;
    }

    // ---------------------------------------------------------------------
    // 이벤트성 싱글톤 버퍼: 하이브리드 브리지(MonoBehaviour)가 이번 프레임에
    // 반드시 알아야 하는 값을 System이 여기 적어 넣으면 브리지가 읽어가고 비운다.
    // ---------------------------------------------------------------------
    public struct DeathVisualChangedElement : IBufferElementData
    {
        public byte PlayerId;
        public bool IsDead;
    }

    public struct PlayerRemovedElement : IBufferElementData
    {
        public byte PlayerId;
    }

    public struct PlayerJoinedElement : IBufferElementData
    {
        public byte PlayerId;
        public bool IsLocal;
    }

    // 싱글톤 태그: 위 3개 버퍼를 보유하는 엔티티를 찾기 위한 마커.
    public struct UiEventSingletonTag : IComponentData { }

    // 원격 플레이어 위치를 보간하기 위한 스냅샷 버퍼는 존재하지 않는다 — 모든 클라이언트가
    // 동일한 시뮬레이션 결과를 직접 가지므로 보간할 근거 자체가 없다.

    // ---------------------------------------------------------------------
    // 런타임에 EntityManager.Instantiate로 인스턴스화할 렌더 프리팹 참조.
    // ---------------------------------------------------------------------
    public struct RenderPrefabs : IComponentData
    {
        public Entity LocalShipPrefab;
        public Entity RemoteShipPrefab;
        public Entity MissilePrefab;
    }
}
