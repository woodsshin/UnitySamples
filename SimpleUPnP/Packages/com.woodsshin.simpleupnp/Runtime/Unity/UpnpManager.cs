// Copyright 2017-present WoodsShin, Inc. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Events;

namespace WoodsShin.SimpleUPNP
{
    /// <summary>A UnityEvent that carries a text, used for the success and failure callbacks of <see cref="UpnpManager"/>.</summary>
    [Serializable]
    public sealed class UpnpMessageEvent : UnityEvent<string>
    {
    }

    /// <summary>
    /// Component that wraps <see cref="UpnpClient"/> for Unity. Add it to a GameObject to open and close router
    /// ports from the Inspector, from UnityEvents or from code. Every event and every awaited call returns on the
    /// Unity main thread.
    /// </summary>
    [AddComponentMenu("Simple UPNP/UPNP Manager")]
    [DisallowMultipleComponent]
    public sealed class UpnpManager : MonoBehaviour
    {

        [Header("Startup")]
        [Tooltip("Search for routers when this component starts.")]
        [SerializeField] private bool discoverOnStart = false;

        [Tooltip("Open the port mappings of the list below when this component starts.")]
        [SerializeField] private bool openStartupMappingsOnStart = false;

        [Tooltip("Delete the mappings that this component opened when the application quits or this component is destroyed.")]
        [SerializeField] private bool closeMappingsOnQuit = true;

        [Tooltip("Keep this object when another scene is loaded.")]
        [SerializeField] private bool persistAcrossScenes = true;

        [Tooltip("Mappings for 'Open Startup Mappings'. An empty Internal Client means: this computer.")]
        [SerializeField]
        private List<PortMappingInfo> startupMappings = new List<PortMappingInfo>
        {
            new PortMappingInfo(UpnpProtocol.Udp, 7777, 7777, "My Game"),
        };

        [Header("Network")]
        [Tooltip("Seconds to wait for one answer of the router.")]
        [Min(0.5f)]
        [SerializeField] private float requestTimeoutSeconds = 5f;

        [Tooltip("Seconds the search for routers listens for answers.")]
        [Min(0.5f)]
        [SerializeField] private float discoveryTimeoutSeconds = 3f;

        [Tooltip("How often a request is repeated after a timeout.")]
        [Range(0, 5)]
        [SerializeField] private int maxRetries = 2;

        [Tooltip("Many routers only accept permanent mappings. When on, a mapping with a lease time is repeated as permanent mapping if the router asks for that.")]
        [SerializeField] private bool fallbackToPermanentLease = true;

        [Tooltip("Seconds that quitting the application may wait for the router while mappings are deleted.")]
        [Min(0.5f)]
        [SerializeField] private float quitCleanupTimeoutSeconds = 3f;

        [Header("Diagnostics")]
        [Tooltip("Also log every request and answer.")]
        [SerializeField] private bool verboseLogging = false;

        [Header("Events")]
        [Tooltip("Called after an operation succeeded. The argument is a short description.")]
        [SerializeField] private UpnpMessageEvent onSucceeded = new UpnpMessageEvent();

        [Tooltip("Called after an operation failed. The argument is the reason.")]
        [SerializeField] private UpnpMessageEvent onFailed = new UpnpMessageEvent();

        private readonly object _stateLock = new object();
        private readonly List<PortMappingInfo> _openedMappings = new List<PortMappingInfo>();

        private UpnpClient _client;
        private SynchronizationContext _mainContext;
        private string _defaultDescription = "Simple UPNP";
        private bool _supported = true;
        private bool _cleanedUp;
        private bool _initialized;
        private bool _destroyed;
        private UpnpResult _lastResult = UpnpResult.Ok;
        private string _lastOperation = string.Empty;

        /// <summary>
        /// Returns the active manager of the running game, or null when there is none. Replaces the former
        /// <c>Instance</c> property. This component keeps no static state, so nothing has to be reset when the
        /// Editor enters Play Mode without a domain reload. Call it from the main thread.
        /// </summary>
        public static UpnpManager FindInstance()
        {
            return FindInitialized(null);
        }

