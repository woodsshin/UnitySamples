// Copyright 2017-present WoodsShin, Inc. All Rights Reserved.

using System;
using System.Collections.Generic;

namespace WoodsShin.SimpleUPNP
{
    /// <summary>One service of a UPnP device description.</summary>
    internal sealed class UpnpService
    {
        public string ServiceType = string.Empty;
        public string ServiceId = string.Empty;

        /// <summary>Absolute URL that receives the SOAP requests.</summary>
        public Uri ControlUrl;

        public string EventSubUrl = string.Empty;
        public string ScpdUrl = string.Empty;
    }

    /// <summary>The parts of a UPnP device description that Simple UPNP needs.</summary>
    internal sealed class UpnpDeviceDescription
    {
        private const string WanIpConnectionPrefix = "urn:schemas-upnp-org:service:WANIPConnection:";
        private const string WanPppConnectionPrefix = "urn:schemas-upnp-org:service:WANPPPConnection:";
        private const string CommonInterfaceConfigPrefix = "urn:schemas-upnp-org:service:WANCommonInterfaceConfig:";

        public ManufacturerInfo Manufacturer = new ManufacturerInfo();

        /// <summary>WANIPConnection and WANPPPConnection services, WANIPConnection first.</summary>
        public List<UpnpService> WanConnectionServices = new List<UpnpService>();

        /// <summary>WANCommonInterfaceConfig service, or null.</summary>
        public UpnpService CommonInterfaceConfig;

        /// <summary>True when the device offers at least one WAN connection service, which makes it an IGD.</summary>
        public bool IsGateway
        {
            get { return WanConnectionServices.Count > 0; }
        }

        /// <summary>
        /// Parses the device description document that a router returned for <paramref name="location"/>.
        /// Never throws. Services whose control URL points to another host are ignored.
        /// </summary>
        public static UpnpDeviceDescription Parse(string xml, Uri location)
        {
            UpnpDeviceDescription description = new UpnpDeviceDescription();
            XmlNodeLite root = LiteXml.Parse(xml);

            XmlNodeLite device = root.Descendant("device");
            XmlNodeLite specVersion = root.Descendant("specVersion");

            if (specVersion != null)
            {
                string major = specVersion.ChildText("major");
                string minor = specVersion.ChildText("minor");
                if (!string.IsNullOrEmpty(major) || !string.IsNullOrEmpty(minor))
                {
                    description.Manufacturer.SpecVersion = (major ?? "0") + "." + (minor ?? "0");
                }
            }

            if (device != null)
            {
                ManufacturerInfo m = description.Manufacturer;
                m.FriendlyName = Pick(device.ChildText("friendlyName"), m.FriendlyName);
                m.Manufacturer = Pick(device.ChildText("manufacturer"), m.Manufacturer);
                m.ManufacturerUrl = Pick(device.ChildText("manufacturerURL"), m.ManufacturerUrl);
                m.ModelDescription = Pick(device.ChildText("modelDescription"), m.ModelDescription);
                m.ModelName = Pick(device.ChildText("modelName"), m.ModelName);
                m.ModelNumber = Pick(device.ChildText("modelNumber"), m.ModelNumber);
                m.ModelUrl = Pick(device.ChildText("modelURL"), m.ModelUrl);
                m.SerialNumber = Pick(device.ChildText("serialNumber"), m.SerialNumber);
            }

            // UPnP 1.0 devices may define a base URL. UPnP 1.1 uses the location of the description instead.
            Uri baseUri = location;
            string urlBaseText = root.DescendantText("URLBase");
            Uri urlBase;
            if (!string.IsNullOrEmpty(urlBaseText) &&
                Uri.TryCreate(urlBaseText, UriKind.Absolute, out urlBase) &&
                string.Equals(urlBase.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(urlBase.Host, location.Host, StringComparison.OrdinalIgnoreCase))
            {
                baseUri = urlBase;
            }

            List<UpnpService> ipServices = new List<UpnpService>();
            List<UpnpService> pppServices = new List<UpnpService>();

            foreach (XmlNodeLite node in root.Descendants("service"))
            {
                UpnpService service = new UpnpService
                {
                    ServiceType = node.ChildText("serviceType") ?? string.Empty,
                    ServiceId = node.ChildText("serviceId") ?? string.Empty,
                    EventSubUrl = node.ChildText("eventSubURL") ?? string.Empty,
                    ScpdUrl = node.ChildText("SCPDURL") ?? string.Empty,
                };

                string controlText = node.ChildText("controlURL");
                if (string.IsNullOrEmpty(controlText) || !TryResolveControlUrl(baseUri, location, controlText, out service.ControlUrl))
                {
                    continue;
                }

                if (service.ServiceType.StartsWith(WanIpConnectionPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    ipServices.Add(service);
                }
                else if (service.ServiceType.StartsWith(WanPppConnectionPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    pppServices.Add(service);
                }
                else if (service.ServiceType.StartsWith(CommonInterfaceConfigPrefix, StringComparison.OrdinalIgnoreCase) &&
                         description.CommonInterfaceConfig == null)
                {
                    description.CommonInterfaceConfig = service;
                }
            }

            description.WanConnectionServices.AddRange(ipServices);
            description.WanConnectionServices.AddRange(pppServices);
            return description;
        }

        private static string Pick(string value, string fallback)
        {
            return string.IsNullOrEmpty(value) ? fallback : value;
        }

        private static bool TryResolveControlUrl(Uri baseUri, Uri location, string text, out Uri result)
        {
            result = null;
            Uri candidate;
            if (!Uri.TryCreate(baseUri, text, out candidate))
            {
                return false;
            }

            // The control URL must stay on the device that was found. Otherwise a forged answer could make
            // this library send requests to an arbitrary host.
            if (!string.Equals(candidate.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(candidate.Host, location.Host, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            result = candidate;
            return true;
        }
    }
}
