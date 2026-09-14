using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 서버(DeterministicSampleServer, 순수 입력 우편함이며 물리를 전혀 계산하지 않음)와 DOTS
/// 클라이언트(SimulationTickSystem.cs 등)와 완전히 동일한 결정론적 재생(replay) 방식으로
/// 동작하는 MonoBehaviour 클라이언트:
///   - 서버는 매 틱 그 틱에 살아있는 전원의 확정 입력만 TickCommit으로 방송한다.
///   - 모든 클라이언트(이 MonoBehaviour, DOTS 클라이언트, 서로 다른 접속자의 클라이언트 전부)가
///     그 입력 시퀀스를 정확히 같은 순서로 재생해 각자 독립적으로 물리를 계산한다 — 그 결과가
///     전원 동일하므로 예측·재조정·보간이 필요 없다.
/// 물리/프로토콜 로직은 SimulationTickSystem.cs의 계산을 그대로 옮긴 결정론적 시뮬레이션이며,
/// 스프라이트 생성, 네임플레이트/HP 바 GameObject 관리, OnGUI 등 렌더링/표시 코드는 프로토콜과
/// 무관한 독립적인 부분이다.
///
/// 방장(가장 먼저 접속한 플레이어) 판정과 로비/게임 시작 기능을 제공한다. 서버는 "누가
/// 방장이다"라는 값을 별도로 방송하지 않는다 — 모든 클라이언트가 JoinSequence(참가 순서)가
/// 가장 작은 현재 접속자를 스스로 계산해 항상 동일한 결론에 도달한다(ComputeIsHost 참고).
/// 게임이 시작되기 전(로비)에는 플레이어가 스폰되어 서로를 볼 수는 있지만 이동/발사/미사일/
/// 리스폰 시뮬레이션은 진행되지 않는다.
/// </summary>
public class CustomClientSample : MonoBehaviour
{
    // ------------------------------------------------------------------
    // [프로토콜] 서버(Protocol.cs, Game.Networking 네임스페이스)와 반드시 동일해야 하는 정의.
    // 이 파일은 그 프로토콜의 물리적 복사본이다 — 공유 라이브러리 참조가 아니므로, 프로토콜을
    // 바꿀 때는 서버/DOTS 클라이언트/봇/이 파일 네 곳을 전부 수동으로 동일하게 갱신해야 한다.
    // ------------------------------------------------------------------
    public enum PacketType : byte
    {
        JoinRequest = 1,
        JoinResponse = 2,
        ClientInput = 3,
        TickCommit = 4,
        Ping = 5,
        Pong = 6,
        StartGameRequest = 7
    }

    /// <summary>TickCommit의 JoinedPlayers/JoinResponse의 ExistingPlayers 한 명분.</summary>
    private struct JoinedPlayerInfo
    {
        public byte PlayerId;
        public long JoinSequence;
    }

    /// <summary>TickCommit에 담긴 입력 한 명분. 물리 값(위치 등)은 전혀 포함하지 않는다.</summary>
    private struct TickPlayerInput
    {
        public byte PlayerId;
        public float Throttle;
        public float Turn;
        public bool Fire;
    }

    /// <summary>파싱된 TickCommit 전체. 이 틱에 처리해야 할 Join/Leave/Input을 모두 담는다.</summary>
    private struct TickCommitData
    {
        public int Tick;
        public float DeltaTimeSeconds;
        public bool GameStarted; // 엣지 신호 — 이 틱에서만 true. IsGameStarted로 래치해서 쓴다.
        public List<JoinedPlayerInfo> JoinedPlayers;
        public List<byte> LeftPlayerIds;
        public List<TickPlayerInput> Inputs;
    }

    /// <summary>
    /// 플레이어 한 명의 유일한 물리+점수 진실. DOTS SimulationTickSystem의 TankPhysicsState +
    /// HealthState + ScoreboardEntry + JoinSequenceState + LastFireElapsedTime + DeathElapsedTime +
    /// SpawnSlotState를 전부 합친 것과 정확히 같은 정보를 담는다 — ECS는 컴포넌트를 나누어
    /// 관리하지만 MonoBehaviour는 클래스 하나에 모아 둔다는 차이만 있을 뿐, 갱신 공식과 순서는
    /// 완전히 동일해야 한다.
    /// </summary>
    private class PlayerRuntimeState
    {
        public byte Id;
        public long JoinSequence;

        public Vector2 Position;
        public float RotationDeg;
        public float Speed;

        public byte Health;
        public bool IsDead;

        public int Kills;
        public int Deaths;

        public int SpawnSlot;

        // 발사 쿨다운/리스폰 판정은 반드시 서버가 방송하는 누적 실측 시간(_totalElapsedSimTime)
        // 기준이어야 한다 — 로컬 실시간(Time.time)을 쓰면 프레임 타이밍에 따라 클라이언트마다
        // 다른 결과가 나와 결정론이 깨진다. 아직 발사/사망 이력이 없으면 float.MinValue/2로
        // 초기화해 감산 결과가 항상 큰 양수(="쿨다운/리스폰 대기 없음")가 되게 한다.
        public float LastFireElapsedTime = float.MinValue / 2f;
        public float DeathElapsedTime = float.MinValue / 2f;

        public GameObject ShipObj;
    }

    /// <summary>
    /// 미사일 한 발의 유일한 물리 진실. DOTS의 MissileId + MissileOwner + MissileMotion +
    /// MissileDistanceTraveled를 합친 것과 동일하다. (OwnerId, FireTick) 쌍이 전역 유일
    /// 식별자다 — 발사 쿨다운 판정이 결정론적이라 한 플레이어가 같은 틱에 두 번 쏠 수 없으므로,
    /// 별도의 ID 발급자 없이 이 쌍만으로 충돌 없이 유일하다.
    /// </summary>
    private class MissileRuntimeState
    {
        public byte OwnerId;
        public int FireTick;
        public Vector2 Position;
        public float RotationDeg;
        public float DistanceTraveled;
        public GameObject GameObj;
    }

    private class HealthBarView
    {
        public GameObject BackgroundObj;
        public GameObject ForegroundObj;
        public SpriteRenderer ForegroundRenderer;
    }

    // ------------------------------------------------------------------
    // 인스펙터 노출 설정값
    // ------------------------------------------------------------------

    [Header("Server Connection")]
    [SerializeField] private string _serverIp = "127.0.0.1";
    [SerializeField] private int _serverPort = 9050;

    [Header("Connection Loss Detection")]
    // 서버 응답이 이 시간 동안 없으면 연결이 끊긴 것으로 판단해 재시도 없이 EntryScene으로
    // 이동한다. 이 값이 유일한 판정 기준이다.
    [SerializeField] private float _connectionTimeoutSeconds = 3.0f;

    [Header("Tank Movement (must exactly mirror server/DOTS SimulationConfig)")]
    [SerializeField] private float _maxForwardSpeed = 3.0f;
    [SerializeField] private float _maxReverseSpeed = 3.0f;
    [SerializeField] private float _acceleration = 4.0f;
    [SerializeField] private float _turnSpeedDeg = 160f;

    // [중요] 우주선 이동은 이 값(X/Y 각각)을 기준으로 wrap(반대편에서 재등장)한다. 서버/DOTS
    // SimulationConfig.ServerWorldHalfExtent와 정확히 같은 값이어야 한다. 화면이 1280x720(16:9)
    // 고정이므로 정사각형이 아니라 16:9 비율을 반영한 값이다.
    [Header("Play Area Bounds (must exactly equal server/DOTS ServerWorldHalfExtent)")]
    [SerializeField] private Vector2 _serverWorldHalfExtent = new Vector2(7.1111111f, 4.0f);
    [SerializeField] private bool _useCameraForBounds = true;
    [SerializeField] private Vector2 _fallbackHalfExtent = new Vector2(7.1111111f, 4.0f);
    [SerializeField] private float _boundsMargin = 0.5f;

    [Header("Missile (must exactly mirror server/DOTS SimulationConfig)")]
    [SerializeField] private float _missileSpeed = 12.0f;
    [SerializeField] private float _fireCooldownSeconds = 0.3f;
    [SerializeField] private float _missileMaxDistance = 14.2222222f;

    [Header("Determinism (must exactly mirror server/DOTS SimulationConfig)")]
    [Tooltip("클라이언트가 로컬에서 마지막으로 확정된 틱보다 이만큼 미래 틱에 적용될 입력을 미리 " +
             "서버로 보낸다. DOTS SimulationConfigAuthoring.InputDelayTicks와 반드시 같아야 한다.")]
    [SerializeField] private int _inputDelayTicks = 2;
    [Tooltip("사망 후 리스폰까지 대기하는 시간(초, 누적 실측 시간 기준). DOTS " +
             "SimulationConfigAuthoring.RespawnDelaySeconds와 반드시 같아야 한다.")]
    [SerializeField] private float _respawnDelaySeconds = 3.0f;

    // 스폰/충돌 판정 상수. DOTS SimulationTickSystem의 동일 이름 상수와 정확히 같은 값이어야
    // 한다 — 여기서만 다르게 두면 이 클라이언트만 다른 스폰 위치/충돌 판정을 계산해 결정론이
    // 깨진다. 인스펙터로 실수로 바꿀 수 없도록 상수로 고정해둔다.
    private const float SPAWN_RADIUS = 1.2f;
    private const int SPAWN_SLOTS = 16;
    private const float MISSILE_RADIUS = 0.06f;
    private const float TANK_COLLISION_RADIUS = 0.175f;

    // DOTS SimulationTickSystem.MAX_HEALTH와 반드시 같은 값이어야 HP 바 채움 비율이 정확하다.
    private const int MAX_HEALTH = 10;

    // ------------------------------------------------------------------
    // 연결/런타임 상태
    // ------------------------------------------------------------------

    private UdpClient _udpClient;
    private IPEndPoint _serverEP;
    private byte _myPlayerId = 0;

    // 틱 진행 자체가 서버 TickCommit 도착에 종속되므로, 클라이언트가 스스로 세는 로컬 틱
    // 개념은 없다 — 대신 아래 값들로 진행 상태를 추적한다.
    private int _lastConfirmedTick = -1;     // 지금까지 실제로 처리를 완료한 가장 최근 틱.
    private float _totalElapsedSimTime = 0f; // 서버가 매 틱 실측해 보내주는 DeltaTimeSeconds의 누적값(초).
    private bool _isGameStarted = false;     // 호스트가 게임을 시작시켰는지(엣지 신호를 래치한 영속값).
    private bool _isHost = false;            // Update()에서 매 프레임 한 번만 계산해 캐시(OnGUI는 프레임당 여러 번 호출될 수 있음).

