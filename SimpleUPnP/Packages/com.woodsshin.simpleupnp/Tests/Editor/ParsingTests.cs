// Copyright 2017-present WoodsShin, Inc. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Net;
using NUnit.Framework;

namespace WoodsShin.SimpleUPNP.Tests
{
    [TestFixture]
    public class ParsingTests
    {
        private const string TypicalDescription =
            "<?xml version=\"1.0\"?>" +
            "<root xmlns=\"urn:schemas-upnp-org:device-1-0\"><specVersion><major>1</major><minor>1</minor></specVersion>" +
            "<device><deviceType>urn:schemas-upnp-org:device:InternetGatewayDevice:1</deviceType>" +
            "<friendlyName>My Router &amp; Co</friendlyName><manufacturer>ACME</manufacturer>" +
            "<manufacturerURL>http://acme.example</manufacturerURL><modelDescription>Router</modelDescription>" +
            "<modelName>R-1</modelName><modelNumber>42</modelNumber><modelURL>http://acme.example/r1</modelURL>" +
            "<serialNumber>S123</serialNumber>" +
            "<deviceList><device><deviceType>urn:schemas-upnp-org:device:WANDevice:1</deviceType>" +
            "<friendlyName>Nested</friendlyName><manufacturer>Nested Corp</manufacturer>" +
            "<serviceList><service><serviceType>urn:schemas-upnp-org:service:WANCommonInterfaceConfig:1</serviceType>" +
            "<serviceId>urn:upnp-org:serviceId:WANCommonIFC1</serviceId><controlURL>/ctl/cic</controlURL>" +
            "<eventSubURL>/evt/cic</eventSubURL><SCPDURL>/cic.xml</SCPDURL></service></serviceList>" +
            "<deviceList><device><deviceType>urn:schemas-upnp-org:device:WANConnectionDevice:1</deviceType>" +
            "<serviceList>" +
            "<service><serviceType>urn:schemas-upnp-org:service:WANPPPConnection:1</serviceType><serviceId>ppp</serviceId>" +
            "<controlURL>/ctl/ppp</controlURL><eventSubURL>/evt/ppp</eventSubURL><SCPDURL>/ppp.xml</SCPDURL></service>" +
            "<service><serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType><serviceId>ip</serviceId>" +
            "<controlURL>/ctl/ip</controlURL><eventSubURL>/evt/ip</eventSubURL><SCPDURL>/ip.xml</SCPDURL></service>" +
            "</serviceList></device></deviceList></device></deviceList></device></root>";

        [Test]
        public void DeviceDescription_ReadsRootDeviceAndServices()
        {
            Uri location = new Uri("http://192.168.1.1:5000/desc.xml");
            UpnpDeviceDescription d = UpnpDeviceDescription.Parse(TypicalDescription, location);

            Assert.IsTrue(d.IsGateway);
            Assert.AreEqual("1.1", d.Manufacturer.SpecVersion);
            Assert.AreEqual("My Router & Co", d.Manufacturer.FriendlyName, "the root device wins over nested devices");
            Assert.AreEqual("ACME", d.Manufacturer.Manufacturer);
            Assert.AreEqual("42", d.Manufacturer.ModelNumber, "modelNumber is read with the spelling of the specification");
            Assert.AreEqual("S123", d.Manufacturer.SerialNumber);

            Assert.AreEqual(2, d.WanConnectionServices.Count);
            Assert.AreEqual("ip", d.WanConnectionServices[0].ServiceId, "WANIPConnection is listed before WANPPPConnection");
            Assert.AreEqual("ppp", d.WanConnectionServices[1].ServiceId);
            Assert.AreEqual("http://192.168.1.1:5000/ctl/ip", d.WanConnectionServices[0].ControlUrl.ToString());
            Assert.IsNotNull(d.CommonInterfaceConfig);
            Assert.AreEqual("http://192.168.1.1:5000/ctl/cic", d.CommonInterfaceConfig.ControlUrl.ToString());
        }

