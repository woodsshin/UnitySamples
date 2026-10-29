// Copyright 2017-present WoodsShin, Inc. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace WoodsShin.SimpleUPNP
{
    /// <summary>
    /// One Internet Gateway Device (router) on the local network. Instances are created by
    /// <see cref="UpnpClient"/>. All methods are safe to call from any thread, requests to the same router run one
    /// after another, and no method throws: problems are reported through <see cref="UpnpResult"/>.
    /// </summary>
    public sealed partial class UpnpGateway
    {
        private readonly UpnpClientOptions _options;
        private readonly UpnpDeviceDescription _description;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly object _stateLock = new object();
        private readonly string _localAddress;

        private UpnpService _wan;
        private UpnpResult _lastResult = UpnpResult.Ok;

        private volatile ManufacturerInfo _manufacturer;
        private volatile DeviceStatusInfo _status;
        private volatile CommonLinkProperties _linkProperties;
        private volatile IReadOnlyList<PortMappingInfo> _portMappings = new ReadOnlyCollection<PortMappingInfo>(new List<PortMappingInfo>());

        internal UpnpGateway(Uri location, SsdpDeviceInfo ssdp, UpnpDeviceDescription description, UpnpClientOptions options)
        {
            Location = location;
            Ssdp = ssdp;
            _description = description;
            _options = options;
            _manufacturer = description.Manufacturer;

            IPAddress local = UpnpNetwork.GetLocalAddressFor(location.DnsSafeHost);
            _localAddress = local == null ? null : local.ToString();

            _linkProperties = new CommonLinkProperties();
            _status = new DeviceStatusInfo
            {
                DeviceLocation = location.ToString(),
                LocalIPAddress = _localAddress ?? UpnpConstants.NotAvailable,
            };
        }

        /// <summary>URL of the device description. It identifies the gateway.</summary>
        public Uri Location { get; }

        /// <summary>Header values of the SSDP answer that revealed this gateway.</summary>
        public SsdpDeviceInfo Ssdp { get; }

        /// <summary>Manufacturer information (available right after discovery).</summary>
        public ManufacturerInfo Manufacturer
        {
            get { return _manufacturer; }
        }

        /// <summary>Connection status. Filled by <see cref="RefreshAsync"/>.</summary>
        public DeviceStatusInfo Status
        {
            get { return _status; }
        }

        /// <summary>Link properties. Filled by <see cref="RefreshAsync"/>.</summary>
        public CommonLinkProperties LinkProperties
        {
            get { return _linkProperties; }
        }

        /// <summary>The port mappings read by the last <see cref="RefreshAsync"/> or <see cref="GetPortMappingsAsync"/>.</summary>
        public IReadOnlyList<PortMappingInfo> PortMappings
        {
            get { return _portMappings; }
        }

        /// <summary>Address of this computer on the network of the gateway, or "N/A".</summary>
        public string LocalIPAddress
        {
            get { return _status.LocalIPAddress; }
        }

        /// <summary>Public address of the router, or "N/A" until <see cref="RefreshAsync"/> succeeded.</summary>
        public string ExternalIPAddress
        {
            get { return _status.ExternalIPAddress; }
        }

        /// <summary>
        /// True when <see cref="ExternalIPAddress"/> is a public internet address. False when it is private, shared
        /// (carrier grade NAT) or otherwise not routable: the router then sits behind another router or behind the
        /// network of the provider, and players on the internet cannot reach a port that you open on it.
        /// Null while the address is unknown.
        /// </summary>
        public bool? HasPublicExternalAddress
        {
            get
            {
                IPAddress address;
                return IPAddress.TryParse(ExternalIPAddress, out address) ? UpnpNetwork.IsPublicAddress(address) : (bool?)null;
            }
        }

        /// <summary>The friendly name of the router, or its address when it has none.</summary>
        public string DisplayName
        {
            get
            {
                string name = _manufacturer.FriendlyName;
                return string.IsNullOrEmpty(name) || name == UpnpConstants.NotAvailable ? Location.Authority : name;
            }
        }

        /// <summary>The result of the most recent operation on this gateway.</summary>
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

        public override string ToString()
        {
            return DisplayName + " (" + Location + ")";
        }

        /// <summary>
        /// Downloads and parses the device description. Returns a failed result when the device cannot be reached or
        /// is not an Internet Gateway Device. Throws <see cref="OperationCanceledException"/> when canceled.
        /// </summary>
        internal static async Task<UpnpResult<UpnpGateway>> CreateAsync(Uri location, SsdpDeviceInfo ssdp, UpnpClientOptions options, CancellationToken cancellationToken)
        {
            List<KeyValuePair<string, string>> headers = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("User-Agent", options.UserAgent),
                new KeyValuePair<string, string>("Accept", "text/xml, application/xml, */*"),
            };

            HttpReply reply = null;
            string failure = "no answer";
            bool timedOut = false;
            int attempts = Math.Max(0, options.MaxRetries) + 1;

            for (int attempt = 1; attempt <= attempts && reply == null; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    reply = await MiniHttp.SendAsync(location, "GET", headers, null, options.RequestTimeout, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (TimeoutException ex)
                {
                    failure = ex.Message;
                    timedOut = true;
                }
                catch (Exception ex)
                {
                    failure = ex.GetType().Name + ": " + ex.Message;
                    timedOut = false;
                }

                if (reply == null && attempt < attempts)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), cancellationToken).ConfigureAwait(false);
                }
            }

            if (reply == null)
            {
                return Failed<UpnpGateway>(timedOut ? UpnpErrorCode.RequestTimeout : UpnpErrorCode.RequestFailed,
                    "Could not read the device description from " + location + ": " + failure);
            }

            if (reply.StatusCode != 200)
            {
                return Failed<UpnpGateway>(UpnpErrorCode.RequestFailed,
                    "The device description at " + location + " returned HTTP " + reply.StatusCode + ".");
            }

            UpnpDeviceDescription description = UpnpDeviceDescription.Parse(reply.BodyText, location);
            if (!description.IsGateway)
            {
                return Failed<UpnpGateway>(UpnpErrorCode.NoGatewayFound,
                    "The device at " + location + " is not an Internet Gateway Device (no WANIPConnection or WANPPPConnection service).");
            }

            SsdpDeviceInfo info = ssdp ?? new SsdpDeviceInfo { Location = location.ToString() };
            return new UpnpResult<UpnpGateway>(new UpnpGateway(location, info, description, options), UpnpResult.Ok);
        }

        // ------------------------------------------------------------------------------------------------------
        // Refresh
        // ------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Reads the connection status, public address, connection type, link properties and (optionally) the
        /// port mapping table from the router.
        /// </summary>
        /// <param name="includePortMappings">Also read the port mapping table.</param>
        public async Task<UpnpResult> RefreshAsync(bool includePortMappings = true, CancellationToken cancellationToken = default)
        {
            try
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    return Remember(await RefreshCoreAsync(includePortMappings, cancellationToken).ConfigureAwait(false));
                }
                finally
                {
                    _gate.Release();
                }
            }
            catch (OperationCanceledException)
            {
                return Remember(new UpnpResult(UpnpErrorCode.Canceled, "The operation was canceled."));
            }
        }

        private async Task<UpnpResult> RefreshCoreAsync(bool includePortMappings, CancellationToken ct)
        {
            await EnsureWanServiceAsync(ct).ConfigureAwait(false);

            DeviceStatusInfo status = new DeviceStatusInfo
            {
                DeviceLocation = Location.ToString(),
                LocalIPAddress = _localAddress ?? UpnpConstants.NotAvailable,
            };
            CommonLinkProperties link = new CommonLinkProperties();

            int answered = 0;
            UpnpResult firstFailure = default(UpnpResult);
            bool anyFailure = false;

            // When the router does not answer at all there is no point in waiting for the remaining requests.
            bool unreachable = false;

            SoapResponse r = await SoapClient.CallAsync(_wan, "GetStatusInfo", null, _options, ct).ConfigureAwait(false);
            if (r.Succeeded)
            {
                answered++;
                status.ConnectionStatus = Pick(r.Get("NewConnectionStatus"));
                status.LastConnectionError = Pick(r.Get("NewLastConnectionError"));
                int uptime;
                if (int.TryParse(r.Get("NewUptime"), NumberStyles.Integer, CultureInfo.InvariantCulture, out uptime))
                {
                    status.Uptime = uptime;
                }
            }
            else
            {
                Note(ref anyFailure, ref firstFailure, Failure(r, UpnpErrorCode.GetStatusFailed, "GetStatusInfo"));
                unreachable = r.TransportFailed;
            }

            if (!unreachable)
            {
                r = await SoapClient.CallAsync(_wan, "GetExternalIPAddress", null, _options, ct).ConfigureAwait(false);
                if (r.Succeeded)
                {
                    answered++;
                    status.ExternalIPAddress = Pick(r.Get("NewExternalIPAddress"));
                }
                else
                {
                    Note(ref anyFailure, ref firstFailure, Failure(r, UpnpErrorCode.GetExternalIpFailed, "GetExternalIPAddress"));
                    unreachable = r.TransportFailed;
                }
            }

            if (!unreachable)
            {
                r = await SoapClient.CallAsync(_wan, "GetConnectionTypeInfo", null, _options, ct).ConfigureAwait(false);
                if (r.Succeeded)
                {
                    answered++;
                    status.ConnectionType = Pick(r.Get("NewConnectionType"));
                    status.PossibleConnectionTypes = Pick(r.Get("NewPossibleConnectionTypes"));
                }
                else
                {
                    Note(ref anyFailure, ref firstFailure, Failure(r, UpnpErrorCode.GetConnectionInfoFailed, "GetConnectionTypeInfo"));
                    unreachable = r.TransportFailed;
                }
            }

            if (!unreachable && _description.CommonInterfaceConfig != null)
            {
                r = await SoapClient.CallAsync(_description.CommonInterfaceConfig, "GetCommonLinkProperties", null, _options, ct).ConfigureAwait(false);
                if (r.Succeeded)
                {
                    answered++;
                    link.WanAccessType = Pick(r.Get("NewWANAccessType"));
                    link.Layer1UpstreamMaxBitRate = Pick(r.Get("NewLayer1UpstreamMaxBitRate"));
                    link.Layer1DownstreamMaxBitRate = Pick(r.Get("NewLayer1DownstreamMaxBitRate"));
                    link.PhysicalLinkStatus = Pick(r.Get("NewPhysicalLinkStatus"));
                }
                else
                {
                    Note(ref anyFailure, ref firstFailure, Failure(r, UpnpErrorCode.GetCommonLinkPropertiesFailed, "GetCommonLinkProperties"));
                    unreachable = r.TransportFailed;
                }
            }

            _status = status;
            _linkProperties = link;

            if (unreachable || answered == 0)
            {
                return anyFailure ? firstFailure : new UpnpResult(UpnpErrorCode.RequestFailed, "The router did not answer.");
            }

            if (includePortMappings)
            {
                UpnpResult<List<PortMappingInfo>> list = await ReadPortMappingsCoreAsync(ct).ConfigureAwait(false);
                if (!list.Succeeded)
                {
                    return list.Result;
                }
            }

            return UpnpResult.Ok;
        }

        private static void Note(ref bool anyFailure, ref UpnpResult firstFailure, UpnpResult failure)
        {
            if (!anyFailure)
            {
                anyFailure = true;
                firstFailure = failure;
            }
        }

        private static string Pick(string value)
        {
            return string.IsNullOrEmpty(value) ? UpnpConstants.NotAvailable : value;
        }

        // ------------------------------------------------------------------------------------------------------
        // Port mapping table
        // ------------------------------------------------------------------------------------------------------

        /// <summary>Reads the whole port mapping table of the router.</summary>
        public async Task<UpnpResult<IReadOnlyList<PortMappingInfo>>> GetPortMappingsAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await EnsureWanServiceAsync(cancellationToken).ConfigureAwait(false);
                    UpnpResult<List<PortMappingInfo>> list = await ReadPortMappingsCoreAsync(cancellationToken).ConfigureAwait(false);
                    Remember(list.Result);
                    return new UpnpResult<IReadOnlyList<PortMappingInfo>>(
                        list.Succeeded ? _portMappings : new List<PortMappingInfo>(), list.Result);
                }
                finally
                {
                    _gate.Release();
                }
            }
            catch (OperationCanceledException)
            {
                UpnpResult canceled = Remember(new UpnpResult(UpnpErrorCode.Canceled, "The operation was canceled."));
                return new UpnpResult<IReadOnlyList<PortMappingInfo>>(new List<PortMappingInfo>(), canceled);
            }
        }

        private async Task<UpnpResult<List<PortMappingInfo>>> ReadPortMappingsCoreAsync(CancellationToken ct)
        {
            List<PortMappingInfo> mappings = new List<PortMappingInfo>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            int max = Math.Max(1, _options.MaxPortMappingEntries);

            for (int index = 0; index < max; index++)
            {
                SoapResponse r = await SoapClient.CallAsync(
                    _wan,
                    "GetGenericPortMappingEntry",
                    new[] { new KeyValuePair<string, string>("NewPortMappingIndex", index.ToString(CultureInfo.InvariantCulture)) },
                    _options,
                    ct).ConfigureAwait(false);

                if (!r.Succeeded)
                {
                    // 713 means "no entry at this index", which is how a router ends the list.
                    bool endOfList = r.UpnpErrorCode == 713 || (index > 0 && !r.TransportFailed);
                    if (endOfList)
                    {
                        break;
                    }

                    if (index > 0)
                    {
                        // A transport problem in the middle of the table: keep what was read.
                        Log(UpnpLogLevel.Warning, "Port mapping list is incomplete: " + r.ErrorMessage);
                        break;
                    }

                    UpnpResult failure = Failure(r, UpnpErrorCode.GetPortMappingEntryFailed, "GetGenericPortMappingEntry");
                    return new UpnpResult<List<PortMappingInfo>>(mappings, failure);
                }

                PortMappingInfo mapping = ParseMapping(r, null, null);
                if (mapping == null)
                {
                    break;
                }

                // Some routers ignore the index and repeat the first entry forever.
                string key = mapping.Protocol + ":" + mapping.ExternalPort + ":" + mapping.RemoteHost;
                if (!seen.Add(key))
                {
                    break;
                }

                mappings.Add(mapping);
            }

            _portMappings = new ReadOnlyCollection<PortMappingInfo>(mappings);
            return new UpnpResult<List<PortMappingInfo>>(mappings, UpnpResult.Ok);
        }

        /// <summary>Builds a mapping from the output arguments of GetGenericPortMappingEntry / GetSpecificPortMappingEntry.</summary>
        private static PortMappingInfo ParseMapping(SoapResponse r, UpnpProtocol? protocol, int? externalPort)
        {
            PortMappingInfo mapping = new PortMappingInfo();

            UpnpProtocol parsedProtocol;
            if (protocol.HasValue)
            {
                mapping.Protocol = protocol.Value;
            }
            else if (UpnpProtocolExtensions.TryParse(r.Get("NewProtocol"), out parsedProtocol))
            {
                mapping.Protocol = parsedProtocol;
            }
            else
            {
                return null;
            }

            int port;
            if (externalPort.HasValue)
            {
                mapping.ExternalPort = externalPort.Value;
            }
            else if (int.TryParse(r.Get("NewExternalPort"), NumberStyles.Integer, CultureInfo.InvariantCulture, out port))
            {
                mapping.ExternalPort = port;
            }
            else
            {
                return null;
            }

            if (int.TryParse(r.Get("NewInternalPort"), NumberStyles.Integer, CultureInfo.InvariantCulture, out port))
            {
                mapping.InternalPort = port;
            }

            int lease;
            if (int.TryParse(r.Get("NewLeaseDuration"), NumberStyles.Integer, CultureInfo.InvariantCulture, out lease))
            {
                mapping.LeaseDuration = lease;
            }

            mapping.InternalClient = r.Get("NewInternalClient") ?? string.Empty;
            mapping.RemoteHost = r.Get("NewRemoteHost") ?? string.Empty;
            mapping.Description = r.Get("NewPortMappingDescription") ?? string.Empty;

            string enabled = r.Get("NewEnabled");
            mapping.Enabled = enabled == "1" || string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase);
            return mapping;
        }

        // ------------------------------------------------------------------------------------------------------
        // Helpers shared with the second part of this class
        // ------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Picks the WAN connection service to use. A router can offer WANIPConnection and WANPPPConnection at the
        /// same time; the one that reports "Connected" is the one that carries the internet traffic.
        /// </summary>
        private async Task EnsureWanServiceAsync(CancellationToken ct)
        {
            if (_wan != null)
            {
                return;
            }

            List<UpnpService> candidates = _description.WanConnectionServices;
            if (candidates.Count == 1)
            {
                _wan = candidates[0];
                return;
            }

            UpnpService firstAnswering = null;
            for (int i = 0; i < candidates.Count; i++)
            {
                SoapResponse r = await SoapClient.CallAsync(candidates[i], "GetStatusInfo", null, _options, ct).ConfigureAwait(false);
                if (!r.Succeeded)
                {
                    continue;
                }

                if (firstAnswering == null)
                {
                    firstAnswering = candidates[i];
                }

                if (string.Equals(r.Get("NewConnectionStatus"), "Connected", StringComparison.OrdinalIgnoreCase))
                {
                    _wan = candidates[i];
                    return;
                }
            }

            _wan = firstAnswering ?? candidates[0];
        }

        /// <summary>The address to forward to when the caller gave none.</summary>
        private string DefaultInternalClient()
        {
            return _localAddress;
        }

        private UpnpResult Remember(UpnpResult result)
        {
            lock (_stateLock)
            {
                _lastResult = result;
            }

            return result;
        }

        private static UpnpResult<T> Failed<T>(UpnpErrorCode code, string message)
        {
            return new UpnpResult<T>(default(T), new UpnpResult(code, message));
        }

        /// <summary>Converts a failed SOAP call into a result.</summary>
        private UpnpResult Failure(SoapResponse r, UpnpErrorCode fallback, string action)
        {
            if (r.TransportFailed)
            {
                UpnpErrorCode code = r.TimedOut ? UpnpErrorCode.RequestTimeout : UpnpErrorCode.RequestFailed;
                return new UpnpResult(code, action + " failed: " + r.ErrorMessage);
            }

            string explanation = UpnpErrors.Describe(r.UpnpErrorCode);
            string text = action + " failed";
            if (explanation.Length > 0)
            {
                text += ": " + explanation;
            }
            else if (r.UpnpErrorDescription.Length > 0)
            {
                text += ": " + r.UpnpErrorDescription;
            }
            else if (r.ErrorMessage.Length > 0)
            {
                text += ": " + r.ErrorMessage;
            }

            return new UpnpResult(fallback, text, r.UpnpErrorCode, r.UpnpErrorDescription);
        }

        private void Log(UpnpLogLevel level, string message)
        {
            Action<UpnpLogLevel, string> logger = _options.Logger;
            if (logger != null)
            {
                logger(level, message);
            }
        }
    }
}
