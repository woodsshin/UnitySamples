// Copyright 2017-present WoodsShin, Inc. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace WoodsShin.SimpleUPNP.Editor
{
    /// <summary>
    /// Window &gt; Simple UPNP &gt; Gateway Tester. Finds your router and opens or closes ports from the Editor,
    /// without entering Play Mode. Handy to check whether UPnP works on your network before you ship a game.
    /// </summary>
    public sealed class UpnpGatewayWindow : EditorWindow
    {
        private const int MaxLogLines = 300;

        private static readonly UpnpGateway[] NoGateways = new UpnpGateway[0];
        private static readonly PortMappingInfo[] NoMappings = new PortMappingInfo[0];
        private static readonly string[] NoLines = new string[0];

        [SerializeField] private UpnpProtocol _protocol = UpnpProtocol.Udp;
        [SerializeField] private int _externalPort = 7777;
        [SerializeField] private int _internalPort = 7777;
        [SerializeField] private string _description = "Simple UPNP Test";
        [SerializeField] private int _leaseSeconds;
        [SerializeField] private Vector2 _scroll;
        [SerializeField] private Vector2 _logScroll;

        private readonly List<string> _log = new List<string>();
        private UpnpClient _client;
        private CancellationTokenSource _cancel;
        private bool _busy;
        private bool _closed;

        // The data that decides which controls are drawn. It is copied during the Layout event, so that the Layout
        // and the Repaint event of one frame see the same controls even if a request finishes in between.
        private UpnpGateway[] _viewGateways = NoGateways;
        private UpnpGateway _viewSelected;
        private PortMappingInfo[] _viewMappings = NoMappings;
        private string[] _viewLog = NoLines;
        private bool _viewBusy;
        private bool _viewNotPublic;

        [MenuItem("Window/Simple UPNP/Gateway Tester")]
        public static void Open()
        {
            UpnpGatewayWindow window = GetWindow<UpnpGatewayWindow>("UPNP Tester");
            window.minSize = new Vector2(440f, 420f);
            window.Show();
        }

        private void OnEnable()
        {
            _closed = false;
        }

        private void OnDisable()
        {
            _closed = true;

            CancellationTokenSource cancel = _cancel;
            _cancel = null;
            if (cancel != null)
            {
                cancel.Cancel();
            }

            if (_client != null)
            {
                _client.Dispose();
                _client = null;
            }
        }

        // ------------------------------------------------------------------------------------------------------
        // Operations
        // ------------------------------------------------------------------------------------------------------

        private UpnpClient GetClient()
        {
            if (_client == null)
            {
                _client = new UpnpClient(new UpnpClientOptions { Logger = OnClientLog });
            }

            return _client;
        }

        private void OnClientLog(UpnpLogLevel level, string message)
        {
            // Raised on any thread. Verbose lines are skipped, they would flood the window.
            if (level >= UpnpLogLevel.Warning)
            {
                Debug.LogWarning("[Simple UPNP] " + message);
            }
        }

        private async void Run(string title, Func<UpnpClient, CancellationToken, Task<UpnpResult>> action)
        {
            if (_busy)
            {
                return;
            }

            _busy = true;
            CancellationTokenSource cancel = new CancellationTokenSource();
            _cancel = cancel;
            Append("> " + title);
            Repaint();

            try
            {
                UpnpResult result = await action(GetClient(), cancel.Token);
                Append(result.Succeeded ? "  OK" + (result.Message.Length > 0 ? " - " + result.Message : string.Empty) : "  FAILED: " + result);
            }
            catch (Exception ex)
            {
                Append("  ERROR: " + ex.Message);
                Debug.LogException(ex);
            }
            finally
            {
                if (ReferenceEquals(_cancel, cancel))
                {
                    _cancel = null;
                }

                _busy = false;
                if (!_closed)
                {
                    Repaint();
                }
            }
        }

        private void Append(string line)
        {
            if (_closed)
            {
                return;
            }

            _log.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + line);
            if (_log.Count > MaxLogLines)
            {
                _log.RemoveRange(0, _log.Count - MaxLogLines);
            }

            _logScroll.y = float.MaxValue;
        }

        private PortMappingInfo BuildMapping()
        {
            return new PortMappingInfo(_protocol, _externalPort, _internalPort, _description, _leaseSeconds);
        }

        private void Discover()
        {
            Run("Search for gateways", async (client, token) =>
            {
                UpnpResult<IReadOnlyList<UpnpGateway>> found = await client.DiscoverAsync(true, token);
                if (found.Succeeded)
                {
                    Append("  " + found.Value.Count + " gateway(s) found.");
                }

                return found.Result;
            });
        }

        private void Refresh()
        {
            Run("Refresh selected gateway", (client, token) => client.RefreshAsync(true, token));
        }

        private void ListMappings()
        {
            Run("Read port mappings", async (client, token) =>
            {
                UpnpResult<IReadOnlyList<PortMappingInfo>> list = await client.GetPortMappingsAsync(token);
                if (list.Succeeded)
                {
                    Append("  " + list.Value.Count + " port mapping(s).");
                }

                return list.Result;
            });
        }

        private void AddMapping(bool allGateways)
        {
            PortMappingInfo mapping = BuildMapping();
            Run("Open " + mapping + (allGateways ? " on all gateways" : string.Empty), async (client, token) =>
            {
                UpnpResult result = allGateways
                    ? await client.PerformAllGatewaysAsync(UpnpAction.AddPort, mapping, token)
                    : await client.AddPortMappingAsync(mapping, token);

                if (result.Succeeded)
                {
                    await client.RefreshAsync(true, token);
                }

                return result;
            });
        }

        private void DeleteMapping(bool allGateways)
        {
            PortMappingInfo mapping = BuildMapping();
            Run("Close " + mapping.Protocol.ToWireString() + " port " + mapping.ExternalPort + (allGateways ? " on all gateways" : string.Empty), async (client, token) =>
            {
                UpnpResult result = allGateways
                    ? await client.PerformAllGatewaysAsync(UpnpAction.DeletePort, mapping, token)
                    : await client.DeletePortMappingAsync(mapping.Protocol, mapping.ExternalPort, token);

                if (result.Succeeded)
                {
                    await client.RefreshAsync(true, token);
                }

                return result;
            });
        }

        // ------------------------------------------------------------------------------------------------------
        // GUI
        // ------------------------------------------------------------------------------------------------------

        private void TakeSnapshot()
        {
            _viewBusy = _busy;
            _viewLog = _log.ToArray();

            if (_client == null)
            {
                _viewGateways = NoGateways;
                _viewSelected = null;
                _viewMappings = NoMappings;
                _viewNotPublic = false;
                return;
            }

            IReadOnlyList<UpnpGateway> gateways = _client.Gateways;
            UpnpGateway[] copy = new UpnpGateway[gateways.Count];
            for (int i = 0; i < copy.Length; i++)
            {
                copy[i] = gateways[i];
            }

            _viewGateways = copy;
            _viewSelected = _client.SelectedGateway;
            _viewNotPublic = _viewSelected != null && _viewSelected.HasPublicExternalAddress == false;

            if (_viewSelected == null)
            {
                _viewMappings = NoMappings;
                return;
            }

            IReadOnlyList<PortMappingInfo> mappings = _viewSelected.PortMappings;
            PortMappingInfo[] mappingCopy = new PortMappingInfo[mappings.Count];
            for (int i = 0; i < mappingCopy.Length; i++)
            {
                mappingCopy[i] = mappings[i];
            }

            _viewMappings = mappingCopy;
        }

        private void OnGUI()
        {
            if (Event.current.type == EventType.Layout)
            {
                TakeSnapshot();
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            DrawToolbar();
            DrawGateways();
            DrawGatewayDetails();
            DrawMappingForm();

            EditorGUILayout.EndScrollView();

            DrawLog();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginDisabledGroup(_viewBusy);
                if (GUILayout.Button("Search Gateways", GUILayout.Height(24f)))
                {
                    Discover();
                }

                EditorGUI.BeginDisabledGroup(_viewSelected == null);
                if (GUILayout.Button("Refresh", GUILayout.Height(24f)))
                {
                    Refresh();
                }

                if (GUILayout.Button("Read Port Mappings", GUILayout.Height(24f)))
                {
                    ListMappings();
                }

                EditorGUI.EndDisabledGroup();
                EditorGUI.EndDisabledGroup();

                EditorGUI.BeginDisabledGroup(!_viewBusy);
                if (GUILayout.Button("Cancel", GUILayout.Width(60f), GUILayout.Height(24f)))
                {
                    CancellationTokenSource cancel = _cancel;
                    if (cancel != null)
                    {
                        cancel.Cancel();
                    }
                }

                EditorGUI.EndDisabledGroup();
            }
        }

        private void DrawGateways()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Gateways", EditorStyles.boldLabel);

            if (_viewGateways.Length == 0)
            {
                EditorGUILayout.HelpBox("No gateway yet. Click \"Search Gateways\". Your computer and your router must be on the same network, and UPnP must be switched on in the router.", MessageType.Info);
                return;
            }

            for (int i = 0; i < _viewGateways.Length; i++)
            {
                UpnpGateway gateway = _viewGateways[i];
                bool selected = ReferenceEquals(gateway, _viewSelected);
                bool toggled = GUILayout.Toggle(selected, gateway.ToString(), EditorStyles.miniButton);
                if (toggled && !selected && _client != null)
                {
                    _client.SelectGateway(gateway);
                }
            }
        }

        private void DrawGatewayDetails()
        {
            UpnpGateway gateway = _viewSelected;
            if (gateway == null)
            {
                return;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Selected Gateway", EditorStyles.boldLabel);

            ManufacturerInfo maker = gateway.Manufacturer;
            DeviceStatusInfo status = gateway.Status;
            CommonLinkProperties link = gateway.LinkProperties;

            Row("Name", gateway.DisplayName);
            Row("Manufacturer", maker.Manufacturer);
            Row("Model", maker.ModelName + " " + maker.ModelNumber);
            Row("Local address", gateway.LocalIPAddress);
            Row("Public address", gateway.ExternalIPAddress + (_viewNotPublic ? "  (not a public address)" : string.Empty));
            Row("Connection", status.ConnectionStatus + " (" + status.ConnectionType + ")");
            Row("Uptime", FormatUptime(status.Uptime));
            Row("Link", link.WanAccessType + " / " + link.PhysicalLinkStatus);
            Row("Max bit rate up / down", link.Layer1UpstreamMaxBitRate + " / " + link.Layer1DownstreamMaxBitRate);

            if (_viewNotPublic)
            {
                EditorGUILayout.HelpBox("The public address of this router is a private or shared address. The router is probably behind another router or behind the network of your provider (double NAT / carrier-grade NAT). The ports that you open here cannot be reached from the internet.", MessageType.Warning);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Port Mappings on the Router (" + _viewMappings.Length + ")", EditorStyles.boldLabel);

            for (int i = 0; i < _viewMappings.Length; i++)
            {
                PortMappingInfo mapping = _viewMappings[i];
                using (new EditorGUILayout.HorizontalScope())
                {
                    string text = mapping.Protocol.ToWireString() + " " + mapping.ExternalPort + " -> " + mapping.InternalClient + ":" + mapping.InternalPort +
                                  "   " + mapping.Description + (mapping.LeaseDuration > 0 ? "   (" + mapping.LeaseDuration + " s)" : string.Empty) +
                                  (mapping.Enabled ? string.Empty : "   [disabled]");
                    EditorGUILayout.LabelField(text);

                    if (GUILayout.Button("Use", EditorStyles.miniButton, GUILayout.Width(36f)))
                    {
                        _protocol = mapping.Protocol;
                        _externalPort = mapping.ExternalPort;
                        _internalPort = mapping.InternalPort;
                        _description = mapping.Description;
                        _leaseSeconds = mapping.LeaseDuration;
                        GUI.FocusControl(null);
                    }
                }
            }
        }

        private void DrawMappingForm()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Open / Close a Port", EditorStyles.boldLabel);

            _protocol = (UpnpProtocol)EditorGUILayout.EnumPopup("Protocol", _protocol);
            _externalPort = Mathf.Clamp(EditorGUILayout.IntField("External Port", _externalPort), 1, 65535);
            _internalPort = Mathf.Clamp(EditorGUILayout.IntField("Internal Port", _internalPort), 1, 65535);
            _description = EditorGUILayout.TextField("Description", _description);
            _leaseSeconds = Math.Max(0, EditorGUILayout.IntField(new GUIContent("Lease (seconds)", "0 = permanent mapping."), _leaseSeconds));

            EditorGUI.BeginDisabledGroup(_viewBusy);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Open Port"))
                {
                    AddMapping(false);
                }

                if (GUILayout.Button("Close Port"))
                {
                    DeleteMapping(false);
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Open on All Gateways"))
                {
                    AddMapping(true);
                }

                if (GUILayout.Button("Close on All Gateways"))
                {
                    DeleteMapping(true);
                }
            }

            EditorGUI.EndDisabledGroup();

            EditorGUILayout.HelpBox("A port that you open here stays open on the router until you close it (or the router restarts). Close it again when you are done testing.", MessageType.None);
        }

        private void DrawLog()
        {
            EditorGUILayout.LabelField("Log", EditorStyles.boldLabel);
            _logScroll = EditorGUILayout.BeginScrollView(_logScroll, GUILayout.Height(110f));
            for (int i = 0; i < _viewLog.Length; i++)
            {
                EditorGUILayout.LabelField(_viewLog[i], EditorStyles.miniLabel);
            }

            EditorGUILayout.EndScrollView();
        }

        private static void Row(string label, string value)
        {
            EditorGUILayout.LabelField(label, string.IsNullOrEmpty(value) ? UpnpConstants.NotAvailable : value);
        }

        private static string FormatUptime(int seconds)
        {
            if (seconds <= 0)
            {
                return UpnpConstants.NotAvailable;
            }

            TimeSpan span = TimeSpan.FromSeconds(seconds);
            return span.Days > 0
                ? span.Days + "d " + span.Hours.ToString("00") + ":" + span.Minutes.ToString("00") + ":" + span.Seconds.ToString("00")
                : span.Hours.ToString("00") + ":" + span.Minutes.ToString("00") + ":" + span.Seconds.ToString("00");
        }
    }
}