    // 참가 거부(게임이 이미 시작됨) 상태와 EntryScene 전환까지 남은 시간(초). 거부 응답을
    // 받으면 true로 래치하고 카운트다운을 3초로 채운 뒤, Update()가 매 프레임 감산하다가 0
    // 이하가 되면 SceneManager.LoadScene("EntryScene")을 호출한다.
    private bool _joinRejected = false;
    private float _sceneTransitionCountdownSeconds = 0f;

    // 서버 응답이 끊긴 것으로 판단되었는지 여부. true가 되는 즉시(재시도 없이) EntryScene으로
    // 전환한다.
    private bool _isConnectionLost = false;

    // EntryScene 전환은 한 번만 트리거되어야 한다 — LoadScene 호출 후 씬이 실제로 언로드되기
    // 까지 몇 프레임이 걸릴 수 있으므로, 그 사이 Update()가 다시 실행되어 조건을 재확인하고
    // LoadScene을 중복 호출하는 것을 막는다.
    private bool _hasTriggeredSceneTransition = false;

    // 같은 TargetTick에 대해 중복 전송하지 않기 위한 기억값.
    private int _lastSentTargetTick = int.MinValue;

    // ReceiveLoop(백그라운드 스레드)가 채워 넣고 Update()(메인 스레드)가 소비하는, 아직 처리되지
    // 않은 JoinResponse. Unity API(GameObject 생성 등)는 메인 스레드 전용이므로 스레드 간 전달이
    // 필요하다. 이 값은 접속당 딱 한 번만 발생하는 이벤트라 성능이 중요하지 않으므로, DOTS의
    // volatile+release/acquire 순서 대신 단순한 lock으로 안전하게 넘긴다.
    private readonly object _pendingJoinLock = new object();
    private bool _hasPendingJoinResponse;
    private bool _pendingJoinRejected;
    private byte _pendingMyPlayerId;
    private long _pendingMyJoinSequence;
    private List<JoinedPlayerInfo> _pendingExistingPlayers;

    private Camera _mainCamera;
    private Vector2 _boundsHalfExtent; // x=좌우 절반 폭, y=상하 절반 높이. 매 프레임(카메라 사용 시) 갱신됨.

    // 서버가 패킷 하나(TickCommit)로 Join/Leave/Input을 전부 실어 보내므로, 소비도 이 큐
    // 하나로 충분하다.
    private readonly ConcurrentQueue<TickCommitData> _tickCommitQueue = new();

    // 플레이어별 유일한 물리+점수 진실. Key = PlayerId. 방장 판정(ComputeIsHost)과
    // 스코어보드(OnGUI)도 전부 이 딕셔너리에서 직접 읽는다 — 별도 캐시를 두지 않는다.
    private readonly Dictionary<byte, PlayerRuntimeState> _players = new();

    // 현재 살아있는 모든 미사일. 순서를 유지하지 않으며, ProcessMissiles가 처리 시점마다
    // (OwnerId 오름차순, 동률이면 FireTick 오름차순)으로 다시 정렬한다.
    private readonly List<MissileRuntimeState> _missiles = new();

    private readonly Dictionary<byte, GameObject> _nameplateViews = new();
    private readonly Dictionary<byte, SpriteRenderer> _playerRenderers = new(); // 사망 시 색상 갱신용 캐시
    private readonly HashSet<byte> _deadPlayerIds = new(); // 현재 사망 상태로 표시 중인 플레이어 (색을 매 틱 다시 칠하지 않도록 변화 시점만 갱신)
    private readonly Dictionary<byte, HealthBarView> _healthBarViews = new();

    private Sprite _shipSprite;
    private Sprite _missileSprite;
    private Sprite _hpBarSprite;

    private long _timeSinceLastServerMessageMs = 0;  // ReceiveLoop(백그라운드 스레드)가 갱신, Interlocked로 접근

    // RTT / Ping 측정 변수
    private float _currentPingMs = 0f;
    private float _pingTimer = 0f;
    private const float PING_INTERVAL = 1.0f;

    // 네트워크 트래픽 측정 변수
    private long _totalBytesSent = 0;
    private long _totalBytesReceived = 0;
    private int _bytesSentThisSec = 0;
    private int _bytesReceivedThisSec = 0;
    private float _kbSentPerSec = 0f;
    private float _kbReceivedPerSec = 0f;
    private float _bandwidthTimer = 0f;

    // FPS 표시용. 매 프레임 값이 심하게 튀는 것을 막기 위해 짧은 주기로만 갱신한다.
    private float _fpsAccumTime;
    private int _fpsAccumFrames;
    private float _displayedFps;
    private const float FpsRefreshInterval = 0.5f;

    private void Awake()
    {
        _shipSprite = CreateTriangleShipSprite(64);
        _missileSprite = CreateMissileSprite(16);
        _hpBarSprite = CreateSolidBarSprite(4);
    }

    // 탱크 스프라이트가 월드 공간에서 차지하는 지름(유닛). 서버/DOTS SPAWN_RADIUS(1.2)/
    // SPAWN_SLOTS(16) 기준 인접 스폰 슬롯 간 최소 거리(~0.468유닛)보다 작아야, 여러 명이
    // 동시에 스폰됐을 때 탱크 그림끼리 겹쳐 보이지 않는다.
    private const float SHIP_WORLD_DIAMETER = 0.35f;

    // 네임플레이트를 탱크 위쪽에 띄우는 수직 오프셋(유닛).
    private const float NAMEPLATE_Y_OFFSET = SHIP_WORLD_DIAMETER * 1.328f;

    // HP 바를 탱크 바로 위(네임플레이트보다는 아래)에 띄우는 수직 오프셋.
    private const float HP_BAR_Y_OFFSET = SHIP_WORLD_DIAMETER * 0.75f;
    private const float HP_BAR_WIDTH = SHIP_WORLD_DIAMETER * 1.2f;  // 탱크 지름보다 살짝 넓게
    private const float HP_BAR_HEIGHT = 0.06f;

    private Sprite CreateTriangleShipSprite(int res)
    {
        Texture2D tex = new Texture2D(res, res);
        Color[] colors = new Color[res * res];

        // 삼각형 꼭짓점 (로컬 스프라이트 좌표, 픽셀 단위)
        // apex: 위쪽 뾰족한 끝 (진행 방향, +Y)
        // baseLeft/baseRight: 아래쪽 밑변 (선체 후미)
        Vector2 apex = new Vector2(res * 0.5f, res * 0.92f);
        Vector2 baseLeft = new Vector2(res * 0.12f, res * 0.08f);
        Vector2 baseRight = new Vector2(res * 0.88f, res * 0.08f);

        for (int y = 0; y < res; y++)
        {
            for (int x = 0; x < res; x++)
            {
                Vector2 p = new Vector2(x + 0.5f, y + 0.5f);
                colors[y * res + x] = IsPointInTriangle(p, apex, baseLeft, baseRight)
                    ? Color.white
                    : Color.clear;
            }
        }

        tex.SetPixels(colors);
        tex.Apply();

        float pixelsPerUnit = res / SHIP_WORLD_DIAMETER;
        return Sprite.Create(tex, new Rect(0, 0, res, res), new Vector2(0.5f, 0.5f), pixelsPerUnit);
    }

    private static bool IsPointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        float d1 = Sign(p, a, b);
        float d2 = Sign(p, b, c);
        float d3 = Sign(p, c, a);

        bool hasNeg = (d1 < 0) || (d2 < 0) || (d3 < 0);
        bool hasPos = (d1 > 0) || (d2 > 0) || (d3 > 0);

