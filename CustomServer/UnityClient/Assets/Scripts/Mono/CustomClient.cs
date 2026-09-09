using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;
using TMPro;

public class CustomClientSample : MonoBehaviour
{
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

    public struct PlayerState2D
    {
        public byte Id;
        public int LastProcessedTick; // 서버가 이 플레이어에게서 실제로 처리한 마지막 clientTick.
                                       // 재조정(reconciliation)에서 pending 입력을 정리할 때 반드시
                                       // 이 값을 기준으로 써야 한다 — ServerLoop 자체가 도는 횟수를
                                       // 세는 전역 카운터(서버 상태 패킷의 tick 필드)는 플레이어의
                                       // 입력 처리와 아무 인과관계가 없어 시간이 지날수록 어긋난다.
        public float X;
        public float Y;
        public float Rotation; // degrees, 0 = +Y(위쪽), 시계방향 증가 (서버와 동일 기준)
        public float CurrentSpeed; // 서버가 보내는 그 틱 시점의 전진(+)/후진(-) 속도
        public byte Health;
        public bool IsDead;
    }

    private struct InputCmd
    {
        public int Tick;
        public float Throttle; // -1(후진) ~ +1(전진)
        public float Turn;     // -1(좌회전) ~ +1(우회전)
    }

    public struct MissileState2D
    {
        public ushort Id;
        public byte OwnerId;
        public float X;
        public float Y;
        public float Rotation;
    }

    /// <summary>
    /// 화면에 실제로 표시 중인 미사일 하나의 상태. 본인이 쏜 미사일은 로컬 예측으로 먼저
    /// 움직이다가(IsLocallyPredicted=true) 서버가 같은 ServerId를 보내오는 순간부터는 서버
    /// 값을 그대로 따라간다. 원격 플레이어의 미사일은 처음부터 서버 값만 사용한다.
    /// </summary>
    private class MissileView
    {
        public GameObject GameObj;
        public ushort? ServerId;       // 서버가 아직 이 미사일을 인지하지 못했다면 null (예측 단계)
        public bool IsLocallyPredicted; // 본인이 쏜, 아직 서버와 매칭 전인 미사일인지
        public Vector2 PredictedPos;
        public float PredictedRot;
        public float SpawnLocalTime;   // 예측 미사일이 서버 확인 없이 너무 오래 떠도는 것을 막기 위한 안전장치용
        public float DistanceTraveled; // 발사 지점으로부터 로컬에서 누적한 이동 거리(유닛). 예측 단계에서만
                                        // 갱신/판정한다 — 서버 확정 후에는 서버가 목록에서 지워주는 것으로
                                        // 충분하므로 이 값을 더 이상 쓰지 않는다.
    }


    private struct Snapshot
    {
        public float LocalTimeStamp;
        public Dictionary<byte, Vector2> Positions;
        public Dictionary<byte, float> Rotations;
    }

    [Header("Server Connection")]
    [SerializeField] private string _serverIp = "127.0.0.1";
    [SerializeField] private int _serverPort = 9050;

    [Header("Reconnection")]
    [SerializeField] private float _connectionTimeoutSeconds = 3.0f; // 이 시간 동안 서버 응답(Pong 등) 없으면 끊김으로 간주
    [SerializeField] private float _reconnectIntervalSeconds = 1.5f; // 재연결 시도 간격

    [Header("Tank Movement (must mirror server constants)")]
    [SerializeField] private float _maxForwardSpeed = 3.0f;
    [SerializeField] private float _maxReverseSpeed = 3.0f;
    [SerializeField] private float _acceleration = 4.0f;
    [SerializeField] private float _turnSpeedDeg = 160f;

    [Header("Play Area Bounds (must exactly equal server WORLD_HALF_EXTENT_X/Y)")]
    [Tooltip("서버 CustomServer.WORLD_HALF_EXTENT_X/Y와 정확히 같은 값이어야 한다(x, y 각각). " +
             "탱크 이동은 이 값을 기준으로 wrap(반대편에서 재등장)한다 — clamp였던 예전과 달리 " +
             "wrap은 '클라이언트가 서버보다 좁게 잡으면 안전하다'는 성질이 없다. wrap 기준 half-extent가 " +
             "서버와 한 유닛이라도 다르면 순간이동이 일어나는 정확한 좌표 자체가 달라져서 재조정이 " +
             "매 틱 어긋난다. 화면 해상도가 1280x720(16:9)으로 고정되어 있으므로 정사각형이 아니라 " +
             "x=7.1111111(=4.0*16/9), y=4.0 이다. 아래 _boundsHalfExtent(카메라 기반, 화면비에 따라 " +
             "이 값보다 좁아질 수 있음)는 오직 화면에 그려지는 시각적 여유 공간 계산용이며 wrap에는 절대 쓰지 않는다.")]
    [SerializeField] private Vector2 _serverWorldHalfExtent = new Vector2(7.1111111f, 4.0f);
    [Tooltip("true면 매 프레임 Camera.main의 실제 Orthographic Size/화면비로 경계를 다시 계산한다. " +
             "false면 아래 Fallback 값을 고정으로 사용한다. 이 계산 결과(_boundsHalfExtent)는 시각적 " +
             "여유 공간 표시에만 쓰이고 이동 시뮬레이션의 wrap 기준으로는 쓰이지 않는다.")]
    [SerializeField] private bool _useCameraForBounds = true;
    [Tooltip("Camera.main을 찾지 못했을 때(또는 _useCameraForBounds가 false일 때) 사용할 절반 크기. " +
             "화면이 1280x720(16:9)으로 고정되므로 _serverWorldHalfExtent와 같은 값을 기본으로 둔다.")]
    [SerializeField] private Vector2 _fallbackHalfExtent = new Vector2(7.1111111f, 4.0f);
    [Tooltip("카메라가 실제로 보여주는 경계에서 이만큼 안쪽으로 더 여유를 둔다. " +
             "탱크 스프라이트 절반 크기(약 0.5) 정도를 넣어야 그림이 화면 끝에서 잘리지 않는다.")]
    [SerializeField] private float _boundsMargin = 0.5f;

    [Header("Prediction & Error Correction")]
    [SerializeField] private float _errorThreshold = 0.05f;          // 위치 오차(미터): 이보다 크면 서서히 보정
    [SerializeField] private float _rotationErrorThresholdDeg = 3.0f; // 회전 오차(도): 이보다 크면 서서히 보정

    [Header("Error Smoothing")]
    [SerializeField] private float _smoothingSpeed = 15.0f;
    [SerializeField] private float _snapThreshold = 2.0f;             // 위치 오차(미터): 이보다 크면 스무딩 없이 즉시 스냅
    [SerializeField] private float _rotationSnapThresholdDeg = 45.0f; // 회전 오차(도): 이보다 크면 스무딩 없이 즉시 스냅

    [Header("Entity Interpolation")]
    [SerializeField] private float _interpolationDelay = 0.1f;

    [Header("Missile (must mirror server constants)")]
    [Tooltip("서버 MISSILE_SPEED와 정확히 같은 값이어야 한다. 다르면 본인이 쏜 미사일이 " +
             "예측 단계에서 서버 확정 위치와 어긋나 순간적으로 튀어 보일 수 있다.")]
    [SerializeField] private float _missileSpeed = 12.0f;
    [Tooltip("서버 FIRE_COOLDOWN_SECONDS와 같은 값. 로컬에서도 동일한 쿨다운을 적용해 " +
             "쿨다운 중에는 예측 미사일조차 생성하지 않게 해서 서버에 도달하지 못할 발사 시도를 미리 걸러낸다.")]
    [SerializeField] private float _fireCooldownSeconds = 0.3f;
    [Tooltip("서버 MISSILE_MAX_DISTANCE와 정확히 같은 값이어야 한다. 서버는 이 거리를 넘은 미사일을 " +
             "삭제해 더 이상 브로드캐스트하지 않으므로, 값이 다르면 예측 미사일이 서버 확정 미사일보다 " +
             "일찍 또는 늦게 사라져 화면이 서버 상태와 어긋나 보인다. 탱크와 달리 미사일은 화면 경계에서 " +
             "wrap하지 않고 발사 방향으로 계속 직진하므로, 오직 이 사거리만으로 수명이 제한된다. 화면이 " +
             "16:9로 고정되며 가로(WORLD_HALF_EXTENT_X*2 ≈ 14.2222)가 세로보다 훨씬 넓어졌으므로, " +
             "사거리는 더 긴 축인 가로 기준으로 잡는다(서버 주석 참고).")]
    [SerializeField] private float _missileMaxDistance = 14.2222222f;
    [Tooltip("예측 미사일이 서버로부터 대응하는 MissileState를 이 시간(초) 안에 받지 못하면 " +
             "(패킷 유실 등으로 서버가 스폰을 못 받았거나 즉시 삭제된 경우) 예측 미사일을 강제로 정리한다. " +
             "[중요] 이 값은 반드시 (_missileMaxDistance / _missileSpeed)보다 커야 한다 — 그보다 작으면 " +
             "정상적으로 사거리 끝까지 날아가는 중인 미사일이 서버 확정을 받기도 전에 이 시간 기반 " +
             "안전장치가 먼저 지워버려서, 서버는 아직 살아있다고 브로드캐스트 중인 미사일이 화면에서만 " +
             "먼저 사라지는 깜빡임이 생긴다. 기본값 14.2222/12.0=약 1.185초보다 넉넉히 긴 1.8초로 잡는다.")]
    [SerializeField] private float _predictedMissileTimeoutSeconds = 1.8f;

    private UdpClient _udpClient;
    private IPEndPoint _serverEP;
    private byte _myPlayerId = 0;
    private int _clientTick = 0;

    private Camera _mainCamera;
    private Vector2 _boundsHalfExtent; // x=좌우 절반 폭, y=상하 절반 높이. 매 프레임(카메라 사용 시) 갱신됨.

    private readonly ConcurrentQueue<(int ServerTick, List<PlayerState2D> States)> _stateQueue = new();
    private readonly Dictionary<byte, GameObject> _playerViews = new();
    private readonly Dictionary<byte, GameObject> _nameplateViews = new();
    private readonly Dictionary<byte, SpriteRenderer> _playerRenderers = new(); // 사망 시 색상 갱신용 캐시
    private readonly HashSet<byte> _deadPlayerIds = new(); // 현재 사망 상태로 표시 중인 플레이어 (색을 매 틱 다시 칠하지 않도록 변화 시점만 갱신)
    private readonly Dictionary<byte, (byte Health, bool IsDead)> _latestHealthById = new(); // OnGUI 표시용 최신 체력 캐시

