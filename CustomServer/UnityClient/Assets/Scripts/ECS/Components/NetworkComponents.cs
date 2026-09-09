using System;
using System.Net;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CustomClient.Dots
{
    // ---------------------------------------------------------------------
    // 패킷 타입 (서버 CustomServer.PacketType과 반드시 값이 동일해야 한다)
    // ---------------------------------------------------------------------
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

        // ReceiveLoop(백그라운드 스레드)와 메인 스레드 System 사이의 유일한 통로.
        // 반드시 스레드-안전 큐만 사용하고, 그 외 필드는 메인 스레드에서만 접근한다.
        public System.Collections.Concurrent.ConcurrentQueue<ServerStateFrame> StateQueue;
        public System.Collections.Concurrent.ConcurrentQueue<MissileStateFrame> MissileStateQueue;
        public System.Collections.Concurrent.ConcurrentQueue<ScoreboardFrame> ScoreboardQueue;

        public long TimeSinceLastServerMessageMs; // Interlocked로 접근
        public long TotalBytesSent;
        public long TotalBytesReceived;
        public int BytesSentThisSec;
        public int BytesReceivedThisSec;

        // Pong 수신 시 수신 스레드가 채워 넣고, 메인 스레드 System이 소비 후 false로 되돌린다.
        // bool/float 단일 값이라 근사적으로 안전하지만, race conditiond이 우려되면 lock을 사용해도 된다.
        public volatile bool HasPendingPingResult;
        public float PendingPingResult;
    }

    public struct ServerStateFrame
    {
        public int ServerTick;
        public PlayerStateWire[] States;
    }

    public struct MissileStateFrame
    {
        public int ServerTick;
        public MissileStateWire[] Missiles;
    }

    public struct ScoreboardFrame
    {
        public ScoreEntryWire[] Entries;
    }

    // 네트워크로 온 바이트를 그대로 옮겨 담는 순수 값 구조체 (블리터블).
    public struct PlayerStateWire
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

    public struct MissileStateWire
    {
        public ushort Id;
        public byte OwnerId;
        public float X;
        public float Y;
        public float Rotation;
    }

    public struct ScoreEntryWire
    {
        public byte PlayerId;
        public int Kills;
        public int Deaths;
    }

    // ---------------------------------------------------------------------
    // 싱글톤: 클라이언트 전역 상태 (연결 여부, 내 플레이어 ID, 틱 카운터 등)
    // ---------------------------------------------------------------------
    public struct ClientStateSingleton : IComponentData
    {
        public byte MyPlayerId;
        public int ClientTick;
        public bool IsConnected;         // 소켓이 열려 있고 최초 Join까지 마쳤는지
        public bool HasReconnectedAtLeastOnce;
        public float ReconnectTimer;
        public bool IsLocalPlayerDead;

        public float CurrentPingMs;
        public float PingTimer;

        public float KbSentPerSec;
        public float KbReceivedPerSec;
        public float BandwidthTimer;

        public float FireCooldownTimer;
    }

    // 설정값(서버와 반드시 동일해야 하는 상수들)을 담는 싱글톤.
    // 기존 MonoBehaviour의 [SerializeField] 인스펙터 필드를 대체한다.
    public struct SimulationConfig : IComponentData
    {
        public FixedString64Bytes ServerIp;
        public int ServerPort;

        public float ConnectionTimeoutSeconds;
        public float ReconnectIntervalSeconds;

        public float MaxForwardSpeed;
        public float MaxReverseSpeed;
        public float Acceleration;
        public float TurnSpeedDeg;

        // 탱크 이동(LocalPlayerFixedStepSystem.SimulateTankStep)의 wrap 기준은 항상
        // 이 ServerWorldHalfExtent다. 서버 WORLD_HALF_EXTENT_X/Y와 정확히 같은 값이어야 하며,
        // 아래 BoundsHalfExtent(카메라 화면비에 따라 이보다 좁아질 수 있는 시각적 계산값)를
        // wrap에 쓰면 안 된다. wrap이 일어나는 좌표 자체가 서버와 달라져 재조정이 매 틱 어긋난다.
        // 화면이 1280x720(16:9)으로 고정되어 X/Y 크기가 서로 다르므로 float 단일 값이 아니라
        // float2(x, y 각각의 half-extent)로 관리한다.
        public float2 ServerWorldHalfExtent;
        public float2 FallbackHalfExtent;
        public float BoundsMargin;
        public bool UseCameraForBounds;

        public float ErrorThreshold;
        public float RotationErrorThresholdDeg;

        public float SmoothingSpeed;
        public float SnapThreshold;
        public float RotationSnapThresholdDeg;

        public float InterpolationDelay;

        public float MissileSpeed;
        public float FireCooldownSeconds;
        public float PredictedMissileTimeoutSeconds;
        // 서버 MISSILE_MAX_DISTANCE와 정확히 같은 값이어야 한다. 미사일은 탱크와 달리 화면
        // 경계에서 wrap하지 않고 발사 방향으로 계속 직진하므로, 오직 이 사거리만으로 수명이
        // 제한된다. 화면이 16:9로 고정되며 가로가 세로보다 훨씬 넓어졌으므로, 사거리는 더 긴
        // 축(가로, ServerWorldHalfExtent.x*2) 기준으로 계산된 값(서버 참고)을 그대로 따른다.
        // PredictedMissileTimeoutSeconds는 이 값을 MissileSpeed로 나눈 시간보다 넉넉히 길어야
        // 한다(그렇지 않으면 정상 비행 중인 예측 미사일이 사거리에 닿기도 전에 시간 기반
        // 안전장치에 의해 먼저 지워진다).
        public float MissileMaxDistance;

        // 카메라 화면비 기반 "지금 실제로 보이는 화면" 절반 크기. 스폰 반경 경고 등 순수
        // 시각적 용도로만 쓰이며, 이동 시뮬레이션의 wrap 기준으로는 절대 쓰지 않는다
        // (wrap 기준은 항상 위 ServerWorldHalfExtent).
        public float2 BoundsHalfExtent; // UpdateBoundsSystem이 매 프레임 갱신
    }

    // ---------------------------------------------------------------------
    // 플레이어 엔티티에 붙는 컴포넌트들
    // ---------------------------------------------------------------------

    // 이 플레이어가 로컬(내가 조작하는) 플레이어인지 표시하는 태그.
    public struct LocalPlayerTag : IComponentData { }

    // 원격(다른 접속자) 플레이어 태그.
    public struct RemotePlayerTag : IComponentData { }

    // 이번 프레임에 막 리스폰한(직전까지 죽어있다가 이번 서버 프레임에서 살아난) 플레이어에게만
    // 한 프레임 동안 붙는 태그. ServerStateApplySystem이 리스폰 전환을 감지한 즉시 붙이고,
    // - 로컬 플레이어라면 같은 System의 ReconcileLocalPlayer가 이 태그를 보고 일반적인
    //   재조정(스냅/보정/유지) 대신 즉시 서버 위치로 점프시킨다.
    // - 원격 플레이어라면 RemotePlayerInterpolationSystem이 이 태그가 붙은 엔티티를 보간
    //   대상에서 제외하고 최신 ServerConfirmedState로 직접 스냅한다.
    // 두 System 모두 처리 후 반드시 이 태그를 제거해야 한다 — 리스폰 점프는 정확히 한 프레임만
    // 유효한 일회성 이벤트이고, 다음 프레임부터는 이미 새 스폰 위치를 기준으로 정상적인
    // 예측/보간이 재개되어야 한다(태그가 남아 있으면 계속 보간을 건너뛰어 뚝뚝 끊겨 보인다).
    public struct JustRespawnedTag : IComponentData { }

    public struct PlayerId : IComponentData
    {
        public byte Value;
    }

    // 서버가 마지막으로 보내온 확정 상태 (reconciliation의 기준점).
    public struct ServerConfirmedState : IComponentData
    {
        public int ServerTick;
        public float2 Position;
        public float RotationDeg;
        public float CurrentSpeed;
        public byte Health;
        public bool IsDead;
    }

    // 로컬 플레이어 예측 상태 (기존 _predictedPos/_predictedRot/_predictedSpeed).
    public struct PredictedTankState : IComponentData
    {
        public float2 Position;
        public float RotationDeg;
        public float Speed;
    }

    // 오차 스무딩용 렌더 오프셋 (기존 _renderErrorOffset/_renderRotErrorOffset).
    public struct RenderErrorOffset : IComponentData
    {
        public float2 Position;
        public float RotationDeg;
    }

    // 화면에 실제로 그려지는 최종 위치/회전 (로컬: 예측+오프셋, 원격: 보간 결과).
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

    // pending input 버퍼 엘리먼트 (기존 _pendingInputs 리스트를 DynamicBuffer로 대체).
    [InternalBufferCapacity(64)]
    public struct PendingInputElement : IBufferElementData
    {
        public int Tick;
        public float Throttle;
        public float Turn;
    }

    // ---------------------------------------------------------------------
    // 원본의 List<Snapshot> _snapshotBuffer를 그대로 옮긴 전역(싱글톤) 버퍼.
    // 서버 상태 프레임이 도착할 때마다 "그 순간의 전체 플레이어 위치/회전"을 한 덩어리로
    // 추가한다. 원격 플레이어 보간(InterpolationSystem)은 이 버퍼에서 렌더 타임 기준
    // 앞뒤 두 스냅샷을 찾아 그 사이를 보간한다. 플레이어별로 따로 버퍼를 두지 않는 이유는,
    // 원본이 "같은 렌더 타임 t"로 전체 플레이어를 동시에 보간하기 때문이다(개별 보간 시
    // 플레이어마다 다른 t를 쓰면 동시에 도착한 서버 프레임인데도 서로 다른 시점으로
    // 보이는 미세한 불일치가 생길 수 있다).
    // ---------------------------------------------------------------------
    public struct PlayerSnapshotSample : IBufferElementData
    {
        public byte PlayerId;
        public float2 Position;
        public float RotationDeg;
    }

    // 스냅샷 하나(한 서버 프레임)의 헤더. PlayerSnapshotSample들과 별도의 버퍼에 두고
    // SnapshotSampleRange로 몇 번째 샘플부터 몇 개가 이 스냅샷 소속인지 표시한다.
    // (DynamicBuffer of DynamicBuffer가 불가능하므로 평평한 구조로 표현)
    public struct SnapshotFrameHeader : IBufferElementData
    {
        public float LocalTimeStamp;
        public int SampleStartIndex;
        public int SampleCount;
    }

    // 스코어보드 표시 정보 (플레이어 엔티티에 붙여 UI 브리지가 읽어간다).
    public struct ScoreboardEntry : IComponentData
    {
        public int Kills;
        public int Deaths;
    }

    // ---------------------------------------------------------------------
    // 사망 시 스프라이트(머티리얼) 교체용 컴포넌트.
    // ---------------------------------------------------------------------

    /// <summary>
    /// ShipRenderAuthoring.Baker가 실어 보내는, "사망 시 보여줄 머티리얼" 참조. Material은
    /// UnityEngine 오브젝트라 매니지드 컴포넌트(class 기반 IComponentData)로만 담을 수 있다.
    /// PlayerVisualColorSystem이 엔티티당 최초 1회만 이 값을 읽어 RuntimeMaterialIds를 채우고,
    /// 그 이후에는 이 컴포넌트 자체를 다시 참조하지 않는다(등록된 BatchMaterialID만 사용).
    /// </summary>
    public class DeathMaterialRef : IComponentData
    {
        public UnityEngine.Material DeadMaterial;
    }

    /// <summary>
    /// EntitiesGraphicsSystem.RegisterMaterial로 한 번 등록해 얻은 생존/사망 머티리얼의
    /// BatchMaterialID를 엔티티별로 캐시한다. 문서 권장대로 "런타임 머티리얼 교체는 등록된
    /// literal Material ID로"라는 경로를 쓰기 위함 — 매 사망 전환마다 재등록하면 등록 테이블만
    /// 계속 불어나므로, 엔티티 생성 후 최초 1회만 등록하고 이 컴포넌트에 결과를 저장해 재사용한다.
    /// AliveMaterialID는 베이킹된 원본 머티리얼(RenderMeshArray의 정적 배열 항목)을 등록한 ID,
    /// DeadMaterialID는 DeathMaterialRef.DeadMaterial을 등록한 ID다. Initialized가 false인 동안은
    /// 아직 등록 전이라는 뜻이며, PlayerVisualColorSystem이 이 상태를 보고 1회성 등록을 수행한다.
    ///
    /// [중요] BatchMaterialID/BatchMeshID는 Unity.Rendering(Entities.Graphics 패키지 네임스페이스 —
    /// MaterialMeshInfo, EntitiesGraphicsSystem이 사는 곳)이 아니라 UnityEngine.Rendering
    /// (엔진 코어, UnityEngine.CoreModule 소속 — BatchRendererGroup이 정의된 곳)에 있다. 이름이
    /// 비슷한 두 네임스페이스가 존재해서 헷갈리기 쉬운데, 컴파일 에러(CS0234: Unity.Rendering에
    /// BatchMaterialID가 없음)로 실제로 걸린 적이 있으니 반드시 UnityEngine.Rendering으로 참조한다.
    /// </summary>
    public struct RuntimeMaterialIds : IComponentData
    {
        public UnityEngine.Rendering.BatchMaterialID AliveMaterialID;
        public UnityEngine.Rendering.BatchMaterialID DeadMaterialID;
        public bool Initialized;
    }

    // 참고: 연결 종료 플레이어 감지는 SeenThisFrameTag 같은 태그 컴포넌트 대신
    // ServerStateApplySystem이 매 프레임 NativeHashSet<byte>로 활성 ID 목록을 만들어
    // 비교하는 방식을 사용한다 (구조 변경 비용이 더 적다).

    // ---------------------------------------------------------------------
    // 미사일 엔티티에 붙는 컴포넌트들
    // ---------------------------------------------------------------------

    public struct MissileTag : IComponentData { }

    // 서버가 아직 인지하지 못한, 본인이 쏜 예측 미사일.
    public struct PredictedMissileTag : IComponentData { }

    public struct MissileId : IComponentData
    {
        public ushort Value;       // 서버 확정 전에는 0(무효), 매칭되면 서버 Id로 설정
        public bool HasServerId;
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

    public struct PredictedMissileSpawnTime : IComponentData
    {
        public float Value; // Time.time 기준, 타임아웃 판정용
    }

    // 예측 단계(PredictedMissileTag 보유) 미사일 전용. 발사 지점으로부터 로컬에서 누적한
    // 이동 거리(유닛)를 담는다. 서버 확정 미사일(PredictedMissileTag 제거됨)은 서버가 사거리
    // 초과 시 목록에서 빼주는 것만으로 충분하므로 이 값을 쓰지 않는다 — MonoBehaviour의
    // MissileView.DistanceTraveled와 정확히 같은 역할.
    public struct PredictedMissileDistance : IComponentData
    {
        public float Value;
    }

    // 참고: 미사일도 마찬가지로 activeServerIds(NativeHashSet<ushort>) 비교 방식을 사용하며
    // 별도 태그 컴포넌트는 두지 않는다 (MissileSystem.ApplyMissileServerState 참고).

    // ---------------------------------------------------------------------
    // 이벤트성 싱글톤 버퍼: 하이브리드 브리지(MonoBehaviour)가 이번 프레임에
    // 반드시 알아야 하는 값(사망 색상 전환, 플레이어 제거 등)을 System이 여기 적어 넣으면
    // 브리지가 읽어가고 비운다.
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

    // 싱글톤 태그: SnapshotFrameHeader + PlayerSnapshotSample 버퍼를 보유하는 엔티티 마커.
    public struct SnapshotBufferSingletonTag : IComponentData { }

    // ---------------------------------------------------------------------
    // 런타임에 EntityManager.Instantiate로 찍어낼 렌더 프리팹 참조.
    // GameBootstrapAuthoring에서 베이킹되며, 실제 GameObject 프리팹은 Entities Graphics용
    // ShipRenderAuthoring / MissileRenderAuthoring이 붙은 SubScene 프리팹이어야 한다.
    // ---------------------------------------------------------------------
    public struct RenderPrefabs : IComponentData
    {
        public Entity LocalShipPrefab;
        public Entity RemoteShipPrefab;
        public Entity MissilePrefab;
    }
}