        /// <summary>Returns the manager of the running game. Creates a new object with a manager when there is none.</summary>
        public static UpnpManager GetOrCreate()
        {
            UpnpManager active = FindInitialized(null);
            if (active != null)
            {
                return active;
            }

            UpnpManager[] all = FindAll();
            if (all.Length > 0)
            {
                // A manager exists in the scene but its Awake has not run yet.
                return all[0];
            }

            GameObject host = new GameObject("Simple UPNP");
            return host.AddComponent<UpnpManager>();
        }

        private static UpnpManager[] FindAll()
        {
#if UNITY_2023_1_OR_NEWER
            return FindObjectsByType<UpnpManager>(FindObjectsSortMode.None);
#else
            return FindObjectsOfType<UpnpManager>();
#endif
        }

        /// <summary>The first manager that finished Awake and is still alive, ignoring <paramref name="exclude"/>.</summary>
        private static UpnpManager FindInitialized(UpnpManager exclude)
        {
            UpnpManager[] all = FindAll();
            for (int i = 0; i < all.Length; i++)
            {
                UpnpManager candidate = all[i];
                if (candidate != null && candidate != exclude && candidate._initialized && !candidate._destroyed)
                {
                    return candidate;
                }
            }

            return null;
        }

        /// <summary>False on platforms that cannot use UPnP (WebGL). Operations then fail with <see cref="UpnpErrorCode.NotSupported"/>.</summary>
        public bool IsSupported
        {
            get { return _supported; }
        }

        /// <summary>The underlying client, for features this component does not wrap.</summary>
        public UpnpClient Client
        {
            get { return _client; }
        }

        /// <summary>The routers found by the last search.</summary>
        public IReadOnlyList<UpnpGateway> Gateways
        {
            get { return _client == null ? Array.Empty<UpnpGateway>() : _client.Gateways; }
        }

        /// <summary>The router that the operations use. Null before a router was found.</summary>
        public UpnpGateway SelectedGateway
        {
            get { return _client == null ? null : _client.SelectedGateway; }
        }

        /// <summary>True while an operation is running.</summary>
        public bool IsBusy
        {
            get { return _client != null && _client.IsBusy; }
        }

        /// <summary>The ports that this component opened and will close again on quit (when <c>Close Mappings On Quit</c> is on).</summary>
        public IReadOnlyList<PortMappingInfo> OpenedMappings
        {
            get
            {
                lock (_stateLock)
                {
                    return _openedMappings.ConvertAll(m => m.Clone());
                }
            }
        }

        /// <summary>The result of the most recent operation.</summary>
        public UpnpResult LastResult
        {
            get
            {
                lock (_stateLock)
                {
                    return _lastResult;
                }
            }
        }

        /// <summary>The name of the most recent operation, for example "Add port mapping".</summary>
        public string LastOperation
        {
            get
            {
                lock (_stateLock)
                {
                    return _lastOperation;
                }
            }
        }

        /// <summary>Raised on the main thread after every operation with its name and result.</summary>
        public event Action<string, UpnpResult> OperationFinished;

        // ------------------------------------------------------------------------------------------------------
        // Unity messages
        // ------------------------------------------------------------------------------------------------------

        private void Awake()
        {
            if (FindInitialized(this) != null)
            {
                Debug.LogWarning("[Simple UPNP] There is already a UPNP Manager. Removing the duplicate component from '" + name + "'.");
                Destroy(this);
                return;
            }

            _mainContext = SynchronizationContext.Current;
            _supported = Application.platform != RuntimePlatform.WebGLPlayer;
            _defaultDescription = string.IsNullOrEmpty(Application.productName) ? "Simple UPNP" : Application.productName;

            if (persistAcrossScenes && transform.parent == null)
            {
                DontDestroyOnLoad(gameObject);
            }

            _client = new UpnpClient(CreateOptions());
            _initialized = true;
        }

