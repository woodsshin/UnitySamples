// Copyright 2017-present WoodsShin, Inc. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Net;

namespace WoodsShin.SimpleUPNP
{
    /// <summary>Result codes reported by every UPNP operation.</summary>
    public enum UpnpErrorCode
    {
        /// <summary>The operation succeeded.</summary>
        Success = 0,
        /// <summary>An unexpected error happened.</summary>
        Unknown,
        /// <summary>The network (sockets / interfaces) could not be initialised.</summary>
        NetworkInitFailed,
        /// <summary>The gateway did not answer or sent a document that could not be used.</summary>
        RequestFailed,
        /// <summary>GetStatusInfo failed.</summary>
        GetStatusFailed,
        /// <summary>No Internet Gateway Device (router) was found on the local network.</summary>
        NoGatewayFound,
        /// <summary>GetExternalIPAddress failed.</summary>
        GetExternalIpFailed,
        /// <summary>GetConnectionTypeInfo failed.</summary>
        GetConnectionInfoFailed,
        /// <summary>GetCommonLinkProperties failed.</summary>
        GetCommonLinkPropertiesFailed,
        /// <summary>GetGenericPortMappingEntry / GetSpecificPortMappingEntry failed.</summary>
        GetPortMappingEntryFailed,
        /// <summary>The SSDP search failed.</summary>
        GetDeviceListFailed,
        /// <summary>AddPortMapping failed.</summary>
        AddMappingFailed,
        /// <summary>DeletePortMapping failed.</summary>
        DeleteMappingFailed,
        /// <summary>A request timed out.</summary>
        RequestTimeout,
        /// <summary>The operation was canceled.</summary>
        Canceled,
        /// <summary>The current platform cannot use UPnP (for example WebGL).</summary>
        NotSupported,
        /// <summary>An argument was out of range or missing.</summary>
        InvalidArgument,
    }

    /// <summary>Transport protocol of a port mapping.</summary>
    public enum UpnpProtocol
    {
        Tcp = 0,
        Udp = 1,
    }

    /// <summary>Action that <c>UpnpClient.PerformAllGatewaysAsync</c> applies to every gateway.</summary>
    public enum UpnpAction
    {
        AddPort = 0,
        DeletePort = 1,
    }

    /// <summary>Severity of a message sent to <see cref="UpnpClientOptions.Logger"/>.</summary>
    public enum UpnpLogLevel
    {
        Verbose = 0,
        Info = 1,
        Warning = 2,
        Error = 3,
    }

    /// <summary>Helpers to convert <see cref="UpnpProtocol"/> from and to the strings used on the wire.</summary>
    public static class UpnpProtocolExtensions
    {
        /// <summary>Returns "TCP" or "UDP".</summary>
        public static string ToWireString(this UpnpProtocol protocol)
        {
            return protocol == UpnpProtocol.Tcp ? "TCP" : "UDP";
        }

        /// <summary>Parses "TCP" / "UDP" (case-insensitive).</summary>
        public static bool TryParse(string text, out UpnpProtocol protocol)
        {
            protocol = UpnpProtocol.Udp;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            string trimmed = text.Trim();
            if (string.Equals(trimmed, "TCP", StringComparison.OrdinalIgnoreCase))
            {
                protocol = UpnpProtocol.Tcp;
                return true;
            }

            if (string.Equals(trimmed, "UDP", StringComparison.OrdinalIgnoreCase))
            {
                protocol = UpnpProtocol.Udp;
                return true;
            }

            return false;
        }
    }

    /// <summary>Outcome of an operation that returns no value.</summary>
    public readonly struct UpnpResult
    {
        private readonly string _message;
        private readonly string _faultDescription;

        public UpnpResult(UpnpErrorCode errorCode, string message = null, int upnpFaultCode = 0, string upnpFaultDescription = null)
        {
            ErrorCode = errorCode;
            _message = message;
            UpnpFaultCode = upnpFaultCode;
            _faultDescription = upnpFaultDescription;
        }

        /// <summary>True when <see cref="ErrorCode"/> is <see cref="UpnpErrorCode.Success"/>.</summary>
        public bool Succeeded
        {
            get { return ErrorCode == UpnpErrorCode.Success; }
        }

        /// <summary>The plugin level result code.</summary>
        public UpnpErrorCode ErrorCode { get; }

        /// <summary>Human readable details (may be empty, never null).</summary>
        public string Message
        {
            get { return _message ?? string.Empty; }
        }

        /// <summary>The UPnP error code returned by the router (for example 718 or 725), or 0 if there was none.</summary>
        public int UpnpFaultCode { get; }

        /// <summary>The UPnP error description returned by the router (may be empty, never null).</summary>
        public string UpnpFaultDescription
        {
            get { return _faultDescription ?? string.Empty; }
        }

        public static UpnpResult Ok
        {
            get { return new UpnpResult(UpnpErrorCode.Success); }
        }

        public static UpnpResult Fail(UpnpErrorCode errorCode, string message = null)
        {
            return new UpnpResult(errorCode, message);
        }

        public override string ToString()
        {
            string text = ErrorCode.ToString();
            if (UpnpFaultCode != 0)
            {
                text += " (UPnP error " + UpnpFaultCode + (UpnpFaultDescription.Length > 0 ? ": " + UpnpFaultDescription : string.Empty) + ")";
            }

            if (Message.Length > 0)
            {
                text += " - " + Message;
            }

            return text;
        }
    }