        [Test]
        public void DeviceDescription_UsesMissingValueMarker()
        {
            UpnpDeviceDescription d = UpnpDeviceDescription.Parse("<root><device><friendlyName>X</friendlyName></device></root>", new Uri("http://10.0.0.1/d.xml"));
            Assert.IsFalse(d.IsGateway);
            Assert.AreEqual("X", d.Manufacturer.FriendlyName);
            Assert.AreEqual(UpnpConstants.NotAvailable, d.Manufacturer.Manufacturer);
            Assert.AreEqual(UpnpConstants.NotAvailable, d.Manufacturer.SpecVersion);
        }

        [Test]
        public void DeviceDescription_HonorsUrlBaseOnTheSameHost()
        {
            string xml = "<root><URLBase>http://192.168.1.1:49000/igd/</URLBase><device><serviceList><service>" +
                         "<serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType><controlURL>ctl</controlURL>" +
                         "</service></serviceList></device></root>";
            UpnpDeviceDescription d = UpnpDeviceDescription.Parse(xml, new Uri("http://192.168.1.1:5000/desc.xml"));
            Assert.AreEqual("http://192.168.1.1:49000/igd/ctl", d.WanConnectionServices[0].ControlUrl.ToString());
        }

        [Test]
        public void DeviceDescription_IgnoresUrlBaseOnAnotherHost()
        {
            string xml = "<root><URLBase>http://evil.example/</URLBase><device><serviceList><service>" +
                         "<serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType><controlURL>/ctl</controlURL>" +
                         "</service></serviceList></device></root>";
            UpnpDeviceDescription d = UpnpDeviceDescription.Parse(xml, new Uri("http://192.168.1.1:5000/desc.xml"));
            Assert.AreEqual("http://192.168.1.1:5000/ctl", d.WanConnectionServices[0].ControlUrl.ToString());
        }

        [Test]
        public void DeviceDescription_RejectsControlUrlOnAnotherHost()
        {
            string xml = "<root><device><serviceList><service>" +
                         "<serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType>" +
                         "<controlURL>http://203.0.113.9/ctl</controlURL></service></serviceList></device></root>";
            UpnpDeviceDescription d = UpnpDeviceDescription.Parse(xml, new Uri("http://192.168.1.1:5000/desc.xml"));
            Assert.IsFalse(d.IsGateway, "a forged answer must not redirect SOAP requests to another host");
        }

        [Test]
        public void DeviceDescription_AcceptsAbsoluteControlUrlOnTheSameHostAndOtherPort()
        {
            string xml = "<root><device><serviceList><service>" +
                         "<serviceType>urn:schemas-upnp-org:service:WANIPConnection:2</serviceType>" +
                         "<controlURL>http://192.168.1.1:7000/ctl</controlURL></service></serviceList></device></root>";
            UpnpDeviceDescription d = UpnpDeviceDescription.Parse(xml, new Uri("http://192.168.1.1:5000/desc.xml"));
            Assert.IsTrue(d.IsGateway);
            Assert.AreEqual("http://192.168.1.1:7000/ctl", d.WanConnectionServices[0].ControlUrl.ToString());
        }

        [Test]
        public void SsdpResponse_IsParsed()
        {
            string text = "HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=1800\r\nEXT:\r\nLOCATION: http://192.168.1.1:5000/rootDesc.xml\r\n" +
                          "Server: Linux/5.4 UPnP/1.1 MiniUPnPd/2.2\r\nST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n" +
                          "USN: uuid:abc::urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n\r\n";
            SsdpDeviceInfo info;
            Assert.IsTrue(SsdpDiscovery.TryParseResponse(text, out info));
            Assert.AreEqual("http://192.168.1.1:5000/rootDesc.xml", info.Location);
            Assert.AreEqual("Linux/5.4 UPnP/1.1 MiniUPnPd/2.2", info.Server, "header names are case-insensitive");
            Assert.AreEqual("max-age=1800", info.CacheControl);
            Assert.AreEqual(UpnpConstants.NotAvailable, info.Date);
            Assert.IsTrue(SsdpDiscovery.LooksLikeGateway(info));
        }

