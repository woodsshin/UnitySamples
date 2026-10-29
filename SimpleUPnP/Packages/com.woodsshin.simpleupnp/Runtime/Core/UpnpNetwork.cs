// Copyright 2017-present WoodsShin, Inc. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace WoodsShin.SimpleUPNP
{
    /// <summary>Address helpers that work on every platform Unity supports.</summary>
    internal static class UpnpNetwork
    {
        /// <summary>
        /// True for addresses that can belong to a local network: private, shared (carrier grade NAT),
        /// link-local and loopback ranges.
        /// </summary>
        public static bool IsLocalNetworkAddress(IPAddress address)
        {
            if (address == null)
            {
                return false;
            }

            if (address.IsIPv4MappedToIPv6)
            {
                address = address.MapToIPv4();
            }

            if (IPAddress.IsLoopback(address))
            {
                return true;
            }

            byte[] b = address.GetAddressBytes();
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                return b[0] == 10 ||
                       (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                       (b[0] == 192 && b[1] == 168) ||
                       (b[0] == 169 && b[1] == 254) ||
                       (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
            }

            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                // fe80::/10 link-local, fc00::/7 unique local.
                return (b[0] == 0xFE && (b[1] & 0xC0) == 0x80) || (b[0] & 0xFE) == 0xFC;
            }

            return false;
        }

        /// <summary>
        /// True for addresses that are routed on the public internet. False for unspecified, private, shared (carrier
        /// grade NAT), loopback, link-local, benchmarking, multicast and other reserved ranges.
        /// </summary>
        public static bool IsPublicAddress(IPAddress address)
        {
            if (address == null)
            {
                return false;
            }

            if (address.IsIPv4MappedToIPv6)
            {
                address = address.MapToIPv4();
            }

            byte[] b = address.GetAddressBytes();
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                if (b[0] == 0 || b[0] == 10 || b[0] == 127)
                {
                    return false;
                }

                if ((b[0] == 100 && b[1] >= 64 && b[1] <= 127) ||    // 100.64.0.0/10 shared address space
                    (b[0] == 169 && b[1] == 254) ||                  // 169.254.0.0/16 link-local
                    (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||     // 172.16.0.0/12 private
                    (b[0] == 192 && b[1] == 168) ||                  // 192.168.0.0/16 private
                    (b[0] == 192 && b[1] == 0 && b[2] == 0) ||       // 192.0.0.0/24 IETF protocol assignments
                    (b[0] == 198 && (b[1] == 18 || b[1] == 19)))     // 198.18.0.0/15 benchmarking
                {
                    return false;
                }

                // 224.0.0.0/4 multicast and 240.0.0.0/4 reserved (including the broadcast address).
                return b[0] < 224;
            }

            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                // 2000::/3 is the global unicast range.
                return (b[0] & 0xE0) == 0x20;
            }

            return false;
        }

        /// <summary>Resolves the host of a URL to an address and checks that it may be used as a gateway.</summary>
        public static async System.Threading.Tasks.Task<bool> IsAllowedGatewayHostAsync(Uri uri, UpnpClientOptions options)
        {
            if (uri == null || !uri.IsAbsoluteUri || !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (options.AllowPublicGatewayAddresses)
            {
                return true;
            }

            IPAddress address;
            if (IPAddress.TryParse(uri.DnsSafeHost, out address))
            {
                return IsLocalNetworkAddress(address);
            }

            try
            {
                IPAddress[] resolved = await Dns.GetHostAddressesAsync(uri.DnsSafeHost).ConfigureAwait(false);
                if (resolved.Length == 0)
                {
                    return false;
                }

                for (int i = 0; i < resolved.Length; i++)
                {
                    if (!IsLocalNetworkAddress(resolved[i]))
                    {
                        return false;
                    }
                }

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Returns the local address the operating system would use to reach <paramref name="remoteHost"/>.
        /// Connecting a UDP socket sends no packet, it only selects the route. Returns null when that fails.
        /// </summary>
        public static IPAddress GetLocalAddressFor(string remoteHost)
        {
            try
            {
                IPAddress remote;
                if (!IPAddress.TryParse(remoteHost, out remote))
                {
                    IPAddress[] resolved = Dns.GetHostAddresses(remoteHost);
                    remote = resolved.Length > 0 ? resolved[0] : null;
                }

                if (remote == null)
                {
                    return null;
                }

                using (Socket socket = new Socket(remote.AddressFamily, SocketType.Dgram, ProtocolType.Udp))
                {
                    socket.Connect(new IPEndPoint(remote, 9));
                    IPEndPoint local = socket.LocalEndPoint as IPEndPoint;
                    return local == null ? null : local.Address;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// IPv4 addresses of the network adapters that can carry the SSDP search. Returns an empty list when the
        /// adapters cannot be listed (some Android versions deny it). Callers then fall back to the default route.
        /// </summary>
        public static List<IPAddress> GetSearchAddresses(UpnpClientOptions options)
        {
            List<IPAddress> addresses = new List<IPAddress>();
            try
            {
                NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
                for (int i = 0; i < interfaces.Length; i++)
                {
                    NetworkInterface adapter = interfaces[i];
                    if (adapter.OperationalStatus != OperationalStatus.Up)
                    {
                        continue;
                    }

                    bool loopback = adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback;
                    if (loopback && !options.IncludeLoopbackInterfaces)
                    {
                        continue;
                    }

                    foreach (UnicastIPAddressInformation info in adapter.GetIPProperties().UnicastAddresses)
                    {
                        IPAddress address = info.Address;
                        if (address.AddressFamily != AddressFamily.InterNetwork || addresses.Contains(address))
                        {
                            continue;
                        }

                        // 169.254.x.x only exists when DHCP failed; there is no router to find there.
                        byte[] b = address.GetAddressBytes();
                        if (b[0] == 169 && b[1] == 254)
                        {
                            continue;
                        }

                        addresses.Add(address);
                    }
                }
            }
            catch (Exception)
            {
                addresses.Clear();
            }

            return addresses;
        }
    }
}
