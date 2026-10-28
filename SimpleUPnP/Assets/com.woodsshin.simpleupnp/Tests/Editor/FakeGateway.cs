// Copyright 2017-present WoodsShin, Inc. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WoodsShin.SimpleUPNP.Tests
{
    /// <summary>
    /// A small Internet Gateway Device on the loopback interface. It serves a device description and answers the
    /// SOAP actions of WANIPConnection and WANCommonInterfaceConfig, so the tests run without a real router.
    /// </summary>
    internal sealed class FakeGateway : IDisposable
    {
        private const string IpConnectionType = "urn:schemas-upnp-org:service:WANIPConnection:1";
        private const string PppConnectionType = "urn:schemas-upnp-org:service:WANPPPConnection:1";
        private const string CommonConfigType = "urn:schemas-upnp-org:service:WANCommonInterfaceConfig:1";

        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly object _lock = new object();
        private readonly List<PortMappingInfo> _mappings = new List<PortMappingInfo>();
        private readonly List<string> _actions = new List<string>();

        /// <summary>Serve a device without WAN services, like a TV or a printer would.</summary>
        public bool IsGateway = true;

        /// <summary>Reject leases other than 0 with UPnP error 725.</summary>
        public bool OnlyPermanentLeases;

        /// <summary>Reject every AddPortMapping with UPnP error 718.</summary>
        public bool ConflictOnAdd;

        /// <summary>Send SOAP answers with chunked transfer encoding.</summary>
        public bool Chunked;

        /// <summary>Accept SOAP requests but never answer.</summary>
        public bool SilentSoap;

        /// <summary>Answer GetGenericPortMappingEntry with error 401.</summary>
        public bool DisableGenericEntries;

        /// <summary>Ignore the index of GetGenericPortMappingEntry and always return the first entry.</summary>
        public bool RepeatFirstEntry;

        /// <summary>Let the control URL of the WAN service point to another host.</summary>
        public string ForeignControlHost;

        /// <summary>Also offer a WANPPPConnection service (listed after the WANIPConnection service).</summary>
        public bool AddPppService;

        /// <summary>ConnectionStatus reported by the WANIPConnection service.</summary>
        public string IpStatus = "Connected";

        /// <summary>ConnectionStatus reported by the WANPPPConnection service.</summary>
        public string PppStatus = "Connected";

        public string FriendlyName = "Fake Router & Co";
        public string ExternalIp = "203.0.113.7";
        public string PppExternalIp = "198.51.100.99";
        public int Uptime = 4242;

        public FakeGateway()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Task.Run(AcceptLoopAsync);
        }

        public int Port { get; }

        public Uri Location
        {
            get { return new Uri("http://127.0.0.1:" + Port + "/rootDesc.xml"); }
        }

        /// <summary>A copy of the current port mapping table.</summary>
        public List<PortMappingInfo> Mappings
        {
            get
            {
                lock (_lock)
                {
                    List<PortMappingInfo> copy = new List<PortMappingInfo>();
                    foreach (PortMappingInfo m in _mappings)
                    {
                        copy.Add(m.Clone());
                    }

                    return copy;
                }
            }
        }

        /// <summary>The names of the SOAP actions received so far.</summary>
        public List<string> Actions
        {
            get
            {
                lock (_lock)
                {
                    return new List<string>(_actions);
                }
            }
        }

        public void Preload(PortMappingInfo mapping)
        {
            lock (_lock)
            {
                _mappings.Add(mapping.Clone());
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            try
            {
                _listener.Stop();
            }
            catch (Exception)
            {
                // Already stopped.
            }
        }

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    return;
                }

                Task ignored = Task.Run(() => HandleAsync(client));
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    NetworkStream stream = client.GetStream();
                    byte[] buffer = new byte[16384];
                    int filled = 0;
                    int headEnd = -1;

                    while (headEnd < 0)
                    {
                        int read = await stream.ReadAsync(buffer, filled, buffer.Length - filled, _stop.Token).ConfigureAwait(false);
                        if (read <= 0)
                        {
                            return;
                        }

                        filled += read;
                        headEnd = FindHeaderEnd(buffer, filled);
                        if (headEnd < 0 && filled == buffer.Length)
                        {
                            return;
                        }
                    }

                    string head = Encoding.ASCII.GetString(buffer, 0, headEnd);
                    string[] lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
                    string[] requestLine = lines[0].Split(' ');
                    Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 1; i < lines.Length; i++)
                    {
                        int colon = lines[i].IndexOf(':');
                        if (colon > 0)
                        {
                            headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
                        }
                    }

                    int contentLength = 0;
                    string lengthText;
                    if (headers.TryGetValue("Content-Length", out lengthText))
                    {
                        int.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out contentLength);
                    }

                    int bodyStart = headEnd + 4;
                    byte[] body = new byte[contentLength];
                    int have = Math.Min(contentLength, filled - bodyStart);
                    if (have > 0)
                    {
                        Buffer.BlockCopy(buffer, bodyStart, body, 0, have);
                    }

                    while (have < contentLength)
                    {
                        int read = await stream.ReadAsync(body, have, contentLength - have, _stop.Token).ConfigureAwait(false);
                        if (read <= 0)
                        {
                            return;
                        }

                        have += read;
                    }

                    await RouteAsync(stream, requestLine[0], requestLine.Length > 1 ? requestLine[1] : "/", headers, Encoding.UTF8.GetString(body)).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // The client went away or the fake was disposed.
                }
            }
        }

        private static int FindHeaderEnd(byte[] buffer, int length)
        {
            for (int i = 0; i + 3 < length; i++)
            {
                if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
                {
                    return i;
                }
            }

            return -1;
        }

        private async Task RouteAsync(Stream stream, string method, string path, Dictionary<string, string> headers, string body)
        {
            if (method == "GET" && path == "/rootDesc.xml")
            {
                await WriteAsync(stream, 200, "OK", DeviceDescription(), false).ConfigureAwait(false);
                return;
            }

            if (method == "POST" && (path == "/ctl/IPConn" || path == "/ctl/PPPConn" || path == "/ctl/CmnIfCfg"))
            {
                string soapAction;
                headers.TryGetValue("SOAPAction", out soapAction);
                soapAction = (soapAction ?? string.Empty).Trim('"');
                int hash = soapAction.IndexOf('#');
                string action = hash >= 0 ? soapAction.Substring(hash + 1) : string.Empty;

                bool ppp = path == "/ctl/PPPConn";
                lock (_lock)
                {
                    _actions.Add(ppp ? "PPP:" + action : action);
                }

                if (SilentSoap)
                {
                    // The request was received and counted, but the router never answers.
                    await Task.Delay(Timeout.Infinite, _stop.Token).ConfigureAwait(false);
                    return;
                }

                XmlNodeLite root = LiteXml.Parse(body);
                XmlNodeLite actionNode = root.Descendant(action);
                Dictionary<string, string> args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (actionNode != null)
                {
                    foreach (XmlNodeLite child in actionNode.Children)
                    {
                        args[child.Name] = child.Text;
                    }
                }

                Tuple<int, string> answer = path == "/ctl/CmnIfCfg" ? HandleCommonConfig(action) : HandleIpConnection(action, args, ppp);
                await WriteAsync(stream, answer.Item1, answer.Item1 == 200 ? "OK" : "Internal Server Error", answer.Item2, Chunked).ConfigureAwait(false);
                return;
            }

            await WriteAsync(stream, 404, "Not Found", "<html>not found</html>", false).ConfigureAwait(false);
        }

        private async Task WriteAsync(Stream stream, int status, string reason, string body, bool chunked)
        {
            byte[] data = Encoding.UTF8.GetBytes(body);
            StringBuilder head = new StringBuilder();
            head.Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n");
            head.Append("Content-Type: text/xml; charset=\"utf-8\"\r\n");
            head.Append("Server: FakeOS/1.0 UPnP/1.1 FakeIGD/1.0\r\n");
            head.Append("Connection: close\r\n");

            if (chunked)
            {
                head.Append("Transfer-Encoding: chunked\r\n\r\n");
                byte[] headBytes = Encoding.ASCII.GetBytes(head.ToString());
                await stream.WriteAsync(headBytes, 0, headBytes.Length).ConfigureAwait(false);

                int split = data.Length / 2;
                await WriteChunkAsync(stream, data, 0, split).ConfigureAwait(false);
                await WriteChunkAsync(stream, data, split, data.Length - split).ConfigureAwait(false);
                byte[] end = Encoding.ASCII.GetBytes("0\r\n\r\n");
                await stream.WriteAsync(end, 0, end.Length).ConfigureAwait(false);
            }
            else
            {
                head.Append("Content-Length: ").Append(data.Length).Append("\r\n\r\n");
                byte[] headBytes = Encoding.ASCII.GetBytes(head.ToString());
                await stream.WriteAsync(headBytes, 0, headBytes.Length).ConfigureAwait(false);
                await stream.WriteAsync(data, 0, data.Length).ConfigureAwait(false);
            }

            await stream.FlushAsync().ConfigureAwait(false);
        }

        private static async Task WriteChunkAsync(Stream stream, byte[] data, int offset, int count)
        {
            if (count <= 0)
            {
                return;
            }

            byte[] size = Encoding.ASCII.GetBytes(count.ToString("X", CultureInfo.InvariantCulture) + "\r\n");
            await stream.WriteAsync(size, 0, size.Length).ConfigureAwait(false);
            await stream.WriteAsync(data, offset, count).ConfigureAwait(false);
            byte[] crlf = Encoding.ASCII.GetBytes("\r\n");
            await stream.WriteAsync(crlf, 0, crlf.Length).ConfigureAwait(false);
        }

        // ------------------------------------------------------------------------------------------------------
        // Documents
        // ------------------------------------------------------------------------------------------------------

        private string DeviceDescription()
        {
            string controlHost = ForeignControlHost == null ? string.Empty : "http://" + ForeignControlHost;
            StringBuilder xml = new StringBuilder();
            xml.Append("<?xml version=\"1.0\"?>\r\n");
            xml.Append("<root xmlns=\"urn:schemas-upnp-org:device-1-0\">");
            xml.Append("<specVersion><major>1</major><minor>0</minor></specVersion>");
            xml.Append("<device>");

            if (!IsGateway)
            {
                xml.Append("<deviceType>urn:schemas-upnp-org:device:MediaRenderer:1</deviceType>");
                xml.Append("<friendlyName>Living Room TV</friendlyName>");
                xml.Append("<manufacturer>TV Corp</manufacturer>");
                xml.Append("<serviceList><service><serviceType>urn:schemas-upnp-org:service:ConnectionManager:1</serviceType>");
                xml.Append("<serviceId>urn:upnp-org:serviceId:ConnectionManager</serviceId><controlURL>/ctl/CM</controlURL>");
                xml.Append("<eventSubURL>/evt/CM</eventSubURL><SCPDURL>/CM.xml</SCPDURL></service></serviceList>");
                xml.Append("</device></root>");
                return xml.ToString();
            }

            xml.Append("<deviceType>urn:schemas-upnp-org:device:InternetGatewayDevice:1</deviceType>");
            xml.Append("<friendlyName>").Append(LiteXml.Escape(FriendlyName)).Append("</friendlyName>");
            xml.Append("<manufacturer>Fake Corp</manufacturer>");
            xml.Append("<manufacturerURL>http://fake.example/</manufacturerURL>");
            xml.Append("<modelDescription>Fake Internet Gateway</modelDescription>");
            xml.Append("<modelName>FIGD-1</modelName>");
            xml.Append("<modelNumber>1.0</modelNumber>");
            xml.Append("<modelURL>http://fake.example/model</modelURL>");
            xml.Append("<serialNumber>SN-0001</serialNumber>");
            xml.Append("<UDN>uuid:00000000-0000-0000-0000-000000000001</UDN>");
            xml.Append("<serviceList><service><serviceType>urn:schemas-upnp-org:service:Layer3Forwarding:1</serviceType>");
            xml.Append("<serviceId>urn:upnp-org:serviceId:L3Forwarding1</serviceId><controlURL>/ctl/L3F</controlURL>");
            xml.Append("<eventSubURL>/evt/L3F</eventSubURL><SCPDURL>/L3F.xml</SCPDURL></service></serviceList>");
            xml.Append("<deviceList><device>");
            xml.Append("<deviceType>urn:schemas-upnp-org:device:WANDevice:1</deviceType>");
            xml.Append("<friendlyName>WANDevice</friendlyName>");
            xml.Append("<manufacturer>Nested Manufacturer</manufacturer>");
            xml.Append("<serviceList><service><serviceType>").Append(CommonConfigType).Append("</serviceType>");
            xml.Append("<serviceId>urn:upnp-org:serviceId:WANCommonIFC1</serviceId><controlURL>/ctl/CmnIfCfg</controlURL>");
            xml.Append("<eventSubURL>/evt/CmnIfCfg</eventSubURL><SCPDURL>/WANCfg.xml</SCPDURL></service></serviceList>");
            xml.Append("<deviceList><device>");
            xml.Append("<deviceType>urn:schemas-upnp-org:device:WANConnectionDevice:1</deviceType>");
            xml.Append("<friendlyName>WANConnectionDevice</friendlyName>");
            xml.Append("<serviceList><service><serviceType>").Append(IpConnectionType).Append("</serviceType>");
            xml.Append("<serviceId>urn:upnp-org:serviceId:WANIPConn1</serviceId>");
            xml.Append("<controlURL>").Append(controlHost).Append("/ctl/IPConn</controlURL>");
            xml.Append("<eventSubURL>/evt/IPConn</eventSubURL><SCPDURL>/WANIPCn.xml</SCPDURL></service>");
            if (AddPppService)
            {
                xml.Append("<service><serviceType>").Append(PppConnectionType).Append("</serviceType>");
                xml.Append("<serviceId>urn:upnp-org:serviceId:WANPPPConn1</serviceId><controlURL>/ctl/PPPConn</controlURL>");
                xml.Append("<eventSubURL>/evt/PPPConn</eventSubURL><SCPDURL>/WANPPPCn.xml</SCPDURL></service>");
            }

            xml.Append("</serviceList>");
            xml.Append("</device></deviceList>");
            xml.Append("</device></deviceList>");
            xml.Append("<presentationURL>http://127.0.0.1/</presentationURL>");
            xml.Append("</device></root>");
            return xml.ToString();
        }

        private static string Envelope(string action, string serviceType, string inner)
        {
            return "<?xml version=\"1.0\"?>\r\n" +
                   "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
                   "<s:Body><u:" + action + "Response xmlns:u=\"" + serviceType + "\">" + inner + "</u:" + action + "Response></s:Body></s:Envelope>";
        }

        private static Tuple<int, string> Fault(int code, string description)
        {
            string xml = "<?xml version=\"1.0\"?>\r\n" +
                         "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
                         "<s:Body><s:Fault><faultcode>s:Client</faultcode><faultstring>UPnPError</faultstring><detail>" +
                         "<UPnPError xmlns=\"urn:schemas-upnp-org:control-1-0\"><errorCode>" + code + "</errorCode>" +
                         "<errorDescription>" + description + "</errorDescription></UPnPError></detail></s:Fault></s:Body></s:Envelope>";
            return Tuple.Create(500, xml);
        }

        private Tuple<int, string> HandleCommonConfig(string action)
        {
            if (action == "GetCommonLinkProperties")
            {
                return Tuple.Create(200, Envelope(action, CommonConfigType,
                    "<NewWANAccessType>Ethernet</NewWANAccessType>" +
                    "<NewLayer1UpstreamMaxBitRate>100000000</NewLayer1UpstreamMaxBitRate>" +
                    "<NewLayer1DownstreamMaxBitRate>1000000000</NewLayer1DownstreamMaxBitRate>" +
                    "<NewPhysicalLinkStatus>Up</NewPhysicalLinkStatus>"));
            }

            return Fault(401, "Invalid Action");
        }

        private Tuple<int, string> HandleIpConnection(string action, Dictionary<string, string> args, bool ppp)
        {
            string serviceType = ppp ? PppConnectionType : IpConnectionType;
            switch (action)
            {
                case "GetStatusInfo":
                    return Tuple.Create(200, Envelope(action, serviceType,
                        "<NewConnectionStatus>" + (ppp ? PppStatus : IpStatus) + "</NewConnectionStatus>" +
                        "<NewLastConnectionError>ERROR_NONE</NewLastConnectionError>" +
                        "<NewUptime>" + Uptime + "</NewUptime>"));

                case "GetExternalIPAddress":
                    return Tuple.Create(200, Envelope(action, serviceType,
                        "<NewExternalIPAddress>" + (ppp ? PppExternalIp : ExternalIp) + "</NewExternalIPAddress>"));

                case "GetConnectionTypeInfo":
                    return Tuple.Create(200, Envelope(action, serviceType,
                        "<NewConnectionType>IP_Routed</NewConnectionType><NewPossibleConnectionTypes>IP_Routed</NewPossibleConnectionTypes>"));

                case "AddPortMapping":
                    return HandleAdd(action, args);

                case "DeletePortMapping":
                    return HandleDelete(action, args);

                case "GetGenericPortMappingEntry":
                    return HandleGenericEntry(action, args);

                case "GetSpecificPortMappingEntry":
                    return HandleSpecificEntry(action, args);

                default:
                    return Fault(401, "Invalid Action");
            }
        }

        private static string Arg(Dictionary<string, string> args, string name)
        {
            string value;
            return args.TryGetValue(name, out value) ? value : string.Empty;
        }

        private static int IntArg(Dictionary<string, string> args, string name)
        {
            int value;
            int.TryParse(Arg(args, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
            return value;
        }

        private Tuple<int, string> HandleAdd(string action, Dictionary<string, string> args)
        {
            if (ConflictOnAdd)
            {
                return Fault(718, "ConflictInMappingEntry");
            }

            int lease = IntArg(args, "NewLeaseDuration");
            if (OnlyPermanentLeases && lease != 0)
            {
                return Fault(725, "OnlyPermanentLeasesSupported");
            }

            UpnpProtocol protocol;
            if (!UpnpProtocolExtensions.TryParse(Arg(args, "NewProtocol"), out protocol))
            {
                return Fault(402, "Invalid Args");
            }

            PortMappingInfo mapping = new PortMappingInfo
            {
                Protocol = protocol,
                ExternalPort = IntArg(args, "NewExternalPort"),
                InternalPort = IntArg(args, "NewInternalPort"),
                InternalClient = Arg(args, "NewInternalClient"),
                RemoteHost = Arg(args, "NewRemoteHost"),
                Description = Arg(args, "NewPortMappingDescription"),
                LeaseDuration = lease,
                Enabled = Arg(args, "NewEnabled") == "1",
            };

            lock (_lock)
            {
                _mappings.RemoveAll(m => m.Protocol == mapping.Protocol && m.ExternalPort == mapping.ExternalPort && m.RemoteHost == mapping.RemoteHost);
                _mappings.Add(mapping);
            }

            return Tuple.Create(200, Envelope(action, IpConnectionType, string.Empty));
        }

        private Tuple<int, string> HandleDelete(string action, Dictionary<string, string> args)
        {
            UpnpProtocol protocol;
            if (!UpnpProtocolExtensions.TryParse(Arg(args, "NewProtocol"), out protocol))
            {
                return Fault(402, "Invalid Args");
            }

            int port = IntArg(args, "NewExternalPort");
            string remote = Arg(args, "NewRemoteHost");

            lock (_lock)
            {
                int removed = _mappings.RemoveAll(m => m.Protocol == protocol && m.ExternalPort == port && m.RemoteHost == remote);
                if (removed == 0)
                {
                    return Fault(714, "NoSuchEntryInArray");
                }
            }

            return Tuple.Create(200, Envelope(action, IpConnectionType, string.Empty));
        }

        private static string MappingXml(PortMappingInfo m, bool includeKey)
        {
            string key = includeKey
                ? "<NewRemoteHost>" + LiteXml.Escape(m.RemoteHost) + "</NewRemoteHost>" +
                  "<NewExternalPort>" + m.ExternalPort + "</NewExternalPort>" +
                  "<NewProtocol>" + m.Protocol.ToWireString() + "</NewProtocol>"
                : string.Empty;

            return key +
                   "<NewInternalPort>" + m.InternalPort + "</NewInternalPort>" +
                   "<NewInternalClient>" + LiteXml.Escape(m.InternalClient) + "</NewInternalClient>" +
                   "<NewEnabled>" + (m.Enabled ? "1" : "0") + "</NewEnabled>" +
                   "<NewPortMappingDescription>" + LiteXml.Escape(m.Description) + "</NewPortMappingDescription>" +
                   "<NewLeaseDuration>" + m.LeaseDuration + "</NewLeaseDuration>";
        }

        private Tuple<int, string> HandleGenericEntry(string action, Dictionary<string, string> args)
        {
            if (DisableGenericEntries)
            {
                return Fault(401, "Invalid Action");
            }

            int index = IntArg(args, "NewPortMappingIndex");
            lock (_lock)
            {
                if (RepeatFirstEntry && _mappings.Count > 0)
                {
                    return Tuple.Create(200, Envelope(action, IpConnectionType, MappingXml(_mappings[0], true)));
                }

                if (index < 0 || index >= _mappings.Count)
                {
                    return Fault(713, "SpecifiedArrayIndexInvalid");
                }

                return Tuple.Create(200, Envelope(action, IpConnectionType, MappingXml(_mappings[index], true)));
            }
        }

        private Tuple<int, string> HandleSpecificEntry(string action, Dictionary<string, string> args)
        {
            UpnpProtocol protocol;
            if (!UpnpProtocolExtensions.TryParse(Arg(args, "NewProtocol"), out protocol))
            {
                return Fault(402, "Invalid Args");
            }

            int port = IntArg(args, "NewExternalPort");
            lock (_lock)
            {
                PortMappingInfo found = _mappings.Find(m => m.Protocol == protocol && m.ExternalPort == port);
                if (found == null)
                {
                    return Fault(714, "NoSuchEntryInArray");
                }

                return Tuple.Create(200, Envelope(action, IpConnectionType, MappingXml(found, false)));
            }
        }
    }

    /// <summary>
    /// Answers SSDP M-SEARCH requests on a loopback UDP port with the locations it was given. The tests point
    /// <see cref="UpnpClientOptions.SsdpEndPoint"/> at it, so no multicast support is needed.
    /// </summary>
    internal sealed class FakeSsdpResponder : IDisposable
    {
        private readonly UdpClient _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly List<Uri> _locations = new List<Uri>();
        private int _searches;
        private volatile bool _stopped;

        public FakeSsdpResponder(params Uri[] locations)
        {
            _locations.AddRange(locations);
            Task.Run(LoopAsync);
        }

        public IPEndPoint EndPoint
        {
            get { return (IPEndPoint)_udp.Client.LocalEndPoint; }
        }

        /// <summary>Number of M-SEARCH requests received.</summary>
        public int SearchCount
        {
            get { return Volatile.Read(ref _searches); }
        }

        private async Task LoopAsync()
        {
            while (!_stopped)
            {
                UdpReceiveResult received;
                try
                {
                    received = await _udp.ReceiveAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    return;
                }

                string text = Encoding.UTF8.GetString(received.Buffer);
                if (!text.StartsWith("M-SEARCH", StringComparison.Ordinal))
                {
                    continue;
                }

                Interlocked.Increment(ref _searches);

                string searchTarget = "upnp:rootdevice";
                foreach (string line in text.Split(new[] { "\r\n" }, StringSplitOptions.None))
                {
                    if (line.StartsWith("ST:", StringComparison.OrdinalIgnoreCase))
                    {
                        searchTarget = line.Substring(3).Trim();
                    }
                }

                foreach (Uri location in _locations)
                {
                    string answer = "HTTP/1.1 200 OK\r\n" +
                                    "CACHE-CONTROL: max-age=120\r\n" +
                                    "EXT:\r\n" +
                                    "LOCATION: " + location + "\r\n" +
                                    "SERVER: FakeOS/1.0 UPnP/1.1 FakeIGD/1.0\r\n" +
                                    "ST: " + searchTarget + "\r\n" +
                                    "USN: uuid:fake-" + location.Port + "::" + searchTarget + "\r\n" +
                                    "DATE: Mon, 26 Oct 2026 00:00:00 GMT\r\n\r\n";
                    byte[] data = Encoding.ASCII.GetBytes(answer);
                    try
                    {
                        _udp.Send(data, data.Length, received.RemoteEndPoint);
                    }
                    catch (Exception)
                    {
                        // The searcher is already gone.
                    }
                }
            }
        }

        public void Dispose()
        {
            _stopped = true;
            try
            {
                _udp.Close();
            }
            catch (Exception)
            {
                // Already closed.
            }
        }
    }
}