    /// <summary>
    /// 탱크 하나의 HP 바(배경 + 전경) 뷰. 배경은 항상 고정 크기(빨강)이고, 전경(초록)만
    /// 체력 비율에 맞춰 가로 폭을 줄인다. 두 오브젝트를 하나로 묶어 위치 갱신/파괴를
    /// 한 번에 처리하기 위한 순수 데이터 홀더.
    /// </summary>
    private class HealthBarView
    {
        public GameObject BackgroundObj;
        public GameObject ForegroundObj;
        public SpriteRenderer ForegroundRenderer;
    }
    private readonly Dictionary<byte, HealthBarView> _healthBarViews = new();

    // 원격 플레이어 리스폰 처리용. 서버 프레임을 받아 스냅샷 버퍼에 기록하는 시점(state queue
    // 처리 블록)에서 "이번 프레임에 막 리스폰한 원격 플레이어" ID를 채워 넣고, 그 다음
    // InterpolateRemotePlayers() 호출 한 번에서 소비하며 즉시 비운다. 이 집합에 있는 동안은
    // 해당 플레이어를 일반적인 스냅샷 보간 대상에서 제외하고 _latestRemotePosition/Rotation의
    // 값을 그대로 대입해 "점프"시킨다 — 리스폰은 서버가 일방적으로 결정하는 순간이동이라
    // wrap과 달리 두 스냅샷 사이를 보간하면 죽은 위치에서 새 스폰 위치까지 화면을 가로질러
    // 미끄러지는 것처럼 보이기 때문이다.
    private readonly HashSet<byte> _justRespawnedRemoteIds = new();
    private readonly Dictionary<byte, Vector2> _latestRemotePosition = new(); // 리스폰 점프 대상용 최신 서버 위치
    private readonly Dictionary<byte, float> _latestRemoteRotation = new();   // 리스폰 점프 대상용 최신 서버 회전
    private readonly ConcurrentQueue<List<(byte PlayerId, int Kills, int Deaths)>> _scoreboardQueue = new();
    private readonly Dictionary<byte, (int Kills, int Deaths)> _scoreboard = new(); // OnGUI 표시용 최신 스코어보드
    private Sprite _shipSprite;

    // 미사일 관련 상태
    private readonly ConcurrentQueue<(int ServerTick, List<MissileState2D> Missiles)> _missileStateQueue = new();
    private readonly Dictionary<ushort, MissileView> _missileViews = new(); // 서버가 인지한 미사일: key = 서버 MissileId
    private readonly List<MissileView> _pendingLocalMissiles = new();       // 아직 서버 확인 전인, 본인이 쏜 예측 미사일
    private Sprite _missileSprite;
    private Sprite _hpBarSprite;
    private float _fireCooldownTimer; // 로컬 쿨다운 타이머. 0 이하일 때만 새로 발사 가능.

    // 예측 및 오차 스무딩 변수
    private readonly List<InputCmd> _pendingInputs = new();
    private Vector2 _predictedPos;
    private float _predictedRot;      // degrees
    private float _predictedSpeed;    // 현재 전진(+)/후진(-) 속도, 서버 CurrentSpeed와 동일 개념
    private Vector2 _renderErrorOffset;
    private float _renderRotErrorOffset; // degrees, 회전에 대한 오차 스무딩
    private bool _isLocalPlayerDead; // 서버가 보낸 최신 IsDead 값(본인). FixedUpdate가 입력 처리를 건너뛸지 결정.

    // 재연결 관련 상태
    private bool _hasReconnectedAtLeastOnce = false; // UI에 "재연결 중" 문구를 보여주기 위한 구분용
    private long _timeSinceLastServerMessageMs = 0; // ReceiveLoop(백그라운드 스레드)가 갱신, Interlocked로 접근
    private float _reconnectTimer = 0f;

    // Entity Interpolation 스냅샷 버퍼
    private readonly List<Snapshot> _snapshotBuffer = new();

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

    /// <summary>
    /// 삼각형 탱크 스프라이트를 만든다. 로컬 +Y 방향(위쪽)이 뾰족한 꼭짓점이 되도록 그리며,
    /// 이 방향이 곧 서버/클라이언트가 공유하는 "회전 0도 = 진행 방향" 기준과 일치한다.
    /// 오브젝트의 transform.rotation(Z축, Unity는 반시계+ 이므로 -Rotation을 적용)만 돌리면
    /// 삼각형 꼭짓점이 항상 실제 진행 방향을 가리키게 된다.
    /// </summary>
    // 탱크 스프라이트가 월드 공간에서 차지하는 지름(유닛). 서버 SPAWN_RADIUS(1.2)/SPAWN_SLOTS(16)
    // 기준 인접 스폰 슬롯 간 최소 거리(~0.468유닛)보다 작아야, 여러 명이 동시에 스폰됐을 때
    // 탱크 그림끼리 겹쳐 보이지 않는다. 서버의 스폰 상수를 바꾸면 이 값도 함께 재확인해야 한다.
    private const float SHIP_WORLD_DIAMETER = 0.35f;

    // 네임플레이트를 탱크 위쪽에 띄우는 수직 오프셋(유닛). SHIP_WORLD_DIAMETER에 비례시켜서
    // 탱크 크기가 바뀌어도(예: 0.64 -> 0.35) 이름표가 상대적으로 항상 같은 위치에 보이게 한다.
    // 기존에는 0.64 기준으로 잡았던 0.85f가 그대로 남아있어, 0.35로 축소된 뒤에는 탱크 반지름
    // 대비 비율이 훨씬 커져 이름표가 지나치게 높이 떠 보였다. 비율(0.85 / 0.64 ≈ 1.328)을 유지한다.
    private const float NAMEPLATE_Y_OFFSET = SHIP_WORLD_DIAMETER * 1.328f;

    // HP 바를 탱크 바로 위(네임플레이트보다는 아래)에 띄우는 수직 오프셋.
    private const float HP_BAR_Y_OFFSET = SHIP_WORLD_DIAMETER * 0.75f;
    private const float HP_BAR_WIDTH = SHIP_WORLD_DIAMETER * 1.2f;  // 탱크 지름보다 살짝 넓게
    private const float HP_BAR_HEIGHT = 0.06f;

    // [중요] 서버 CustomServer.MAX_HEALTH와 정확히 같은 값이어야 한다. 서버는
    // 체력의 "현재값"(byte Health)만 브로드캐스트하고 최대값 자체는 보내지 않으므로, HP
    // 바가 채움 비율(현재/최대)을 계산하려면 클라이언트가 최대값을 별도로 알고 있어야 한다.
    // 값이 다르면 체력이 가득 찼는데도 바가 다 안 채워지거나, 반대로 줄어들기 전인데 이미
    // 가득 차 보이는 식으로 실제 체력과 화면 표시가 어긋난다.
    private const int CLIENT_MAX_HEALTH = 10;

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

