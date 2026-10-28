// Copyright 2017-present WoodsShin, Inc. All Rights Reserved.

using System;

namespace WoodsShin.SimpleUPNP
{
    /// <summary>Text shown for values a router did not report.</summary>
    public static class UpnpConstants
    {
        public const string NotAvailable = "N/A";
    }

    /// <summary>Describes one UDP/TCP port mapping on a gateway.</summary>
    [Serializable]
    public class PortMappingInfo
    {
        /// <summary>TCP or UDP.</summary>
        public UpnpProtocol Protocol = UpnpProtocol.Udp;

        /// <summary>Port that is opened on the router (1-65535).</summary>
        public int ExternalPort = 7777;

        /// <summary>Port on the local machine the traffic is forwarded to (1-65535).</summary>
        public int InternalPort = 7777;

        /// <summary>Local address to forward to, for example 192.168.1.11. Leave empty to use this machine's address on the gateway's network.</summary>
        public string InternalClient = string.Empty;

        /// <summary>Remote host the mapping is limited to. Leave empty to allow every host.</summary>
        public string RemoteHost = string.Empty;

        /// <summary>Description that the router shows for the mapping.</summary>
        public string Description = "Simple UPNP";

        /// <summary>Lease time in seconds. 0 asks for a permanent mapping.</summary>
        public int LeaseDuration;

        /// <summary>Whether the mapping is active.</summary>
        public bool Enabled = true;

        public PortMappingInfo()
        {
        }

        public PortMappingInfo(UpnpProtocol protocol, int externalPort, int internalPort, string description = null, int leaseDuration = 0)
        {
            Protocol = protocol;
            ExternalPort = externalPort;
            InternalPort = internalPort;
            if (description != null)
            {
                Description = description;
            }

            LeaseDuration = leaseDuration;
        }

        /// <summary>Returns a copy of this mapping.</summary>
        public PortMappingInfo Clone()
        {
            return (PortMappingInfo)MemberwiseClone();
        }

        /// <summary>Returns null when the values can be sent to a router, otherwise a description of the problem.</summary>
        public string Validate()
        {
            if (ExternalPort < 1 || ExternalPort > 65535)
            {
                return "ExternalPort must be between 1 and 65535.";
            }

            if (InternalPort < 1 || InternalPort > 65535)
            {
                return "InternalPort must be between 1 and 65535.";
            }

            if (LeaseDuration < 0)
            {
                return "LeaseDuration must not be negative.";
            }

            return null;
        }

        public override string ToString()
        {
            return Protocol.ToWireString() + " " + ExternalPort + " -> " +
                   (string.IsNullOrEmpty(InternalClient) ? "<auto>" : InternalClient) + ":" + InternalPort;
        }
    }

    /// <summary>Manufacturer information from the device description of a gateway.</summary>
    [Serializable]
    public class ManufacturerInfo
    {
        public string SpecVersion = UpnpConstants.NotAvailable;
        public string FriendlyName = UpnpConstants.NotAvailable;
        public string Manufacturer = UpnpConstants.NotAvailable;
        public string ManufacturerUrl = UpnpConstants.NotAvailable;
        public string ModelDescription = UpnpConstants.NotAvailable;
        public string ModelName = UpnpConstants.NotAvailable;
        public string ModelNumber = UpnpConstants.NotAvailable;
        public string ModelUrl = UpnpConstants.NotAvailable;
        public string SerialNumber = UpnpConstants.NotAvailable;
    }

    /// <summary>Connection status of a gateway (GetStatusInfo, GetExternalIPAddress, GetConnectionTypeInfo).</summary>
    [Serializable]
    public class DeviceStatusInfo
    {
        /// <summary>Seconds the connection has been up.</summary>
        public int Uptime;

        /// <summary>For example "Connected".</summary>
        public string ConnectionStatus = UpnpConstants.NotAvailable;

        public string LastConnectionError = UpnpConstants.NotAvailable;

        /// <summary>Public address of the router.</summary>
        public string ExternalIPAddress = UpnpConstants.NotAvailable;

        /// <summary>Address of this machine on the network that found the gateway.</summary>
        public string LocalIPAddress = UpnpConstants.NotAvailable;

        /// <summary>URL of the device description.</summary>
        public string DeviceLocation = UpnpConstants.NotAvailable;

        public string ConnectionType = UpnpConstants.NotAvailable;
        public string PossibleConnectionTypes = UpnpConstants.NotAvailable;
    }

    /// <summary>Link properties of a gateway (GetCommonLinkProperties).</summary>
    [Serializable]
    public class CommonLinkProperties
    {
        /// <summary>For example "Ethernet".</summary>
        public string WanAccessType = UpnpConstants.NotAvailable;

        /// <summary>Upstream speed in bits per second.</summary>
        public string Layer1UpstreamMaxBitRate = UpnpConstants.NotAvailable;

        /// <summary>Downstream speed in bits per second.</summary>
        public string Layer1DownstreamMaxBitRate = UpnpConstants.NotAvailable;

        /// <summary>For example "Up" or "Down".</summary>
        public string PhysicalLinkStatus = UpnpConstants.NotAvailable;
    }

    /// <summary>Header values of the SSDP answer a device sent.</summary>
    [Serializable]
    public class SsdpDeviceInfo
    {
        /// <summary>URL of the UPnP description of the root device.</summary>
        public string Location = UpnpConstants.NotAvailable;

        /// <summary>OS and UPnP version, specified by the vendor.</summary>
        public string Server = UpnpConstants.NotAvailable;

        /// <summary>CACHE-CONTROL header: how long the advertisement is valid.</summary>
        public string CacheControl = UpnpConstants.NotAvailable;

        /// <summary>EXT header (kept for UPnP 1.0 compatibility).</summary>
        public string Ext = UpnpConstants.NotAvailable;

        /// <summary>Search target.</summary>
        public string St = UpnpConstants.NotAvailable;

        /// <summary>Unique service name.</summary>
        public string Usn = UpnpConstants.NotAvailable;

        /// <summary>Date the answer was generated.</summary>
        public string Date = UpnpConstants.NotAvailable;
    }
}
