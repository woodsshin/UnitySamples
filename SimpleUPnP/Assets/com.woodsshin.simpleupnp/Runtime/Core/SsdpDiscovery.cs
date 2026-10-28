// Copyright 2017-present WoodsShin, Inc. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WoodsShin.SimpleUPNP
{
    /// <summary>
    /// Simple Service Discovery Protocol (SSDP) search. Sends M-SEARCH requests to the multicast group from every
    /// local IPv4 adapter and collects the unicast answers.
    /// </summary>
    internal static class SsdpDiscovery
    {
        /// <summary>Searches the local network. Returns the answers in arrival order, without duplicate locations.</summary>
        public static async Task<List<SsdpDeviceInfo>> SearchAsync(UpnpClientOptions options, CancellationToken cancellationToken)
        {
            List<SsdpDeviceInfo> found = new List<SsdpDeviceInfo>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            List<IPAddress> localAddresses = UpnpNetwork.GetSearchAddresses(options);
            if (localAddresses.Count == 0)
            {
                // The adapters could not be listed. Let the operating system choose the default route.
                localAddresses.Add(IPAddress.Any);
            }

            using (CancellationTokenSource search = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                search.CancelAfter(options.DiscoveryTimeout);
                Stopwatch clock = Stopwatch.StartNew();

                List<Task> workers = new List<Task>();
                int graceStarted = 0;

                for (int i = 0; i < localAddresses.Count; i++)
                {
                    IPAddress local = localAddresses[i];
                    workers.Add(Task.Run(() => SearchOnAddressAsync(local, options, search, found, seen, () =>
                    {
                        // The first answer of a real gateway starts a short grace period for further gateways.
                        if (Interlocked.Exchange(ref graceStarted, 1) == 0)
                        {
                            // Never extend the search beyond the overall discovery timeout.
                            TimeSpan remaining = options.DiscoveryTimeout - clock.Elapsed;
                            TimeSpan delay = remaining < options.DiscoveryGracePeriod ? remaining : options.DiscoveryGracePeriod;
                            search.CancelAfter(delay < TimeSpan.Zero ? TimeSpan.Zero : delay);
                        }
                    })));
                }

                await Task.WhenAll(workers).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();

            lock (found)
            {
                return new List<SsdpDeviceInfo>(found);
            }
        }

        private static async Task SearchOnAddressAsync(
            IPAddress local,
            UpnpClientOptions options,
            CancellationTokenSource search,
            List<SsdpDeviceInfo> found,
            HashSet<string> seen,
            Action onGatewayAnswer)
        {
            UdpClient client = null;
            try
            {
                client = new UdpClient(new IPEndPoint(local, 0));
                Socket socket = client.Client;

                TrySetOption(socket, SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, options.MulticastTimeToLive);
                TrySetOption(socket, SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, 1);
                if (!local.Equals(IPAddress.Any))
                {
                    // Without this the multicast packet leaves through the default adapter only.
                    TrySetOption(socket, SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
                }

                Task sender = SendSearchesAsync(client, options, search.Token);
                Task receiver = ReceiveLoopAsync(client, options, search.Token, found, seen, onGatewayAnswer);

                await Task.WhenAll(sender, receiver).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (!(ex is OperationCanceledException) && !(ex is ObjectDisposedException))
                {
                    Log(options, UpnpLogLevel.Verbose, "SSDP search on " + local + " failed: " + ex.Message);
                }
            }
            finally
            {
                if (client != null)
                {
                    try
                    {
                        client.Close();
                    }
                    catch (Exception)
                    {
                        // Already closed.
                    }
                }
            }
        }

        private static void TrySetOption(Socket socket, SocketOptionLevel level, SocketOptionName name, object value)
        {
            try
            {
                if (value is int)
                {
                    socket.SetSocketOption(level, name, (int)value);
                }
                else
                {
                    socket.SetSocketOption(level, name, (byte[])value);
                }
            }
            catch (Exception)
            {
                // Not every platform supports every option. The search still works without it.
            }
        }

        private static async Task SendSearchesAsync(UdpClient client, UpnpClientOptions options, CancellationToken token)
        {
            // UDP is unreliable, so the burst is sent twice.
            for (int round = 0; round < 2; round++)
            {
                for (int i = 0; i < options.SearchTargets.Count; i++)
                {
                    byte[] packet = Encoding.ASCII.GetBytes(BuildSearchRequest(options, options.SearchTargets[i]));
                    try
                    {
                        // A synchronous send of one small datagram never blocks in practice, and the overload
                        // exists on every Mono profile that Unity ships.
                        client.Send(packet, packet.Length, options.SsdpEndPoint);
                    }
                    catch (Exception ex)
                    {
                        Log(options, UpnpLogLevel.Verbose, "SSDP send failed: " + ex.Message);
                        return;
                    }
                }

                if (round == 0)
                {
                    try
                    {
                        await Task.Delay(300, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }
        }

        internal static string BuildSearchRequest(UpnpClientOptions options, string searchTarget)
        {
            int mx = Math.Max(1, Math.Min(5, (int)Math.Ceiling(options.DiscoveryTimeout.TotalSeconds) - 1));
            return "M-SEARCH * HTTP/1.1\r\n" +
                   "HOST: " + options.SsdpEndPoint.Address + ":" + options.SsdpEndPoint.Port + "\r\n" +
                   "MAN: \"ssdp:discover\"\r\n" +
                   "MX: " + mx + "\r\n" +
                   "ST: " + searchTarget + "\r\n" +
                   "USER-AGENT: " + options.UserAgent + "\r\n" +
                   "\r\n";
        }

        private static async Task ReceiveLoopAsync(
            UdpClient client,
            UpnpClientOptions options,
            CancellationToken token,
            List<SsdpDeviceInfo> found,
            HashSet<string> seen,
            Action onGatewayAnswer)
        {
            Task cancelled = Task.Delay(Timeout.Infinite, token);
            int failures = 0;

            while (!token.IsCancellationRequested)
            {
                Task<UdpReceiveResult> receive = client.ReceiveAsync();

                Task finished = await Task.WhenAny(receive, cancelled).ConfigureAwait(false);
                if (finished != receive)
                {
                    // The search time is over. Closing the client later aborts the pending receive.
                    ObserveLater(receive);
                    return;
                }

                UdpReceiveResult result;
                try
                {
                    result = receive.Result;
                    failures = 0;
                }
                catch (Exception)
                {
                    // For example an ICMP "port unreachable" echo of one of our own packets. Keep listening,
                    // but give up when the socket keeps failing (for example because it was closed).
                    if (++failures > 20)
                    {
                        return;
                    }

                    continue;
                }

                SsdpDeviceInfo info;
                if (!TryParseResponse(Encoding.UTF8.GetString(result.Buffer), out info))
                {
                    continue;
                }

                if (!await IsAcceptableAsync(info, result.RemoteEndPoint, options).ConfigureAwait(false))
                {
                    continue;
                }

                bool added;
                lock (found)
                {
                    added = seen.Add(info.Location);
                    if (added)
                    {
                        found.Add(info);
                    }
                }

                if (added)
                {
                    Log(options, UpnpLogLevel.Verbose, "SSDP answer from " + result.RemoteEndPoint.Address + ": " + info.Location + " (" + info.St + ")");
                    if (LooksLikeGateway(info))
                    {
                        onGatewayAnswer();
                    }
                }
            }
        }

        private static void ObserveLater(Task<UdpReceiveResult> receive)
        {
            // Reading the exception keeps an aborted receive from surfacing as an unobserved task exception.
            receive.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }

        private static async Task<bool> IsAcceptableAsync(SsdpDeviceInfo info, IPEndPoint sender, UpnpClientOptions options)
        {
            Uri location;
            if (!Uri.TryCreate(info.Location, UriKind.Absolute, out location))
            {
                return false;
            }

            if (!options.AllowPublicGatewayAddresses && !UpnpNetwork.IsLocalNetworkAddress(sender.Address))
            {
                return false;
            }

            return await UpnpNetwork.IsAllowedGatewayHostAsync(location, options).ConfigureAwait(false);
        }

        internal static bool LooksLikeGateway(SsdpDeviceInfo info)
        {
            string text = info.St + " " + info.Usn;
            return text.IndexOf("InternetGatewayDevice", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   text.IndexOf("WANIPConnection", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   text.IndexOf("WANPPPConnection", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   text.IndexOf("WANDevice", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Parses the text of an SSDP answer ("HTTP/1.1 200 OK" plus headers). Returns false when it is no answer.</summary>
        internal static bool TryParseResponse(string text, out SsdpDeviceInfo info)
        {
            info = null;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            string[] lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            if (lines.Length == 0 || !lines[0].StartsWith("HTTP/1.", StringComparison.OrdinalIgnoreCase) || lines[0].IndexOf(" 200", StringComparison.Ordinal) < 0)
            {
                return false;
            }

            Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon > 0)
                {
                    headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
                }
            }

            string location;
            if (!headers.TryGetValue("LOCATION", out location) || location.Length == 0)
            {
                return false;
            }

            info = new SsdpDeviceInfo
            {
                Location = location,
                Server = Header(headers, "SERVER"),
                CacheControl = Header(headers, "CACHE-CONTROL"),
                Ext = Header(headers, "EXT"),
                St = Header(headers, "ST"),
                Usn = Header(headers, "USN"),
                Date = Header(headers, "DATE"),
            };
            return true;
        }

        private static string Header(Dictionary<string, string> headers, string name)
        {
            string value;
            return headers.TryGetValue(name, out value) ? value : UpnpConstants.NotAvailable;
        }

        private static void Log(UpnpClientOptions options, UpnpLogLevel level, string message)
        {
            Action<UpnpLogLevel, string> logger = options.Logger;
            if (logger != null)
            {
                logger(level, message);
            }
        }
    }
}
