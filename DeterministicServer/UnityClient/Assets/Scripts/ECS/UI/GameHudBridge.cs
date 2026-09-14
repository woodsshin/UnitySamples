using System.Collections.Generic;
using System.Threading;
using Game.Networking;
using TMPro;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace DeterministicSample.Dots
{
    /// <summary>
    /// 순수 ECS 시뮬레이션(위치/회전/체력/스코어보드/네트워크 통계)을 읽어와 TextMeshPro
    /// 네임플레이트와 OnGUI HUD/스코어보드를 그리는 하이브리드 브리지.
    ///
    /// 사용법: 씬에 빈 GameObject 하나를 만들고 이 컴포넌트를 붙인다 (SubScene 밖, 일반 씬).
    /// ECS 쪽은 순수 시뮬레이션 데이터만 갖고 있으며 TMP/OnGUI 관련 어떤 것도 알지 못한다 —
    /// 이 브리지가 유일하게 두 세계를 잇는 지점이다.
    ///
    /// 상수: SHIP_WORLD_DIAMETER = 0.35f, NAMEPLATE_Y_OFFSET = SHIP_WORLD_DIAMETER * 1.328f
    /// </summary>
    public class GameHudBridge : MonoBehaviour
    {
        private const float ShipWorldDiameter = 0.35f;
        private const float NameplateYOffset = ShipWorldDiameter * 1.328f;

        // HP 프로그레스바. 배경(빨강, 고정폭)과 전경(초록, Health/MAX_HEALTH 비율만큼
        // 스케일)을 각각 별도 SpriteRenderer로 두고 탱크 바로 위에 띄운다.
        private const int MAX_HEALTH = 10; // SimulationTickSystem.MAX_HEALTH와 반드시 같은 값이어야 한다.
        private const float HpBarYOffset = ShipWorldDiameter * 0.75f;
        private const float HpBarWidth = ShipWorldDiameter * 1.2f;
        private const float HpBarHeight = 0.06f;

        private class HealthBarView
        {
            public GameObject BackgroundObj;
            public GameObject ForegroundObj;
        }

        private readonly Dictionary<byte, GameObject> _nameplateViews = new();
        private readonly Dictionary<byte, HealthBarView> _healthBarViews = new();
        private Sprite _hpBarSprite;

        private EntityManager _em;
        private World _world;

        // OnGUI에서 매 프레임 다시 계산하기보다, Update에서 한 번 모아 캐시해 둔다.
        private byte _myPlayerId;
        private bool _isConnected;
        private float _currentPingMs;
        private float _kbSentPerSec;
        private float _kbReceivedPerSec;
        private long _totalBytesSent;
        private long _totalBytesReceived;

        // 로비/시작 버튼 표시용 캐시. _configEntity는 OnGUI의 버튼 클릭 핸들러
        // (SendStartGameRequest)가 Update()에서 이미 찾아둔 엔티티를 재사용하기 위해
        // 저장해둔다 — OnGUI는 프레임당 여러 번 호출될 수 있는 IMGUI 콜백이라 매번 엔티티
        // 쿼리를 새로 돌리는 대신 Update() 결과를 그대로 쓰는 편이 낫다.
        private bool _isGameStarted;
        private bool _isHost;
        private Entity _configEntity;

        // 참가 거부/연결 끊김 상태. NetworkConnectionSystem이 ClientStateSingleton에
        // 래치해두는 값을 그대로 읽어와, EntryScene으로 전환되기 전 짧은 동안 사용자에게
        // 상황을 알리는 메시지와 카운트다운을 보여주는 데만 쓴다. 실제 씬 전환은
        // NetworkConnectionSystem이 직접 수행하므로 이 브리지는 표시만 담당한다.
        private bool _isJoinRejected;
        private bool _isConnectionLost;
        private float _sceneTransitionCountdownSeconds;

        // FPS 표시용. 매 프레임 값이 심하게 튀는 것을 막기 위해 짧은 주기로만 갱신한다.
        private float _fpsAccumTime;
        private int _fpsAccumFrames;
        private float _displayedFps;
        private const float FpsRefreshInterval = 0.5f;

        private struct PlayerHudRow
        {
            public byte Id;
            public float2 Position;
            public byte Health;
            public bool IsDead;
            public int Kills;
            public int Deaths;
        }

        private readonly List<PlayerHudRow> _rows = new();

        private void Start()
        {
            _hpBarSprite = CreateSolidBarSprite(4);

            _world = World.DefaultGameObjectInjectionWorld;
            if (_world == null || !_world.IsCreated)
            {
                Debug.LogWarning("[GameHudBridge] Default ECS World를 찾을 수 없습니다.");
                enabled = false;
                return;
            }
            _em = _world.EntityManager;
        }

        private void Update()
        {
            UpdateFpsCounter();

            if (_world == null || !_world.IsCreated) return;

            if (!TryGetSingletonEntity<SimulationConfig>(out Entity configEntity)) return;

            // SimulationConfig는 SubScene 베이킹 시점에 붙지만, ClientStateSingleton은
            // NetworkConnectionSystem.OnStartRunning()이 런타임에 나중에 추가한다. 그 System은
            // InitializationSystemGroup에 있고 SubScene 로딩 System들과 같은 그룹 안에서 실행
            // 순서가 명시적으로 고정되어 있지 않으므로, SimulationConfig 엔티티가 이번
            // 프레임에 막 생겨난 경우 OnStartRunning이 다음 프레임까지 미뤄질 수 있다.
            // SystemBase 계열은 RequireForUpdate<ClientStateSingleton>()으로 이 틈을 자동으로
            // 건너뛰지만, 이 MonoBehaviour는 그런 게이팅이 없으므로 HasComponent로 직접
            // 방어한다 — 없으면 이번 프레임은 HUD 갱신을 건너뛰고 다음 프레임에 다시
            // 시도한다.
            if (!_em.HasComponent<ClientStateSingleton>(configEntity)) return;

            var clientState = _em.GetComponentData<ClientStateSingleton>(configEntity);
            _configEntity = configEntity;
            _myPlayerId = clientState.MyPlayerId;
            _isConnected = clientState.IsConnected;
            _currentPingMs = clientState.CurrentPingMs;
            _kbSentPerSec = clientState.KbSentPerSec;
            _kbReceivedPerSec = clientState.KbReceivedPerSec;
            _isGameStarted = clientState.IsGameStarted;
            _isJoinRejected = clientState.IsJoinRejected;
            _isConnectionLost = clientState.IsConnectionLost;
            _sceneTransitionCountdownSeconds = clientState.SceneTransitionCountdownSeconds;
            _isHost = ComputeIsHost();

            if (_em.HasComponent<NetworkConnectionData>(configEntity))
            {
                var netData = _em.GetComponentObject<NetworkConnectionData>(configEntity);
                _totalBytesSent = Interlocked.Read(ref netData.TotalBytesSent);
                _totalBytesReceived = Interlocked.Read(ref netData.TotalBytesReceived);
            }

            RefreshPlayerRows();
            UpdateNameplates();
            UpdateHealthBars();
            ConsumeUiEvents();
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
        /// 현재 방장을 이 클라이언트 스스로 계산한다 — 서버는 방장이 누구인지 별도로
        /// 방송하지 않는다(NetworkComponents.cs의 JoinSequenceState 참고). 현재 존재하는
        /// 모든 플레이어 엔티티 중 JoinSequenceState.Value가 가장 작은 사람이 방장이다.
        /// 접속 완료 전(_myPlayerId==0)이거나 플레이어가 하나도 없으면 false.
        /// </summary>
        private bool ComputeIsHost()
        {
            if (_myPlayerId == 0) return false;

            using var query = _em.CreateEntityQuery(
                ComponentType.ReadOnly<PlayerId>(),
                ComponentType.ReadOnly<JoinSequenceState>());
            using var entities = query.ToEntityArray(Allocator.Temp);
            if (entities.Length == 0) return false;

            byte hostId = 0;
            long minSequence = long.MaxValue;
            foreach (var entity in entities)
            {
                long seq = _em.GetComponentData<JoinSequenceState>(entity).Value;
                if (seq < minSequence)
                {
                    minSequence = seq;
                    hostId = _em.GetComponentData<PlayerId>(entity).Value;
                }
            }
            return hostId == _myPlayerId;
        }

        /// <summary>
        /// "Start Game" 버튼 클릭 시 호출된다. Update()가 이미 찾아둔 _configEntity를 그대로
        /// 재사용한다(OnGUI 콜백에서 매번 새로 쿼리하지 않기 위함). PingSystem/
        /// InputCaptureSystem이 패킷을 보내는 것과 동일한 경로(NetworkConnectionSystem.SendPacket)를
        /// 따른다 — 소켓 전송 경로를 단일하게 유지하기 위해서다.
        /// </summary>
        private void SendStartGameRequest()
        {
            if (_myPlayerId == 0) return;
            if (_configEntity == Entity.Null || !_em.Exists(_configEntity)) return;
            if (!_em.HasComponent<NetworkConnectionData>(_configEntity)) return;

            var netData = _em.GetComponentObject<NetworkConnectionData>(_configEntity);
            var netSystem = _world.GetExistingSystemManaged<NetworkConnectionSystem>();
            netSystem.SendPacket(netData, Protocol.BuildStartGameRequest(_myPlayerId));
        }

        private void RefreshPlayerRows()
        {
            _rows.Clear();

            using var query = _em.CreateEntityQuery(
                ComponentType.ReadOnly<PlayerId>(),
                ComponentType.ReadOnly<RenderTransform2D>(),
                ComponentType.ReadOnly<HealthState>(),
                ComponentType.ReadOnly<ScoreboardEntry>());

            using var entities = query.ToEntityArray(Allocator.Temp);
            foreach (var entity in entities)
            {
                var id = _em.GetComponentData<PlayerId>(entity).Value;
                var rt = _em.GetComponentData<RenderTransform2D>(entity);
                var health = _em.GetComponentData<HealthState>(entity);
                var score = _em.GetComponentData<ScoreboardEntry>(entity);

                _rows.Add(new PlayerHudRow
                {
                    Id = id,
                    Position = rt.Position,
                    Health = health.Health,
                    IsDead = health.IsDead,
                    Kills = score.Kills,
                    Deaths = score.Deaths
                });
            }
        }

        /// <summary>
        /// 네임플레이트 위치를 갱신한다. 생성/삭제는 ConsumeUiEvents가
        /// PlayerJoinedElement/PlayerRemovedElement 이벤트를 소비하며 처리하고, 이 메서드는
        /// 매 프레임 위치만 우주선의 RenderTransform2D를 따라가도록 갱신한다.
        /// </summary>
        private void UpdateNameplates()
        {
            foreach (var row in _rows)
            {
                if (_nameplateViews.TryGetValue(row.Id, out GameObject nameplateObj) && nameplateObj != null)
                {
                    Vector3 shipPos = new Vector3(row.Position.x, row.Position.y, 0f);
                    nameplateObj.transform.position = shipPos + new Vector3(0, NameplateYOffset, 0);
                }
            }
        }

        /// <summary>
        /// 탱크 위에 떠 있는 HP 프로그레스바(배경 빨강 고정폭 + 전경 초록 Health/MAX_HEALTH
        /// 비율 스케일)의 위치와 채움 정도를 매 프레임 갱신한다. 생성/삭제는 ConsumeUiEvents가
        /// PlayerJoinedElement/PlayerRemovedElement를 소비하며 처리한다 — 이 메서드는 이미
        /// 존재하는 뷰의 위치/폭만 갱신한다.
        /// </summary>
        private void UpdateHealthBars()
        {
            foreach (var row in _rows)
            {
                if (!_healthBarViews.TryGetValue(row.Id, out HealthBarView bar)) continue;
                if (bar.BackgroundObj == null || bar.ForegroundObj == null) continue;

                Vector3 shipPos = new Vector3(row.Position.x, row.Position.y, 0f);
                Vector3 barPos = shipPos + new Vector3(-HpBarWidth * 0.5f, HpBarYOffset, 0f);
                bar.BackgroundObj.transform.position = barPos;
                bar.ForegroundObj.transform.position = barPos;

                float ratio = Mathf.Clamp01((float)row.Health / MAX_HEALTH);
                Vector3 scale = bar.ForegroundObj.transform.localScale;
                scale.x = HpBarWidth * ratio;
                bar.ForegroundObj.transform.localScale = scale;
            }
        }

        /// <summary>단색 사각형 스프라이트 하나를 만든다. HP 바의 배경/전경 양쪽에 재사용한다.</summary>
        private Sprite CreateSolidBarSprite(int res)
        {
            Texture2D tex = new Texture2D(res, res);
            Color[] colors = new Color[res * res];
            for (int i = 0; i < colors.Length; i++) colors[i] = Color.white;

            tex.SetPixels(colors);
            tex.Apply();

            return Sprite.Create(tex, new Rect(0, 0, res, res), new Vector2(0f, 0.5f), res);
        }

        private void CreateHealthBar(byte id)
        {
            if (_healthBarViews.ContainsKey(id)) return;

            GameObject bgObj = new GameObject($"HPBarBG_{id}");
            var bgRenderer = bgObj.AddComponent<SpriteRenderer>();
            bgRenderer.sprite = _hpBarSprite;
            bgRenderer.color = Color.red;
            bgRenderer.sortingOrder = 6;
            bgObj.transform.localScale = new Vector3(HpBarWidth, HpBarHeight, 1f);

            GameObject fgObj = new GameObject($"HPBarFG_{id}");
            var fgRenderer = fgObj.AddComponent<SpriteRenderer>();
            fgRenderer.sprite = _hpBarSprite;
            fgRenderer.color = Color.green;
            fgRenderer.sortingOrder = 7;
            fgObj.transform.localScale = new Vector3(HpBarWidth, HpBarHeight, 1f);

            _healthBarViews[id] = new HealthBarView { BackgroundObj = bgObj, ForegroundObj = fgObj };
        }

        private void RemoveHealthBar(byte id)
        {
            if (_healthBarViews.TryGetValue(id, out HealthBarView bar))
            {
                if (bar.BackgroundObj != null) Destroy(bar.BackgroundObj);
                if (bar.ForegroundObj != null) Destroy(bar.ForegroundObj);
                _healthBarViews.Remove(id);
            }
        }

        /// <summary>
        /// SimulationTickSystem이 UiEventSingletonTag 버퍼에 적어 넣은 이벤트를 소비한다.
        /// 소비 후에는 반드시 버퍼를 비워야 다음 프레임에 중복 처리되지 않는다.
        /// </summary>
        private void ConsumeUiEvents()
        {
            if (!TryGetSingletonEntity<UiEventSingletonTag>(out Entity uiEntity)) return;

            var joined = _em.GetBuffer<PlayerJoinedElement>(uiEntity);
            for (int i = 0; i < joined.Length; i++)
            {
                CreateNameplate(joined[i].PlayerId, joined[i].IsLocal);
                CreateHealthBar(joined[i].PlayerId);
            }
            joined.Clear();

            var removed = _em.GetBuffer<PlayerRemovedElement>(uiEntity);
            for (int i = 0; i < removed.Length; i++)
            {
                RemoveNameplate(removed[i].PlayerId);
                RemoveHealthBar(removed[i].PlayerId);
            }
            removed.Clear();

            // 사망 색상 전환은 순수 렌더링(PlayerVisualColorSystem)이 이미 처리하므로
            // 브리지는 이벤트를 소비만 하고 별도 시각 처리는 하지 않는다.
            var deathEvents = _em.GetBuffer<DeathVisualChangedElement>(uiEntity);
            deathEvents.Clear();
        }

        private void CreateNameplate(byte id, bool isLocal)
        {
            if (_nameplateViews.ContainsKey(id)) return;

            GameObject nameplateObj = new GameObject($"NameplateTMP_{id}");

            TextMeshPro tmp = nameplateObj.AddComponent<TextMeshPro>();
            tmp.text = isLocal ? $"Player {id} (You)" : $"Player {id}";
            tmp.fontSize = 2.5f;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = isLocal ? Color.yellow : Color.white;
            tmp.sortingOrder = 10;
            tmp.rectTransform.sizeDelta = new Vector2(5, 1.2f);

            _nameplateViews[id] = nameplateObj;
        }

        private void RemoveNameplate(byte id)
        {
            if (_nameplateViews.TryGetValue(id, out GameObject nameplateObj))
            {
                if (nameplateObj != null) Destroy(nameplateObj);
                _nameplateViews.Remove(id);
            }
        }

        private bool TryGetSingletonEntity<T>(out Entity entity) where T : unmanaged, IComponentData
        {
            using var query = _em.CreateEntityQuery(ComponentType.ReadOnly<T>());
            if (query.CalculateEntityCount() == 0)
            {
                entity = Entity.Null;
                return false;
            }
            entity = query.GetSingletonEntity();
            return true;
        }

        // ---------------------------------------------------------------
        // HUD 렌더링. 레이아웃/문구/색상 로직.
        // ---------------------------------------------------------------
        private void OnGUI()
        {
            // 1. 왼쪽 위: 네트워크 통계 및 상태 정보
            GUILayout.BeginArea(new Rect(10, StatsPanelY, 280, StatsPanelHeight), GUI.skin.box);
            GUILayout.Label("<b>Deterministic Client Sample (DOTS)</b>");

            string fpsColor = _displayedFps >= 55f ? "green" : (_displayedFps >= 30f ? "yellow" : "red");
            GUILayout.Label($"FPS: <color={fpsColor}>{_displayedFps:F0}</color>");

            // 참가 거부/연결 끊김은 다른 어떤 상태보다도 우선해서 보여준다 — 이 두 경우는
            // 곧 EntryScene으로 전환되므로, 아래의 정상 접속 UI(호스트 버튼, 트래픽 등)를
            // 계속 그리는 것은 의미가 없고 혼란만 준다.
            if (_isJoinRejected)
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
                GUILayout.Label($"Active Players: {_rows.Count}");

                string pingColor = _currentPingMs < 50f ? "green" : (_currentPingMs < 120f ? "yellow" : "red");
                GUILayout.Label($"Ping (RTT): <color={pingColor}>{_currentPingMs:F1} ms</color>");

                if (!_isGameStarted)
                {
                    GUILayout.Space(5);
                    if (_isHost)
                    {
                        if (GUILayout.Button("Start Game"))
                        {
                            SendStartGameRequest();
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

            // 2. 오른쪽 위: 접속 중인 플레이어 ID 및 실시간 좌표
            if (_myPlayerId != 0 && _rows.Count > 0)
            {
                float width = 250f;
                float height = 35f + (_rows.Count * 22f);
                float xPos = Screen.width - width - 10f;

                GUILayout.BeginArea(new Rect(xPos, 10, width, height), GUI.skin.box);
                GUILayout.Label($"<b>Connected Users ({_rows.Count})</b>");

                foreach (var row in _rows)
                {
                    // 소수점 넷째 자리까지 반올림 없이 표시한다. FormatTruncated4 참고.
                    string posStr = $"({FormatTruncated4(row.Position.x)}, {FormatTruncated4(row.Position.y)})";
                    string healthStr = row.IsDead ? " <color=grey>[DEAD]</color>" : $" HP:{row.Health}";

                    if (row.Id == _myPlayerId)
                    {
                        GUILayout.Label($"<color=lime>• Player {row.Id} (You): {posStr}{healthStr}</color>");
                    }
                    else
                    {
                        GUILayout.Label($"• Player {row.Id}: {posStr}{healthStr}");
                    }
                }

                GUILayout.EndArea();
            }

            // 3. 왼쪽 아래: 스코어보드 (킬 내림차순, 동점이면 데스 오름차순)
            // 유저가 늘어나도 이 패널의 top(yPos)이 Stats 패널(1번, 같은 x열, 위쪽)과
            // 겹치지 않도록 CalculateMaxSafeScoreboardRows()로 계산한 최대 행 수만큼만
            // 표시한다. 본인 점수는 화면 밖으로 밀려나 안 보이는 일이 없도록 항상
            // 포함시키고, 나머지 자리는 킬 상위권부터 채운다.
            if (_myPlayerId != 0 && _rows.Count > 0)
            {
                int maxRows = CalculateMaxSafeScoreboardRows();

                var sorted = new List<PlayerHudRow>(_rows);
                sorted.Sort((a, b) =>
                {
                    int killCompare = b.Kills.CompareTo(a.Kills);
                    if (killCompare != 0) return killCompare;
                    return a.Deaths.CompareTo(b.Deaths);
                });

                bool hasSelf = _rows.Exists(r => r.Id == _myPlayerId);
                var displayRows = new List<PlayerHudRow>();
                if (hasSelf)
                {
                    displayRows.Add(_rows.Find(r => r.Id == _myPlayerId));
                }
                foreach (var row in sorted)
                {
                    if (displayRows.Count >= maxRows) break;
                    if (row.Id == _myPlayerId) continue;
                    displayRows.Add(row);
                }

                float width = ScoreboardPanelWidth;
                float height = ScoreboardHeaderHeight + (displayRows.Count * ScoreboardRowHeight);
                float yPos = Screen.height - height - 10f;

                GUILayout.BeginArea(new Rect(10, yPos, width, height), GUI.skin.box);
                GUILayout.Label($"<b>Scoreboard (K / D) - {_rows.Count} players</b>");

                foreach (var row in displayRows)
                {
                    string label = $"Player {row.Id}: {row.Kills} / {row.Deaths}";
                    if (row.Id == _myPlayerId)
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

        /// <summary>
        /// 소수점 넷째 자리까지 반올림 없이(자리 아래는 잘라서) 표시한다. C#의 표준 "F4"
        /// 포맷은 다섯째 자리에서 반올림하므로, 10000을 곱해 Math.Truncate(0을 향해 자름)로
        /// 정수부만 남긴 뒤 다시 10000으로 나누고 그 결과를 F4로 포맷한다. 값 자체가 이미
        /// 4자리 배수로 잘려 있으므로 F4가 다시 반올림을 해도 표시되는 자리에는 영향이 없다.
        /// </summary>
        private static string FormatTruncated4(float value)
        {
            double truncated = System.Math.Truncate((double)value * 10000.0) / 10000.0;
            return truncated.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
        }

        // Stats 패널(왼쪽 위)이 차지하는 영역. 스코어보드(왼쪽 아래, 같은 x열)가 아래에서 위로
        // 자라날 때 이 영역과 겹치면 안 된다.
        private const float StatsPanelY = 10f;
        private const float StatsPanelHeight = 275f;
        private const float ScoreboardPanelWidth = 220f;
        private const float ScoreboardHeaderHeight = 35f; // 패널 제목 줄 + 여백
        private const float ScoreboardRowHeight = 22f;    // GUILayout.Label 한 줄의 대략적인 높이
        private const float PanelGap = 10f; // Stats 패널 하단과 스코어보드 패널 상단 사이 최소 여백

        /// <summary>
        /// 유저가 늘어나도 스코어보드(왼쪽 아래, 아래에서 위로 자람)가 Stats 패널(왼쪽 위,
        /// y=10~285)과 겹치지 않도록, 화면 안에 안전하게 들어가는 최대 행 수를 계산한다.
        /// 스코어보드의 top(yPos = Screen.height - height - 10)이 (StatsPanelY +
        /// StatsPanelHeight + PanelGap)보다 위로 올라가면 안 된다는 조건에서 역산한다.
        /// </summary>
        private static int CalculateMaxSafeScoreboardRows()
        {
            float statsBottom = StatsPanelY + StatsPanelHeight + PanelGap;
            float maxHeight = Screen.height - statsBottom - PanelGap;
            float availableForRows = maxHeight - ScoreboardHeaderHeight;
            int maxRows = Mathf.FloorToInt(availableForRows / ScoreboardRowHeight);
            return Mathf.Max(1, maxRows);
        }

        private void OnDestroy()
        {
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
        }
    }
}