        return !(hasNeg && hasPos);
    }

    private static float Sign(Vector2 p1, Vector2 p2, Vector2 p3)
    {
        return (p1.x - p3.x) * (p2.y - p3.y) - (p2.x - p3.x) * (p1.y - p3.y);
    }

    private const float MISSILE_WORLD_DIAMETER = 0.12f;

    private Sprite CreateMissileSprite(int res)
    {
        Texture2D tex = new Texture2D(res, res);
        Color[] colors = new Color[res * res];

        Vector2 center = new Vector2(res * 0.5f, res * 0.5f);
        float radiusPx = res * 0.45f;

        for (int y = 0; y < res; y++)
        {
            for (int x = 0; x < res; x++)
            {
                Vector2 p = new Vector2(x + 0.5f, y + 0.5f);
                colors[y * res + x] = (p - center).sqrMagnitude <= radiusPx * radiusPx
                    ? Color.white
                    : Color.clear;
            }
        }

        tex.SetPixels(colors);
        tex.Apply();

        float pixelsPerUnit = res / MISSILE_WORLD_DIAMETER;
        return Sprite.Create(tex, new Rect(0, 0, res, res), new Vector2(0.5f, 0.5f), pixelsPerUnit);
    }

    private Sprite CreateSolidBarSprite(int res)
    {
        Texture2D tex = new Texture2D(res, res);
        Color[] colors = new Color[res * res];
        for (int i = 0; i < colors.Length; i++) colors[i] = Color.white;

        tex.SetPixels(colors);
        tex.Apply();

        return Sprite.Create(tex, new Rect(0, 0, res, res), new Vector2(0f, 0.5f), res);
    }

    private void Start()
    {
        EnforceFixedResolution();

        _mainCamera = Camera.main;
        if (_mainCamera == null)
        {
            Debug.LogWarning("[Client] Camera.main을 찾지 못했습니다. " +
                $"화면 경계를 Fallback Half Extent({_fallbackHalfExtent})로 고정합니다.");
        }
        UpdateBounds();

        if (_boundsHalfExtent.x < SPAWN_RADIUS || _boundsHalfExtent.y < SPAWN_RADIUS)
        {
            Debug.LogWarning(
                $"[Client] 계산된 화면 경계({_boundsHalfExtent.x:F2}, {_boundsHalfExtent.y:F2})가 " +
                $"서버 스폰 반경({SPAWN_RADIUS})보다 좁습니다. 해상도가 1280x720으로 " +
                "고정되지 않았거나 Orthographic Size 설정이 예상과 다를 수 있습니다.");
        }

        ConnectToServer();
    }

    /// <summary>
    /// 카메라의 실제 Orthographic Size와 화면비로 "지금 실제로 보이는 화면"의 절반 폭/높이를
    /// 계산해 _boundsHalfExtent에 저장한다. 이동 시뮬레이션(SimulateTankStep)은 이 값을 wrap
    /// 기준으로 쓰지 않는다 — wrap은 항상 _serverWorldHalfExtent만 사용한다. 이 값은 순수
    /// 시각적 용도(스폰 반경 경고 등)로만 쓰인다.
    /// </summary>
    private void UpdateBounds()
    {
        Vector2 computed;
        if (_useCameraForBounds && _mainCamera != null && _mainCamera.orthographic)
        {
            float halfHeight = _mainCamera.orthographicSize;
            float halfWidth = halfHeight * _mainCamera.aspect;

            computed = new Vector2(
                Mathf.Max(0.1f, halfWidth - _boundsMargin),
                Mathf.Max(0.1f, halfHeight - _boundsMargin));
        }
        else
        {
            computed = _fallbackHalfExtent;
        }

        _boundsHalfExtent = new Vector2(
            Mathf.Min(computed.x, _serverWorldHalfExtent.x),
            Mathf.Min(computed.y, _serverWorldHalfExtent.y));
    }

    private const int FIXED_SCREEN_WIDTH = 1280;
    private const int FIXED_SCREEN_HEIGHT = 720;

    /// <summary>
    /// 화면(창) 해상도를 1280x720으로 고정한다. wrap 기준 플레이 영역이 정확히 16:9(1280x720)
    /// 화면비를 전제로 계산되어 있으므로, 실제 창 크기가 이 비율에서 벗어나면 화면에 레터박스가
    /// 생기거나 플레이 영역 밖의 여백이 보이는 시각적 불일치가 생긴다.
    /// </summary>
    private void EnforceFixedResolution()
    {
#if UNITY_EDITOR
        Debug.LogWarning(
            "[Client] 에디터에서는 Screen.SetResolution으로 Game 뷰 해상도를 바꿀 수 없습니다. " +
            "wrap 기준 플레이 영역이 1280x720(16:9)을 전제로 계산되어 있으므로, Game 뷰 상단 " +
            "해상도 드롭다운에서 '1280x720' 프리셋을 직접 추가하고 선택해주세요.");
#endif
#if UNITY_STANDALONE || UNITY_EDITOR
        if (Screen.width != FIXED_SCREEN_WIDTH || Screen.height != FIXED_SCREEN_HEIGHT)
        {
            Screen.SetResolution(FIXED_SCREEN_WIDTH, FIXED_SCREEN_HEIGHT, FullScreenMode.Windowed);
        }
#else
        Debug.LogWarning(
            "[Client] 이 플랫폼에서는 Screen.SetResolution으로 해상도를 강제할 수 없습니다. " +
            "플레이 영역 wrap 기준이 1280x720(16:9)을 전제로 계산되어 있으므로, 플랫폼별 설정에서 " +
            "화면비를 16:9로 맞춰야 벽 위치가 시각적으로 정확합니다.");
#endif
    }

    /// <summary>
    /// 서버에 최초 연결한다. Start()에서 딱 한 번만 호출되며, 이후 연결이 끊기면
    /// UpdateConnectionLossCheck가 재시도 없이 곧바로 EntryScene 전환을 예약한다.
    /// </summary>
    public void ConnectToServer()
    {
        DisconnectInternal();

        Interlocked.Exchange(ref _timeSinceLastServerMessageMs, DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond);

        _serverEP = new IPEndPoint(IPAddress.Parse(_serverIp), _serverPort);
        _udpClient = new UdpClient();

        // Windows에서 UDP 소켓이 ICMP Port Unreachable을 받으면 다음 Send()/Receive() 호출에서
        // SocketException(WSAECONNRESET)이 발생할 수 있다(서버, DOTS 클라이언트와 동일한 조치).
        // SendPacket/ReceiveLoop가 SocketException을 방어적으로 처리하긴 하지만, 예외 자체가
        // 발생하지 않도록 억제해두는 편이 근본적이다 — 억제가 없으면 예외 발생 빈도에 따라
        // 재연결이 불필요하게 잦아지거나 입력/핑 전송이 자주 유실될 수 있다.
        try
        {
            const int SIO_UDP_CONNRESET = -1744830452;
            _udpClient.Client.IOControl((IOControlCode)SIO_UDP_CONNRESET, new byte[] { 0 }, null);
        }
        catch { }

        _udpClient.Connect(_serverEP);

        UdpClient socketForThisConnection = _udpClient;
        Thread rxThread = new Thread(() => ReceiveLoop(socketForThisConnection)) { IsBackground = true };
        rxThread.Start();

        SendPacket(BuildJoinRequestPacket());

        Debug.Log($"[Client] Connecting to {_serverIp}:{_serverPort}...");
    }

    /// <summary>
    /// 소켓/틱 진행 상태/플레이어 목록/미사일 등 이번 연결(세션)에 속한 모든 상태를 리셋한다.
    /// ConnectToServer의 첫 단계로 호출되며, OnDestroy에서도 호출된다.
    /// </summary>
    private void DisconnectInternal()
    {
        if (_udpClient != null)
        {
            try { _udpClient.Close(); } catch { }
            _udpClient = null;
        }

        _myPlayerId = 0;
        _lastConfirmedTick = -1;
        _totalElapsedSimTime = 0f;
        _isGameStarted = false;
        _isHost = false;
        _lastSentTargetTick = int.MinValue;

        lock (_pendingJoinLock)
        {
            _hasPendingJoinResponse = false;
            _pendingJoinRejected = false;
            _pendingExistingPlayers = null;
        }

        while (_tickCommitQueue.TryDequeue(out _)) { }

        ClearAllPlayerViews();
        ClearAllMissileViews();
    }

    private void SendPacket(byte[] bytes)
    {
        try
        {
            _udpClient.Send(bytes, bytes.Length);
            Interlocked.Add(ref _totalBytesSent, bytes.Length);
            Interlocked.Add(ref _bytesSentThisSec, bytes.Length);
        }
        catch (ObjectDisposedException)
        {
            // 종료 과정에서 소켓이 이미 닫힌 경우.
        }
        catch (SocketException)
        {
            // 일시적 전송 오류. 재접속을 시도하지 않으므로 다음 틱에서 다시 시도될 뿐이다.
        }
    }

    // ------------------------------------------------------------------
    // 패킷 빌드 (Protocol.cs BuildXxx와 정확히 동일한 와이어 포맷)
    // ------------------------------------------------------------------

    private static byte[] BuildJoinRequestPacket()
    {
        return new byte[] { (byte)PacketType.JoinRequest };
    }

    private byte[] BuildClientInputPacket(int targetTick, float throttle, float turn, bool fire)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write((byte)PacketType.ClientInput);
        bw.Write(_myPlayerId);
        bw.Write(targetTick);
        bw.Write(throttle);
        bw.Write(turn);
        bw.Write(fire);
        return ms.ToArray();
    }

    private byte[] BuildPingPacket()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write((byte)PacketType.Ping);
        bw.Write(_myPlayerId);
        bw.Write(DateTime.UtcNow.Ticks);
        return ms.ToArray();
    }

    private byte[] BuildStartGameRequestPacket()
    {
        return new byte[] { (byte)PacketType.StartGameRequest, _myPlayerId };
    }

    /// <summary>
    /// 서버가 보내는 패킷은 JoinResponse/TickCommit/Pong 세 가지다. 이 스레드(백그라운드)는
    /// Unity API를 직접 건드릴 수 없으므로, JoinResponse는 _pendingXxx 필드(락으로 보호)에,
    /// TickCommit은 스레드 안전 큐에 적재만 하고 실제 처리는 전부 Update()(메인 스레드)가 한다.
    /// </summary>
    private void ReceiveLoop(UdpClient socket)
    {
        IPEndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
        while (socket == _udpClient)
        {
            try
            {
                byte[] data = socket.Receive(ref remoteEP);

                Interlocked.Add(ref _totalBytesReceived, data.Length);
                Interlocked.Add(ref _bytesReceivedThisSec, data.Length);

                if (data.Length == 0) continue;

                using var ms = new MemoryStream(data);
                using var br = new BinaryReader(ms);
                PacketType type = (PacketType)br.ReadByte();

                Interlocked.Exchange(ref _timeSinceLastServerMessageMs, DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond);

                if (type == PacketType.JoinResponse)
                {
                    byte assignedPlayerId = br.ReadByte();
                    long assignedJoinSequence = br.ReadInt64();

                    int existingCount = br.ReadByte();
                    var existingPlayers = new List<JoinedPlayerInfo>(existingCount);
                    for (int i = 0; i < existingCount; i++)
                    {
                        existingPlayers.Add(new JoinedPlayerInfo { PlayerId = br.ReadByte(), JoinSequence = br.ReadInt64() });
                    }

                    bool gameAlreadyStarted = br.ReadBoolean();

                    lock (_pendingJoinLock)
                    {
                        if (gameAlreadyStarted)
                        {
                            // 게임이 이미 시작된 뒤의 접속 시도 — 서버가 명단에 올리지 않은
                            // 거부 응답이다(assignedPlayerId는 항상 0 sentinel). Update()가
                            // 이 플래그를 보고 소켓을 정리하며 재시도하지 않는다.
                            _pendingJoinRejected = true;
                        }
                        else
                        {
                            _pendingMyPlayerId = assignedPlayerId;
                            _pendingMyJoinSequence = assignedJoinSequence;
                            _pendingExistingPlayers = existingPlayers;
                            _hasPendingJoinResponse = true;
                        }
                    }
                }
                else if (type == PacketType.TickCommit)
                {
                    int tick = br.ReadInt32();
                    float deltaTimeSeconds = br.ReadSingle();
                    bool gameStarted = br.ReadBoolean();

                    int joinCount = br.ReadByte();
                    var joined = new List<JoinedPlayerInfo>(joinCount);
                    for (int i = 0; i < joinCount; i++)
                    {
                        joined.Add(new JoinedPlayerInfo { PlayerId = br.ReadByte(), JoinSequence = br.ReadInt64() });
                    }

                    int leaveCount = br.ReadByte();
                    var left = new List<byte>(leaveCount);
                    for (int i = 0; i < leaveCount; i++) left.Add(br.ReadByte());

                    int inputCount = br.ReadByte();
                    var inputs = new List<TickPlayerInput>(inputCount);
                    for (int i = 0; i < inputCount; i++)
                    {
                        inputs.Add(new TickPlayerInput
                        {
                            PlayerId = br.ReadByte(),
                            Throttle = br.ReadSingle(),
                            Turn = br.ReadSingle(),
                            Fire = br.ReadBoolean()
                        });
                    }

                    _tickCommitQueue.Enqueue(new TickCommitData
                    {
                        Tick = tick,
                        DeltaTimeSeconds = deltaTimeSeconds,
                        GameStarted = gameStarted,
                        JoinedPlayers = joined,
                        LeftPlayerIds = left,
                        Inputs = inputs
                    });
                }
                else if (type == PacketType.Pong)
                {
                    long sendTimeTicks = br.ReadInt64();
                    float rttMs = (float)(DateTime.UtcNow.Ticks - sendTimeTicks) / TimeSpan.TicksPerMillisecond;
                    _currentPingMs = rttMs;
                }
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                // 소켓이 닫혔거나 일시적 네트워크 오류. socket == _udpClient 검사로 다음 루프에서 자연
                // 종료된다. SIO_UDP_CONNRESET 억제가 플랫폼에 따라 우회될 가능성에 대비해, DOTS
                // 클라이언트(NetworkConnectionSystem.cs)와 동일하게 짧게 양보한다 — 억제가 실제로
                // 우회되어 SocketException이 연달아 발생하면 이 스레드가 CPU를 독점해
                // _timeSinceLastServerMessageMs 갱신이 밀릴 위험이 있다.
                Thread.Sleep(10);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Client] ReceiveLoop error: {ex.Message}");
            }
        }
    }

    private void Update()
    {
        UpdateFpsCounter();

        // 씬 전환이 이미 트리거되었으면(EntryScene 로딩 중) 더 이상 게임 로직을 진행할 이유가
        // 없다 — 로딩이 끝나기 전 몇 프레임 동안 불필요한 네트워크/렌더 처리를 막는다.
        if (_hasTriggeredSceneTransition) return;

        UpdateBounds();
        UpdateTrafficMetrics();
        SendPingCheck();
        UpdateConnectionLossCheck();
        UpdateSceneTransitionCountdown();

        // 거부/연결끊김으로 카운트다운이 진행 중이면(아직 트리거 전이라도) 이하의 게임 로직은
        // 더 이상 의미가 없다 — 곧 EntryScene으로 전환될 상태이므로 여기서 멈춘다.
        if (_joinRejected || _isConnectionLost) return;

        // 반드시 이 순서대로: (1) 밀린 JoinResponse부터 반영해 기존 플레이어를 스폰하고, (2)
        // 그 다음에 TickCommit들을 처리해야 한다 — 그래야 TickCommit.Inputs에 기존 플레이어의
        // 입력이 섞여 와도 대응하는 엔티티가 이미 존재한다(DOTS SimulationTickSystem과 동일한
        // 순서 의존성).
        ConsumePendingJoinResponse();
        ProcessTickCommits();
        _isHost = ComputeIsHost();

        // 입력 캡처와 전송만 수행한다(물리 예측 없음). 반드시 ProcessTickCommits() 다음에
        // 호출해야 TargetTick 계산이 이번 프레임에 갱신된 최신 _lastConfirmedTick을 사용한다.
        CaptureAndSendInput();

        // 렌더링 동기화는 항상 마지막 — 이번 프레임에 계산이 끝난 최종 물리 값만 그대로
        // 화면에 반영한다(보간 없음, DOTS PhysicsToRenderSyncSystem과 동일한 설계 원칙).
        RenderAllPlayers();
        UpdateAttachedUiPositions();
        RenderAllMissiles();
    }

    /// <summary>
    /// ReceiveLoop가 적재해둔 JoinResponse를 메인 스레드에서 소비한다. 거부된 접속이면 메시지를
    /// 표시하고 3초 후 EntryScene으로 이동하도록 카운트다운을 시작하며 재시도하지 않는다. 정상
    /// 접속이면 이미 존재하던 플레이어 전원을 JoinSequence 오름차순으로 스폰한다.
    /// </summary>
    private void ConsumePendingJoinResponse()
    {
        bool hasPending;
        bool rejected;
        byte pendingPlayerId = 0;
        List<JoinedPlayerInfo> pendingExisting = null;

        lock (_pendingJoinLock)
        {
            hasPending = _hasPendingJoinResponse;
            rejected = _pendingJoinRejected;
            if (hasPending)
            {
                pendingPlayerId = _pendingMyPlayerId;
                pendingExisting = _pendingExistingPlayers;
            }
            _hasPendingJoinResponse = false;
            _pendingJoinRejected = false;
            _pendingExistingPlayers = null;
        }

        if (rejected)
        {
            Debug.LogWarning("[Client] 접속이 거부되었습니다: 게임이 이미 시작되었습니다. 3초 후 EntryScene으로 이동합니다.");
            _joinRejected = true;
            _sceneTransitionCountdownSeconds = 3f;

            // 서버가 이미 거부했으므로 이 소켓으로 더 할 일이 없다 — 곧바로 닫는다. 다만
            // DisconnectInternal은 호출하지 않는다 — 그 함수는 플레이어/미사일 뷰까지 정리하고
            // _joinRejected/_sceneTransitionCountdownSeconds 등 방금 세팅한 상태까지 되돌릴
            // 수 있으므로, 여기서는 소켓만 닫는다.
            if (_udpClient != null)
            {
                try { _udpClient.Close(); } catch { }
                _udpClient = null;
            }
            return;
        }

        if (!hasPending) return;

        _myPlayerId = pendingPlayerId;
        Debug.Log($"[Client] Connected! Assigned My Player ID: {_myPlayerId} (existing players: {pendingExisting.Count})");

        pendingExisting.Sort((a, b) => a.JoinSequence.CompareTo(b.JoinSequence));
        foreach (var info in pendingExisting)
        {
            SpawnPlayer(info.PlayerId, info.JoinSequence);
        }

        // [중요] 자기 자신은 여기서 스폰하지 않는다 — 서버가 이 접속을 확정한 바로 그 틱의
        // TickCommit.JoinedPlayers 목록에 자신의 (PlayerId, JoinSequence)가 포함되어 오므로,
        // 그 틱이 도착했을 때 ProcessOneTick의 평소 Join 처리 경로로 자동 스폰된다. DOTS
        // SimulationTickSystem과 완전히 동일한 순서 — 자신을 위한 특별 경로를 두지 않아야
        // 스폰 슬롯 계산이 다른 클라이언트들과 항상 일치한다.
    }

    private void ProcessTickCommits()
    {
        while (_tickCommitQueue.TryDequeue(out TickCommitData commit))
        {
            _totalElapsedSimTime += commit.DeltaTimeSeconds;

            // GameStarted는 엣지 신호(그 틱에서만 true)이므로 관측한 즉시 영속적인
            // _isGameStarted로 래치한다. 반드시 ProcessOneTick보다 먼저 해야, 게임이 시작된
            // 바로 그 틱의 입력이 같은 틱 안에서 곧바로(한 틱 지연 없이) 반영된다.
            if (commit.GameStarted)
            {
                _isGameStarted = true;
            }

            ProcessOneTick(commit);
            _lastConfirmedTick = commit.Tick;
        }
    }

    /// <summary>
    /// TickCommit 하나를 처리 순서 규약대로 실행한다: Join -> Leave -> (게임 시작 여부 게이트)
    /// -> Input(이동/발사) -> 미사일 -> 리스폰. 이 순서를 절대 바꾸지 않는다 — 순서 자체가
    /// 결정론의 일부다.
    /// </summary>
    private void ProcessOneTick(TickCommitData commit)
    {
        int tick = commit.Tick;

        // 1) Join (JoinSequence 오름차순)
        commit.JoinedPlayers.Sort((a, b) => a.JoinSequence.CompareTo(b.JoinSequence));
        foreach (var info in commit.JoinedPlayers)
        {
            SpawnPlayer(info.PlayerId, info.JoinSequence);
        }

        // 2) Leave (PlayerId 오름차순)
        commit.LeftPlayerIds.Sort();
        foreach (byte playerId in commit.LeftPlayerIds)
        {
            RemovePlayer(playerId);
        }

        // 게임이 아직 시작되지 않았으면(로비) 여기서 멈춘다. 플레이어는 스폰된 자리에 가만히
        // 서서 대기만 한다 — 이동/발사/미사일/리스폰은 호스트가 StartGameRequest를 보내 서버가
        // TickCommit.GameStarted를 방송하기 전까지 실행하지 않는다.
        if (!_isGameStarted) return;

        // 3) 이동/발사 판정 (PlayerId 오름차순)
        commit.Inputs.Sort((a, b) => a.PlayerId.CompareTo(b.PlayerId));
        foreach (var input in commit.Inputs)
        {
            ProcessPlayerTick(input, tick, commit.DeltaTimeSeconds, _totalElapsedSimTime);
        }

        // 4) 미사일 이동/사거리 소진/충돌 판정
        ProcessMissiles(commit.DeltaTimeSeconds, _totalElapsedSimTime);

        // 5) 리스폰 판정 — 반드시 미사일 충돌 판정(4번) 이후에 실행한다.
        ProcessRespawns(_totalElapsedSimTime);
    }

    // ------------------------------------------------------------------
    // Join / Leave / 스폰
    // ------------------------------------------------------------------

    /// <summary>
    /// 신규 플레이어를 스폰한다. 스폰 위치는 ComputeSpawnTransform으로 계산하며, "현재 존재하는
    /// 모든 플레이어"의 SpawnSlot을 근거로 빈 슬롯을 찾는다. 이미 존재하면(재전송 등) 건너뛴다.
    /// </summary>
    private void SpawnPlayer(byte playerId, long joinSequence)
    {
        if (_players.ContainsKey(playerId)) return;

        (Vector2 spawnPos, float spawnRot, int slot) = ComputeSpawnTransform();

        var state = new PlayerRuntimeState
        {
            Id = playerId,
            JoinSequence = joinSequence,
            Position = spawnPos,
            RotationDeg = spawnRot,
            Speed = 0f,
            Health = MAX_HEALTH,
            IsDead = false,
            Kills = 0,
            Deaths = 0,
            SpawnSlot = slot
        };

        state.ShipObj = CreatePlayerObject(playerId, spawnRot);
        _players[playerId] = state;
    }

    private void RemovePlayer(byte playerId)
    {
        if (!_players.TryGetValue(playerId, out PlayerRuntimeState state)) return;

        if (state.ShipObj != null) Destroy(state.ShipObj);
        if (_nameplateViews.TryGetValue(playerId, out GameObject nameplate))
        {
            if (nameplate != null) Destroy(nameplate);
            _nameplateViews.Remove(playerId);
        }
        if (_healthBarViews.TryGetValue(playerId, out HealthBarView bar))
        {
            if (bar.BackgroundObj != null) Destroy(bar.BackgroundObj);
            if (bar.ForegroundObj != null) Destroy(bar.ForegroundObj);
            _healthBarViews.Remove(playerId);
        }
        _playerRenderers.Remove(playerId);
        _deadPlayerIds.Remove(playerId);

        _players.Remove(playerId);
    }

    /// <summary>
    /// 서버/DOTS ComputeSpawnTransform과 정확히 동일한 알고리즘. 현재 존재하는 모든 플레이어의
    /// SpawnSlot을 점유 슬롯 집합으로 삼아 빈 슬롯을 오름차순으로 찾는다. 이 함수 자체는 호출
    /// 시점의 점유 슬롯 집합만 같다면 항상 같은 결과를 낸다 — 누가 이 함수를 호출하는 순서
    /// (Join 처리 순서, JoinSequence 오름차순)가 호출부에서 이미 고정되어 있어야 모든
    /// 클라이언트가 동일한 점유 슬롯 집합을 갖는다.
    /// </summary>
    private (Vector2 position, float rotationDeg, int slot) ComputeSpawnTransform()
    {
        var occupiedSlots = new HashSet<int>();
        foreach (var state in _players.Values)
        {
            occupiedSlots.Add(state.SpawnSlot);
        }

        int chosenSlot = 0;
        for (int i = 0; i < SPAWN_SLOTS; i++)
        {
            if (!occupiedSlots.Contains(i))
            {
                chosenSlot = i;
                break;
            }
            chosenSlot = i;
        }

        float angleDeg = chosenSlot * (360f / SPAWN_SLOTS);
        float angleRad = angleDeg * Mathf.Deg2Rad;

        float x = Mathf.Sin(angleRad) * SPAWN_RADIUS;
        float y = Mathf.Cos(angleRad) * SPAWN_RADIUS;

        return (new Vector2(x, y), angleDeg, chosenSlot);
    }

    // ------------------------------------------------------------------
    // 이동 / 발사
    // ------------------------------------------------------------------

    /// <summary>
    /// 탱크 컨트롤 입력을 (throttle, turn, fire)으로 반환한다. 발사 빈도 제한(쿨다운)은 이
    /// 함수가 아니라 결정론적 시뮬레이션(ProcessPlayerTick) 안에서만 판정한다 — 그래야 모든
    /// 클라이언트가 동일한 쿨다운 결과를 재현한다.
    /// </summary>
    private (float throttle, float turn, bool fire) GetTankInput()
    {
        bool fire = Input.GetKey(KeyCode.Space);

        if (InputConfigManager.Instance != null)
        {
            Vector2 raw = InputConfigManager.Instance.GetMovementInput();
            return (Mathf.Clamp(raw.y, -1f, 1f), Mathf.Clamp(raw.x, -1f, 1f), fire);
        }

        float throttle = Input.GetAxisRaw("Vertical");
        float turn = Input.GetAxisRaw("Horizontal");
        return (Mathf.Clamp(throttle, -1f, 1f), Mathf.Clamp(turn, -1f, 1f), fire);
    }

    /// <summary>
    /// 로컬 키보드 입력을 읽어 서버로 전송한다. DOTS InputCaptureSystem과 동일한 역할이며
    /// 물리를 전혀 계산하지 않는다 — 사망 여부에 따라 입력을 0으로 강제하지도 않는다(그 판단은
    /// 전적으로 ProcessPlayerTick이 그 틱 시점의 IsDead를 보고 결정한다).
    /// </summary>
    private void CaptureAndSendInput()
    {
        if (_udpClient == null || _myPlayerId == 0) return;

        // [재연결 감지] _lastConfirmedTick == -1은 DisconnectInternal이 방금 연결을 리셋했다는
        // 신호다. "마지막으로 보낸 TargetTick" 기억도 함께 리셋해야, 새 연결의 낮은 틱 번호를
        // "이미 보냈다"고 착각해 첫 입력 전송을 건너뛰지 않는다.
        if (_lastConfirmedTick == -1)
        {
            _lastSentTargetTick = int.MinValue;
        }

        var (throttle, turn, fireHeld) = GetTankInput();

        // TargetTick 계산: 서버가 다음으로 확정할 틱은 (_lastConfirmedTick + 1)이다. 이 입력이
        // 그 틱에 늦지 않게 도착하려면 _inputDelayTicks만큼 더 미래를 향해 보내야 한다.
        int targetTick = _lastConfirmedTick + 1 + _inputDelayTicks;
        if (targetTick == _lastSentTargetTick) return;

        SendPacket(BuildClientInputPacket(targetTick, throttle, turn, fireHeld));
        _lastSentTargetTick = targetTick;
    }

    /// <summary>
    /// 좌표 하나를 [-halfExtent, +halfExtent) 범위로 순환(wrap)시킨다. 서버/DOTS
    /// WrapCoordinate와 정확히 동일한 공식이어야 한다.
    /// </summary>
    private static float WrapCoordinate(float value, float halfExtent)
    {
        float range = halfExtent * 2f;
        if (range <= 0f) return 0f;

        float shifted = value + halfExtent;
        float wrapped = shifted % range;
        if (wrapped < 0f) wrapped += range;
        return wrapped - halfExtent;
    }

    /// <summary>
    /// 서버/DOTS SimulateTankStep과 정확히 동일한 순서/공식으로 한 스텝을 시뮬레이션한다.
    /// 이 함수를 수정할 때는 서버(SimulationTickSystem.SimulateTankStep)도 반드시 같이
    /// 맞춰야 한다 — 세 곳(서버/DOTS/이 파일) 중 하나만 달라도 결정론이 깨진다.
    /// </summary>
    private (Vector2 pos, float rot, float speed) SimulateTankStep(
        Vector2 pos, float rotDeg, float speed, float throttle, float turn, float dt)
    {
        throttle = Mathf.Clamp(throttle, -1f, 1f);
        turn = Mathf.Clamp(turn, -1f, 1f);

        // 1) 회전
        rotDeg += turn * _turnSpeedDeg * dt;
        rotDeg %= 360f;
        if (rotDeg < 0f) rotDeg += 360f;

        // 2) 목표 속도로 가속/감속
        float targetSpeed = throttle >= 0f ? throttle * _maxForwardSpeed : throttle * _maxReverseSpeed;
        float speedDelta = _acceleration * dt;
        if (speed < targetSpeed)
        {
            speed = Mathf.Min(speed + speedDelta, targetSpeed);
        }
        else if (speed > targetSpeed)
        {
            speed = Mathf.Max(speed - speedDelta, targetSpeed);
        }

        // 3) 현재 방향으로 전진/후진 (0도 = +Y, 시계방향 증가)
        float rotRad = rotDeg * Mathf.Deg2Rad;
        Vector2 dir = new Vector2(Mathf.Sin(rotRad), Mathf.Cos(rotRad));
        pos += dir * speed * dt;

        // 4) 경계에서 반대편으로 순환(wrap). 각 축은 반드시 자신의 half-extent로만 wrap해야 한다
        //    (화면이 16:9로 고정되어 X/Y half-extent가 다르므로).
        pos.x = WrapCoordinate(pos.x, _serverWorldHalfExtent.x);
        pos.y = WrapCoordinate(pos.y, _serverWorldHalfExtent.y);

        return (pos, rotDeg, speed);
    }

    /// <summary>
    /// 서버/DOTS가 공유하는 회전 규칙(0도 = +Y, 시계방향 증가)을 Unity의 Z축 오일러 각
    /// (반시계방향 증가)으로 변환해 오브젝트에 적용한다.
    /// </summary>
    private static void ApplyShipRotation(GameObject shipObj, float rotationDeg)
    {
        shipObj.transform.rotation = Quaternion.Euler(0f, 0f, -rotationDeg);
    }

    /// <summary>
    /// 서버/DOTS ProcessPlayerTick과 정확히 동일한 순서/공식: 사망 시 건너뜀 -> 이동
    /// 시뮬레이션 -> 발사 쿨다운 판정. dt(이동 계산용)는 서버가 실측해 보낸
    /// commit.DeltaTimeSeconds를, elapsedTime(쿨다운 판정용)은 그 값이 이미 누적된
    /// _totalElapsedSimTime을 그대로 쓴다.
    /// </summary>
    private void ProcessPlayerTick(TickPlayerInput input, int tick, float dt, float elapsedTime)
    {
        if (!_players.TryGetValue(input.PlayerId, out PlayerRuntimeState state)) return;

        // 사망한 플레이어는 이동/회전/발사를 전혀 반영하지 않는다.
        if (state.IsDead) return;

        float throttle = Mathf.Clamp(input.Throttle, -1f, 1f);
        float turn = Mathf.Clamp(input.Turn, -1f, 1f);

        (state.Position, state.RotationDeg, state.Speed) =
            SimulateTankStep(state.Position, state.RotationDeg, state.Speed, throttle, turn, dt);

        // 발사 쿨다운 판정 — 반드시 누적 실측 시간(elapsedTime) 기반. "마지막 발사 이후 누적
        // 경과 시간 >= FireCooldownSeconds"로 판정한다.
        if (input.Fire && (elapsedTime - state.LastFireElapsedTime) >= _fireCooldownSeconds)
        {
            state.LastFireElapsedTime = elapsedTime;
            SpawnMissile(input.PlayerId, tick, state.Position, state.RotationDeg);
        }
    }

    // ------------------------------------------------------------------
    // 미사일
    // ------------------------------------------------------------------

    private void SpawnMissile(byte ownerId, int fireTick, Vector2 position, float rotationDeg)
    {
        GameObject missileObj = new GameObject($"Missile_{ownerId}_{fireTick}");
        var sr = missileObj.AddComponent<SpriteRenderer>();
        sr.sprite = _missileSprite;
        sr.color = (ownerId == _myPlayerId) ? Color.yellow : Color.white;
        sr.sortingOrder = 5;
        // 위치는 여기서 대입하지 않는다 — 이번 프레임 끝의 RenderAllMissiles()가 모든 미사일의
        // 최신 물리 위치를 한 번에 반영한다(물리 계산과 렌더링 반영을 분리하는 것이 DOTS
        // PhysicsToRenderSyncSystem과 동일한 설계 원칙이다).

        _missiles.Add(new MissileRuntimeState
        {
            OwnerId = ownerId,
            FireTick = fireTick,
            Position = position,
            RotationDeg = rotationDeg,
            DistanceTraveled = 0f,
            GameObj = missileObj
        });
    }

    /// <summary>
    /// 미사일 이동/사거리 소진/탱크 충돌을 처리한다. 서버/DOTS ProcessMissiles와 정확히 동일한
    /// 순서(미사일은 OwnerId 오름차순, 동률이면 FireTick 오름차순 / 충돌 대상은 PlayerId
    /// 오름차순)로 순회해야 한다 — 이 순서 자체가 결정론의 일부다. 반드시 3단계(이동/발사
    /// 판정)가 전부 끝난 뒤에 호출되어야 한다 — 아래에서 읽는 각 플레이어의 Position이
    /// "이번 틱의 최종 확정 위치"여야 충돌 판정이 정확하다.
    /// </summary>
    private void ProcessMissiles(float dt, float elapsedTime)
    {
        float collisionDistSqr = (MISSILE_RADIUS + TANK_COLLISION_RADIUS) * (MISSILE_RADIUS + TANK_COLLISION_RADIUS);
        float stepDistance = _missileSpeed * dt;

        _missiles.Sort((a, b) =>
        {
            int ownerCompare = a.OwnerId.CompareTo(b.OwnerId);
            return ownerCompare != 0 ? ownerCompare : a.FireTick.CompareTo(b.FireTick);
        });

        // 살아있는 플레이어 목록을 PlayerId 오름차순으로 미리 모아둔다. [중요] 이번 틱 안에서
        // 앞선 미사일이 죽인 플레이어는 justDiedThisTick으로 배제해 뒤따르는 미사일 판정에서
        // 자동으로 제외한다 — 서버/DOTS의 "실시간 IsDead 재확인"과 동일한 효과를, 목록 자체를
        // 다시 만들지 않고 HashSet 배제로 값싸게 재현한다.
        var alivePlayers = new List<PlayerRuntimeState>();
        foreach (var state in _players.Values)
        {
            if (!state.IsDead) alivePlayers.Add(state);
        }
        alivePlayers.Sort((a, b) => a.Id.CompareTo(b.Id));

        var justDiedThisTick = new HashSet<byte>();

        // [구현 세부사항] ECS는 EntityManager.DestroyEntity로 즉시(지연 없이) 별도 스냅샷 목록의
        // 엔티티를 파괴해도 그 목록 자체의 foreach가 깨지지 않지만, 이 파일은 실제 List<T>를
        // 직접 순회하므로 그 자리에서 _missiles.Remove(...)를 호출하면
        // InvalidOperationException이 난다. 그래서 파괴 대상을 toDestroy에 모았다가 순회가
        // 끝난 뒤 한 번에 제거한다 — 판정 로직 자체(어떤 미사일이 어떤 순서로 무엇과
        // 충돌하는지)는 서버/DOTS와 완전히 동일하고, 이 지연 제거는 순수 구현 디테일이다.
        var toDestroy = new List<MissileRuntimeState>();

        foreach (var missile in _missiles)
        {
            float rotRad = missile.RotationDeg * Mathf.Deg2Rad;
            missile.Position += new Vector2(Mathf.Sin(rotRad), Mathf.Cos(rotRad)) * stepDistance;
            missile.DistanceTraveled += stepDistance;

            // 1) 사거리 소진
            if (missile.DistanceTraveled >= _missileMaxDistance)
            {
                toDestroy.Add(missile);
                continue;
            }

            // 2) 탱크 충돌: 발사자 자신, 이미 사망한 플레이어(스냅샷 시점 기준), 이번 틱에
            //    다른 미사일에 이미 죽은 플레이어(justDiedThisTick)를 모두 제외하고 PlayerId
            //    오름차순으로 검사. 가장 먼저 반지름 조건을 만족하는 타겟 하나에만 명중
            //    처리한다(한 미사일은 한 틱에 최대 하나의 타겟에만 명중).
            foreach (var target in alivePlayers)
            {
                if (target.Id == missile.OwnerId) continue;
                if (justDiedThisTick.Contains(target.Id)) continue;

                Vector2 delta = missile.Position - target.Position;
                if (delta.sqrMagnitude <= collisionDistSqr)
                {
                    toDestroy.Add(missile);
                    bool died = ApplyDamage(target, missile.OwnerId, elapsedTime);
                    if (died) justDiedThisTick.Add(target.Id);
                    break;
                }
            }
        }

        foreach (var missile in toDestroy)
        {
            if (missile.GameObj != null) Destroy(missile.GameObj);
            _missiles.Remove(missile);
        }
    }

    /// <summary>
    /// 체력을 1 깎고, 0 이하로 떨어지면 사망 처리 + 킬/데스 집계 + 사망 시각효과/HP 바 갱신.
    /// 서버/DOTS ApplyDamage와 정확히 동일한 판정이다. 이번 호출로 사망이 실제로 발생했는지를
    /// 반환한다 — 호출부(ProcessMissiles)가 같은 틱 안에서 이 타겟을 다시 명중 판정 대상에서
    /// 제외하기 위해 필요하다.
    /// </summary>
    private bool ApplyDamage(PlayerRuntimeState target, byte killerId, float elapsedTime)
    {
        target.Health = (byte)Mathf.Max(0, target.Health - 1);

        if (target.Health == 0 && !target.IsDead)
        {
            target.IsDead = true;
            target.DeathElapsedTime = elapsedTime;
            target.Speed = 0f;

            UpdateDeathVisual(target.Id, true);
            UpdateHealthBar(target.Id, target.Health);

            // 킬/데스 집계. 가해자가 이미 존재하지 않으면(방금 접속 끊김 등) 조용히 무시.
            if (_players.TryGetValue(killerId, out PlayerRuntimeState killer))
            {
                killer.Kills += 1;
            }
            target.Deaths += 1;

            return true;
        }

        UpdateHealthBar(target.Id, target.Health);
        return false;
    }

    // ------------------------------------------------------------------
    // 리스폰
    // ------------------------------------------------------------------

    /// <summary>
    /// 사망한 플레이어 중 (_totalElapsedSimTime - DeathElapsedTime) >= _respawnDelaySeconds인
    /// 대상을 새 스폰 슬롯으로 되살린다. PlayerId 오름차순으로 검사 — 여러 명이 같은 틱에
    /// 동시에 리스폰 조건을 만족하면, 이 순서가 곧 ComputeSpawnTransform 호출 순서이자
    /// 슬롯 배정 순서다.
    /// </summary>
    private void ProcessRespawns(float elapsedTime)
    {
        var candidates = new List<PlayerRuntimeState>();
        foreach (var state in _players.Values)
        {
            if (state.IsDead && (elapsedTime - state.DeathElapsedTime) >= _respawnDelaySeconds)
            {
                candidates.Add(state);
            }
        }
        candidates.Sort((a, b) => a.Id.CompareTo(b.Id));

        foreach (var state in candidates)
        {
            (Vector2 spawnPos, float spawnRot, int slot) = ComputeSpawnTransform();

            state.Position = spawnPos;
            state.RotationDeg = spawnRot;
            state.Speed = 0f;
            state.SpawnSlot = slot;
            state.IsDead = false;
            state.Health = MAX_HEALTH;

            UpdateDeathVisual(state.Id, false);
            UpdateHealthBar(state.Id, state.Health);
        }
    }

    // ------------------------------------------------------------------
    // 방장 / 게임 시작
    // ------------------------------------------------------------------

    /// <summary>
    /// "현재 방장"을 스스로 계산한다 — 서버는 방장이 누구인지 별도로 방송하지 않는다. 현재
    /// 존재하는 모든 플레이어 중 JoinSequence가 가장 작은 사람이 방장이다. 방장이 나가면
    /// (RemovePlayer로 _players에서 제거됨) 다음 호출에서 자동으로 다음 최소값 보유자가
    /// 방장이 되므로, 별도의 승계 로직이 필요 없다. 모든 클라이언트가 동일한 Join/Leave
    /// 이력을 동일한 순서로 재생하므로 이 계산 결과는 항상 전원 일치한다.
    /// </summary>
    private bool ComputeIsHost()
    {
        if (_myPlayerId == 0 || _players.Count == 0) return false;

        byte hostId = 0;
        long minSequence = long.MaxValue;
        foreach (var state in _players.Values)
        {
            if (state.JoinSequence < minSequence)
            {
                minSequence = state.JoinSequence;
                hostId = state.Id;
            }
        }
        return hostId == _myPlayerId;
    }

    // ------------------------------------------------------------------
    // 렌더링 동기화 (보간 없음 — 결정론적 시뮬레이션의 최종 값을 그대로 복사)
    // ------------------------------------------------------------------

    /// <summary>
    /// 매 프레임, 방금(또는 이전 틱에서 마지막으로) 계산된 물리 상태를 그대로 화면에 복사한다.
    /// DOTS의 PhysicsToRenderSyncSystem + SyncRenderTransformSystem을 합친 것과 동일한 역할이며,
    /// 그 두 System과 마찬가지로 보간을 전혀 하지 않는다. 로컬/원격을 구분하지 않는다 — 모든
    /// 플레이어가 동일한 결정론적 시뮬레이션의 산출물이므로 예측 오차 스무딩이 필요 없다.
    /// </summary>
    private void RenderAllPlayers()
    {
        foreach (var state in _players.Values)
        {
            if (state.ShipObj == null) continue;
            state.ShipObj.transform.position = (Vector3)state.Position;
            ApplyShipRotation(state.ShipObj, state.RotationDeg);
        }
    }

    private void RenderAllMissiles()
    {
        foreach (var missile in _missiles)
        {
            if (missile.GameObj == null) continue;
            missile.GameObj.transform.position = (Vector3)missile.Position;
        }
    }

    /// <summary>
    /// 네임플레이트와 HP 바는 탱크의 자식이 아니므로(회전에 딸려 기울어지거나, HP 바의 경우
    /// 부모 localScale과 곱연산으로 얽혀 폭 조절이 왜곡되는 것을 막기 위해), 매 프레임 대응하는
    /// 탱크의 현재 위치를 읽어 자신의 위치만 갱신한다. 반드시 RenderAllPlayers() 다음에
    /// 호출해야 이번 프레임에 갱신된 최신 탱크 위치를 따라간다.
    /// </summary>
    private void UpdateAttachedUiPositions()
    {
        foreach (var state in _players.Values)
        {
            byte id = state.Id;
            GameObject shipObj = state.ShipObj;
            if (shipObj == null) continue;

            Vector3 shipPos = shipObj.transform.position;

            if (_nameplateViews.TryGetValue(id, out GameObject nameplateObj) && nameplateObj != null)
            {
                nameplateObj.transform.position = shipPos + new Vector3(0, NAMEPLATE_Y_OFFSET, 0);
            }

            if (_healthBarViews.TryGetValue(id, out HealthBarView bar))
            {
                Vector3 barPos = shipPos + new Vector3(-HP_BAR_WIDTH * 0.5f, HP_BAR_Y_OFFSET, 0f);
                if (bar.BackgroundObj != null) bar.BackgroundObj.transform.position = barPos;
                if (bar.ForegroundObj != null) bar.ForegroundObj.transform.position = barPos;
            }
        }
    }

    /// <summary>
    /// 서버로부터 _connectionTimeoutSeconds 이상 아무 응답도 없으면, 재연결을 시도하지 않고
    /// 곧바로 EntryScene 전환을 예약한다. _sceneTransitionCountdownSeconds를 0으로 두어
    /// Update()가 이번 프레임에 곧바로 LoadScene을 호출하게 한다(거부 응답의 3초 대기와 달리
    /// 즉시 전환). 이미 _isConnectionLost가 true면 다시 트리거하지 않는다.
    /// </summary>
    private void UpdateConnectionLossCheck()
    {
        if (_udpClient == null) return;
        if (_isConnectionLost) return;

        long lastMsgMs = Interlocked.Read(ref _timeSinceLastServerMessageMs);
        long nowMs = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        float secondsSinceLastMessage = (nowMs - lastMsgMs) / 1000f;

        if (secondsSinceLastMessage < _connectionTimeoutSeconds) return;

        Debug.LogWarning("[Client] 서버 응답이 끊겼습니다. EntryScene으로 이동합니다.");
        _isConnectionLost = true;
        _sceneTransitionCountdownSeconds = 0f;

        if (_udpClient != null)
        {
            try { _udpClient.Close(); } catch { }
            _udpClient = null;
        }
    }

    /// <summary>
    /// _joinRejected 또는 _isConnectionLost로 카운트다운이 진행 중이면 매 프레임 감산하다가
    /// 0 이하가 되면 EntryScene으로 전환한다. 두 상태 모두 이 하나의 헬퍼로 처리한다 — 거부는
    /// 3초 대기 후, 연결 끊김은 대기 없이(카운트다운이 이미 0으로 세팅되어 있으므로) 전환된다는
    /// 차이만 있을 뿐 흐름은 동일하다.
    /// </summary>
    private void UpdateSceneTransitionCountdown()
    {
        if (_hasTriggeredSceneTransition) return;
        if (!_joinRejected && !_isConnectionLost) return;

        _sceneTransitionCountdownSeconds -= Time.deltaTime;
        if (_sceneTransitionCountdownSeconds <= 0f)
        {
            _hasTriggeredSceneTransition = true;
            SceneManager.LoadScene("EntryScene");
        }
    }

    private void UpdateTrafficMetrics()
    {
        _bandwidthTimer += Time.deltaTime;
        if (_bandwidthTimer >= 1.0f)
        {
            int sent = Interlocked.Exchange(ref _bytesSentThisSec, 0);
            int recv = Interlocked.Exchange(ref _bytesReceivedThisSec, 0);

            _kbSentPerSec = sent / 1024f;
            _kbReceivedPerSec = recv / 1024f;
            _bandwidthTimer = 0f;
        }
    }

    private void SendPingCheck()
    {
        if (_udpClient == null || _myPlayerId == 0) return;

        _pingTimer += Time.deltaTime;
        if (_pingTimer >= PING_INTERVAL)
        {
            _pingTimer = 0f;
            SendPacket(BuildPingPacket());
        }
    }

    /// <summary>
    /// 0.5초 주기로 평균 FPS를 계산해 캐시한다. 매 프레임 1/deltaTime을 그대로 표시하면
    /// 값이 심하게 튀어 읽기 어려우므로 짧은 구간 평균을 사용한다.
    /// </summary>
    private void UpdateFpsCounter()
    {
        _fpsAccumFrames++;
        _fpsAccumTime += Time.unscaledDeltaTime;

        if (_fpsAccumTime >= FpsRefreshInterval)
        {
            _displayedFps = _fpsAccumFrames / _fpsAccumTime;
            _fpsAccumFrames = 0;
            _fpsAccumTime = 0f;
        }
    }

    private GameObject CreatePlayerObject(byte id, float initialRotationDeg)
    {
        GameObject shipObj = new GameObject($"PlayerShip_{id}");
        var sr = shipObj.AddComponent<SpriteRenderer>();
        sr.sprite = _shipSprite;
        sr.sortingOrder = 0;

        bool isMe = (id == _myPlayerId);
        sr.color = isMe ? Color.green : Color.red;
        _playerRenderers[id] = sr;

        ApplyShipRotation(shipObj, initialRotationDeg);

        GameObject nameplateObj = new GameObject($"NameplateTMP_{id}");
        nameplateObj.transform.position = shipObj.transform.position + new Vector3(0, NAMEPLATE_Y_OFFSET, 0);

        TextMeshPro tmp = nameplateObj.AddComponent<TextMeshPro>();
        tmp.text = isMe ? $"Player {id} (You)" : $"Player {id}";
        tmp.fontSize = 2.5f;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.color = isMe ? Color.yellow : Color.white;
        tmp.sortingOrder = 10;
        tmp.rectTransform.sizeDelta = new Vector2(5, 1.2f);

        _nameplateViews[id] = nameplateObj;

        Vector3 barBasePos = shipObj.transform.position + new Vector3(-HP_BAR_WIDTH * 0.5f, HP_BAR_Y_OFFSET, 0f);

        GameObject bgObj = new GameObject($"HPBarBG_{id}");
        var bgRenderer = bgObj.AddComponent<SpriteRenderer>();
        bgRenderer.sprite = _hpBarSprite;
        bgRenderer.color = Color.red;
        bgRenderer.sortingOrder = 6;
        bgObj.transform.position = barBasePos;
        bgObj.transform.localScale = new Vector3(HP_BAR_WIDTH, HP_BAR_HEIGHT, 1f);

        GameObject fgObj = new GameObject($"HPBarFG_{id}");
        var fgRenderer = fgObj.AddComponent<SpriteRenderer>();
        fgRenderer.sprite = _hpBarSprite;
        fgRenderer.color = Color.green;
        fgRenderer.sortingOrder = 7;
        fgObj.transform.position = barBasePos;
        fgObj.transform.localScale = new Vector3(HP_BAR_WIDTH, HP_BAR_HEIGHT, 1f);

        _healthBarViews[id] = new HealthBarView
        {
            BackgroundObj = bgObj,
            ForegroundObj = fgObj,
            ForegroundRenderer = fgRenderer
        };

        return shipObj;
    }

    private static readonly Color DEAD_COLOR = new Color(0.4f, 0.4f, 0.4f, 1f);

    /// <summary>
    /// 사망 상태가 실제로 바뀐 경우에만 스프라이트 색을 갱신한다. 사망 -> 회색, 생존 -> 원래
    /// 색(본인 초록/원격 빨강)으로 되돌린다.
    /// </summary>
    private void UpdateDeathVisual(byte id, bool isDead)
    {
        bool wasDead = _deadPlayerIds.Contains(id);
        if (wasDead == isDead) return;

        if (_playerRenderers.TryGetValue(id, out SpriteRenderer sr) && sr != null)
        {
            if (isDead)
            {
                sr.color = DEAD_COLOR;
            }
            else
            {
                bool isMe = (id == _myPlayerId);
                sr.color = isMe ? Color.green : Color.red;
            }
        }

        if (isDead)
        {
            _deadPlayerIds.Add(id);
        }
        else
        {
            _deadPlayerIds.Remove(id);
        }
    }

    /// <summary>서버가 계산한 현재 체력(byte)에 맞춰 HP 바 전경(초록)의 가로 폭을 조절한다.</summary>
    private void UpdateHealthBar(byte id, byte currentHealth)
    {
        if (!_healthBarViews.TryGetValue(id, out HealthBarView bar)) return;
        if (bar.ForegroundObj == null) return;

        float ratio = Mathf.Clamp01((float)currentHealth / MAX_HEALTH);
        Vector3 scale = bar.ForegroundObj.transform.localScale;
        scale.x = HP_BAR_WIDTH * ratio;
        bar.ForegroundObj.transform.localScale = scale;
    }

    /// <summary>재연결 시(또는 종료 시) 현재 그려져 있던 모든 플레이어 오브젝트/네임플레이트/HP 바를 정리한다.</summary>
    private void ClearAllPlayerViews()
    {
        foreach (var state in _players.Values)
        {
            if (state.ShipObj != null) Destroy(state.ShipObj);
        }
        _players.Clear();

        foreach (var go in _nameplateViews.Values)
        {
            if (go != null) Destroy(go);
        }
        _nameplateViews.Clear();

        foreach (var bar in _healthBarViews.Values)
        {
            if (bar.BackgroundObj != null) Destroy(bar.BackgroundObj);
            if (bar.ForegroundObj != null) Destroy(bar.ForegroundObj);
        }
        _healthBarViews.Clear();

        _playerRenderers.Clear();
        _deadPlayerIds.Clear();
    }

    /// <summary>재연결 시(또는 종료 시) 모든 미사일 오브젝트를 정리한다.</summary>
    private void ClearAllMissileViews()
    {
        foreach (var missile in _missiles)
        {
            if (missile.GameObj != null) Destroy(missile.GameObj);
        }
        _missiles.Clear();
    }

    // OnGUI 레이아웃 상수.
    private const float STATS_PANEL_X = 10f;
    private const float STATS_PANEL_Y = 10f;
    private const float STATS_PANEL_WIDTH = 280f;
    // 로비 상태 줄(Start 버튼 또는 대기 문구)까지 포함한 접속 중 케이스의 최대 줄 수를
    // 기준으로 잡은 높이다. 게임 시작 후에는 그 줄이 사라지므로 오히려 여유가 생긴다.
    private const float STATS_PANEL_HEIGHT = 275f;
    private const float PANEL_ROW_HEIGHT = 22f; // GUILayout.Label 한 줄의 대략적인 높이
    private const float PANEL_HEADER_HEIGHT = 35f; // 패널 제목 줄 + 여백

    // ECS(GameHudBridge) UI와 동일한 3패널 레이아웃이다 — 스코어보드는 왼쪽 아래(Stats
    // 패널과 같은 x열, 아래에서 위로 자람), 좌표/HP는 오른쪽 위 별도 패널이다. 헤더/행
    // 높이(35f/22f)는 위의 PANEL_HEADER_HEIGHT/PANEL_ROW_HEIGHT를 그대로 공유한다.
    private const float SCOREBOARD_PANEL_WIDTH = 220f;
    private const float CONNECTED_USERS_PANEL_WIDTH = 250f;
    private const float PANEL_GAP = 10f; // Stats 패널 하단과 스코어보드 패널 상단 사이 최소 여백

    /// <summary>
    /// 스코어보드가 Stats 패널과 같은 왼쪽 열의 아래쪽에서 위로 자라나므로, 최대 행 수는
    /// "Stats 패널 하단(+ 여백)부터 화면 하단까지"를 기준으로 계산한다 — 그래야 유저가
    /// 늘어나도 두 패널이 겹치지 않는다. ECS(GameHudBridge)의 CalculateMaxSafeScoreboardRows와
    /// 정확히 동일한 계산이다.
    /// </summary>
    private static int CalculateMaxSafeRows()
    {
        float statsBottom = STATS_PANEL_Y + STATS_PANEL_HEIGHT + PANEL_GAP;
        float maxHeight = Screen.height - statsBottom - PANEL_GAP;
        float availableForRows = maxHeight - PANEL_HEADER_HEIGHT;
        int maxRows = Mathf.FloorToInt(availableForRows / PANEL_ROW_HEIGHT);
        return Mathf.Max(1, maxRows);
    }

    /// <summary>
    /// 소수점 넷째 자리까지 반올림 없이(자리 아래는 잘라서) 표시한다. C#의 표준 "F4" 포맷은
    /// 다섯째 자리에서 반올림하므로, 10000을 곱해 Math.Truncate(0을 향해 자름)로 정수부만
    /// 남긴 뒤 다시 10000으로 나누고 그 결과를 F4로 포맷한다. 값 자체가 이미 4자리 배수로
    /// 잘려 있으므로 F4가 다시 반올림을 해도 표시되는 자리에는 영향이 없다.
    /// ECS(GameHudBridge)의 동일 이름 헬퍼와 정확히 같은 구현이다.
    /// </summary>
    private static string FormatTruncated4(float value)
    {
        double truncated = System.Math.Truncate((double)value * 10000.0) / 10000.0;
        return truncated.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
    }

    private void OnGUI()
    {
        // 1. 왼쪽 위: 네트워크 통계 및 상태 정보
        GUILayout.BeginArea(new Rect(STATS_PANEL_X, STATS_PANEL_Y, STATS_PANEL_WIDTH, STATS_PANEL_HEIGHT), GUI.skin.box);
        GUILayout.Label("<b>CustomClient Sample</b>");

        string fpsColor = _displayedFps >= 55f ? "green" : (_displayedFps >= 30f ? "yellow" : "red");
        GUILayout.Label($"FPS: <color={fpsColor}>{_displayedFps:F0}</color>");

        // 참가 거부/연결 끊김은 다른 어떤 상태보다도 우선해서 보여준다 — 이 두 경우는 곧
        // EntryScene으로 전환되므로, 아래의 정상 접속 UI를 계속 그리는 것은 의미가 없다.
        if (_joinRejected)
        {
            GUILayout.Label("<color=red>게임이 이미 시작되었습니다.</color>");
            GUILayout.Label($"{Mathf.Max(0f, _sceneTransitionCountdownSeconds):F1}초 후 시작 화면으로 이동합니다...");
        }
        else if (_isConnectionLost)
        {
            GUILayout.Label("<color=red>서버와 연결이 끊겼습니다.</color>");
            GUILayout.Label("시작 화면으로 이동합니다...");
        }
        else if (_myPlayerId == 0)
        {
            GUILayout.Label("Status: Connecting...");
        }
        else
        {
            GUILayout.Label($"My Player ID: {_myPlayerId}");
            GUILayout.Label($"Active Players: {_players.Count}");

            string pingColor = _currentPingMs < 50f ? "green" : (_currentPingMs < 120f ? "yellow" : "red");
            GUILayout.Label($"Ping (RTT): <color={pingColor}>{_currentPingMs:F1} ms</color>");

            // 로비/게임 시작 상태 표시. 게임이 이미 시작되었으면 표시하지 않는다.
            if (!_isGameStarted)
            {
                GUILayout.Space(5);
                if (_isHost)
                {
                    if (GUILayout.Button("Start Game"))
                    {
                        SendPacket(BuildStartGameRequestPacket());
                    }
                }
                else
                {
                    GUILayout.Label("<color=yellow>Waiting for host to start the game...</color>");
                }
            }

            GUILayout.Space(5);
            GUILayout.Label("<b>Network Traffic</b>");

            string sentTotalStr = FormatBytes(_totalBytesSent);
            GUILayout.Label($"↑ Out: <b>{_kbSentPerSec:F2} KB/s</b> ({sentTotalStr})");

            string recvTotalStr = FormatBytes(_totalBytesReceived);
            GUILayout.Label($"↓ In:  <b>{_kbReceivedPerSec:F2} KB/s</b> ({recvTotalStr})");

            GUILayout.Space(5);
            GUILayout.Label("Controls: ↑/W Forward, ↓/S Reverse, ←→/AD Turn, Space Fire");
        }
        GUILayout.EndArea();

        // ECS(GameHudBridge) UI와 동일한 3패널 레이아웃이다 — 2번은 오른쪽 위 Connected
        // Users(좌표/HP), 3번은 왼쪽 아래 Scoreboard(K/D)다.

        // 2. 오른쪽 위: 접속 중인 플레이어 ID 및 실시간 좌표 + HP.
        //    좌표는 소수점 넷째 자리까지 반올림 없이 표시한다(FormatTruncated4 참고).
        //    _players에서 직접 순회한다 — ECS의 _rows 같은 별도 스냅샷 캐시가 필요 없다
        //    (이 클래스는 이미 _players 자체가 매 프레임 최신 물리 상태를 담고 있으므로).
        if (_myPlayerId != 0 && _players.Count > 0)
        {
            float cuWidth = CONNECTED_USERS_PANEL_WIDTH;
            float cuHeight = PANEL_HEADER_HEIGHT + (_players.Count * PANEL_ROW_HEIGHT);
            float cuXPos = Screen.width - cuWidth - 10f;

            GUILayout.BeginArea(new Rect(cuXPos, 10, cuWidth, cuHeight), GUI.skin.box);
            GUILayout.Label($"<b>Connected Users ({_players.Count})</b>");

            // ECS(GameHudBridge)의 _rows처럼 표시 순서가 고정될 필요는 없지만(둘 다 참가
            // 순서 등으로 정렬하지 않는다), Dictionary 순회 순서는 보장되지 않으므로 PlayerId
            // 오름차순으로 정렬해 프레임마다 순서가 흔들리지 않게 한다.
            List<byte> sortedForDisplay = new List<byte>(_players.Keys);
            sortedForDisplay.Sort();

            foreach (byte id in sortedForDisplay)
            {
                var state = _players[id];
                string posStr = $"({FormatTruncated4(state.Position.x)}, {FormatTruncated4(state.Position.y)})";
                string healthStr = state.IsDead ? " <color=grey>[DEAD]</color>" : $" HP:{state.Health}";

                if (id == _myPlayerId)
                {
                    GUILayout.Label($"<color=lime>• Player {id} (You): {posStr}{healthStr}</color>");
                }
                else
                {
                    GUILayout.Label($"• Player {id}: {posStr}{healthStr}");
                }
            }

            GUILayout.EndArea();
        }

        // 3. 왼쪽 아래: 스코어보드 (킬 내림차순, 동점이면 데스 적은 순). 본인 점수를 항상 맨 위에
        //    고정 표시하고, 그 아래에 본인을 제외한 상위 순위자를 화면에 안전하게 들어가는
        //    만큼만 나열한다. 스코어는 _players에서 직접 읽는다(별도 스코어보드 캐시 없음).
        //    이 패널은 ECS UI와 동일하게 왼쪽 아래에 위치하며, CalculateMaxSafeRows가 Stats
        //    패널 하단 기준으로 최대 행 수를 계산하므로 유저가 늘어나도 두 패널이 겹치지 않는다.
        if (_myPlayerId != 0 && _players.Count > 0)
        {
            int maxRows = CalculateMaxSafeRows();

            List<byte> sortedIds = new List<byte>(_players.Keys);
            sortedIds.Sort((a, b) =>
            {
                var scoreA = _players[a];
                var scoreB = _players[b];
                int killCompare = scoreB.Kills.CompareTo(scoreA.Kills); // 킬 내림차순
                if (killCompare != 0) return killCompare;
                return scoreA.Deaths.CompareTo(scoreB.Deaths); // 동점이면 데스 오름차순
            });

            bool hasSelf = _players.ContainsKey(_myPlayerId);
            List<byte> displayIds = new List<byte>();
            if (hasSelf) displayIds.Add(_myPlayerId);

            foreach (byte id in sortedIds)
            {
                if (displayIds.Count >= maxRows) break;
                if (id == _myPlayerId) continue;
                displayIds.Add(id);
            }

            float width = SCOREBOARD_PANEL_WIDTH;
            float height = PANEL_HEADER_HEIGHT + (displayIds.Count * PANEL_ROW_HEIGHT);
            float yPos = Screen.height - height - 10f;

            GUILayout.BeginArea(new Rect(10, yPos, width, height), GUI.skin.box);
            GUILayout.Label($"<b>Scoreboard (K/D) - {_players.Count} players</b>");

            foreach (byte id in displayIds)
            {
                var state = _players[id];
                string label = $"Player {id}: {state.Kills} / {state.Deaths}";

                if (id == _myPlayerId)
                {
                    GUILayout.Label($"<color=lime>{label} (You)</color>");
                }
                else
                {
                    GUILayout.Label(label);
                }
            }

            GUILayout.EndArea();
        }
    }

    private string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024f:F1} KB";
        return $"{bytes / (1024f * 1024f):F2} MB";
    }

    private void OnDestroy()
    {
        DisconnectInternal();
    }
}