    /// <summary>Outcome of an operation that returns a value.</summary>
    public readonly struct UpnpResult<T>
    {
        public UpnpResult(T value, UpnpResult result)
        {
            Value = value;
            Result = result;
        }

        /// <summary>The returned value. Only meaningful when <see cref="Succeeded"/> is true.</summary>
        public T Value { get; }

        /// <summary>The plain result without the value.</summary>
        public UpnpResult Result { get; }

        public bool Succeeded
        {
            get { return Result.Succeeded; }
        }

        public UpnpErrorCode ErrorCode
        {
            get { return Result.ErrorCode; }
        }

        public string Message
        {
            get { return Result.Message; }
        }

        public override string ToString()
        {
            return Result.ToString();
        }
    }

    /// <summary>Settings of an <c>UpnpClient</c>. Every value has a sensible default.</summary>
    public sealed class UpnpClientOptions
    {
        /// <summary>Timeout of a single HTTP request to the router. Default 5 seconds.</summary>
        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>How long the SSDP search listens for answers. Default 3 seconds.</summary>
        public TimeSpan DiscoveryTimeout { get; set; } = TimeSpan.FromSeconds(3);

        /// <summary>After the first gateway answered, wait this long for further gateways. Default 600 ms.</summary>
        public TimeSpan DiscoveryGracePeriod { get; set; } = TimeSpan.FromMilliseconds(600);

        /// <summary>How often a request is repeated after a timeout or connection error. Default 2.</summary>
        public int MaxRetries { get; set; } = 2;

        /// <summary>Upper bound of port mapping entries that are read from a router. Default 256.</summary>
        public int MaxPortMappingEntries { get; set; } = 256;

        /// <summary>
        /// Many routers only accept permanent mappings (UPnP error 725). When true (default) a request with a lease
        /// time is repeated with lease 0 after such an error. Permanent mappings stay on the router until they are
        /// deleted, so remove them when your game quits.
        /// </summary>
        public bool FallbackToPermanentLease { get; set; } = true;

        /// <summary>Multicast group used for SSDP. Default 239.255.255.250:1900.</summary>
        public IPEndPoint SsdpEndPoint { get; set; } = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);

        /// <summary>Time to live of the SSDP multicast packets. Default 2.</summary>
        public int MulticastTimeToLive { get; set; } = 2;

        /// <summary>Also search on loopback interfaces. Only useful for tests.</summary>
        public bool IncludeLoopbackInterfaces { get; set; }

        /// <summary>
        /// When false (default) a gateway is only accepted when its address is private, link-local or loopback.
        /// This keeps forged SSDP answers from pointing the client at hosts on the internet.
        /// </summary>
        public bool AllowPublicGatewayAddresses { get; set; }

        /// <summary>Value of the User-Agent / USER-AGENT header.</summary>
        public string UserAgent { get; set; } = "SimpleUPNP/1.0 UPnP/1.1";

        /// <summary>SSDP search targets (ST header). The defaults cover IGD v1 and v2.</summary>
        public IList<string> SearchTargets { get; set; } = new List<string>
        {
            "urn:schemas-upnp-org:device:InternetGatewayDevice:1",
            "urn:schemas-upnp-org:device:InternetGatewayDevice:2",
            "urn:schemas-upnp-org:service:WANIPConnection:1",
            "urn:schemas-upnp-org:service:WANIPConnection:2",
            "urn:schemas-upnp-org:service:WANPPPConnection:1",
            "upnp:rootdevice",
        };

        /// <summary>Optional log sink. The Unity layer connects it to <c>Debug.Log</c>.</summary>
        public Action<UpnpLogLevel, string> Logger { get; set; }
    }
}