        [Test]
        public void SsdpResponse_RejectsOtherMessages()
        {
            SsdpDeviceInfo info;
            Assert.IsFalse(SsdpDiscovery.TryParseResponse(null, out info));
            Assert.IsFalse(SsdpDiscovery.TryParseResponse("", out info));
            Assert.IsFalse(SsdpDiscovery.TryParseResponse("NOTIFY * HTTP/1.1\r\nLOCATION: http://192.168.1.1/x\r\n\r\n", out info));
            Assert.IsFalse(SsdpDiscovery.TryParseResponse("HTTP/1.1 404 Not Found\r\nLOCATION: http://192.168.1.1/x\r\n\r\n", out info));
            Assert.IsFalse(SsdpDiscovery.TryParseResponse("HTTP/1.1 200 OK\r\nST: upnp:rootdevice\r\n\r\n", out info), "LOCATION is required");
        }

        [Test]
        public void SsdpResponse_RootDeviceAloneIsNotAGatewayHint()
        {
            SsdpDeviceInfo info;
            Assert.IsTrue(SsdpDiscovery.TryParseResponse("HTTP/1.1 200 OK\r\nLOCATION: http://192.168.1.5/d.xml\r\nST: upnp:rootdevice\r\nUSN: uuid:tv::upnp:rootdevice\r\n\r\n", out info));
            Assert.IsFalse(SsdpDiscovery.LooksLikeGateway(info));
        }

        [Test]
        public void SearchRequest_ContainsTheRequiredHeaders()
        {
            UpnpClientOptions options = new UpnpClientOptions();
            string request = SsdpDiscovery.BuildSearchRequest(options, "upnp:rootdevice");
            StringAssert.Contains("M-SEARCH * HTTP/1.1\r\n", request);
            StringAssert.Contains("HOST: 239.255.255.250:1900\r\n", request);
            StringAssert.Contains("MAN: \"ssdp:discover\"\r\n", request);
            StringAssert.Contains("ST: upnp:rootdevice\r\n", request);
            StringAssert.Contains("MX: 2\r\n", request);
            Assert.IsTrue(request.EndsWith("\r\n\r\n", StringComparison.Ordinal));
        }

        [Test]
        public void LocalNetworkAddresses_AreRecognized()
        {
            string[] local = { "192.168.1.1", "10.0.0.5", "172.16.0.1", "172.31.255.1", "169.254.1.1", "100.64.0.1", "127.0.0.1", "fe80::1", "fc00::1", "fd12::1", "::1", "::ffff:192.168.1.1" };
            string[] remote = { "8.8.8.8", "172.32.0.1", "172.15.0.1", "100.128.0.1", "203.0.113.1", "2001:db8::1", "2606:4700::1111" };

            foreach (string text in local)
            {
                Assert.IsTrue(UpnpNetwork.IsLocalNetworkAddress(IPAddress.Parse(text)), text + " should be local");
            }

            foreach (string text in remote)
            {
                Assert.IsFalse(UpnpNetwork.IsLocalNetworkAddress(IPAddress.Parse(text)), text + " should not be local");
            }

            Assert.IsFalse(UpnpNetwork.IsLocalNetworkAddress(null));
        }

        [Test]
        public void PublicAddresses_AreRecognized()
        {
            string[] pub = { "8.8.8.8", "1.1.1.1", "203.0.113.7", "172.15.255.255", "172.32.0.1", "100.63.255.255", "100.128.0.1", "193.168.0.1", "198.17.0.1", "198.20.0.1", "223.255.255.255", "2606:4700:4700::1111", "::ffff:8.8.8.8" };
            string[] other = { "0.0.0.0", "10.1.2.3", "100.64.0.1", "100.127.255.255", "127.0.0.1", "169.254.1.1", "172.16.0.1", "172.31.255.255", "192.168.1.1", "192.0.0.8", "198.18.0.1", "198.19.255.255", "224.0.0.1", "239.255.255.250", "240.0.0.1", "255.255.255.255", "::", "::1", "fe80::1", "fc00::1", "::ffff:192.168.1.1" };

            foreach (string text in pub)
            {
                Assert.IsTrue(UpnpNetwork.IsPublicAddress(IPAddress.Parse(text)), text + " should be public");
            }

            foreach (string text in other)
            {
                Assert.IsFalse(UpnpNetwork.IsPublicAddress(IPAddress.Parse(text)), text + " should not be public");
            }

            Assert.IsFalse(UpnpNetwork.IsPublicAddress(null));
        }