        private async void Start()
        {
            if (_client == null)
            {
                return;
            }

            try
            {
                if (discoverOnStart)
                {
                    await DiscoverGatewaysAsync();
                }

                if (openStartupMappingsOnStart)
                {
                    await OpenStartupMappingsAsync();
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        private void OnApplicationQuit()
        {
            CleanUp();
        }

        private void OnDestroy()
        {
            CleanUp();
            _destroyed = true;

            if (_client != null)
            {
                _client.Dispose();
            }
        }

        private UpnpClientOptions CreateOptions()
        {
            return new UpnpClientOptions
            {
                RequestTimeout = TimeSpan.FromSeconds(Mathf.Max(0.5f, requestTimeoutSeconds)),
                DiscoveryTimeout = TimeSpan.FromSeconds(Mathf.Max(0.5f, discoveryTimeoutSeconds)),
                MaxRetries = Mathf.Clamp(maxRetries, 0, 5),
                FallbackToPermanentLease = fallbackToPermanentLease,
                Logger = WriteLog,
            };
        }

        private void WriteLog(UpnpLogLevel level, string message)
        {
            // Debug.Log may be called from any thread.
            string text = "[Simple UPNP] " + message;
            switch (level)
            {
                case UpnpLogLevel.Verbose:
                    if (verboseLogging)
                    {
                        Debug.Log(text);
                    }

                    break;
                case UpnpLogLevel.Info:
                    Debug.Log(text);
                    break;
                case UpnpLogLevel.Warning:
                    Debug.LogWarning(text);
                    break;
                default:
                    Debug.LogError(text);
                    break;
            }
        }

        /// <summary>Deletes the mappings this component opened. Waits at most <c>Quit Cleanup Timeout Seconds</c>.</summary>
        private void CleanUp()
        {
            if (_cleanedUp || _client == null)
            {
                return;
            }

            _cleanedUp = true;
            if (!closeMappingsOnQuit)
            {
                return;
            }

            List<PortMappingInfo> opened;
            lock (_stateLock)
            {
                opened = new List<PortMappingInfo>(_openedMappings);
                _openedMappings.Clear();
            }

            IReadOnlyList<UpnpGateway> gateways = _client.Gateways;
            if (opened.Count == 0 || gateways.Count == 0)
            {
                return;
            }

            // Deleting a mapping that does not exist on a router is harmless, so every known router is asked.
            List<Task<UpnpResult>> pending = new List<Task<UpnpResult>>();
            for (int g = 0; g < gateways.Count; g++)
            {
                for (int m = 0; m < opened.Count; m++)
                {
                    pending.Add(gateways[g].DeletePortMappingAsync(opened[m].Protocol, opened[m].ExternalPort, opened[m].RemoteHost));
                }
            }

            try
            {
                // The tasks run on thread pool threads and never need the main thread, so waiting here cannot deadlock.
                Task.WaitAll(pending.ToArray(), TimeSpan.FromSeconds(Mathf.Max(0.5f, quitCleanupTimeoutSeconds)));
            }
            catch (AggregateException)
            {
                // Nothing more can be done while quitting.
            }
        }

        // ------------------------------------------------------------------------------------------------------
        // Methods for UnityEvents and buttons (no return value)
        // ------------------------------------------------------------------------------------------------------

        /// <summary>Searches for routers.</summary>
        public void DiscoverGateways()
        {
            Forget(DiscoverGatewaysAsync());
        }

        /// <summary>Reads status, public address and port mappings of the selected router.</summary>
        public void RefreshGateway()
        {
            Forget(RefreshGatewayAsync());
        }

        /// <summary>Opens the mappings of the <c>Startup Mappings</c> list.</summary>
        public void OpenStartupMappings()
        {
            Forget(OpenStartupMappingsAsync());
        }

        /// <summary>Closes the mappings of the <c>Startup Mappings</c> list.</summary>
        public void CloseStartupMappings()
        {
            Forget(CloseStartupMappingsAsync());
        }

        /// <summary>Opens a TCP port with the same number on the router and on this computer.</summary>
        public void OpenTcpPort(int port)
        {
            Forget(OpenPortAsync(UpnpProtocol.Tcp, port));
        }

        /// <summary>Opens a UDP port with the same number on the router and on this computer.</summary>
        public void OpenUdpPort(int port)
        {
            Forget(OpenPortAsync(UpnpProtocol.Udp, port));
        }

        /// <summary>Closes a TCP port on the router.</summary>
        public void CloseTcpPort(int port)
        {
            Forget(ClosePortAsync(UpnpProtocol.Tcp, port));
        }

        /// <summary>Closes a UDP port on the router.</summary>
        public void CloseUdpPort(int port)
        {
            Forget(ClosePortAsync(UpnpProtocol.Udp, port));
        }

        private static void Forget(Task task)
        {
            task.ContinueWith(t => Debug.LogException(t.Exception), TaskContinuationOptions.OnlyOnFaulted);
        }

        // ------------------------------------------------------------------------------------------------------
        // Asynchronous API
        // ------------------------------------------------------------------------------------------------------

        /// <summary>Searches for routers. See <see cref="UpnpClient.DiscoverAsync"/>.</summary>
        public async Task<UpnpResult<IReadOnlyList<UpnpGateway>>> DiscoverGatewaysAsync(bool refreshSelected = true, CancellationToken cancellationToken = default)
        {
            const string operation = "Discover gateways";
            if (!CanRun())
            {
                UpnpResult refused = Refuse(operation);
                return new UpnpResult<IReadOnlyList<UpnpGateway>>(new UpnpGateway[0], refused);
            }

            UpnpResult<IReadOnlyList<UpnpGateway>> result = await _client.DiscoverAsync(refreshSelected, cancellationToken);
            Publish(operation, result.Result, result.Succeeded ? result.Value.Count + " gateway(s) found" : null);
            return result;
        }

        /// <summary>Reads status, public address, link properties and port mappings of the selected router.</summary>
        public async Task<UpnpResult> RefreshGatewayAsync(bool includePortMappings = true, CancellationToken cancellationToken = default)
        {
            const string operation = "Refresh gateway";
            if (!CanRun())
            {
                return Refuse(operation);
            }

            UpnpResult result = await _client.RefreshAsync(includePortMappings, cancellationToken);
            Publish(operation, result, "Gateway refreshed");
            return result;
        }

        /// <summary>Reads the port mapping table of the selected router.</summary>
        public async Task<UpnpResult<IReadOnlyList<PortMappingInfo>>> GetPortMappingsAsync(CancellationToken cancellationToken = default)
        {
            const string operation = "Get port mappings";
            if (!CanRun())
            {
                UpnpResult refused = Refuse(operation);
                return new UpnpResult<IReadOnlyList<PortMappingInfo>>(new PortMappingInfo[0], refused);
            }

            UpnpResult<IReadOnlyList<PortMappingInfo>> result = await _client.GetPortMappingsAsync(cancellationToken);
            Publish(operation, result.Result, result.Succeeded ? result.Value.Count + " port mapping(s)" : null);
            return result;
        }

        /// <summary>
        /// Opens a port on the router. <paramref name="internalPort"/> 0 means the same number as the external port.
        /// A lease of 0 seconds asks for a permanent mapping.
        /// </summary>
        public Task<UpnpResult> OpenPortAsync(UpnpProtocol protocol, int externalPort, int internalPort = 0, string description = null, int leaseSeconds = 0, CancellationToken cancellationToken = default)
        {
            PortMappingInfo mapping = new PortMappingInfo(
                protocol,
                externalPort,
                internalPort > 0 ? internalPort : externalPort,
                string.IsNullOrEmpty(description) ? _defaultDescription : description,
                leaseSeconds);
            return AddPortMappingAsync(mapping, cancellationToken);
        }

        /// <summary>Closes a port on the router.</summary>
        public Task<UpnpResult> ClosePortAsync(UpnpProtocol protocol, int externalPort, CancellationToken cancellationToken = default)
        {
            return DeletePortMappingAsync(new PortMappingInfo(protocol, externalPort, externalPort), cancellationToken);
        }

        /// <summary>Adds a port mapping on the selected router. When no router is known yet, the network is searched first.</summary>
        public async Task<UpnpResult> AddPortMappingAsync(PortMappingInfo mapping, CancellationToken cancellationToken = default)
        {
            const string operation = "Add port mapping";
            if (!CanRun())
            {
                return Refuse(operation);
            }

            UpnpResult result = await _client.AddPortMappingAsync(mapping, cancellationToken);
            if (result.Succeeded && mapping != null)
            {
                RememberOpened(mapping);
            }

            Publish(operation, result, mapping == null ? null : "Opened " + mapping);
            return result;
        }

        /// <summary>Deletes a port mapping (protocol and external port) on the selected router.</summary>
        public async Task<UpnpResult> DeletePortMappingAsync(PortMappingInfo mapping, CancellationToken cancellationToken = default)
        {
            const string operation = "Delete port mapping";
            if (!CanRun())
            {
                return Refuse(operation);
            }

            if (mapping == null)
            {
                UpnpResult invalid = new UpnpResult(UpnpErrorCode.InvalidArgument, "mapping is null.");
                Publish(operation, invalid, null);
                return invalid;
            }

            UpnpResult result = await _client.DeletePortMappingAsync(mapping, cancellationToken);
            if (result.Succeeded)
            {
                ForgetOpened(mapping);
            }

            Publish(operation, result, "Closed " + mapping.Protocol.ToWireString() + " port " + mapping.ExternalPort);
            return result;
        }

        /// <summary>Adds or deletes a mapping on every router of the network. The network is searched again first.</summary>
        public async Task<UpnpResult> PerformAllGatewaysAsync(UpnpAction action, PortMappingInfo mapping, CancellationToken cancellationToken = default)
        {
            const string operation = "Perform on all gateways";
            if (!CanRun())
            {
                return Refuse(operation);
            }

            UpnpResult result = await _client.PerformAllGatewaysAsync(action, mapping, cancellationToken);
            if (result.Succeeded && mapping != null)
            {
                if (action == UpnpAction.AddPort)
                {
                    RememberOpened(mapping);
                }
                else
                {
                    ForgetOpened(mapping);
                }
            }

            Publish(operation, result, result.Message);
            return result;
        }

        /// <summary>Opens every mapping of the <c>Startup Mappings</c> list. Succeeds when all of them were opened.</summary>
        public async Task<UpnpResult> OpenStartupMappingsAsync(CancellationToken cancellationToken = default)
        {
            List<PortMappingInfo> list = new List<PortMappingInfo>(startupMappings);
            int succeeded = 0;
            UpnpResult firstFailure = default(UpnpResult);
            bool failed = false;

            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] == null)
                {
                    continue;
                }

                UpnpResult result = await AddPortMappingAsync(list[i].Clone(), cancellationToken);
                if (result.Succeeded)
                {
                    succeeded++;
                }
                else
                {
                    if (!failed)
                    {
                        failed = true;
                        firstFailure = result;
                    }

                    if (IsFatalForTheList(result))
                    {
                        // Without a router every further mapping would search the network again and fail the same way.
                        break;
                    }
                }
            }

            return failed ? firstFailure : new UpnpResult(UpnpErrorCode.Success, succeeded + " mapping(s) opened.");
        }