        // pixelsPerUnit을 역산해서, 텍스처 해상도(res)와 무관하게 항상 SHIP_WORLD_DIAMETER
        // 유닛 크기로 렌더링되게 한다.
        float pixelsPerUnit = res / SHIP_WORLD_DIAMETER;
        return Sprite.Create(tex, new Rect(0, 0, res, res), new Vector2(0.5f, 0.5f), pixelsPerUnit);
    }

    private static bool IsPointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        // 부호 있는 면적을 이용한 barycentric 판정: 세 부호가 모두 같으면 내부에 있음
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

    // 미사일 스프라이트가 월드 공간에서 차지하는 지름(유닛). 서버 MISSILE_RADIUS(0.06)의
    // 지름(0.12)과 시각적으로 맞춘다 — 화면상 크기와 실제 충돌 판정 크기가 크게 다르면
    // "안 맞았는데 맞은 것처럼 보이거나" 그 반대로 보이는 위화감이 생긴다.
    private const float MISSILE_WORLD_DIAMETER = 0.12f;

    /// <summary>
    /// 작은 원형 미사일 스프라이트를 만든다. 미사일은 회전 방향을 표시할 필요가 없는
    /// (단순 탄환) 모양이라 원으로 충분하며, 회전을 적용해도 시각적으로 동일하다.
    /// </summary>
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

    /// <summary>
    /// HP 바(배경/전경 공용)에 쓰는 단색 사각형 스프라이트. 배경(빨강)과 전경(초록)이 같은
    /// 스프라이트를 재사용하고 SpriteRenderer.color만 다르게 입힌다.
    ///
    /// [중요] pivot을 (0f, 0.5f) — 왼쪽 중앙 — 로 잡는다. 기본 pivot(0.5, 0.5)를 쓰면 체력
    /// 비율에 따라 localScale.x를 줄일 때 막대가 중심을 기준으로 양쪽에서 줄어들어(왼쪽도
    /// 오른쪽도 안으로 먹혀듦) 부자연스럽다. 왼쪽 pivot을 쓰면 왼쪽 끝이 항상 고정된 채
    /// 오른쪽 끝만 줄어드는, HP 바에서 흔히 보는 자연스러운 형태가 된다.
    /// </summary>
    private Sprite CreateSolidBarSprite(int res)
    {
        Texture2D tex = new Texture2D(res, res);
        Color[] colors = new Color[res * res];
        for (int i = 0; i < colors.Length; i++) colors[i] = Color.white;

        tex.SetPixels(colors);
        tex.Apply();

        // 이 스프라이트는 HP_BAR_WIDTH x HP_BAR_HEIGHT 크기의 사각형을 pixelsPerUnit=res로
        // 그린 뒤, 실제 배치 시 transform.localScale로 원하는 최종 크기를 맞춘다(월드 지름
        // 상수를 스프라이트 자체에 역산해 넣는 다른 스프라이트들과 달리, 배경/전경 두 개를
        // 서로 다른 최종 크기로 스케일해야 하므로 스프라이트 자체는 1x1 유닛 정사각형으로
        // 단순하게 만들고 크기는 GameObject 쪽에서 조절한다).
        return Sprite.Create(tex, new Rect(0, 0, res, res), new Vector2(0f, 0.5f), res);
    }

    private void Start()
    {
        ValidateFixedTimestepMatchesServer();
        EnforceFixedResolution();

        _mainCamera = Camera.main;
        if (_mainCamera == null)
        {
            Debug.LogWarning("[Client] Camera.main을 찾지 못했습니다. " +
                $"화면 경계를 Fallback Half Extent({_fallbackHalfExtent})로 고정합니다.");
        }
        UpdateBounds();

        // 화면이 극단적으로 좁으면(세로로 매우 긴 화면 등) 서버가 정한 스폰 위치가
        // 클라이언트 화면 경계보다 밖에 위치할 수 있다. 해상도가 1280x720(16:9)으로
        // 고정된 이후에는 이 경고가 정상 실행 중에는 발생하지 않아야 한다 — 만약 발생한다면
        // EnforceFixedResolution()이 실패했거나(플랫폼이 SetResolution을 무시하는 경우 등)
        // _serverWorldHalfExtent 값 자체가 잘못 설정된 것이다.
        const float SERVER_SPAWN_RADIUS = 1.2f; // 서버 SPAWN_RADIUS와 동일한 값
        if (_boundsHalfExtent.x < SERVER_SPAWN_RADIUS || _boundsHalfExtent.y < SERVER_SPAWN_RADIUS)
        {
            Debug.LogWarning(
                $"[Client] 계산된 화면 경계({_boundsHalfExtent.x:F2}, {_boundsHalfExtent.y:F2})가 " +
                $"서버 스폰 반경({SERVER_SPAWN_RADIUS})보다 좁습니다. 해상도가 1280x720으로 " +
                "고정되지 않았거나 Orthographic Size 설정이 예상과 다를 수 있습니다.");
        }

        ConnectToServer();
    }

    /// <summary>
    /// 카메라의 실제 Orthographic Size와 화면비로 "지금 실제로 보이는 화면"의 절반 폭/높이를
    /// 계산해 _boundsHalfExtent에 저장한다.
    ///
    /// [중요] 이동 시뮬레이션(SimulateTankStep)은 더 이상 이 값을 wrap 기준으로 사용하지 않는다 —
    /// wrap은 항상 _serverWorldHalfExtent(서버와 정확히 동일해야 하는 값, x/y 각각)만 사용한다.
    /// 이 함수가 계산하는 _boundsHalfExtent는 오직 두 가지 순수 시각적 용도로만 남아 있다:
    /// (1) Start()의 스폰 반경 경고(화면이 너무 좁아 스폰 지점이 안 보일 수 있는지 확인),
    /// (2) 필요 시 향후 카메라 클리핑/UI 배치 계산. 두 값(_boundsHalfExtent와 _serverWorldHalfExtent)이
    /// 다르다고 해서 예측이 어긋나지는 않는다 — 예전 clamp 시절과 달리 이 값이 이동 로직에
    /// 관여하지 않기 때문이다. 해상도가 1280x720(16:9)으로 고정되어 있으므로 정상적인 실행
    /// 환경에서는 카메라 계산값과 _serverWorldHalfExtent가 (margin을 제외하면) 거의 일치한다.
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

        // 서버가 허용하는 영역(16:9 직사각형, x/y 절반 크기 _serverWorldHalfExtent)보다
        // 넓어지지 않도록 축마다 최종 clamp.
        _boundsHalfExtent = new Vector2(
            Mathf.Min(computed.x, _serverWorldHalfExtent.x),
            Mathf.Min(computed.y, _serverWorldHalfExtent.y));
    }

    /// <summary>
    /// 서버는 60Hz(dt = 1/60)로 시뮬레이션을 계산한다. 클라이언트의 예측(SimulateTankStep)이
    /// FixedUpdate의 Time.fixedDeltaTime을 그대로 dt로 사용하므로, Unity 프로젝트 설정의
    /// Fixed Timestep이 1/60초가 아니면 공식이 완전히 같아도 dt가 달라 예측이 어긋난다.
    /// (Edit > Project Settings > Time > Fixed Timestep)
    /// </summary>
    private void ValidateFixedTimestepMatchesServer()
    {
        const float SERVER_DT = 1f / 60f;
        if (Mathf.Abs(Time.fixedDeltaTime - SERVER_DT) > 0.0005f)
        {
            Debug.LogWarning(
                $"[Client] Fixed Timestep이 {Time.fixedDeltaTime:F5}s로 설정되어 있습니다. " +
                $"서버는 {SERVER_DT:F5}s(60Hz) 기준으로 시뮬레이션하므로, " +
                "Project Settings > Time > Fixed Timestep을 0.01667로 맞추지 않으면 " +
                "클라이언트 예측이 서버와 어긋나 위치/회전이 계속 미세하게 보정되는 현상이 나타날 수 있습니다.");
        }
    }

    private const int FIXED_SCREEN_WIDTH = 1280;
    private const int FIXED_SCREEN_HEIGHT = 720;

    /// <summary>
    /// 화면(창) 해상도를 1280x720으로 고정한다. wrap 기준 플레이 영역(_serverWorldHalfExtent =
    /// x:7.1111, y:4.0)이 정확히 16:9(1280x720) 화면비를 전제로 계산되어 있으므로, 실제 창
    /// 크기가 이 비율에서 벗어나면(창 크기 조절, 다른 해상도 빌드 설정 등) 화면에 레터박스가
    /// 생기거나(좁아진 경우) 플레이 영역 밖의 여백이 보이는(넓어진 경우) 시각적 불일치가
    /// 생긴다 — wrap이 일어나는 좌표 자체는 여전히 정확하지만("wrap 기준은 화면비와 무관하게
    /// 서버 값 하나로 고정한다"는 기존 설계 원칙 덕분), 화면에 보이는 벽의 위치가 실제
    /// 시뮬레이션 경계와 어긋나 보이게 된다.
    ///
    /// [중요] Screen.SetResolution은 빌드된 실행 파일(스탠드얼론)에서만 창 크기를 바꾼다.
    /// Unity 에디터의 Game 뷰 해상도는 이 API로 제어되지 않는, 별도의 에디터 UI 설정이다 —
    /// 에디터에서 정확히 1280x720 비율로 테스트하려면 Game 뷰 상단의 해상도 드롭다운에서
    /// "1280x720" 프리셋을 직접 추가/선택해야 한다(코드로 자동화할 수 없는 에디터 전용 설정).
    /// WebGL 빌드에서도 이 API는 브라우저 캔버스 크기를 바꾸지 못하므로, WebGL 템플릿의
    /// index.html에서 캔버스 크기를 1280x720으로 고정해야 한다.
    /// </summary>
    private void EnforceFixedResolution()
    {
#if UNITY_EDITOR
        // Screen.SetResolution은 에디터의 Game 뷰 렌더링 해상도에 영향을 주지 못한다(Unity의
        // 알려진 동작 — 호출은 되지만 조용히 무시된다). 그래서 아래 SetResolution 호출은
        // 에디터에서는 사실상 아무 효과가 없다. 코드가 "성공한 것처럼" 조용히 넘어가면
        // 사람이 이 사실을 놓치기 쉬우므로, Play 모드 진입 시 매번 안내를 남긴다.
        Debug.LogWarning(
            "[Client] 에디터에서는 Screen.SetResolution으로 Game 뷰 해상도를 바꿀 수 없습니다. " +
            "wrap 기준 플레이 영역이 1280x720(16:9)을 전제로 계산되어 있으므로, Game 뷰 상단 " +
            "해상도 드롭다운에서 '1280x720' 프리셋을 직접 추가하고 선택해주세요. " +
            "그렇지 않으면 벽(wrap 경계)이 실제 화면 가장자리와 어긋나 보일 수 있습니다 " +
            "(시뮬레이션 좌표 자체는 정확하지만, 화면비가 다르면 보이는 위치가 어긋납니다).");
#endif
#if UNITY_STANDALONE || UNITY_EDITOR
        if (Screen.width != FIXED_SCREEN_WIDTH || Screen.height != FIXED_SCREEN_HEIGHT)
        {
            Screen.SetResolution(FIXED_SCREEN_WIDTH, FIXED_SCREEN_HEIGHT, FullScreenMode.Windowed);
        }
#else
        Debug.LogWarning(
            $"[Client] 이 플랫폼에서는 Screen.SetResolution으로 해상도를 강제할 수 없습니다. " +
            $"플레이 영역 wrap 기준이 1280x720(16:9)을 전제로 계산되어 있으므로, 플랫폼별 설정 " +
            "(모바일 화면, WebGL 캔버스 등)에서 화면비를 16:9로 맞춰야 벽 위치가 시각적으로 정확합니다.");
#endif
    }

    /// <summary>
    /// 서버에 연결(또는 재연결)을 시도한다. 이미 연결된 소켓이 있다면 먼저 정리한 뒤
    /// 새로 연결하므로, 서버가 재시작된 뒤에도 이 함수를 다시 호출하는 것만으로 복구된다.
    /// </summary>
    public void ConnectToServer()
    {
        // 기존 연결이 있다면 완전히 정리 (재연결 시도를 막던 기존의 조기 리턴 제거)
        DisconnectInternal();

        // 재연결 시도 자체를 "서버가 살아있다는 신호가 막 왔다"처럼 취급해 타이머를 리셋한다.
        // 이렇게 하지 않으면 재연결 응답이 오기 전에 워치독이 또 재연결을 걸어버릴 수 있다.
        Interlocked.Exchange(ref _timeSinceLastServerMessageMs, DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond);
        _reconnectTimer = 0f;

        _serverEP = new IPEndPoint(IPAddress.Parse(_serverIp), _serverPort);
        _udpClient = new UdpClient();
        _udpClient.Connect(_serverEP);

        UdpClient socketForThisConnection = _udpClient;
        Thread rxThread = new Thread(() => ReceiveLoop(socketForThisConnection)) { IsBackground = true };
        rxThread.Start();

        byte[] joinPacket = { (byte)PacketType.JoinRequest };
        SendPacket(joinPacket);

        Debug.Log($"[Client] Connecting to {_serverIp}:{_serverPort}...");
    }

    /// <summary>현재 소켓을 닫고 플레이어 관련 로컬 상태를 초기화한다. 재연결 전 정리용.</summary>
    private void DisconnectInternal()
    {
        if (_udpClient != null)
        {
            try { _udpClient.Close(); } catch { }
            _udpClient = null;
        }

        _myPlayerId = 0;
        _clientTick = 0;
        _pendingInputs.Clear();
        _snapshotBuffer.Clear();
        _isLocalPlayerDead = false;
        _scoreboard.Clear();
        ClearAllPlayerViews();
        ClearAllMissileViews();
    }

    private void SendPacket(byte[] bytes)
    {
        if (_udpClient == null) return;

        _udpClient.Send(bytes, bytes.Length);
        Interlocked.Add(ref _totalBytesSent, bytes.Length);
        Interlocked.Add(ref _bytesSentThisSec, bytes.Length);
    }

    private void ReceiveLoop(UdpClient socket)
    {
        // 이 스레드는 시작될 때 전달받은 socket 인스턴스만 사용한다.
        // 필드 _udpClient를 직접 참조하면, 재연결로 소켓이 교체된 뒤에도
        // 이전 스레드가 새 소켓을 같이 Receive하는 race condition이 발생할 수 있다.
        IPEndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
        while (socket == _udpClient)
        {
            try
            {
                byte[] data = socket.Receive(ref remoteEP);

                Interlocked.Add(ref _totalBytesReceived, data.Length);
                Interlocked.Add(ref _bytesReceivedThisSec, data.Length);

                using var ms = new MemoryStream(data);
                using var br = new BinaryReader(ms);
                PacketType type = (PacketType)br.ReadByte();

                // 서버로부터 어떤 패킷이든 도착했다는 것은 연결이 살아있다는 뜻이므로
                // 재연결 워치독 타이머를 매번 초기화한다.
                Interlocked.Exchange(ref _timeSinceLastServerMessageMs, DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond);

                if (type == PacketType.JoinResponse)
                {
                    _myPlayerId = br.ReadByte();
                    Debug.Log($"[Client] Connected! Assigned My Player ID: {_myPlayerId}");
                }
                else if (type == PacketType.ServerState)
                {
                    int tick = br.ReadInt32();
                    int count = br.ReadInt32();

                    List<PlayerState2D> states = new List<PlayerState2D>();
                    for (int i = 0; i < count; i++)
                    {
                        states.Add(new PlayerState2D
                        {
                            Id = br.ReadByte(),
                            LastProcessedTick = br.ReadInt32(),
                            X = br.ReadSingle(),
                            Y = br.ReadSingle(),
                            Rotation = br.ReadSingle(),
                            CurrentSpeed = br.ReadSingle(),
                            Health = br.ReadByte(),
                            IsDead = br.ReadBoolean()
                        });
                    }
                    _stateQueue.Enqueue((tick, states));
                }
                else if (type == PacketType.Pong)
                {
                    long sendTimeTicks = br.ReadInt64();
                    float rttMs = (float)(DateTime.UtcNow.Ticks - sendTimeTicks) / TimeSpan.TicksPerMillisecond;
                    _currentPingMs = rttMs;
                }
                else if (type == PacketType.MissileState)
                {
                    int tick = br.ReadInt32();
                    int count = br.ReadInt32();

                    List<MissileState2D> missiles = new List<MissileState2D>();
                    for (int i = 0; i < count; i++)
                    {
                        missiles.Add(new MissileState2D
                        {
                            Id = br.ReadUInt16(),
                            OwnerId = br.ReadByte(),
                            X = br.ReadSingle(),
                            Y = br.ReadSingle(),
                            Rotation = br.ReadSingle()
                        });
                    }
                    _missileStateQueue.Enqueue((tick, missiles));
                }
                else if (type == PacketType.ScoreboardState)
                {
                    int count = br.ReadInt32();

                    List<(byte PlayerId, int Kills, int Deaths)> entries = new();
                    for (int i = 0; i < count; i++)
                    {
                        byte pid = br.ReadByte();
                        int kills = br.ReadInt32();
                        int deaths = br.ReadInt32();
                        entries.Add((pid, kills, deaths));
                    }
                    _scoreboardQueue.Enqueue(entries);
                }
            }
            catch (ObjectDisposedException)
            {
                // 재연결/종료 과정에서 소켓이 Close된 경우. 정상적인 스레드 종료 경로이므로 조용히 빠져나간다.
                break;
            }
            catch (SocketException)
            {
                // 소켓이 닫혔거나 일시적 네트워크 오류. socket == _udpClient 검사로 다음 루프에서 자연 종료된다.
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Client] ReceiveLoop error: {ex.Message}");
            }
        }
    }

    private void FixedUpdate()
    {
        if (_udpClient == null || _myPlayerId == 0) return;

        UpdateBounds();

        var (throttle, turn, fireHeld) = GetTankInput();

        // 사망한 동안은 서버가 어차피 이동/발사를 모두 무시하므로, 클라이언트도 로컬 예측을
        // 진행하지 않는다. 예측을 계속 돌리면 죽은 채로 화면상 움직이다가 다음 서버 상태를
        // 받는 순간 원래 죽은 자리로 스냅되는 어색한 끊김이 생긴다. 입력 패킷 자체는 그대로
        // 보내서(throttle/turn은 0으로) 서버의 LastProcessedTick 갱신과 재조정 흐름은 유지한다.
        if (_isLocalPlayerDead)
        {
            throttle = 0f;
            turn = 0f;
            fireHeld = false;
        }

        // 입력이 완전히 0이어도(관성으로 감속 중일 수 있으므로) 시뮬레이션은 계속 돌려야 한다.
        // 과거 코드처럼 "입력이 0이면 통째로 스킵"하면 감속/정지 과정이 서버와 어긋난다.
        _clientTick++;

        float dt = Time.fixedDeltaTime;
        if (!_isLocalPlayerDead)
        {
            (_predictedPos, _predictedRot, _predictedSpeed) =
                SimulateTankStep(_predictedPos, _predictedRot, _predictedSpeed, throttle, turn, dt);
        }

        _pendingInputs.Add(new InputCmd { Tick = _clientTick, Throttle = throttle, Turn = turn });

        // 발사: 스페이스바를 누르고 있는 동안 쿨다운(_fireCooldownSeconds)마다 자동으로
        // 계속 발사한다. 엣지 검출 없이 fireHeld가 true인 동안 쿨다운만 확인한다.
        if (_fireCooldownTimer > 0f) _fireCooldownTimer -= dt;

        if (fireHeld && _fireCooldownTimer <= 0f)
        {
            _fireCooldownTimer = _fireCooldownSeconds;
            SpawnPredictedMissile();
        }

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write((byte)PacketType.ClientInput);
        bw.Write(_myPlayerId);
        bw.Write(_clientTick);
        bw.Write(throttle);
        bw.Write(turn);
        // 서버에는 "눌려있는 동안 계속 true"인 원본 상태(fireHeld)를 보낸다. 서버도 동일하게
        // 엣지 검출 없이 쿨다운만으로 발사를 허용하므로, 이 패킷 하나가 유실돼도 다음 프레임에
        // 도착한 패킷만으로 정상적으로 연사가 이어진다 — 순간의 엣지 신호에 의존하지 않는다.
        bw.Write(fireHeld);

        SendPacket(ms.ToArray());
    }

    /// <summary>
    /// 탱크 컨트롤 입력을 (throttle, turn, fire)으로 반환한다.
    /// throttle: 위쪽 입력 = +1(전진 가속), 아래쪽 입력 = -1(후진). turn: 오른쪽 = +1, 왼쪽 = -1.
    /// fire: 스페이스바가 눌려있는 동안 true. 실제 발사 빈도 제한(쿨다운)은 호출부(FixedUpdate)에서 한다.
    /// InputConfigManager가 있는 프로젝트에서는 기존 Vector2 인터페이스를 그대로 활용하되
    /// y축을 throttle, x축을 turn으로 매핑해 하위 호환을 유지한다.
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
    /// 좌표 하나를 [-halfExtent, +halfExtent) 범위로 순환(wrap)시킨다.
    ///
    /// [중요] CustomServer.WrapCoordinate, 그리고 ECS의
    /// LocalPlayerFixedStepSystem.WrapCoordinate와 정확히 동일한 공식이어야 한다. 세 곳 중
    /// 하나만 달라도(예: 반개구간 방향, 이중 % 처리 방식) wrap이 일어나는 정확한 좌표가
    /// 미세하게 어긋나 재조정이 매 틱 실패하기 시작한다.
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
    /// 서버(HandleClientInput)와 정확히 동일한 순서/공식으로 한 스텝을 시뮬레이션한다.
    /// FixedUpdate의 예측과 reconciliation replay 양쪽에서 반드시 이 함수만 사용해야
    /// 클라이언트 예측이 서버 결과와 어긋나지 않는다. 이 함수를 수정할 때는 서버의
    /// HandleClientInput도 반드시 같이 맞춰야 한다.
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

        // 3) 현재 방향으로 전진/후진 (0도 = +Y, 시계방향 증가 — 서버와 동일 기준)
        float rotRad = rotDeg * Mathf.Deg2Rad;
        Vector2 dir = new Vector2(Mathf.Sin(rotRad), Mathf.Cos(rotRad));
        pos += dir * speed * dt;

        // 4) 화면(플레이 가능 영역) 경계에서 반대편으로 순환(wrap)한다. 속도/회전은 그대로
        //    둔다 — wrap은 위치 좌표만 바꾸는 순간이동이다.
        //    [중요] 반드시 _serverWorldHalfExtent(x/y 각각)를 기준으로 wrap해야 한다. 카메라
        //    화면비에 따라 달라지는 _boundsHalfExtent(더 좁을 수 있음)를 쓰면, wrap이 일어나는
        //    좌표 자체가 서버와 달라져서 매 틱 재조정이 실패한다 — clamp였던 예전과 달리
        //    "좁게 잡는 것은 안전하다"는 성질이 wrap에는 없다. 화면이 16:9로 고정되어 X/Y의
        //    half-extent가 서로 다르므로, 각 축은 반드시 자신의 값으로만 wrap해야 한다.
        pos.x = WrapCoordinate(pos.x, _serverWorldHalfExtent.x);
        pos.y = WrapCoordinate(pos.y, _serverWorldHalfExtent.y);

        return (pos, rotDeg, speed);
    }

    /// <summary>
    /// 서버/클라이언트가 공유하는 회전 규칙(0도 = +Y, 시계방향 증가)을
    /// Unity의 Z축 오일러 각(반시계방향 증가)으로 변환해 오브젝트에 적용한다.
    /// 회전을 적용하는 모든 지점(본인 예측, 원격 보간)은 반드시 이 함수를 거쳐야
    /// 두 규칙이 뒤섞여 방향이 반대로 나오는 실수를 막을 수 있다.
    /// </summary>
    private static void ApplyShipRotation(GameObject shipObj, float rotationDeg)
    {
        shipObj.transform.rotation = Quaternion.Euler(0f, 0f, -rotationDeg);
    }

    /// <summary>
    /// 스페이스바가 쿨다운을 통과해 발사가 허용될 때마다(누르고 있으면 반복 호출됨),
    /// 서버 응답을 기다리지 않고 로컬에서 즉시 미사일을 만들어 발사 반응성을 높인다.
    /// 이 미사일은 아직 서버가 모르는 상태(ServerId == null)이며, Update의 미사일 상태
    /// 처리 루프에서 서버가 보낸 목록과 위치/시간 기준으로 매칭되면 그때부터 서버 값으로
    /// 갈아탄다(예측 -> 확정 전환). 매칭에 실패한 채 타임아웃이 지나면(서버가 발사를
    /// 거부했거나 패킷이 유실된 경우) 스스로 정리된다.
    /// </summary>
    private void SpawnPredictedMissile()
    {
        GameObject missileObj = new GameObject("PredictedMissile");
        var sr = missileObj.AddComponent<SpriteRenderer>();
        sr.sprite = _missileSprite;
        sr.color = Color.yellow;
        sr.sortingOrder = 5;

        missileObj.transform.position = (Vector3)_predictedPos;

        var view = new MissileView
        {
            GameObj = missileObj,
            ServerId = null,
            IsLocallyPredicted = true,
            PredictedPos = _predictedPos,
            PredictedRot = _predictedRot,
            SpawnLocalTime = Time.time,
            DistanceTraveled = 0f
        };

        _pendingLocalMissiles.Add(view);
    }

    private void Update()
    {
        UpdateBounds();
        UpdateTrafficMetrics();
        SendPingCheck();
        UpdateReconnectionWatchdog();
        UpdateAttachedUiPositions();
        UpdateFpsCounter();

        while (_stateQueue.TryDequeue(out var frame))
        {
            // [수정] 여기서 더 이상 frame.ServerTick(ServerLoop가 도는 횟수를 세는, 플레이어
            // 입력 처리와 무관한 전역 카운터)을 쓰지 않는다. pending 입력 정리는 이제 아래
            // foreach 안에서 플레이어별 s.LastProcessedTick 기준으로 처리한다 — 이유는 그
            // 아래 reconciliation 분기의 주석 참고.
            var states = frame.States;
            HashSet<byte> activeIds = new HashSet<byte>();
            var snapshotPositions = new Dictionary<byte, Vector2>();
            var snapshotRotations = new Dictionary<byte, float>();

            foreach (var s in states)
            {
                activeIds.Add(s.Id);
                Vector2 serverPos = new Vector2(s.X, s.Y);
                snapshotPositions[s.Id] = serverPos;
                snapshotRotations[s.Id] = s.Rotation;

                if (!_playerViews.TryGetValue(s.Id, out GameObject shipObj))
                {
                    shipObj = CreatePlayerObject(s.Id, s.Rotation);
                    _playerViews[s.Id] = shipObj;
                    if (s.Id == _myPlayerId)
                    {
                        _predictedPos = serverPos;
                        _predictedRot = s.Rotation;
                        _predictedSpeed = s.CurrentSpeed;
                    }
                }

                bool justRespawned = UpdateDeathVisual(s.Id, s.IsDead);
                _latestHealthById[s.Id] = (s.Health, s.IsDead);
                UpdateHealthBar(s.Id, s.Health);

                if (s.Id == _myPlayerId)
                {
                    _isLocalPlayerDead = s.IsDead;

                    if (justRespawned)
                    {
                        _pendingInputs.Clear();

                        _predictedPos = serverPos;
                        _predictedRot = s.Rotation;
                        _predictedSpeed = s.CurrentSpeed;
                        _renderErrorOffset = Vector2.zero;
                        _renderRotErrorOffset = 0f;
                    }
                    else
                    {
                        // 서버가 이 플레이어에게서 실제로 처리한 마지막 입력 틱인
                        // s.LastProcessedTick(HandleClientInput이 그 플레이어가 보낸
                        // clientTick을 그대로 기록한 값)과 비교한다. 이 값은 ServerLoop의
                        // 실행 속도나 접속 시점과 완전히 무관하게, 항상 "서버가 실제로 반영한
                        // 내 마지막 입력이 몇 번째 틱이었는가"만을 정확히 가리킨다.
                        _pendingInputs.RemoveAll(cmd => cmd.Tick <= s.LastProcessedTick);

                        // 서버가 확정한 (위치, 회전, 속도)에서 시작해서, 서버가 아직 처리하지 못한
                        // pending 입력들을 순서대로 재생한다. 반드시 SimulateTankStep 하나만 써서
                        // FixedUpdate의 예측과 완전히 동일한 공식으로 계산해야 드리프트가 없다.
                        Vector2 reconciledPos = serverPos;
                        float reconciledRot = s.Rotation;
                        float reconciledSpeed = s.CurrentSpeed;
                        float dt = Time.fixedDeltaTime;

                        foreach (var pending in _pendingInputs)
                        {
                            (reconciledPos, reconciledRot, reconciledSpeed) =
                                SimulateTankStep(reconciledPos, reconciledRot, reconciledSpeed, pending.Throttle, pending.Turn, dt);
                        }

                        // [중요] 단순 뺄셈(reconciledPos - _predictedPos) 대신 wrap 경계를 최단 경로로
                        // 넘는 차이를 구한다. 예측과 재조정이 wrap 경계를 사이에 두고 한쪽만 이미
                        // wrap된 상태(예: 예측=-3.99, 재조정=+3.99, 실제로는 0.02만큼만 떨어진 이웃
                        // 좌표)라면, 단순 뺄셈은 이를 거의 range만큼의 거대한 오차로 오인해
                        // 불필요한 스냅을 일으킨다. WrapCoordinate로 최단 차이를 구하면 이런 경우
                        // 실제 물리적 거리(0.02)에 가까운 값이 나와 오차 판정(스냅/보정/유지)이
                        // 항상 "실제로 얼마나 떨어져 있는가" 기준으로 정확하게 이루어진다. 화면이
                        // 16:9로 고정되어 X/Y half-extent가 다르므로, 각 축은 자신의 값(.x/.y)으로
                        // wrap해야 한다.
                        Vector2 posError = new Vector2(
                            WrapCoordinate(reconciledPos.x - _predictedPos.x, _serverWorldHalfExtent.x),
                            WrapCoordinate(reconciledPos.y - _predictedPos.y, _serverWorldHalfExtent.y));
                        float rotError = Mathf.DeltaAngle(_predictedRot, reconciledRot); // -180..180 범위의 최단 각도 차이
                        float posErrorSqrMag = posError.sqrMagnitude;
                        float rotErrorAbs = Mathf.Abs(rotError);

                        // 위치: 스냅(즉시 이동) / 보정(오프셋으로 부드럽게 흡수) / 유지(오차가 작아 무시) 3단 처리
                        if (posErrorSqrMag > _snapThreshold * _snapThreshold)
                        {
                            _predictedPos = reconciledPos;
                            _renderErrorOffset = Vector2.zero;
                        }
                        else if (posErrorSqrMag > _errorThreshold * _errorThreshold)
                        {
                            _predictedPos = reconciledPos;
                            _renderErrorOffset -= posError;
                        }

                        // 회전도 위치와 동일한 3단 구조로 처리. 임계값은 각도(도) 단위 전용 필드를 사용한다.
                        if (rotErrorAbs > _rotationSnapThresholdDeg)
                        {
                            _predictedRot = reconciledRot;
                            _renderRotErrorOffset = 0f;
                        }
                        else if (rotErrorAbs > _rotationErrorThresholdDeg)
                        {
                            _predictedRot = reconciledRot;
                            _renderRotErrorOffset -= rotError;
                        }

                        _predictedSpeed = reconciledSpeed;
                    }
                }
                else if (justRespawned)
                {
                    // 원격 플레이어가 이번 프레임에 리스폰했다. InterpolateRemotePlayers()는
                    // _interpolationDelay(기본 0.1초)만큼 과거 시점을 스냅샷 두 개 사이로 보간해
                    // 렌더링하므로, 아무 조치 없이 두면 "죽은 위치"와 "새 스폰 위치" 스냅샷
                    // 사이를 그대로 선형보간해 화면을 가로질러 미끄러지는 것처럼 보인다.
                    // ID만 기록해두고 실제 점프 처리(보간 건너뛰기)는 InterpolateRemotePlayers()가
                    // 이 프레임 렌더링 시점에 한다 — 여기서 바로 transform을 만지지 않는 이유는
                    // 아직 이 오브젝트의 화면 좌표가 로컬 시점 기준 몇 프레임 전 상태일 수 있어,
                    // 렌더링 파이프라인의 정해진 위치(보간 단계)에서 처리해야 다른 로직과
                    // 순서가 꼬이지 않기 때문이다.
                    _justRespawnedRemoteIds.Add(s.Id);
                }

                // 리스폰 점프 대상 판정과 무관하게, 다음 리스폰 감지를 위해 매 프레임 최신
                // 서버 위치/회전을 캐시해둔다. InterpolateRemotePlayers()가 _justRespawnedRemoteIds에
                // 있는 ID를 이 값으로 즉시 스냅한다.
                if (s.Id != _myPlayerId)
                {
                    _latestRemotePosition[s.Id] = serverPos;
                    _latestRemoteRotation[s.Id] = s.Rotation;
                }
            }

            _snapshotBuffer.Add(new Snapshot
            {
                LocalTimeStamp = Time.time,
                Positions = snapshotPositions,
                Rotations = snapshotRotations
            });

            _snapshotBuffer.RemoveAll(sn => Time.time - sn.LocalTimeStamp > 2.0f);
            RemoveDisconnectedPlayers(activeIds);
        }

        while (_missileStateQueue.TryDequeue(out var missileFrame))
        {
            ApplyMissileServerState(missileFrame.Missiles);
        }

        while (_scoreboardQueue.TryDequeue(out var entries))
        {
            ApplyScoreboardState(entries);
        }

        UpdatePredictedMissiles(Time.deltaTime);

        InterpolateRemotePlayers();

        if (_playerViews.TryGetValue(_myPlayerId, out GameObject myObj))
        {
            _renderErrorOffset = Vector2.Lerp(_renderErrorOffset, Vector2.zero, Time.deltaTime * _smoothingSpeed);
            _renderRotErrorOffset = Mathf.Lerp(_renderRotErrorOffset, 0f, Time.deltaTime * _smoothingSpeed);

            Vector2 displayPos = _predictedPos + _renderErrorOffset;
            float displayRot = _predictedRot + _renderRotErrorOffset;

            myObj.transform.position = (Vector3)displayPos;
            ApplyShipRotation(myObj, displayRot);
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

    /// <summary>
    /// 서버가 보낸 이번 틱의 활성 미사일 목록을 화면에 반영한다.
    /// - 목록에 있는데 아직 뷰가 없는 미사일: 새로 생성한다. 단, 발사자가 본인(_myPlayerId)이라면
    ///   먼저 _pendingLocalMissiles에서 시간상 가장 가까운(그리고 아직 서버와 매칭 안 된) 예측
    ///   미사일을 찾아 그 GameObject를 그대로 재사용하며 "확정" 상태로 전환한다. 이렇게 하면
    ///   예측 미사일이 사라졌다가 확정 미사일이 새로 나타나는 깜빡임 없이 매끄럽게 이어진다.
    /// - 목록에 있고 뷰도 있는 미사일: 위치/회전을 서버 값으로 갱신한다(원격 미사일은 보간 없이
    ///   직접 대입 — 미사일은 수명이 짧고 빨라서 오차 스무딩보다 즉시 반영이 더 자연스럽다).
    /// - 목록에 없는데 뷰가 남아있는 미사일: 서버에서 이미 삭제된 것이므로(사거리 소진 또는
    ///   탱크 충돌) 뷰도 즉시 파괴한다.
    /// </summary>
    private void ApplyMissileServerState(List<MissileState2D> serverMissiles)
    {
        var activeServerIds = new HashSet<ushort>();

        foreach (var m in serverMissiles)
        {
            activeServerIds.Add(m.Id);

            if (_missileViews.TryGetValue(m.Id, out MissileView view))
            {
                if (view.GameObj != null)
                {
                    view.GameObj.transform.position = new Vector3(m.X, m.Y, 0f);
                }
                continue;
            }

            // 아직 이 서버 미사일 ID에 대응하는 뷰가 없다 -> 신규.
            // 본인이 쏜 미사일이라면 예측 목록에서 매칭을 시도해 GameObject를 재사용한다.
            MissileView matched = null;
            if (m.OwnerId == _myPlayerId)
            {
                matched = FindClosestPendingLocalMissile(new Vector2(m.X, m.Y));
            }

            if (matched != null)
            {
                _pendingLocalMissiles.Remove(matched);
                matched.ServerId = m.Id;
                matched.IsLocallyPredicted = false;
                if (matched.GameObj != null)
                {
                    matched.GameObj.transform.position = new Vector3(m.X, m.Y, 0f);
                }
                _missileViews[m.Id] = matched;
            }
            else
            {
                // 예측 매칭이 안 됐다 — 원격 플레이어의 미사일이거나, 본인 미사일인데 예측을
                // 만들지 못한 경우(쿨다운 등)다. 새로 생성한다.
                GameObject missileObj = new GameObject($"Missile_{m.Id}");
                var sr = missileObj.AddComponent<SpriteRenderer>();
                sr.sprite = _missileSprite;
                sr.color = (m.OwnerId == _myPlayerId) ? Color.yellow : Color.white;
                sr.sortingOrder = 5;
                missileObj.transform.position = new Vector3(m.X, m.Y, 0f);

                _missileViews[m.Id] = new MissileView
                {
                    GameObj = missileObj,
                    ServerId = m.Id,
                    IsLocallyPredicted = false
                };
            }
        }

        // 서버 목록에서 사라진(사거리 소진 또는 탱크 충돌로 삭제된) 미사일의 뷰를 정리한다.
        List<ushort> toRemove = new List<ushort>();
        foreach (var kvp in _missileViews)
        {
            if (!activeServerIds.Contains(kvp.Key))
            {
                if (kvp.Value.GameObj != null) Destroy(kvp.Value.GameObj);
                toRemove.Add(kvp.Key);
            }
        }
        foreach (var id in toRemove) _missileViews.Remove(id);
    }

    /// <summary>
    /// 서버가 보낸 이번 틱의 전체 킬/데스 목록으로 로컬 스코어보드 캐시를 완전히 교체한다.
    /// 서버가 매 틱 전체 스냅샷을 보내므로(증분이 아니라), 여기서는 통째로 덮어써도 안전하고
    /// 접속 종료된 플레이어의 옛 기록이 자동으로 사라지는 효과도 겸한다.
    /// </summary>
    private void ApplyScoreboardState(List<(byte PlayerId, int Kills, int Deaths)> entries)
    {
        _scoreboard.Clear();
        foreach (var e in entries)
        {
            _scoreboard[e.PlayerId] = (e.Kills, e.Deaths);
        }
    }

    /// <summary>
    /// 아직 서버와 매칭되지 않은 예측 미사일 중, 주어진 서버 위치와 가장 가까운 것을 찾는다.
    /// 발사 직후 몇 프레임 안에는 예측 위치와 서버 위치가 거의 같으므로 단순 최근접 매칭으로
    /// 충분하다(같은 플레이어가 같은 프레임에 두 발 이상 쏘는 것은 쿨다운으로 이미 막혀 있다).
    /// </summary>
    private MissileView FindClosestPendingLocalMissile(Vector2 serverPos)
    {
        MissileView best = null;
        float bestDistSqr = float.MaxValue;

        foreach (var candidate in _pendingLocalMissiles)
        {
            float distSqr = (candidate.PredictedPos - serverPos).sqrMagnitude;
            if (distSqr < bestDistSqr)
            {
                bestDistSqr = distSqr;
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>
    /// 아직 서버 확인을 받지 못한 예측 미사일들을 매 프레임 직접 전진시킨다(탱크처럼 별도
    /// 재조정 로직 없이 단순 직선 이동만 수행 — 미사일은 수명이 짧아 서버 확정이 오면 바로
    /// 그 값으로 대체되므로 정교한 오차 보정이 필요 없다). 다음 두 조건 중 하나라도 만족하면
    /// 스스로 제거한다:
    /// 1) 누적 이동 거리가 _missileMaxDistance에 도달 — 서버도 같은 조건으로 이 미사일을
    ///    이미(또는 곧) 지울 것이므로, 클라이언트가 사거리 끝에서 예측을 미리 정리해 서버
    ///    확정을 기다리는 동안 미사일이 화면 밖으로 계속 날아가 보이는 것을 막는다.
    /// 2) 타임아웃(_predictedMissileTimeoutSeconds) 초과 — 패킷 유실 등으로 서버가 스폰
    ///    자체를 못 받았거나 즉시 삭제한 경우의 안전장치. 정상 케이스에서는 조건 1이 먼저
    ///    발동하므로 이 타임아웃은 항상 (_missileMaxDistance / _missileSpeed)보다 넉넉히
    ///    길게 설정되어 있어야 한다.
    /// </summary>
    private void UpdatePredictedMissiles(float dt)
    {
        for (int i = _pendingLocalMissiles.Count - 1; i >= 0; i--)
        {
            var view = _pendingLocalMissiles[i];

            if (Time.time - view.SpawnLocalTime > _predictedMissileTimeoutSeconds)
            {
                if (view.GameObj != null) Destroy(view.GameObj);
                _pendingLocalMissiles.RemoveAt(i);
                continue;
            }

            if (view.DistanceTraveled >= _missileMaxDistance)
            {
                if (view.GameObj != null) Destroy(view.GameObj);
                _pendingLocalMissiles.RemoveAt(i);
                continue;
            }

            float rotRad = view.PredictedRot * Mathf.Deg2Rad;
            Vector2 dir = new Vector2(Mathf.Sin(rotRad), Mathf.Cos(rotRad));
            float stepDistance = _missileSpeed * dt;
            view.PredictedPos += dir * stepDistance;
            view.DistanceTraveled += stepDistance;

            if (view.GameObj != null)
            {
                view.GameObj.transform.position = (Vector3)view.PredictedPos;
            }
        }
    }

    /// <summary>
    /// 네임플레이트와 HP 바는 더 이상 탱크의 자식이 아니므로(회전에 딸려 기울어지거나,
    /// HP 바의 경우 부모 localScale과 곱연산으로 얽혀 폭 조절이 왜곡되는 것을 막기 위해),
    /// 매 프레임 대응하는 탱크의 현재 위치를 읽어 자신의 위치만 갱신한다. 회전은 절대
    /// 따라가지 않는다. (구 UpdateNameplatePositions — HP 바 추가로 "탱크를 따라다니는
    /// 붙어있는 UI 전반"을 다루게 되어 이름을 일반화했다.)
    /// </summary>
    private void UpdateAttachedUiPositions()
    {
        foreach (var kvp in _playerViews)
        {
            byte id = kvp.Key;
            GameObject shipObj = kvp.Value;
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
    /// 서버로부터 일정 시간 동안 아무 응답도 없으면(서버가 죽었다가 재시작된 경우 포함)
    /// 연결이 끊긴 것으로 간주하고 주기적으로 재연결을 시도한다.
    /// 최초 연결이 아직 한 번도 성립하지 않은 상태(_myPlayerId == 0 && 소켓이 없음)에서는
    /// Start()의 최초 ConnectToServer 호출이 이미 처리했으므로 여기서 다시 트리거하지 않는다.
    /// </summary>
    private void UpdateReconnectionWatchdog()
    {
        if (_udpClient == null) return; // 아직 한 번도 연결 시도가 없었던 초기 상태

        long lastMsgMs = Interlocked.Read(ref _timeSinceLastServerMessageMs);
        long nowMs = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        float secondsSinceLastMessage = (nowMs - lastMsgMs) / 1000f;

        if (secondsSinceLastMessage < _connectionTimeoutSeconds)
        {
            _reconnectTimer = 0f;
            return; // 아직 정상 범위 내
        }

        // 타임아웃 초과: 연결이 끊긴 것으로 간주. 일정 간격으로 재연결을 재시도한다.
        _reconnectTimer += Time.deltaTime;
        if (_reconnectTimer >= _reconnectIntervalSeconds)
        {
            _reconnectTimer = 0f;
            _hasReconnectedAtLeastOnce = true;
            Debug.Log("[Client] Server unresponsive, attempting to reconnect...");
            ConnectToServer();
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

    /// <summary>
    /// [-halfExtent, +halfExtent) 범위로 순환(wrap)하는 축 하나에서, a에서 b로 가는 "최단 경로"
    /// 기준으로 t만큼 보간한다. Mathf.LerpAngle이 0/360도 경계를 최단 경로로 보간하는 것과 정확히
    /// 같은 원리를 위치 좌표에 적용한 것이다. X축과 Y축은 half-extent 값이 서로 다르므로
    /// (화면이 16:9로 고정되어 X:7.1111, Y:4.0), 호출부에서 축마다 이 함수를 따로 호출해
    /// 각자의 half-extent를 넘긴다.
    ///
    /// 탱크가 화면 경계에서 반대편으로 wrap하면서, 원격 플레이어 보간에 쓰이는
    /// 두 스냅샷(s1, s2)이 정확히 그 wrap 순간의 앞/뒤를 걸칠 수 있다(예: Y축에서 s1.Y=3.9,
    /// s2.Y=-3.9 — 실제로는 탱크가 위쪽 벽을 살짝 넘어 아래쪽 벽에서 막 나타난, 아주 가까운
    /// 거리다). 이걸 그냥 Vector3.Lerp로 단순 보간하면 좌표상 거리가 먼 것으로 계산되어
    /// (3.9 -> -3.9는 7.8만큼의 이동), 화면을 대각선으로 가로질러 순간이동하는 것처럼 미끄러져
    /// 보인다. 최단 경로로 보간하면 짧은 방향(이 예시에서는 0.2만큼, 위쪽 벽 바깥쪽 방향)으로만
    /// 자연스럽게 이동한 뒤 wrap되어, 실제 탱크가 화면을 가로지르지 않고 벽 쪽에서
    /// 사라졌다 반대쪽에서 나타나는 것처럼 보인다.
    /// </summary>
    private static float LerpWrapped(float a, float b, float t, float halfExtent)
    {
        float range = halfExtent * 2f;
        if (range <= 0f) return b;

        float delta = WrapCoordinate(b - a, halfExtent); // 최단 방향의 차이(-half..+half 범위)로 정규화
        return WrapCoordinate(a + delta * t, halfExtent);
    }

    private void InterpolateRemotePlayers()
    {
        if (_snapshotBuffer.Count == 0)
        {
            ApplyRespawnJumps();
            return;
        }

        float renderTime = Time.time - _interpolationDelay;

        int index = 0;
        while (index < _snapshotBuffer.Count && _snapshotBuffer[index].LocalTimeStamp < renderTime)
        {
            index++;
        }

        if (index == 0)
        {
            ApplySnapshotPositions(_snapshotBuffer[0].Positions, _snapshotBuffer[0].Rotations);
        }
        else if (index >= _snapshotBuffer.Count)
        {
            var last = _snapshotBuffer[^1];
            ApplySnapshotPositions(last.Positions, last.Rotations);
        }
        else
        {
            Snapshot s1 = _snapshotBuffer[index - 1];
            Snapshot s2 = _snapshotBuffer[index];

            float t = (renderTime - s1.LocalTimeStamp) / (s2.LocalTimeStamp - s1.LocalTimeStamp);
            t = Mathf.Clamp01(t);

            foreach (var kvp in _playerViews)
            {
                byte id = kvp.Key;
                if (id == _myPlayerId) continue;
                // 이번 프레임에 막 리스폰한 원격 플레이어는 여기서 보간하지 않는다 — 아래
                // ApplyRespawnJumps()가 함수 마지막에 최신 위치로 강제 스냅하므로, 여기서
                // 보간해봤자 곧바로 덮어써진다. 애초에 s1(죽은 위치)과 s2(새 스폰 위치) 사이를
                // 보간하면 화면을 가로질러 미끄러지는 시각적 오류가 생기므로 아예 건너뛴다.
                if (_justRespawnedRemoteIds.Contains(id)) continue;

                if (s1.Positions.TryGetValue(id, out Vector2 p1) && s2.Positions.TryGetValue(id, out Vector2 p2))
                {
                    // 단순 Vector3.Lerp 대신 wrap 경계를 최단 경로로 넘어가는 보간을 쓴다.
                    // 그렇지 않으면 상대방이 벽에서 wrap한 프레임에 화면을 가로질러
                    // 순간이동하는 것처럼 미끄러져 보인다. 화면이 16:9로 고정되어 X/Y의
                    // half-extent가 다르므로, 각 축은 자신의 값(.x/.y)으로 보간해야 한다.
                    float ix = LerpWrapped(p1.x, p2.x, t, _serverWorldHalfExtent.x);
                    float iy = LerpWrapped(p1.y, p2.y, t, _serverWorldHalfExtent.y);
                    kvp.Value.transform.position = new Vector3(ix, iy, 0f);
                }

                if (s1.Rotations.TryGetValue(id, out float r1) && s2.Rotations.TryGetValue(id, out float r2))
                {
                    // LerpAngle은 0/360 경계를 최단 경로로 보간하므로 359도->1도 같은 케이스에서
                    // 반대로 크게 도는 것을 막아준다.
                    float interpolatedRot = Mathf.LerpAngle(r1, r2, t);
                    ApplyShipRotation(kvp.Value, interpolatedRot);
                }
            }
        }

        ApplyRespawnJumps();
    }

    /// <summary>
    /// _justRespawnedRemoteIds에 담긴 원격 플레이어들을 interpolation 없이 최신 서버 위치로
    /// 즉시 스냅(점프)시키고 리스폰 대기열을 비운다. InterpolateRemotePlayers()의 모든 분기(단일 스냅샷
    /// 적용, 두 스냅샷 사이 보간, 스냅샷 버퍼가 아직 비어있는 초기 상태) 뒤에 공통으로 호출되어,
    /// 어느 경로를 타든 리스폰한 플레이어는 항상 이 함수가 최종적으로 위치를 확정한다.
    /// 집합을 처리 직후 비우는 이유: 리스폰 점프는 정확히 한 프레임만 유효한 일회성 이벤트이고,
    /// 다음 프레임부터는 이미 새 스폰 위치를 기준으로 정상적인 스냅샷 보간이 재개되어야 한다
    /// (그러지 않으면 리스폰 이후에도 계속 보간이 아니라 매 프레임 스냅되어 원격 플레이어가
    /// 뚝뚝 끊겨 움직이는 것처럼 보인다).
    /// </summary>
    private void ApplyRespawnJumps()
    {
        if (_justRespawnedRemoteIds.Count == 0) return;

        foreach (byte id in _justRespawnedRemoteIds)
        {
            if (!_playerViews.TryGetValue(id, out GameObject shipObj) || shipObj == null) continue;

            if (_latestRemotePosition.TryGetValue(id, out Vector2 pos))
            {
                shipObj.transform.position = new Vector3(pos.x, pos.y, 0f);
            }
            if (_latestRemoteRotation.TryGetValue(id, out float rot))
            {
                ApplyShipRotation(shipObj, rot);
            }
        }

        _justRespawnedRemoteIds.Clear();
    }

    private void ApplySnapshotPositions(Dictionary<byte, Vector2> positions, Dictionary<byte, float> rotations)
    {
        foreach (var kvp in positions)
        {
            if (kvp.Key == _myPlayerId) continue;
            if (_playerViews.TryGetValue(kvp.Key, out GameObject shipObj))
            {
                shipObj.transform.position = kvp.Value;
                if (rotations.TryGetValue(kvp.Key, out float rot))
                {
                    ApplyShipRotation(shipObj, rot);
                }
            }
        }
    }

    private void SendPingCheck()
    {
        if (_udpClient == null || _myPlayerId == 0) return;

        _pingTimer += Time.deltaTime;
        if (_pingTimer >= PING_INTERVAL)
        {
            _pingTimer = 0f;

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write((byte)PacketType.Ping);
            bw.Write(_myPlayerId);
            bw.Write(DateTime.UtcNow.Ticks);

            SendPacket(ms.ToArray());
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

        // 네임플레이트는 탱크의 회전에 영향받지 않도록 부모-자식 관계를 두지 않고
        // Update에서 위치만 별도로 따라가게 한다 (회전에 딸려 기울어지는 것 방지).
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

        // HP 바(배경 빨강 + 전경 초록). 네임플레이트와 마찬가지로 탱크의 자식으로 두지
        // 않는다 — 자식으로 두면 탱크 회전과 함께 바가 함께 기울어지고, 게다가 부모의
        // localScale에 곱연산으로 얽혀 전경 폭 조절(체력 비율)이 예상과 다르게 왜곡된다.
        // 위치만 매 프레임 탱크를 따라가게 한다(UpdateNameplatePositions에서 함께 처리).
        Vector3 barBasePos = shipObj.transform.position + new Vector3(-HP_BAR_WIDTH * 0.5f, HP_BAR_Y_OFFSET, 0f);

        GameObject bgObj = new GameObject($"HPBarBG_{id}");
        var bgRenderer = bgObj.AddComponent<SpriteRenderer>();
        bgRenderer.sprite = _hpBarSprite;
        bgRenderer.color = Color.red;
        bgRenderer.sortingOrder = 6; // 탱크(0)/미사일(5)보다 위, 네임플레이트(10)보다 아래
        bgObj.transform.position = barBasePos;
        bgObj.transform.localScale = new Vector3(HP_BAR_WIDTH, HP_BAR_HEIGHT, 1f);

        GameObject fgObj = new GameObject($"HPBarFG_{id}");
        var fgRenderer = fgObj.AddComponent<SpriteRenderer>();
        fgRenderer.sprite = _hpBarSprite;
        fgRenderer.color = Color.green;
        fgRenderer.sortingOrder = 7; // 배경보다 한 단계 위에 그려야 겹쳐 보인다
        fgObj.transform.position = barBasePos;
        // 처음엔 항상 만피(가득 참) 상태로 시작한다 — 실제 체력은 다음 서버 프레임(ApplyServerState)에서
        // 곧바로 UpdateHealthBar를 통해 정확한 값으로 갱신되므로, 초기값은 시각적 기본값일 뿐이다.
        fgObj.transform.localScale = new Vector3(HP_BAR_WIDTH, HP_BAR_HEIGHT, 1f);

        _healthBarViews[id] = new HealthBarView
        {
            BackgroundObj = bgObj,
            ForegroundObj = fgObj,
            ForegroundRenderer = fgRenderer
        };

        return shipObj;
    }

    private static readonly Color DEAD_COLOR = new Color(0.4f, 0.4f, 0.4f, 1f); // 사망 상태 표시용 회색

    /// <summary>
    /// 사망 상태가 실제로 바뀐 경우에만 스프라이트 색을 갱신한다(매 틱 무조건 다시 칠하지
    /// 않도록 _deadPlayerIds로 이전 상태를 추적). 사망 -> 회색, 생존 -> 원래 색(본인 초록/
    /// 원격 빨강)으로 되돌린다. 네임플레이트는 그대로 두어 죽은 상태에서도 누구인지 알 수
    /// 있게 한다(리스폰 대기 중임을 회색으로만 표시).
    ///
    /// 반환값은 "이번 호출이 정확히 리스폰 전환(직전까지 죽어있었는데 이번엔 살아있음) 순간인지"를
    /// 나타낸다. 색상 갱신과 같은 wasDead/isDead 비교 로직을 그대로 재사용해, 호출부(Update())가
    /// 로컬 재조정과 원격 보간 양쪽에서 "이번 프레임은 위치가 순간이동했으니 interpolation/
    /// reconciliation을 건너뛰고 즉시 점프시켜야 한다"는 판단에 쓸 수 있게 한다.
    /// </summary>
    private bool UpdateDeathVisual(byte id, bool isDead)
    {
        bool wasDead = _deadPlayerIds.Contains(id);
        if (wasDead == isDead) return false;

        bool justRespawned = wasDead && !isDead;

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

        return justRespawned;
    }

    /// <summary>
    /// 서버가 보낸 현재 체력(byte)에 맞춰 HP 바 전경(초록)의 가로 폭을 조절한다.
    /// UpdateDeathVisual과 달리 "전환 시에만" 갱신하는 최적화를 하지 않는다 — 체력은
    /// 사망 여부(bool)와 달리 미사일에 맞을 때마다 세밀하게 바뀌는 값이라, 매 프레임 값이
    /// 같은지 비교하는 비용이 그냥 대입하는 비용과 별 차이가 없고, 비교 로직을 따로 두면
    /// 코드만 복잡해진다.
    /// </summary>
    private void UpdateHealthBar(byte id, byte currentHealth)
    {
        if (!_healthBarViews.TryGetValue(id, out HealthBarView bar)) return;
        if (bar.ForegroundObj == null) return;

        float ratio = Mathf.Clamp01((float)currentHealth / CLIENT_MAX_HEALTH);
        Vector3 scale = bar.ForegroundObj.transform.localScale;
        scale.x = HP_BAR_WIDTH * ratio;
        bar.ForegroundObj.transform.localScale = scale;
    }

    private void RemoveDisconnectedPlayers(HashSet<byte> activeIds)
    {
        List<byte> disconnectedIds = new List<byte>();
        foreach (var id in _playerViews.Keys)
        {
            if (!activeIds.Contains(id))
            {
                Destroy(_playerViews[id]);
                if (_nameplateViews.TryGetValue(id, out GameObject nameplate))
                {
                    Destroy(nameplate);
                    _nameplateViews.Remove(id);
                }
                if (_healthBarViews.TryGetValue(id, out HealthBarView bar))
                {
                    if (bar.BackgroundObj != null) Destroy(bar.BackgroundObj);
                    if (bar.ForegroundObj != null) Destroy(bar.ForegroundObj);
                    _healthBarViews.Remove(id);
                }
                _playerRenderers.Remove(id);
                _deadPlayerIds.Remove(id);
                _latestHealthById.Remove(id);
                // 리스폰 점프 관련 캐시도 함께 정리한다. 그러지 않으면 이 ID가 나중에
                // 다른 접속자에게 재배정됐을 때, 예전 값이 남아 있다가 엉뚱한 타이밍에
                // 소비되거나(_justRespawnedRemoteIds) 새 플레이어의 첫 위치와 무관한
                // 값(_latestRemotePosition/Rotation)이 잠깐 노출될 위험이 있다.
                _justRespawnedRemoteIds.Remove(id);
                _latestRemotePosition.Remove(id);
                _latestRemoteRotation.Remove(id);
                disconnectedIds.Add(id);
            }
        }
        foreach (var id in disconnectedIds) _playerViews.Remove(id);
    }

    /// <summary>재연결 시 기존에 그려져 있던 모든 플레이어 오브젝트/네임플레이트/HP 바를 정리한다.</summary>
    private void ClearAllPlayerViews()
    {
        foreach (var go in _playerViews.Values)
        {
            if (go != null) Destroy(go);
        }
        _playerViews.Clear();

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
        _latestHealthById.Clear();
        _justRespawnedRemoteIds.Clear();
        _latestRemotePosition.Clear();
        _latestRemoteRotation.Clear();
    }

    /// <summary>재연결 시 확정/예측 상태의 모든 미사일 오브젝트를 정리한다.</summary>
    private void ClearAllMissileViews()
    {
        foreach (var view in _missileViews.Values)
        {
            if (view.GameObj != null) Destroy(view.GameObj);
        }
        _missileViews.Clear();

        foreach (var view in _pendingLocalMissiles)
        {
            if (view.GameObj != null) Destroy(view.GameObj);
        }
        _pendingLocalMissiles.Clear();

        _fireCooldownTimer = 0f;
    }

    // OnGUI 레이아웃 상수.
    private const float STATS_PANEL_X = 10f;
    private const float STATS_PANEL_Y = 10f;
    private const float STATS_PANEL_WIDTH = 280f;
    private const float STATS_PANEL_HEIGHT = 250f;
    private const float PANEL_SAFETY_GAP = 10f; // 패널과 화면 가장자리 사이 최소 여백
    private const float PANEL_ROW_HEIGHT = 22f; // GUILayout.Label 한 줄의 대략적인 높이
    private const float PANEL_HEADER_HEIGHT = 35f; // 패널 제목 줄 + 여백

    /// <summary>
    /// 화면 위쪽에서 시작해 아래로 자라나는 패널(스코어보드 등)이 화면 세로 길이를 넘어
    /// 잘리지 않으면서 표시할 수 있는 최대 줄 수를 계산한다. 봇을 대량으로 띄운 부하
    /// 테스트에서(접속자 수가 100명을 넘는 등) 항목 수에 비례해 패널이 한없이 커지면,
    /// 패널 높이가 화면 높이(예: 720px)를 훌쩍 넘어 아래쪽 내용이 화면 밖으로 잘려
    /// 보이지 않게 된다. 이 함수는 그런 잘림이 생기지 않는 선에서 최대 몇 줄까지
    /// 안전한지를 화면 크기 기준으로 매 프레임 다시 계산한다(해상도가 바뀌어도 항상
    /// 정확하도록 하드코딩된 개수를 쓰지 않는다).
    /// </summary>
    private static int CalculateMaxSafeRows()
    {
        float panelTop = STATS_PANEL_Y; // 스코어보드도 Stats 패널과 같은 y(10)에서 시작한다.
        float availableForRows = Screen.height - panelTop - PANEL_SAFETY_GAP - PANEL_HEADER_HEIGHT;
        int maxRows = Mathf.FloorToInt(availableForRows / PANEL_ROW_HEIGHT);
        return Mathf.Max(1, maxRows); // 화면이 극단적으로 작아도 최소 1줄(본인)은 항상 보장한다.
    }

    private void OnGUI()
    {
        // 1. 왼쪽 위: 네트워크 통계 및 상태 정보
        GUILayout.BeginArea(new Rect(STATS_PANEL_X, STATS_PANEL_Y, STATS_PANEL_WIDTH, STATS_PANEL_HEIGHT), GUI.skin.box);
        GUILayout.Label("<b>CustomClient Sample</b>");

        string fpsColor = _displayedFps >= 55f ? "green" : (_displayedFps >= 30f ? "yellow" : "red");
        GUILayout.Label($"FPS: <color={fpsColor}>{_displayedFps:F0}</color>");

        if (_myPlayerId == 0)
        {
            string statusText = _hasReconnectedAtLeastOnce ? "Status: Reconnecting..." : "Status: Connecting...";
            GUILayout.Label(statusText);
        }
        else
        {
            GUILayout.Label($"My Player ID: {_myPlayerId}");
            GUILayout.Label($"Active Players: {_playerViews.Count}");

            string pingColor = _currentPingMs < 50f ? "green" : (_currentPingMs < 120f ? "yellow" : "red");
            GUILayout.Label($"Ping (RTT): <color={pingColor}>{_currentPingMs:F1} ms</color>");

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

        // 2. 오른쪽 위: 스코어보드 (킬 내림차순, 동점이면 데스 적은 순)
        //    본인 점수를 항상 맨 위에 고정 표시하고, 그 아래에 본인을 제외한 상위 순위자를
        //    화면에 안전하게 들어가는 만큼만 나열한다. 봇을 대량으로 띄운 부하 테스트처럼
        //    접속자가 매우 많을 때 전원을 다 표시하면 패널이 화면 높이를 넘어 아래쪽이
        //    잘리므로, 총 표시 줄 수를 CalculateMaxSafeRows()로 매 프레임 계산해 제한한다.
        //    좌표(X,Y) 표시는 각 탱크 위에 뜨는 HP 바가 대신하므로 더 이상 표시하지 않는다.
        if (_myPlayerId != 0 && _scoreboard.Count > 0)
        {
            int maxRows = CalculateMaxSafeRows();

            List<byte> sortedIds = new List<byte>(_scoreboard.Keys);
            sortedIds.Sort((a, b) =>
            {
                var scoreA = _scoreboard[a];
                var scoreB = _scoreboard[b];
                int killCompare = scoreB.Kills.CompareTo(scoreA.Kills); // 킬 내림차순
                if (killCompare != 0) return killCompare;
                return scoreA.Deaths.CompareTo(scoreB.Deaths); // 동점이면 데스 오름차순
            });

            // 본인이 정렬된 목록 어디에 있든 맨 위 1줄에 고정 표시하고, 나머지 줄은 본인을
            // 제외한 상위 순위자로 채운다 — 그래야 본인이 순위 밖으로 밀려나도 항상 자기
            // 점수를 볼 수 있고, 동시에 같은 사람이 목록에 두 번(맨 위 + 원래 순위 자리)
            // 나오는 중복도 생기지 않는다.
            bool hasSelf = _scoreboard.ContainsKey(_myPlayerId);
            List<byte> displayIds = new List<byte>();
            if (hasSelf) displayIds.Add(_myPlayerId);

            foreach (byte id in sortedIds)
            {
                if (displayIds.Count >= maxRows) break;
                if (id == _myPlayerId) continue; // 이미 맨 위에 넣었으므로 중복 제외
                displayIds.Add(id);
            }

            float width = 220f;
            float height = PANEL_HEADER_HEIGHT + (displayIds.Count * PANEL_ROW_HEIGHT);
            float xPos = Screen.width - width - 10f;

            GUILayout.BeginArea(new Rect(xPos, 10, width, height), GUI.skin.box);
            GUILayout.Label($"<b>Scoreboard (K/D) - {_scoreboard.Count} players</b>");

            foreach (byte id in displayIds)
            {
                var (kills, deaths) = _scoreboard[id];
                string label = $"Player {id}: {kills} / {deaths}";

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