        [Test]
        public void LocalAddressLookup_ReturnsLoopbackForLoopback()
        {
            IPAddress address = UpnpNetwork.GetLocalAddressFor("127.0.0.1");
            Assert.IsNotNull(address);
            Assert.AreEqual("127.0.0.1", address.ToString());
        }

        [Test]
        public void SoapEnvelope_EscapesArgumentsAndNamesTheAction()
        {
            string body = SoapClient.BuildEnvelope(
                "urn:schemas-upnp-org:service:WANIPConnection:1",
                "AddPortMapping",
                new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("NewPortMappingDescription", "Tom & <Jerry>"),
                    new KeyValuePair<string, string>("NewRemoteHost", string.Empty),
                });

            StringAssert.Contains("<u:AddPortMapping xmlns:u=\"urn:schemas-upnp-org:service:WANIPConnection:1\">", body);
            StringAssert.Contains("<NewPortMappingDescription>Tom &amp; &lt;Jerry&gt;</NewPortMappingDescription>", body);
            StringAssert.Contains("<NewRemoteHost></NewRemoteHost>", body);
            StringAssert.Contains("</u:AddPortMapping></s:Body></s:Envelope>", body);

            // The result must be readable by a strict parser as well.
            System.Xml.XmlDocument strict = new System.Xml.XmlDocument();
            strict.LoadXml(body);
            Assert.AreEqual("AddPortMapping", strict.DocumentElement.FirstChild.FirstChild.LocalName);
        }

        [Test]
        public void SoapAction_IsQuoted()
        {
            Assert.AreEqual("\"urn:schemas-upnp-org:service:WANIPConnection:1#GetStatusInfo\"",
                SoapClient.BuildSoapAction("urn:schemas-upnp-org:service:WANIPConnection:1", "GetStatusInfo"));
        }

        [Test]
        public void SoapReply_ParsesOutputArguments()
        {
            HttpReply reply = new HttpReply { StatusCode = 200, Body = System.Text.Encoding.UTF8.GetBytes(
                "<s:Envelope xmlns:s=\"x\"><s:Body><u:GetExternalIPAddressResponse xmlns:u=\"y\">" +
                "<NewExternalIPAddress>198.51.100.4</NewExternalIPAddress></u:GetExternalIPAddressResponse></s:Body></s:Envelope>") };
            SoapResponse r = SoapClient.ParseReply(reply, "GetExternalIPAddress");
            Assert.IsTrue(r.Succeeded);
            Assert.AreEqual("198.51.100.4", r.Get("NewExternalIPAddress"));
            Assert.AreEqual("198.51.100.4", r.Get("newexternalipaddress"), "argument names are case-insensitive");
        }

        [Test]
        public void SoapReply_ParsesUpnpFault()
        {
            HttpReply reply = new HttpReply { StatusCode = 500, Body = System.Text.Encoding.UTF8.GetBytes(
                "<s:Envelope xmlns:s=\"x\"><s:Body><s:Fault><faultcode>s:Client</faultcode><faultstring>UPnPError</faultstring>" +
                "<detail><UPnPError xmlns=\"urn:schemas-upnp-org:control-1-0\"><errorCode>718</errorCode>" +
                "<errorDescription>ConflictInMappingEntry</errorDescription></UPnPError></detail></s:Fault></s:Body></s:Envelope>") };
            SoapResponse r = SoapClient.ParseReply(reply, "AddPortMapping");
            Assert.IsFalse(r.Succeeded);
            Assert.IsFalse(r.TransportFailed);
            Assert.AreEqual(718, r.UpnpErrorCode);
            Assert.AreEqual("ConflictInMappingEntry", r.UpnpErrorDescription);
            Assert.AreEqual("UPnPError", r.ErrorMessage);
        }

