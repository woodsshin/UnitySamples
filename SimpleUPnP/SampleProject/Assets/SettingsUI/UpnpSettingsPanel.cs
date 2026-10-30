// Copyright 2017-present WoodsShin, Inc. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace WoodsShin.SimpleUPNP.Samples
{
    /// <summary>
    /// The "UPNP Settings" window of the <c>UpnpSettings</c> scene, built with Unity UI (uGUI).
    /// It has three tabs: <b>Manufacturer</b> (who made the router), <b>Device Status</b> (connection status, address and
    /// link speed, and the list of routers that were found) and <b>Port Mapping</b> (add and remove ports).
    /// Everything it does goes through <see cref="UpnpManager"/>, so the script is also a short example of how to use it.
    /// The window finds the router when the scene starts. Ports that you add here are closed again when you stop the game
    /// (see <c>Close Mappings On Quit</c> on the UPNP Manager).
    /// </summary>
    [AddComponentMenu("Simple UPNP/Samples/UPNP Settings Panel")]
    public sealed class UpnpSettingsPanel : MonoBehaviour
    {
        /// <summary>The colors of the status line.</summary>
        private enum StatusKind
        {
            Info,
            Success,
            Warning,
            Error,
        }

        private static readonly IReadOnlyList<UpnpGateway> NoGateways = new UpnpGateway[0];
        private static readonly IReadOnlyList<PortMappingInfo> NoMappings = new PortMappingInfo[0];

#pragma warning disable 0649 // The references are assigned in the Inspector.

        [Header("Behaviour")]
        [Tooltip("Search for routers as soon as the scene starts. Turn 'Discover On Start' off on the UPNP Manager then.")]
        [SerializeField] private bool searchOnStart = true;

        [Tooltip("The tab that is shown first: 0 = Manufacturer, 1 = Device Status, 2 = Port Mapping.")]
        [SerializeField] private int startTab = 1;

        [Tooltip("Lease of the ports that 'Add Port' opens, in seconds. 0 asks the router for a permanent mapping.")]
        [SerializeField] private int leaseSeconds;

        [Tooltip("Create an Event System when the scene has none. Without one, no button reacts to the mouse or to touch.")]
        [SerializeField] private bool createEventSystem = true;

        [Header("Window")]
        [Tooltip("The object that Close hides and Open shows.")]
        [SerializeField] private GameObject window;

        [Tooltip("Optional. A button outside of the window that opens it again. It is only visible while the window is closed.")]
        [SerializeField] private Button openButton;

        [SerializeField] private Button closeButton;

        [Tooltip("Reads status, address, link properties and the port mappings of the selected router again.")]
        [SerializeField] private Button refreshButton;

        [SerializeField] private Text statusText;

        [Header("Tabs")]
        [SerializeField] private Button[] tabButtons = new Button[0];
        [SerializeField] private GameObject[] tabPages = new GameObject[0];
        [SerializeField] private Color tabColor = new Color(0.44f, 0.44f, 0.44f, 1f);
        [SerializeField] private Color selectedTabColor = new Color(0.37f, 0.53f, 0.74f, 1f);

        [Header("Device Status tab")]
        [Tooltip("Lists the routers that were found. Choosing one selects it and reads its information.")]
        [SerializeField] private Dropdown locationDropdown;

        [Tooltip("Searches the network for routers again.")]
        [SerializeField] private Button searchButton;

        [Header("Port Mapping tab")]
        [SerializeField] private InputField internalPortInput;
        [SerializeField] private InputField externalPortInput;
        [SerializeField] private Dropdown protocolDropdown;
        [SerializeField] private Button addPortButton;

        [Tooltip("The parent of the table lines. It is resized to fit them, so a ScrollRect can scroll it.")]
        [SerializeField] private RectTransform mappingContent;

        [Tooltip("A line of the table. It is hidden, and a copy is made for every port mapping.")]
        [SerializeField] private UpnpMappingRow rowTemplate;

        [Tooltip("Shown instead of the table when the router has no port mappings.")]
        [SerializeField] private Text emptyText;

        [SerializeField] private float rowHeight = 40f;

        [Header("Status line colors")]
        [SerializeField] private Color infoColor = new Color(0.82f, 0.85f, 0.9f, 1f);
        [SerializeField] private Color successColor = new Color(0.55f, 0.88f, 0.55f, 1f);
        [SerializeField] private Color warningColor = new Color(1f, 0.8f, 0.35f, 1f);
        [SerializeField] private Color errorColor = new Color(1f, 0.6f, 0.56f, 1f);

#pragma warning restore 0649

        private readonly List<UpnpMappingRow> _rows = new List<UpnpMappingRow>();

        private UpnpManager _manager;
        private UpnpInfoField[] _fields = new UpnpInfoField[0];
        private CancellationTokenSource _lifetime;
        private int _busy;
        private bool _hasGateways;
        private bool _destroyed;

        /// <summary>True while the window is shown.</summary>
        public bool IsOpen
        {
            get { return window == null || window.activeSelf; }
        }

        // ------------------------------------------------------------------------------------------------------
        // Unity messages
        // ------------------------------------------------------------------------------------------------------

        private void Awake()
        {
            _lifetime = new CancellationTokenSource();
            _fields = GetComponentsInChildren<UpnpInfoField>(true);

            if (rowTemplate != null)
            {
                // The template only shows what a line looks like in the editor.
                rowTemplate.gameObject.SetActive(false);
            }

            for (int i = 0; i < tabButtons.Length; i++)
            {
                if (tabButtons[i] != null)
                {
                    int index = i;
                    tabButtons[i].onClick.AddListener(() => SelectTab(index));
                }
            }

            Hook(closeButton, Close);
            Hook(openButton, Open);
            Hook(refreshButton, RefreshGateway);
            Hook(searchButton, SearchGateways);
            Hook(addPortButton, AddPort);

            if (locationDropdown != null)
            {
                locationDropdown.onValueChanged.AddListener(OnLocationChanged);
            }

            if (createEventSystem)
            {
                EnsureEventSystem();
            }

            SetWindowVisible(true);
            SelectTab(startTab);
            ApplyState();
            SetStatus("Press Refresh to search for your router.", StatusKind.Info);
        }

        private void Start()
        {
            _manager = UpnpManager.GetOrCreate();
            if (searchOnStart)
            {
                SearchGateways();
            }
        }

        private void OnDestroy()
        {
            _destroyed = true;
            if (_lifetime != null)
            {
                // Ends the requests that are still running.
                _lifetime.Cancel();
                _lifetime.Dispose();
            }
        }

        private static void Hook(Button button, UnityEngine.Events.UnityAction action)
        {
            if (button != null)
            {
                button.onClick.AddListener(action);
            }
        }

        // ------------------------------------------------------------------------------------------------------
        // Window and tabs
        // ------------------------------------------------------------------------------------------------------

        /// <summary>Shows the window.</summary>
        public void Open()
        {
            SetWindowVisible(true);
        }

        /// <summary>Hides the window. The open button, if there is one, takes its place.</summary>
        public void Close()
        {
            SetWindowVisible(false);
        }

        /// <summary>Shows the window when it is hidden and hides it when it is shown.</summary>
        public void Toggle()
        {
            SetWindowVisible(!IsOpen);
        }

        /// <summary>Shows a tab: 0 = Manufacturer, 1 = Device Status, 2 = Port Mapping.</summary>
        public void SelectTab(int index)
        {
            int count = Mathf.Max(tabPages.Length, tabButtons.Length);
            if (count == 0)
            {
                return;
            }

            index = Mathf.Clamp(index, 0, count - 1);

            for (int i = 0; i < tabPages.Length; i++)
            {
                if (tabPages[i] != null)
                {
                    tabPages[i].SetActive(i == index);
                }
            }

            for (int i = 0; i < tabButtons.Length; i++)
            {
                if (tabButtons[i] != null && tabButtons[i].targetGraphic != null)
                {
                    tabButtons[i].targetGraphic.color = i == index ? selectedTabColor : tabColor;
                }
            }
        }

        private void SetWindowVisible(bool visible)
        {
            if (window != null)
            {
                window.SetActive(visible);
            }

            if (openButton != null)
            {
                openButton.gameObject.SetActive(!visible);
            }
        }

        // ------------------------------------------------------------------------------------------------------
        // Button handlers
        // ------------------------------------------------------------------------------------------------------

        /// <summary>Searches the network for routers and reads the information of the first one.</summary>
        public void SearchGateways()
        {
            _ = RunGuardedAsync(SearchAsync);
        }

        /// <summary>Reads the information and the port mappings of the selected router again. Searches first when there is none.</summary>
        public void RefreshGateway()
        {
            _ = RunGuardedAsync(RefreshAsync);
        }

        /// <summary>Opens the port that is typed into the Port Mapping tab.</summary>
        public void AddPort()
        {
            int externalPort;
            int internalPort;
            string problem;
            if (!TryReadPorts(externalPortInput == null ? null : externalPortInput.text,
                    internalPortInput == null ? null : internalPortInput.text,
                    out externalPort, out internalPort, out problem))
            {
                SetStatus(problem, StatusKind.Error);
                return;
            }

            UpnpProtocol protocol = SelectedProtocol();
            _ = RunGuardedAsync(() => AddPortAsync(protocol, externalPort, internalPort));
        }

        private void RemovePort(PortMappingInfo mapping)
        {
            _ = RunGuardedAsync(() => RemovePortAsync(mapping));
        }

        private void OnLocationChanged(int index)
        {
            if (_manager == null || _manager.Client == null || !_hasGateways)
            {
                return;
            }

            if (_manager.Client.SelectGateway(index))
            {
                RefreshGateway();
            }
        }

        // ------------------------------------------------------------------------------------------------------
        // Operations. They run through the UPNP Manager, which reports every outcome as a result and never throws.
        // The code after an await runs on the main thread again, so it may use Unity objects.
        // ------------------------------------------------------------------------------------------------------

        private async Task RunGuardedAsync(Func<Task> work)
        {
            if (_destroyed || _manager == null)
            {
                return;
            }

            _busy++;
            UpdateInteractable();
            try
            {
                await work();
            }
            catch (OperationCanceledException)
            {
                // The window was destroyed while a request was running.
            }
            catch (Exception ex)
            {
                if (!_destroyed)
                {
                    Debug.LogException(ex);
                    SetStatus(ex.Message, StatusKind.Error);
                }
            }
            finally
            {
                _busy--;
                if (!_destroyed)
                {
                    UpdateInteractable();
                }
            }
        }

        private async Task SearchAsync()
        {
            SetStatus("Searching for routers...", StatusKind.Info);
            UpnpResult<IReadOnlyList<UpnpGateway>> result = await _manager.DiscoverGatewaysAsync(true, _lifetime.Token);
            if (_destroyed)
            {
                return;
            }

            ApplyState();
            if (!result.Succeeded)
            {
                SetStatus("Search failed: " + result, StatusKind.Error);
                return;
            }

            UpnpGateway selected = _manager.SelectedGateway;
            if (selected != null && !selected.LastResult.Succeeded)
            {
                SetStatus("Found " + selected.DisplayName + ", but reading it failed: " + selected.LastResult, StatusKind.Error);
                return;
            }

            ShowReadSuccess(result.Value.Count == 1 ? "Found 1 router." : "Found " + result.Value.Count + " routers.");
        }

        private async Task RefreshAsync()
        {
            SetStatus("Reading the router...", StatusKind.Info);
            UpnpResult result = await _manager.RefreshGatewayAsync(true, _lifetime.Token);
            if (_destroyed)
            {
                return;
            }

            ApplyState();
            if (!result.Succeeded)
            {
                SetStatus("Refresh failed: " + result, StatusKind.Error);
                return;
            }

            ShowReadSuccess("Router information updated.");
        }

        private async Task AddPortAsync(UpnpProtocol protocol, int externalPort, int internalPort)
        {
            SetStatus("Opening " + protocol.ToWireString() + " port " + externalPort + "...", StatusKind.Info);
            UpnpResult result = await _manager.OpenPortAsync(
                protocol, externalPort, internalPort, BuildDescription(internalPort), Mathf.Max(0, leaseSeconds), _lifetime.Token);
            if (_destroyed)
            {
                return;
            }

            if (!result.Succeeded)
            {
                ApplyState();
                SetStatus("Add Port failed: " + result, StatusKind.Error);
                return;
            }

            // The router's table changed. Read it again so that the new line shows up.
            UpnpResult<IReadOnlyList<PortMappingInfo>> table = await _manager.GetPortMappingsAsync(_lifetime.Token);
            if (_destroyed)
            {
                return;
            }

            ApplyState();
            string opened = "Opened " + protocol.ToWireString() + " port " + externalPort + ".";
            if (!table.Succeeded)
            {
                SetStatus(opened + " The list could not be updated: " + table, StatusKind.Warning);
                return;
            }

            SetStatus(opened, StatusKind.Success);
        }

        private async Task RemovePortAsync(PortMappingInfo mapping)
        {
            SetStatus("Closing " + mapping.Protocol.ToWireString() + " port " + mapping.ExternalPort + "...", StatusKind.Info);
            UpnpResult result = await _manager.DeletePortMappingAsync(mapping, _lifetime.Token);
            if (_destroyed)
            {
                return;
            }

            if (!result.Succeeded)
            {
                ApplyState();
                SetStatus("Remove failed: " + result, StatusKind.Error);
                return;
            }

            UpnpResult<IReadOnlyList<PortMappingInfo>> table = await _manager.GetPortMappingsAsync(_lifetime.Token);
            if (_destroyed)
            {
                return;
            }

            ApplyState();
            string closed = "Closed " + mapping.Protocol.ToWireString() + " port " + mapping.ExternalPort + ".";
            SetStatus(table.Succeeded ? closed : closed + " The list could not be updated: " + table,
                table.Succeeded ? StatusKind.Success : StatusKind.Warning);
        }

        private void ShowReadSuccess(string text)
        {
            UpnpGateway selected = _manager.SelectedGateway;
            if (selected != null && selected.HasPublicExternalAddress == false)
            {
                // A private or shared address means that this router sits behind another router (double NAT or
                // carrier-grade NAT). The mapping works on the router, but players on the internet cannot reach it.
                SetStatus(text + " The address of the router on the internet (" + selected.ExternalIPAddress +
                          ") is not a public address, so ports opened on it cannot be reached from the internet.", StatusKind.Warning);
                return;
            }

            SetStatus(text, StatusKind.Success);
        }

        private string BuildDescription(int internalPort)
        {
            string product = string.IsNullOrEmpty(Application.productName) ? "Simple UPNP" : Application.productName;
            UpnpGateway selected = _manager == null ? null : _manager.SelectedGateway;
            string local = selected == null ? null : selected.LocalIPAddress;
            if (string.IsNullOrEmpty(local) || local == UpnpConstants.NotAvailable)
            {
                return product;
            }

            return product + " at " + local + ":" + internalPort.ToString(CultureInfo.InvariantCulture);
        }

        // ------------------------------------------------------------------------------------------------------
        // Showing the state of the selected router
        // ------------------------------------------------------------------------------------------------------

        /// <summary>Copies the state that the manager holds into the window. Does not touch the network.</summary>
        private void ApplyState()
        {
            UpnpGateway selected = _manager == null ? null : _manager.SelectedGateway;

            for (int i = 0; i < _fields.Length; i++)
            {
                if (_fields[i] != null)
                {
                    _fields[i].Show(selected);
                }
            }

            RebuildLocationList(selected);
            RebuildTable(selected);
            UpdateInteractable();
        }

        private void RebuildLocationList(UpnpGateway selected)
        {
            IReadOnlyList<UpnpGateway> gateways = _manager == null ? NoGateways : _manager.Gateways;
            _hasGateways = gateways.Count > 0;

            if (locationDropdown == null)
            {
                return;
            }

            List<string> names = new List<string>();
            int selectedIndex = 0;
            for (int i = 0; i < gateways.Count; i++)
            {
                names.Add(gateways[i].Location.ToString());
                if (ReferenceEquals(gateways[i], selected))
                {
                    selectedIndex = i;
                }
            }

            if (names.Count == 0)
            {
                names.Add("No router found");
            }

            locationDropdown.ClearOptions();
            locationDropdown.AddOptions(names);
            locationDropdown.SetValueWithoutNotify(selectedIndex);
            locationDropdown.RefreshShownValue();
        }

        private void RebuildTable(UpnpGateway selected)
        {
            IReadOnlyList<PortMappingInfo> mappings = selected == null ? NoMappings : selected.PortMappings;

            if (rowTemplate != null && mappingContent != null)
            {
                for (int i = 0; i < mappings.Count; i++)
                {
                    UpnpMappingRow row;
                    if (i < _rows.Count)
                    {
                        row = _rows[i];
                    }
                    else
                    {
                        row = Instantiate(rowTemplate, mappingContent, false);
                        row.gameObject.name = "Row " + (i + 1);
                        _rows.Add(row);
                    }

                    PlaceRow(row, i);
                    row.Bind(mappings[i], i, RemovePort);
                    row.gameObject.SetActive(true);
                }

                // Lines of an earlier, longer list are kept for later and hidden.
                for (int i = mappings.Count; i < _rows.Count; i++)
                {
                    _rows[i].gameObject.SetActive(false);
                }

                mappingContent.sizeDelta = new Vector2(mappingContent.sizeDelta.x, mappings.Count * rowHeight);
            }

            if (emptyText != null)
            {
                emptyText.gameObject.SetActive(mappings.Count == 0);
                emptyText.text = _hasGateways ? "This router has no port mappings." : "No router found. Press the Refresh button next to Device Location to search again.";
            }
        }

        private void PlaceRow(UpnpMappingRow row, int index)
        {
            RectTransform rect = (RectTransform)row.transform;
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, -index * rowHeight);
            rect.sizeDelta = new Vector2(0f, rowHeight);
        }

        /// <summary>The buttons wait while a request is running, so that two requests do not overlap.</summary>
        private void UpdateInteractable()
        {
            bool idle = _busy == 0;
            SetInteractable(refreshButton, idle);
            SetInteractable(searchButton, idle);
            SetInteractable(addPortButton, idle);

            if (locationDropdown != null)
            {
                locationDropdown.interactable = idle && _hasGateways;
            }

            for (int i = 0; i < _rows.Count; i++)
            {
                _rows[i].SetInteractable(idle);
            }
        }

        private static void SetInteractable(Selectable control, bool value)
        {
            if (control != null)
            {
                control.interactable = value;
            }
        }

        private void SetStatus(string text, StatusKind kind)
        {
            if (statusText == null)
            {
                return;
            }

            statusText.text = text;
            switch (kind)
            {
                case StatusKind.Success:
                    statusText.color = successColor;
                    break;
                case StatusKind.Warning:
                    statusText.color = warningColor;
                    break;
                case StatusKind.Error:
                    statusText.color = errorColor;
                    break;
                default:
                    statusText.color = infoColor;
                    break;
            }
        }

        // ------------------------------------------------------------------------------------------------------
        // Input
        // ------------------------------------------------------------------------------------------------------

        private UpnpProtocol SelectedProtocol()
        {
            if (protocolDropdown != null && protocolDropdown.options.Count > 0)
            {
                int index = Mathf.Clamp(protocolDropdown.value, 0, protocolDropdown.options.Count - 1);
                UpnpProtocol parsed;
                if (UpnpProtocolExtensions.TryParse(protocolDropdown.options[index].text, out parsed))
                {
                    return parsed;
                }
            }

            return UpnpProtocol.Udp;
        }

        /// <summary>
        /// Reads the two port boxes. An empty box takes the number of the other one, so that a single number is enough
        /// when both ports are the same. Returns false and a message when a number is missing or out of range.
        /// </summary>
        public static bool TryReadPorts(string externalText, string internalText, out int externalPort, out int internalPort, out string problem)
        {
            externalPort = 0;
            internalPort = 0;
            problem = null;

            bool hasExternal = !string.IsNullOrWhiteSpace(externalText);
            bool hasInternal = !string.IsNullOrWhiteSpace(internalText);
            if (!hasExternal && !hasInternal)
            {
                problem = "Enter the port number that you want to open.";
                return false;
            }

            if (hasExternal && !TryParsePort(externalText, out externalPort))
            {
                problem = "External Port must be a number between 1 and 65535.";
                return false;
            }

            if (hasInternal && !TryParsePort(internalText, out internalPort))
            {
                problem = "Internal Port must be a number between 1 and 65535.";
                return false;
            }

            if (!hasExternal)
            {
                externalPort = internalPort;
            }

            if (!hasInternal)
            {
                internalPort = externalPort;
            }

            return true;
        }

        private static bool TryParsePort(string text, out int port)
        {
            return int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port) && port >= 1 && port <= 65535;
        }

        // ------------------------------------------------------------------------------------------------------
        // Event System
        // ------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Buttons only react when the scene has an Event System. The scene of this sample has none on purpose: which
        /// input module it needs depends on the Active Input Handling setting of the project, so it is made here.
        /// </summary>
        private static void EnsureEventSystem()
        {
            if (EventSystem.current != null)
            {
                return;
            }

#if UNITY_2023_1_OR_NEWER
            EventSystem existing = FindFirstObjectByType<EventSystem>();
#else
            EventSystem existing = FindObjectOfType<EventSystem>();
#endif
            if (existing != null)
            {
                return;
            }

            GameObject host = new GameObject("EventSystem");
            host.AddComponent<EventSystem>();

#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
            // Only the Input System package is active, so the old input module would throw. The module of the package is
            // looked up by name to keep this sample free of a reference to the package.
            Type module = Type.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");
            if (module != null)
            {
                Component added = host.AddComponent(module);
                System.Reflection.MethodInfo assign = module.GetMethod("AssignDefaultActions");
                if (assign != null)
                {
                    assign.Invoke(added, null);
                }

                return;
            }
#endif
            host.AddComponent<StandaloneInputModule>();
        }
    }
}