        /// <summary>Closes every mapping of the <c>Startup Mappings</c> list.</summary>
        public async Task<UpnpResult> CloseStartupMappingsAsync(CancellationToken cancellationToken = default)
        {
            List<PortMappingInfo> list = new List<PortMappingInfo>(startupMappings);
            int succeeded = 0;
            UpnpResult firstFailure = default(UpnpResult);
            bool failed = false;

            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] == null)
                {
                    continue;
                }

                UpnpResult result = await DeletePortMappingAsync(list[i], cancellationToken);
                if (result.Succeeded)
                {
                    succeeded++;
                }
                else
                {
                    if (!failed)
                    {
                        failed = true;
                        firstFailure = result;
                    }

                    if (IsFatalForTheList(result))
                    {
                        break;
                    }
                }
            }

            return failed ? firstFailure : new UpnpResult(UpnpErrorCode.Success, succeeded + " mapping(s) closed.");
        }

        // ------------------------------------------------------------------------------------------------------
        // Bookkeeping
        // ------------------------------------------------------------------------------------------------------

        private bool CanRun()
        {
            return _client != null && _supported;
        }

        private UpnpResult Refuse(string operation)
        {
            UpnpResult refused = _client == null
                ? new UpnpResult(UpnpErrorCode.Canceled, "The UPNP Manager is not active.")
                : new UpnpResult(UpnpErrorCode.NotSupported, "UPnP is not available on this platform.");
            Publish(operation, refused, null);
            return refused;
        }

        private void RememberOpened(PortMappingInfo mapping)
        {
            lock (_stateLock)
            {
                _openedMappings.RemoveAll(m => SameMapping(m, mapping));
                _openedMappings.Add(mapping.Clone());
            }
        }

        private void ForgetOpened(PortMappingInfo mapping)
        {
            lock (_stateLock)
            {
                _openedMappings.RemoveAll(m => SameMapping(m, mapping));
            }
        }

        /// <summary>A router identifies a mapping by protocol, external port and remote host.</summary>
        private static bool SameMapping(PortMappingInfo a, PortMappingInfo b)
        {
            return a.Protocol == b.Protocol &&
                   a.ExternalPort == b.ExternalPort &&
                   string.Equals(a.RemoteHost ?? string.Empty, b.RemoteHost ?? string.Empty, StringComparison.Ordinal);
        }

        /// <summary>
        /// True when trying the next mapping is pointless because the problem is not about the mapping itself:
        /// no router was found, the network is unusable or the operation was canceled.
        /// </summary>
        private static bool IsFatalForTheList(UpnpResult result)
        {
            return result.ErrorCode == UpnpErrorCode.NoGatewayFound ||
                   result.ErrorCode == UpnpErrorCode.NetworkInitFailed ||
                   result.ErrorCode == UpnpErrorCode.GetDeviceListFailed ||
                   result.ErrorCode == UpnpErrorCode.NotSupported ||
                   result.ErrorCode == UpnpErrorCode.Canceled;
        }

        /// <summary>Stores the result and raises the events on the main thread.</summary>
        private void Publish(string operation, UpnpResult result, string successText)
        {
            lock (_stateLock)
            {
                _lastResult = result;
                _lastOperation = operation;
            }

            Action raise = () =>
            {
                if (_destroyed)
                {
                    // The component is gone (scene change or quit). Its listeners may be gone too.
                    return;
                }

                Action<string, UpnpResult> handler = OperationFinished;
                if (handler != null)
                {
                    // A subscriber that throws must neither skip the other subscribers nor break the awaiting code.
                    foreach (Delegate subscriber in handler.GetInvocationList())
                    {
                        try
                        {
                            ((Action<string, UpnpResult>)subscriber)(operation, result);
                        }
                        catch (Exception ex)
                        {
                            Debug.LogException(ex);
                        }
                    }
                }

                if (result.Succeeded)
                {
                    onSucceeded.Invoke(string.IsNullOrEmpty(successText) ? operation : successText);
                }
                else
                {
                    onFailed.Invoke(operation + ": " + result);
                }
            };

            if (_mainContext == null || SynchronizationContext.Current == _mainContext)
            {
                raise();
            }
            else
            {
                _mainContext.Post(state => raise(), null);
            }
        }
    }
}