        [Test]
        public void SoapReply_HttpErrorWithoutFaultFails()
        {
            SoapResponse r = SoapClient.ParseReply(new HttpReply { StatusCode = 403, ReasonPhrase = "Forbidden" }, "AddPortMapping");
            Assert.IsFalse(r.Succeeded);
            Assert.AreEqual(403, r.HttpStatus);
            StringAssert.Contains("403", r.ErrorMessage);
        }

        [Test]
        public void SoapReply_UnknownWrapperFallsBackToLeafElements()
        {
            HttpReply reply = new HttpReply { StatusCode = 200, Body = System.Text.Encoding.UTF8.GetBytes(
                "<Envelope><Body><Weird><NewUptime>77</NewUptime></Weird></Body></Envelope>") };
            SoapResponse r = SoapClient.ParseReply(reply, "GetStatusInfo");
            Assert.IsTrue(r.Succeeded);
            Assert.AreEqual("77", r.Get("NewUptime"));
        }

        [Test]
        public void PortMappingInfo_ValidatesPortsAndLease()
        {
            Assert.IsNull(new PortMappingInfo(UpnpProtocol.Tcp, 7777, 7777).Validate());
            Assert.IsNotNull(new PortMappingInfo(UpnpProtocol.Tcp, 0, 7777).Validate());
            Assert.IsNotNull(new PortMappingInfo(UpnpProtocol.Tcp, 70000, 7777).Validate());
            Assert.IsNotNull(new PortMappingInfo(UpnpProtocol.Udp, 7777, 0).Validate());
            Assert.IsNotNull(new PortMappingInfo(UpnpProtocol.Udp, 7777, 7777, "x", -1).Validate());
        }

        [Test]
        public void PortMappingInfo_CloneIsIndependent()
        {
            PortMappingInfo a = new PortMappingInfo(UpnpProtocol.Udp, 1000, 2000, "a");
            PortMappingInfo b = a.Clone();
            b.ExternalPort = 1;
            Assert.AreEqual(1000, a.ExternalPort);
        }

        [Test]
        public void Protocol_ConvertsToAndFromWireText()
        {
            Assert.AreEqual("TCP", UpnpProtocol.Tcp.ToWireString());
            Assert.AreEqual("UDP", UpnpProtocol.Udp.ToWireString());

            UpnpProtocol p;
            Assert.IsTrue(UpnpProtocolExtensions.TryParse(" tcp ", out p));
            Assert.AreEqual(UpnpProtocol.Tcp, p);
            Assert.IsTrue(UpnpProtocolExtensions.TryParse("UDP", out p));
            Assert.AreEqual(UpnpProtocol.Udp, p);
            Assert.IsFalse(UpnpProtocolExtensions.TryParse("ICMP", out p));
            Assert.IsFalse(UpnpProtocolExtensions.TryParse(null, out p));
        }

        [Test]
        public void DefaultResult_IsSafeToPrint()
        {
            UpnpResult empty = default(UpnpResult);
            Assert.IsTrue(empty.Succeeded);
            Assert.AreEqual(string.Empty, empty.Message);
            Assert.IsNotNull(empty.ToString());

            UpnpResult failed = new UpnpResult(UpnpErrorCode.AddMappingFailed, "nope", 718, "ConflictInMappingEntry");
            StringAssert.Contains("718", failed.ToString());
            StringAssert.Contains("nope", failed.ToString());
        }

        [Test]
        public void ErrorTable_ExplainsTheCommonCodes()
        {
            Assert.AreNotEqual(string.Empty, UpnpErrors.Describe(718));
            Assert.AreNotEqual(string.Empty, UpnpErrors.Describe(725));
            Assert.AreNotEqual(string.Empty, UpnpErrors.Describe(714));
            Assert.AreEqual(string.Empty, UpnpErrors.Describe(99999));
        }
    }
}
