using System.Collections.Generic;
using System.Threading;
using TMPro;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace CustomClient.Dots
{
    /// <summary>
    /// 순수 ECS 시뮬레이션(위치/회전/체력/스코어보드/네트워크 통계)을 읽어와 기존과 동일한
    /// TextMeshPro 네임플레이트와 OnGUI HUD/스코어보드를 그리는 하이브리드 브리지.
    ///
    /// 사용법: 씬에 빈 GameObject 하나를 만들고 이 컴포넌트를 붙인다 (SubScene 밖, 일반 씬).
    /// ECS 쪽은 순수 시뮬레이션 데이터만 갖고 있어서 별도로 UI를 처리하는 MonoBehaviour가 필요.
    ///
    /// 원본과 동일하게 유지한 상수:
    ///   SHIP_WORLD_DIAMETER = 0.35f, NAMEPLATE_Y_OFFSET = SHIP_WORLD_DIAMETER * 1.328f
    /// </summary>
    public class GameHudBridge : MonoBehaviour
    {
        private const float ShipWorldDiameter = 0.35f;
        private const float NameplateYOffset = ShipWorldDiameter * 1.328f;

        // HP 바를 탱크 바로 위(네임플레이트보다는 아래)에 띄우는 수직 오프셋.
        private const float HpBarYOffset = ShipWorldDiameter * 0.75f;
        private const float HpBarWidth = ShipWorldDiameter * 1.2f;
        private const float HpBarHeight = 0.06f;

        // [중요] 서버 CustomServer.MAX_HEALTH와 정확히 같은 값이어야 한다. 서버는
        // 체력의 "현재값"(byte Health)만 브로드캐스트하고 최대값 자체는 보내지 않으므로, HP
        // 바가 채움 비율(현재/최대)을 계산하려면 이 브리지가 최대값을 별도로 알고 있어야 한다.
        private const int ClientMaxHealth = 10;

        // OnGUI 레이아웃 상수 (스코어보드가 화면 세로 길이를 넘어 잘리지 않도록 최대 표시
        // 줄 수를 계산하는 데 쓰인다. MonoBehaviour 클라이언트의 동일 상수/계산과 대응된다).
        private const float StatsPanelY = 10f;
        private const float PanelSafetyGap = 10f;
        private const float PanelRowHeight = 22f;
        private const float PanelHeaderHeight = 35f;

        private readonly Dictionary<byte, GameObject> _nameplateViews = new();

        /// <summary>탱크 하나의 HP 바(배경 + 전경) 뷰. MonoBehaviour 클라이언트의 HealthBarView와 동일한 역할.</summary>
        private class HealthBarView
        {
            public GameObject BackgroundObj;
            public GameObject ForegroundObj;
        }
        private readonly Dictionary<byte, HealthBarView> _healthBarViews = new();
        private Sprite _hpBarSprite;

        private EntityManager _em;
        private World _world;

        // OnGUI에서 매 프레임 다시 계산하기보다, Update에서 한 번 모아 캐시해 둔다.
        private byte _myPlayerId;
        private bool _isConnected;
        private bool _hasReconnectedAtLeastOnce;
        private float _currentPingMs;
        private float _kbSentPerSec;
        private float _kbReceivedPerSec;
        private long _totalBytesSent;
        private long _totalBytesReceived;

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

            var clientState = _em.GetComponentData<ClientStateSingleton>(configEntity);
            _myPlayerId = clientState.MyPlayerId;
            _isConnected = clientState.IsConnected;
            _hasReconnectedAtLeastOnce = clientState.HasReconnectedAtLeastOnce;
            _currentPingMs = clientState.CurrentPingMs;
            _kbSentPerSec = clientState.KbSentPerSec;
            _kbReceivedPerSec = clientState.KbReceivedPerSec;

            if (_em.HasComponent<NetworkConnectionData>(configEntity))
            {
                var netData = _em.GetComponentObject<NetworkConnectionData>(configEntity);
                _totalBytesSent = Interlocked.Read(ref netData.TotalBytesSent);
                _totalBytesReceived = Interlocked.Read(ref netData.TotalBytesReceived);
            }

            RefreshPlayerRows();
            UpdateAttachedUi();
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
        /// 원본 UpdateNameplatePositions + CreatePlayerObject의 네임플레이트 생성 부분.
        /// ECS 쪽에서 PlayerJoinedElement/PlayerRemovedElement 이벤트가 오면 생성/삭제하고,
        /// 매 프레임 위치만 탱크의 RenderTransform2D를 따라가도록 갱신한다. HP 바 추가로
        /// "탱크를 따라다니는 붙어있는 UI 전반"을 다루게 되어 이름을 일반화했다
        /// (MonoBehaviour 클라이언트의 UpdateAttachedUiPositions와 대응).
        /// </summary>
        private void UpdateAttachedUi()
        {
            foreach (var row in _rows)
            {
                Vector3 shipPos = new Vector3(row.Position.x, row.Position.y, 0f);

                if (_nameplateViews.TryGetValue(row.Id, out GameObject nameplateObj) && nameplateObj != null)
                {
                    nameplateObj.transform.position = shipPos + new Vector3(0, NameplateYOffset, 0);
                }

                if (_healthBarViews.TryGetValue(row.Id, out HealthBarView bar))
                {
                    Vector3 barPos = shipPos + new Vector3(-HpBarWidth * 0.5f, HpBarYOffset, 0f);
                    if (bar.BackgroundObj != null) bar.BackgroundObj.transform.position = barPos;
                    if (bar.ForegroundObj != null)
                    {
                        bar.ForegroundObj.transform.position = barPos;

                        float ratio = Mathf.Clamp01((float)row.Health / ClientMaxHealth);
                        Vector3 scale = bar.ForegroundObj.transform.localScale;
                        scale.x = HpBarWidth * ratio;
                        bar.ForegroundObj.transform.localScale = scale;
                    }
                }
            }
        }

        /// <summary>
        /// ServerStateApplySystem이 UiEventSingletonTag 버퍼에 적어 넣은 이벤트를 소비한다.
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

        /// <summary>
        /// HP 바(배경/전경 공용)에 쓰는 단색 사각형 스프라이트. MonoBehaviour 클라이언트의
        /// CreateSolidBarSprite와 정확히 동일한 방식 — pivot을 (0f, 0.5f)로 잡아 localScale.x를
        /// 줄일 때 왼쪽 끝이 고정된 채 오른쪽만 줄어드는 자연스러운 막대 형태를 만든다.
        /// </summary>
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
            // 처음엔 항상 만피(가득 참) 상태로 시작한다 — 실제 체력은 다음 UpdateAttachedUi()
            // 호출에서 곧바로 정확한 값으로 갱신되므로, 초기값은 시각적 기본값일 뿐이다.
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
        // 원본 OnGUI를 그대로 옮긴 부분. 레이아웃/문구/색상 로직 전부 동일하게 유지.
        // ---------------------------------------------------------------
        /// <summary>
        /// 화면 위쪽에서 시작해 아래로 자라나는 패널(스코어보드 등)이 화면 세로 길이를 넘어
        /// 잘리지 않으면서 표시할 수 있는 최대 줄 수를 계산한다. MonoBehaviour 클라이언트의
        /// CalculateMaxSafeRows와 정확히 같은 계산 — 봇을 대량으로 띄운 부하 테스트에서
        /// 접속자 수에 비례해 패널이 한없이 커지는 것을 막는다.
        /// </summary>
        private static int CalculateMaxSafeRows()
        {
            float availableForRows = Screen.height - StatsPanelY - PanelSafetyGap - PanelHeaderHeight;
            int maxRows = Mathf.FloorToInt(availableForRows / PanelRowHeight);
            return Mathf.Max(1, maxRows);
        }

        private void OnGUI()
        {
            // 1. 왼쪽 위: 네트워크 통계 및 상태 정보
            GUILayout.BeginArea(new Rect(10, StatsPanelY, 280, 250), GUI.skin.box);
            GUILayout.Label("<b>Custom Client Sample (DOTS)</b>");

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
                GUILayout.Label($"Active Players: {_rows.Count}");

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

            // 2. 오른쪽 위: 스코어보드 (킬 내림차순, 동점이면 데스 오름차순)
            //    본인 점수를 항상 맨 위에 고정 표시하고, 그 아래에 본인을 제외한 상위 순위자를
            //    화면에 안전하게 들어가는 만큼만 나열한다(MonoBehaviour 클라이언트와 동일한
            //    설계). 좌표(X,Y) 표시는 각 탱크 위에 뜨는 HP 바가 대신하므로 더 이상 표시하지 않는다.
            if (_myPlayerId != 0 && _rows.Count > 0)
            {
                int maxRows = CalculateMaxSafeRows();

                var sorted = new List<PlayerHudRow>(_rows);
                sorted.Sort((a, b) =>
                {
                    int killCompare = b.Kills.CompareTo(a.Kills);
                    if (killCompare != 0) return killCompare;
                    return a.Deaths.CompareTo(b.Deaths);
                });

                // 본인이 정렬된 목록 어디에 있든 맨 위 1줄에 고정 표시하고, 나머지 줄은 본인을
                // 제외한 상위 순위자로 채운다 — 그래야 본인이 순위 밖으로 밀려나도 항상 자기
                // 점수를 볼 수 있고, 동시에 같은 사람이 목록에 두 번(맨 위 + 원래 순위 자리)
                // 나오는 중복도 생기지 않는다.
                PlayerHudRow? selfRow = null;
                foreach (var row in _rows)
                {
                    if (row.Id == _myPlayerId) { selfRow = row; break; }
                }

                List<PlayerHudRow> displayRows = new List<PlayerHudRow>();
                if (selfRow.HasValue) displayRows.Add(selfRow.Value);

                foreach (var row in sorted)
                {
                    if (displayRows.Count >= maxRows) break;
                    if (row.Id == _myPlayerId) continue; // 이미 맨 위에 넣었으므로 중복 제외
                    displayRows.Add(row);
                }

                float width = 220f;
                float height = PanelHeaderHeight + (displayRows.Count * PanelRowHeight);
                float xPos = Screen.width - width - 10f;

                GUILayout.BeginArea(new Rect(xPos, StatsPanelY, width, height), GUI.skin.box);
                GUILayout.Label($"<b>Scoreboard (K/D) - {_rows.Count} players</b>");

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
