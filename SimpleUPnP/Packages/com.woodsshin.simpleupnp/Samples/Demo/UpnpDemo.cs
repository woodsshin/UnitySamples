// Copyright 2017-present WoodsShin, Inc. All Rights Reserved.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace WoodsShin.SimpleUPNP.Samples
{
    /// <summary>
    /// Demo of Simple UPNP. Add this component to an empty GameObject and press Play: it searches for your router,
    /// shows its information, lists the port mappings and lets you open and close ports.
    /// The UPNP Manager component is added automatically. Ports opened by the demo are closed again when you stop.
    /// Everything is drawn with IMGUI, so it works in every render pipeline and needs no scene setup.
    /// </summary>
    [RequireComponent(typeof(UpnpManager))]
    [AddComponentMenu("Simple UPNP/Samples/UPNP Demo")]
    public sealed class UpnpDemo : MonoBehaviour
    {
        private const int MaxLogLines = 60;

        private static readonly string[] ProtocolNames = { "TCP", "UDP" };

        [Tooltip("Search for routers when the demo starts.")]
        [SerializeField] private bool searchOnStart = true;

        private readonly List<string> _log = new List<string>();
        private int _logVersion;

        private UpnpManager _manager;
        private GUIStyle _bold;
        private Vector2 _scroll;

        private int _protocol = 1;
        private string _externalPort = "7777";
        private string _internalPort = "7777";
        private string _description = "Simple UPNP Demo";
        private string _lease = "0";

        // Copied during the Layout event, so the Layout and the Repaint event of one frame draw the same controls
        // even if a request finishes between them.
        private UpnpGateway[] _gateways = new UpnpGateway[0];
        private UpnpGateway _selected;
        private PortMappingInfo[] _mappings = new PortMappingInfo[0];
        private IReadOnlyList<PortMappingInfo> _opened = new PortMappingInfo[0];
        private string[] _logView = new string[0];
        private int _logViewVersion = -1;
        private string _state = "Idle";
        private string _last = string.Empty;
        private bool _busy;
        private bool _notPublic;

        private void Awake()
        {
            _manager = GetComponent<UpnpManager>();
        }

        private void OnEnable()
        {
            _manager.OperationFinished += OnOperationFinished;
        }

        private void OnDisable()
        {
            _manager.OperationFinished -= OnOperationFinished;
        }

        private void Start()
        {
            if (searchOnStart)
            {
                _manager.DiscoverGateways();
            }
        }

        private void OnOperationFinished(string operation, UpnpResult result)
        {
            AddLog(operation + ": " + (result.Succeeded ? "OK" : "FAILED - " + result));

            // Show the new state of the router's table after a mapping was added or deleted.
            bool changedMappings = operation == "Add port mapping" || operation == "Delete port mapping" || operation == "Perform on all gateways";
            if (result.Succeeded && changedMappings)
            {
                _manager.RefreshGateway();
            }
        }

        private void AddLog(string line)
        {
            _log.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + line);
            if (_log.Count > MaxLogLines)
            {
                _log.RemoveAt(0);
            }

            _logVersion++;
        }

        // ------------------------------------------------------------------------------------------------------
        // Actions
        // ------------------------------------------------------------------------------------------------------

        private bool TryReadMapping(out PortMappingInfo mapping)
        {
            mapping = null;

            int external;
            int local;
            int lease;
            if (!int.TryParse(_externalPort, out external) || external < 1 || external > 65535)
            {
                AddLog("The external port must be a number from 1 to 65535.");
                return false;
            }

            if (!int.TryParse(_internalPort, out local) || local < 1 || local > 65535)
            {
                AddLog("The internal port must be a number from 1 to 65535.");
                return false;
            }

            if (!int.TryParse(_lease, out lease) || lease < 0)
            {
                AddLog("The lease must be 0 (permanent) or a number of seconds.");
                return false;
            }

            mapping = new PortMappingInfo(_protocol == 0 ? UpnpProtocol.Tcp : UpnpProtocol.Udp, external, local, _description, lease);
            return true;
        }

        private void OpenPort(bool allGateways)
        {
            PortMappingInfo mapping;
            if (!TryReadMapping(out mapping))
            {
                return;
            }

            // Both calls return a Task that you can await. This demo reads the result from the OperationFinished event.
            if (allGateways)
            {
                _ = _manager.PerformAllGatewaysAsync(UpnpAction.AddPort, mapping);
            }
            else
            {
                _ = _manager.AddPortMappingAsync(mapping);
            }
        }

        private void ClosePort(bool allGateways)
        {
            PortMappingInfo mapping;
            if (!TryReadMapping(out mapping))
            {
                return;
            }

            if (allGateways)
            {
                _ = _manager.PerformAllGatewaysAsync(UpnpAction.DeletePort, mapping);
            }
            else
            {
                _ = _manager.DeletePortMappingAsync(mapping);
            }
        }

        // ------------------------------------------------------------------------------------------------------
        // GUI
        // ------------------------------------------------------------------------------------------------------

        private void TakeSnapshot()
        {
            _busy = _manager.IsBusy;
            _state = _busy && _manager.Client != null ? _manager.Client.CurrentOperation.ToString() : "Idle";
            _last = string.IsNullOrEmpty(_manager.LastOperation) ? string.Empty : _manager.LastOperation + ": " + _manager.LastResult;

            IReadOnlyList<UpnpGateway> gateways = _manager.Gateways;
            UpnpGateway[] gatewayCopy = new UpnpGateway[gateways.Count];
            for (int i = 0; i < gatewayCopy.Length; i++)
            {
                gatewayCopy[i] = gateways[i];
            }

            _gateways = gatewayCopy;
            _selected = _manager.SelectedGateway;
            _notPublic = _selected != null && _selected.HasPublicExternalAddress == false;

            if (_selected == null)
            {
                _mappings = new PortMappingInfo[0];
            }
            else
            {
                IReadOnlyList<PortMappingInfo> mappings = _selected.PortMappings;
                PortMappingInfo[] mappingCopy = new PortMappingInfo[mappings.Count];
                for (int i = 0; i < mappingCopy.Length; i++)
                {
                    mappingCopy[i] = mappings[i];
                }

                _mappings = mappingCopy;
            }

            _opened = _manager.OpenedMappings;

            if (_logViewVersion != _logVersion)
            {
                _logView = _log.ToArray();
                _logViewVersion = _logVersion;
            }
        }

        private void OnGUI()
        {
            if (Event.current.type == EventType.Layout)
            {
                TakeSnapshot();
            }

            if (_bold == null)
            {
                _bold = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
            }

            // Larger controls on high resolution screens.
            float scale = Mathf.Clamp(Screen.height / 640f, 1f, 3f);
            Matrix4x4 oldMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));

            float width = Screen.width / scale;
            float height = Screen.height / scale;

            GUILayout.BeginArea(new Rect(8f, 8f, width - 16f, height - 16f), GUI.skin.box);
            _scroll = GUILayout.BeginScrollView(_scroll);

            GUILayout.Label("Simple UPNP Demo", _bold);

            if (!_manager.IsSupported)
            {
                GUILayout.Label("UPnP is not available on this platform (WebGL cannot open sockets).");
            }
            else
            {
                DrawToolbar();
                DrawGateways();
                DrawSelectedGateway();
                DrawForm();
                DrawOpened();
            }

            DrawLog();

            GUILayout.EndScrollView();
            GUILayout.EndArea();

            GUI.matrix = oldMatrix;
        }

        private void DrawToolbar()
        {
            GUILayout.Label("State: " + _state);
            if (_last.Length > 0)
            {
                GUILayout.Label("Last: " + _last);
            }

            GUILayout.BeginHorizontal();
            GUI.enabled = !_busy;
            if (GUILayout.Button("Search Gateways"))
            {
                _manager.DiscoverGateways();
            }

            GUI.enabled = !_busy && _selected != null;
            if (GUILayout.Button("Refresh"))
            {
                _manager.RefreshGateway();
            }

            if (GUILayout.Button("Read Port Mappings"))
            {
                _ = _manager.GetPortMappingsAsync();
            }

            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }

        private void DrawGateways()
        {
            GUILayout.Space(6f);
            GUILayout.Label("Gateways (" + _gateways.Length + ")", _bold);

            for (int i = 0; i < _gateways.Length; i++)
            {
                bool selected = ReferenceEquals(_gateways[i], _selected);
                bool toggled = GUILayout.Toggle(selected, _gateways[i].ToString(), GUI.skin.button);
                if (toggled && !selected && _manager.Client != null)
                {
                    _manager.Client.SelectGateway(_gateways[i]);
                }
            }
        }

        private void DrawSelectedGateway()
        {
            if (_selected == null)
            {
                return;
            }

            ManufacturerInfo maker = _selected.Manufacturer;
            DeviceStatusInfo status = _selected.Status;
            CommonLinkProperties link = _selected.LinkProperties;

            GUILayout.Space(6f);
            GUILayout.Label("Selected Gateway", _bold);
            GUILayout.Label("Name: " + _selected.DisplayName);
            GUILayout.Label("Manufacturer: " + maker.Manufacturer + "   Model: " + maker.ModelName);
            GUILayout.Label("Local address: " + _selected.LocalIPAddress + "   Public address: " + _selected.ExternalIPAddress);
            if (_notPublic)
            {
                GUILayout.Label("Warning: that is not a public address. The router is behind another router or the network of your provider, so players on the internet cannot reach opened ports.");
            }

            GUILayout.Label("Connection: " + status.ConnectionStatus + " (" + status.ConnectionType + ")   Uptime: " + status.Uptime + " s");
            GUILayout.Label("Link: " + link.WanAccessType + " / " + link.PhysicalLinkStatus + "   Up/Down bit rate: " + link.Layer1UpstreamMaxBitRate + " / " + link.Layer1DownstreamMaxBitRate);

            GUILayout.Space(6f);
            GUILayout.Label("Port Mappings on the Router (" + _mappings.Length + ")", _bold);
            for (int i = 0; i < _mappings.Length; i++)
            {
                PortMappingInfo mapping = _mappings[i];
                GUILayout.Label(mapping.Protocol.ToWireString() + " " + mapping.ExternalPort + " -> " + mapping.InternalClient + ":" + mapping.InternalPort +
                                "   " + mapping.Description + (mapping.LeaseDuration > 0 ? "   (" + mapping.LeaseDuration + " s)" : string.Empty));
            }
        }

        private void DrawForm()
        {
            GUILayout.Space(6f);
            GUILayout.Label("Open / Close a Port", _bold);

            _protocol = GUILayout.Toolbar(_protocol, ProtocolNames);
            _externalPort = Field("External port", _externalPort, 5);
            _internalPort = Field("Internal port", _internalPort, 5);
            _description = Field("Description", _description, 40);
            _lease = Field("Lease (s, 0 = permanent)", _lease, 8);

            GUI.enabled = !_busy;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Open Port"))
            {
                OpenPort(false);
            }

            if (GUILayout.Button("Close Port"))
            {
                ClosePort(false);
            }

            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Open on All Gateways"))
            {
                OpenPort(true);
            }

            if (GUILayout.Button("Close on All Gateways"))
            {
                ClosePort(true);
            }

            GUILayout.EndHorizontal();
            GUI.enabled = true;
        }

        private void DrawOpened()
        {
            GUILayout.Space(6f);
            GUILayout.Label("Opened by this demo (closed again when you stop)", _bold);
            if (_opened.Count == 0)
            {
                GUILayout.Label("-");
                return;
            }

            for (int i = 0; i < _opened.Count; i++)
            {
                GUILayout.Label(_opened[i].ToString());
            }
        }

        private void DrawLog()
        {
            GUILayout.Space(6f);
            GUILayout.Label("Log", _bold);
            for (int i = _logView.Length - 1; i >= 0; i--)
            {
                GUILayout.Label(_logView[i]);
            }
        }

        private static string Field(string label, string value, int maxLength)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(170f));
            string result = GUILayout.TextField(value, maxLength);
            GUILayout.EndHorizontal();
            return result;
        }
    }
}
