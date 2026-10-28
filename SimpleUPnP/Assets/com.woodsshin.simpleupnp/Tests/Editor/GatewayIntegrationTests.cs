// Copyright 2017-present WoodsShin, Inc. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace WoodsShin.SimpleUPNP.Tests
{
    /// <summary>
    /// End-to-end tests against a fake router on the loopback interface: SSDP search, device description,
    /// SOAP actions and the error cases of real routers.
    /// </summary>
    [TestFixture]
    public class GatewayIntegrationTests
    {
        private readonly List<IDisposable> _disposables = new List<IDisposable>();

        [TearDown]
        public void TearDown()
        {
            foreach (IDisposable d in _disposables)
            {
                d.Dispose();
            }

            _disposables.Clear();
        }

        private T Track<T>(T disposable) where T : IDisposable
        {
            _disposables.Add(disposable);
            return disposable;
        }

        private static T Wait<T>(Task<T> task, int timeoutMilliseconds = 20000)
        {
            if (!task.Wait(timeoutMilliseconds))
            {
                Assert.Fail("The operation did not finish within " + timeoutMilliseconds + " ms.");
            }

            return task.Result;
        }

        private static UpnpClientOptions Options(FakeSsdpResponder ssdp)
        {
            return new UpnpClientOptions
            {
                SsdpEndPoint = ssdp.EndPoint,
                IncludeLoopbackInterfaces = true,
                DiscoveryTimeout = TimeSpan.FromSeconds(2),
                DiscoveryGracePeriod = TimeSpan.FromMilliseconds(300),
                RequestTimeout = TimeSpan.FromSeconds(3),
                MaxRetries = 0,
            };
        }

        private UpnpClient NewClient(FakeSsdpResponder ssdp, Action<UpnpClientOptions> tweak = null)
        {
            UpnpClientOptions options = Options(ssdp);
            if (tweak != null)
            {
                tweak(options);
            }

            return Track(new UpnpClient(options));
        }

        private static int Count(List<string> actions, string name)
        {
            int count = 0;
            foreach (string action in actions)
            {
                if (action == name)
                {
                    count++;
                }
            }

            return count;
        }

        // ------------------------------------------------------------------------------------------------------
        // Discovery
        // ------------------------------------------------------------------------------------------------------

        [Test]
        public void Discover_FindsTheGatewayAndReadsTheManufacturer()
        {
            FakeGateway gateway = Track(new FakeGateway());
            FakeSsdpResponder ssdp = Track(new FakeSsdpResponder(gateway.Location));
            UpnpClient client = NewClient(ssdp);

            UpnpResult<IReadOnlyList<UpnpGateway>> result = Wait(client.DiscoverAsync(false));

            Assert.IsTrue(result.Succeeded, result.ToString());
            Assert.AreEqual(1, result.Value.Count);
            UpnpGateway found = result.Value[0];
            Assert.IsTrue(ReferenceEquals(found, client.SelectedGateway), "the first gateway is selected");
            Assert.AreEqual("Fake Router & Co", found.Manufacturer.FriendlyName);
            Assert.AreEqual("Fake Corp", found.Manufacturer.Manufacturer);
            Assert.AreEqual("FIGD-1", found.Manufacturer.ModelName);
            Assert.AreEqual("1.0", found.Manufacturer.SpecVersion);
            Assert.AreEqual(gateway.Location.ToString(), found.Location.ToString());
            StringAssert.Contains("InternetGatewayDevice", found.Ssdp.St);
            StringAssert.Contains("FakeIGD", found.Ssdp.Server);
            Assert.AreEqual("127.0.0.1", found.LocalIPAddress);
            Assert.IsTrue(ssdp.SearchCount >= 1);
            Assert.IsFalse(client.IsBusy);
            Assert.AreEqual(UpnpOperation.Idle, client.CurrentOperation);
        }

        [Test]
        public void Discover_IgnoresDevicesThatAreNotGateways()
        {
            FakeGateway tv = Track(new FakeGateway { IsGateway = false });
            FakeGateway router = Track(new FakeGateway());
            FakeSsdpResponder ssdp = Track(new FakeSsdpResponder(tv.Location, router.Location));
            UpnpClient client = NewClient(ssdp);

            UpnpResult<IReadOnlyList<UpnpGateway>> result = Wait(client.DiscoverAsync(false));

            Assert.IsTrue(result.Succeeded, result.ToString());
            Assert.AreEqual(1, result.Value.Count);
            Assert.AreEqual(router.Location.ToString(), result.Value[0].Location.ToString());
        }

        [Test]
        public void Discover_ReportsWhenNobodyAnswers()
        {
            FakeSsdpResponder ssdp = Track(new FakeSsdpResponder());
            UpnpClient client = NewClient(ssdp, o => o.DiscoveryTimeout = TimeSpan.FromMilliseconds(800));

            Stopwatch clock = Stopwatch.StartNew();
            UpnpResult<IReadOnlyList<UpnpGateway>> result = Wait(client.DiscoverAsync(false));

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(UpnpErrorCode.NoGatewayFound, result.ErrorCode);
            StringAssert.Contains("No UPnP device answered", result.Message);
            Assert.Less(clock.ElapsedMilliseconds, 4000, "the search must stop at the discovery timeout");
            Assert.AreEqual(0, client.Gateways.Count);
            Assert.IsNull(client.SelectedGateway);
        }

        [Test]
        public void Discover_ReportsWhenOnlyOtherDevicesAnswer()
        {
            FakeGateway tv = Track(new FakeGateway { IsGateway = false });
            FakeSsdpResponder ssdp = Track(new FakeSsdpResponder(tv.Location));
            UpnpClient client = NewClient(ssdp, o => o.DiscoveryTimeout = TimeSpan.FromMilliseconds(800));

            UpnpResult<IReadOnlyList<UpnpGateway>> result = Wait(client.DiscoverAsync(false));

            Assert.AreEqual(UpnpErrorCode.NoGatewayFound, result.ErrorCode);
            StringAssert.Contains("none of them", result.Message);
        }

        [Test]
        public void Discover_RejectsControlUrlsOnForeignHosts()
        {
            FakeGateway forged = Track(new FakeGateway { ForeignControlHost = "203.0.113.9" });
            FakeSsdpResponder ssdp = Track(new FakeSsdpResponder(forged.Location));
            UpnpClient client = NewClient(ssdp, o => o.DiscoveryTimeout = TimeSpan.FromMilliseconds(800));

            UpnpResult<IReadOnlyList<UpnpGateway>> result = Wait(client.DiscoverAsync(false));

            Assert.AreEqual(UpnpErrorCode.NoGatewayFound, result.ErrorCode);
        }

        [Test]
        public void Discover_KeepsGatewayInstancesBetweenSearches()
        {
            FakeGateway gateway = Track(new FakeGateway());
            FakeSsdpResponder ssdp = Track(new FakeSsdpResponder(gateway.Location));
            UpnpClient client = NewClient(ssdp);

            UpnpGateway first = Wait(client.DiscoverAsync(false)).Value[0];
            UpnpGateway second = Wait(client.DiscoverAsync(false)).Value[0];

            Assert.IsTrue(ReferenceEquals(first, second));
        }

        [Test]
        public void Discover_WithDefaultSettingsEndsCleanly()
        {
            // This test uses the real multicast address and the real adapters. Whatever the local network offers
            // (a router, nothing, blocked multicast), the call must finish and report an ordinary result.
            UpnpClient client = Track(new UpnpClient(new UpnpClientOptions { DiscoveryTimeout = TimeSpan.FromSeconds(1) }));

            Stopwatch clock = Stopwatch.StartNew();
            UpnpResult<IReadOnlyList<UpnpGateway>> result = Wait(client.DiscoverAsync(false), 15000);

            bool expected = result.Succeeded ||
                            result.ErrorCode == UpnpErrorCode.NoGatewayFound ||
                            result.ErrorCode == UpnpErrorCode.NetworkInitFailed;
            Assert.IsTrue(expected, result.ToString());
            Assert.Less(clock.ElapsedMilliseconds, 10000);
        }

        [Test]
        public void AddGateway_WorksWithoutAnSsdpSearch()
        {
            FakeGateway gateway = Track(new FakeGateway());
            FakeSsdpResponder ssdp = Track(new FakeSsdpResponder());
            UpnpClient client = NewClient(ssdp);

            UpnpResult<UpnpGateway> added = Wait(client.AddGatewayAsync(gateway.Location));
            Assert.IsTrue(added.Succeeded, added.ToString());
            Assert.IsTrue(ReferenceEquals(added.Value, client.SelectedGateway));

            UpnpResult mapped = Wait(client.AddPortMappingAsync(new PortMappingInfo(UpnpProtocol.Tcp, 5000, 5000)));
            Assert.IsTrue(mapped.Succeeded, mapped.ToString());
            Assert.AreEqual(0, ssdp.SearchCount, "no search is needed when a gateway is selected");
            Assert.AreEqual(1, gateway.Mappings.Count);
        }

        [Test]
        public void AddGateway_RejectsPublicAddressesAndOtherDevices()
        {
            FakeGateway tv = Track(new FakeGateway { IsGateway = false });
            FakeSsdpResponder ssdp = Track(new FakeSsdpResponder());
            UpnpClient client = NewClient(ssdp);

            UpnpResult<UpnpGateway> publicHost = Wait(client.AddGatewayAsync(new Uri("http://8.8.8.8/desc.xml")));
            Assert.AreEqual(UpnpErrorCode.InvalidArgument, publicHost.ErrorCode);

            UpnpResult<UpnpGateway> notGateway = Wait(client.AddGatewayAsync(tv.Location));
            Assert.AreEqual(UpnpErrorCode.NoGatewayFound, notGateway.ErrorCode);

            UpnpResult<UpnpGateway> missing = Wait(client.AddGatewayAsync(null));
            Assert.AreEqual(UpnpErrorCode.InvalidArgument, missing.ErrorCode);
        }

        // ------------------------------------------------------------------------------------------------------
        // Refresh
        // ------------------------------------------------------------------------------------------------------

        [Test]
        public void Refresh_ReadsStatusExternalAddressLinkAndMappings()
        {
            FakeGateway gateway = Track(new FakeGateway());
            gateway.Preload(new PortMappingInfo(UpnpProtocol.Tcp, 8080, 80, "web") { InternalClient = "192.168.1.20" });
            gateway.Preload(new PortMappingInfo(UpnpProtocol.Udp, 27015, 27015, "game", 3600) { InternalClient = "192.168.1.21", Enabled = false });
            FakeSsdpResponder ssdp = Track(new FakeSsdpResponder(gateway.Location));
            UpnpClient client = NewClient(ssdp);

            UpnpResult<IReadOnlyList<UpnpGateway>> discovered = Wait(client.DiscoverAsync(true));
            Assert.IsTrue(discovered.Succeeded, discovered.ToString());

            UpnpGateway found = client.SelectedGateway;
            Assert.IsTrue(found.LastResult.Succeeded, found.LastResult.ToString());
            Assert.AreEqual("Connected", found.Status.ConnectionStatus);
            Assert.AreEqual("ERROR_NONE", found.Status.LastConnectionError);
            Assert.AreEqual(4242, found.Status.Uptime);
            Assert.AreEqual("203.0.113.7", found.Status.ExternalIPAddress);
            Assert.AreEqual("203.0.113.7", found.ExternalIPAddress);
            Assert.AreEqual("IP_Routed", found.Status.ConnectionType);
            Assert.AreEqual(gateway.Location.ToString(), found.Status.DeviceLocation);
            Assert.AreEqual("Ethernet", found.LinkProperties.WanAccessType);
            Assert.AreEqual("100000000", found.LinkProperties.Layer1UpstreamMaxBitRate);
            Assert.AreEqual("1000000000", found.LinkProperties.Layer1DownstreamMaxBitRate);
            Assert.AreEqual("Up", found.LinkProperties.PhysicalLinkStatus);

            Assert.AreEqual(2, found.PortMappings.Count);
            Assert.AreEqual(UpnpProtocol.Tcp, found.PortMappings[0].Protocol);
            Assert.AreEqual(8080, found.PortMappings[0].ExternalPort);
            Assert.AreEqual(80, found.PortMappings[0].InternalPort);
            Assert.AreEqual("192.168.1.20", found.PortMappings[0].InternalClient);
            Assert.AreEqual("web", found.PortMappings[0].Description);
            Assert.IsTrue(found.PortMappings[0].Enabled);
            Assert.AreEqual(UpnpProtocol.Udp, found.PortMappings[1].Protocol);
            Assert.AreEqual(3600, found.PortMappings[1].LeaseDuration);
            Assert.IsFalse(found.PortMappings[1].Enabled);
        }

        [Test]
        public void Refresh_ParsesChunkedAnswers()
        {
            FakeGateway gateway = Track(new FakeGateway { Chunked = true });
            gateway.Preload(new PortMappingInfo(UpnpProtocol.Tcp, 8080, 80, "chunked") { InternalClient = "192.168.1.20" });
            FakeSsdpResponder ssdp = Track(new FakeSsdpResponder(gateway.Location));
            UpnpClient client = NewClient(ssdp);

            Wait(client.DiscoverAsync(true));

            Assert.AreEqual("203.0.113.7", client.SelectedGateway.ExternalIPAddress);
            Assert.AreEqual(1, client.SelectedGateway.PortMappings.Count);
            Assert.AreEqual("chunked", client.SelectedGateway.PortMappings[0].Description);
        }

        [Test]
        public void Refresh_StopsWhenTheRouterRepeatsEntries()
        {
            FakeGateway gateway = Track(new FakeGateway { RepeatFirstEntry = true });
            gateway.Preload(new PortMappingInfo(UpnpProtocol.Tcp, 1111, 1111, "a") { InternalClient = "192.168.1.2" });
            gateway.Preload(new PortMappingInfo(UpnpProtocol.Tcp, 2222, 2222, "b") { InternalClient = "192.168.1.2" });
            FakeSsdpResponder ssdp = Track(new FakeSsdpResponder(gateway.Location));
            UpnpClient client = NewClient(ssdp);

            Wait(client.DiscoverAsync(true));

            Assert.AreEqual(1, client.SelectedGateway.PortMappings.Count);
            Assert.AreEqual(2, Count(gateway.Actions, "GetGenericPortMappingEntry"));
        }

        [Test]
        public void Refresh_ReportsATableTheRouterDoesNotExpose()
        {
            FakeGateway gateway = Track(new FakeGateway { DisableGenericEntries = true });
            FakeSsdpResponder ssdp = Track(new FakeSsdpResponder(gateway.Location));
            UpnpClient client = NewClient(ssdp);

            Wait(client.DiscoverAsync(false));
            UpnpResult result = Wait(client.RefreshAsync(true));

            Assert.AreEqual(UpnpErrorCode.GetPortMappingEntryFailed, result.ErrorCode);
            Assert.AreEqual(401, result.UpnpFaultCode);
            Assert.AreEqual("203.0.113.7", client.SelectedGateway.ExternalIPAddress, "the other information is still available");
        }

        [Test]
        public void Refresh_UsesTheConnectedWanService()
        {
            FakeGateway gateway = Track(new FakeGateway { AddPppService = true, IpStatus = "Disconnected", PppStatus = "Connected" });
            FakeSsdpResponder ssdp = Track(new FakeSsdpResponder(gateway.Location));
            UpnpClient client = NewClient(ssdp);

            Wait(client.DiscoverAsync(true));

            Assert.AreEqual("198.51.100.99", client.SelectedGateway.ExternalIPAddress, "the PPP service is the connected one");
            Assert.AreEqual("Connected", client.SelectedGateway.Status.ConnectionStatus);
        }

        [Test]
        public void Refresh_PrefersTheIpServiceWhenBothAreConnected()
        {
            FakeGateway gateway = Track(new FakeGateway { AddPppService = true });
            FakeSsdpResponder ssdp = Track(new FakeSsdpResponder(gateway.Location));
            UpnpClient client = NewClient(ssdp);

            Wait(client.DiscoverAsync(true));

            Assert.AreEqual("203.0.113.7", client.SelectedGateway.ExternalIPAddress);
        }

        [Test]
        public void Refresh_FallsBackToTheFirstServiceWhenNoneIsConnected()
        {
            FakeGateway gateway = Track(new FakeGateway { AddPppService = true, IpStatus = "Disconnected", PppStatus = "Disconnected" });
            FakeSsdpResponder ssdp = Track(new FakeSsdpResponder(gateway.Location));
            UpnpClient client = NewClient(ssdp);

            Wait(client.DiscoverAsync(true));

            Assert.AreEqual("203.0.113.7", client.SelectedGateway.ExternalIPAddress);
            Assert.AreEqual("Disconnected", client.SelectedGateway.Status.ConnectionStatus);
        }

        [Test]
        public void Delete_ByMappingUsesTheRemoteHost()
        {
            FakeGateway gateway = Track(new FakeGateway());
            FakeSsdpResponder ssdp = Track(new FakeSsdpResponder(gateway.Location));
            UpnpClient client = NewClient(ssdp);
            Wait(client.DiscoverAsync(false));
            PortMappingInfo mapping = new PortMappingInfo(UpnpProtocol.Tcp, 9000, 9000, "restricted") { RemoteHost = "203.0.113.50" };
            Assert.IsTrue(Wait(client.AddPortMappingAsync(mapping)).Succeeded);
            Assert.AreEqual(1, gateway.Mappings.Count);

            // Without the remote host the router does not find the entry. That counts as success, but nothing changes.
            Assert.IsTrue(Wait(client.DeletePortMappingAsync(UpnpProtocol.Tcp, 9000)).Succeeded);
            Assert.AreEqual(1, gateway.Mappings.Count, "an entry that is limited to a remote host needs that host to be deleted");

            UpnpResult deleted = Wait(client.DeletePortMappingAsync(mapping));
            Assert.IsTrue(deleted.Succeeded, deleted.ToString());
            Assert.AreEqual(0, gateway.Mappings.Count);

            UpnpResult nothing = Wait(client.DeletePortMappingAsync((PortMappingInfo)null));
            Assert.AreEqual(UpnpErrorCode.InvalidArgument, nothing.ErrorCode);
        }

        [Test]
        public void ExternalAddress_TellsWhetherTheRouterIsReachableFromTheInternet()
        {
            FakeGateway gateway = Track(new FakeGateway());
            FakeSsdpResponder ssdp = Track(new FakeSsdpResponder(gateway.Location));
            UpnpClient client = NewClient(ssdp);

            Wait(client.DiscoverAsync(false));
            UpnpGateway router = client.SelectedGateway;
            Assert.IsFalse(router.HasPublicExternalAddress.HasValue, "unknown before the first refresh");

            Wait(router.RefreshAsync(false));
            Assert.AreEqual("203.0.113.7", router.ExternalIPAddress);
            Assert.AreEqual(true, router.HasPublicExternalAddress);

            string[] behindAnotherNat = { "192.168.0.5", "10.20.30.40", "100.72.1.9", "0.0.0.0" };
            foreach (string address in behindAnotherNat)
            {
                gateway.ExternalIp = address;
                Wait(router.RefreshAsync(false));
                Assert.AreEqual(address, router.ExternalIPAddress);
                Assert.AreEqual(false, router.HasPublicExternalAddress, address + " is not a public address");
            }
        }

        [Test]
        public void ExternalAddress_CanBeReadOnItsOwn()
        {
            FakeGateway gateway = Track(new FakeGateway { ExternalIp = "198.51.100.77" });
            FakeSsdpResponder ssdp = Track(new FakeSsdpResponder(gateway.Location));
            UpnpClient client = NewClient(ssdp);

            Wait(client.DiscoverAsync(false));
            UpnpResult<string> address = Wait(client.SelectedGateway.GetExternalIPAddressAsync());

            Assert.IsTrue(address.Succeeded, address.ToString());
            Assert.AreEqual("198.51.100.77", address.Value);
            Assert.AreEqual("198.51.100.77", client.SelectedGateway.ExternalIPAddress);
        }

        // ------------------------------------------------------------------------------------------------------
        // Port mappings
        // ------------------------------------------------------------------------------------------------------

        [Test]
        public void AddPortMapping_SendsTheExpectedArguments()
        {
            FakeGateway gateway = Track(new FakeGateway());
            FakeSsdpResponder ssdp = Track(new FakeSsdpResponder(gateway.Location));
            UpnpClient client = NewClient(ssdp);

            Wait(client.DiscoverAsync(false));
            UpnpResult result = Wait(client.AddPortMappingAsync(new PortMappingInfo(UpnpProtocol.Udp, 7777, 7778, "Tom & Jerry <game>")));

            Assert.IsTrue(result.Succeeded, result.ToString());
            List<PortMappingInfo> table = gateway.Mappings;
            Assert.AreEqual(1, table.Count);
            Assert.AreEqual(UpnpProtocol.Udp, table[0].Protocol);
            Assert.AreEqual(7777, table[0].ExternalPort);
            Assert.AreEqual(7778, table[0].InternalPort);
            Assert.AreEqual("127.0.0.1", table[0].InternalClient, "an empty InternalClient becomes the local address");
            Assert.AreEqual("Tom & Jerry <game>", table[0].Description, "special characters survive the XML round trip");
            Assert.IsTrue(table[0].Enabled);
            Assert.AreEqual(0, table[0].LeaseDuration);
            Assert.AreEqual(string.Empty, table[0].RemoteHost);
        }

        [Test]
        public void AddPortMapping_SearchesFirstWhenNoGatewayIsKnown()
        {
            FakeGateway gateway = Track(new FakeGateway());
            FakeSsdpResponder ssdp = Track(new FakeSsdpResponder(gateway.Location));
            UpnpClient client = NewClient(ssdp);

            UpnpResult result = Wait(client.AddPortMappingAsync(new PortMappingInfo(UpnpProtocol.Tcp, 9000, 9000, "auto")));

            Assert.IsTrue(result.Succeeded, result.ToString());
            Assert.IsTrue(ssdp.SearchCount >= 1);
            Assert.AreEqual(1, gateway.Mappings.Count);
        }

        [Test]
        public void AddPortMapping_UsesAnExplicitInternalClient()
        {
            FakeGateway gateway = Track(new FakeGateway());
            FakeSsdpResponder ssdp = Track(new FakeSsdpResponder(gateway.Location));
            UpnpClient client = NewClient(ssdp);

            PortMappingInfo mapping = new PortMappingInfo(UpnpProtocol.Tcp, 9001, 9002, "explicit") { InternalClient = "192.168.1.55", LeaseDuration = 0 };
            Assert.IsTrue(Wait(client.AddPortMappingAsync(mapping)).Succeeded);

            Assert.AreEqual("192.168.1.55", gateway.Mappings[0].InternalClient);
        }

        [Test]
        public void AddPortMapping_ReplacesAnExistingMappingOfTheSamePort()
        {
            FakeGateway gateway = Track(new FakeGateway());
            FakeSsdpResponder ssdp = Track(new FakeSsdpResponder(gateway.Location));
            UpnpClient client = NewClient(ssdp);

            Wait(client.AddPortMappingAsync(new PortMappingInfo(UpnpProtocol.Tcp, 9100, 1, "first")));
            Wait(client.AddPortMappingAsync(new PortMappingInfo(UpnpProtocol.Tcp, 9100, 2, "second")));

            List<PortMappingInfo> table = gateway.Mappings;
            Assert.AreEqual(1, table.Count);
            Assert.AreEqual(2, table[0].InternalPort);
        }

        [Test]
        public void AddPortMapping_RejectsInvalidInputBeforeAnyNetworkTraffic()
        {
            FakeGateway gateway = Track(new FakeGateway());
            FakeSsdpResponder ssdp = Track(new FakeSsdpResponder(gateway.Location));
            UpnpClient client = NewClient(ssdp);

            UpnpResult badPort = Wait(client.AddPortMappingAsync(new PortMappingInfo(UpnpProtocol.Tcp, 0, 7777)));
            UpnpResult missing = Wait(client.AddPortMappingAsync(null));

            Assert.AreEqual(UpnpErrorCode.InvalidArgument, badPort.ErrorCode);
            Assert.AreEqual(UpnpErrorCode.InvalidArgument, missing.ErrorCode);
            Assert.AreEqual(0, ssdp.SearchCount);
            Assert.AreEqual(0, gateway.Actions.Count);
        }

        [Test]
        public void AddPortMapping_FallsBackToAPermanentLease()
        {
            FakeGateway gateway = Track(new FakeGateway { OnlyPermanentLeases = true });
            FakeSsdpResponder ssdp = Track(new FakeSsdpResponder(gateway.Location));
            UpnpClient client = NewClient(ssdp);

            UpnpResult result = Wait(client.AddPortMappingAsync(new PortMappingInfo(UpnpProtocol.Tcp, 9200, 9200, "lease", 3600)));

            Assert.IsTrue(result.Succeeded, result.ToString());
            Assert.AreEqual(0, gateway.Mappings[0].LeaseDuration);
            Assert.AreEqual(2, Count(gateway.Actions, "AddPortMapping"), "the request is repeated once with lease 0");
        }

        [Test]
        public void AddPortMapping_ReportsTheRouterErrorWhenTheFallbackIsOff()
        {
            FakeGateway gateway = Track(new FakeGateway { OnlyPermanentLeases = true });
            FakeSsdpResponder ssdp = Track(new FakeSsdpResponder(gateway.Location));
            UpnpClient client = NewClient(ssdp, o => o.FallbackToPermanentLease = false);

            UpnpResult result = Wait(client.AddPortMappingAsync(new PortMappingInfo(UpnpProtocol.Tcp, 9200, 9200, "lease", 3600)));

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(UpnpErrorCode.AddMappingFailed, result.ErrorCode);
            Assert.AreEqual(725, result.UpnpFaultCode);
            StringAssert.Contains("permanent", result.Message);
            Assert.AreEqual(0, gateway.Mappings.Count);
        }

        [Test]
        public void AddPortMapping_ReportsAConflict()
        {
            FakeGateway gateway = Track(new FakeGateway { ConflictOnAdd = true });
            FakeSsdpResponder ssdp = Track(new FakeSsdpResponder(gateway.Location));
            UpnpClient client = NewClient(ssdp);

            UpnpResult result = Wait(client.AddPortMappingAsync(new PortMappingInfo(UpnpProtocol.Tcp, 9300, 9300)));

            Assert.AreEqual(UpnpErrorCode.AddMappingFailed, result.ErrorCode);
            Assert.AreEqual(718, result.UpnpFaultCode);
            Assert.AreEqual("ConflictInMappingEntry", result.UpnpFaultDescription);
            StringAssert.Contains("already mapped", result.Message);
        }

        [Test]
        public void GetPortMappings_ListsWhatWasAdded()
        {
            FakeGateway gateway = Track(new FakeGateway());
            FakeSsdpResponder ssdp = Track(new FakeSsdpResponder(gateway.Location));
            UpnpClient client = NewClient(ssdp);

            Wait(client.AddPortMappingAsync(new PortMappingInfo(UpnpProtocol.Tcp, 9400, 9400, "one")));
            Wait(client.AddPortMappingAsync(new PortMappingInfo(UpnpProtocol.Udp, 9401, 9401, "two")));

            UpnpResult<IReadOnlyList<PortMappingInfo>> list = Wait(client.GetPortMappingsAsync());

            Assert.IsTrue(list.Succeeded, list.ToString());
            Assert.AreEqual(2, list.Value.Count);
            Assert.AreEqual("one", list.Value[0].Description);
            Assert.AreEqual(UpnpProtocol.Udp, list.Value[1].Protocol);
            Assert.AreEqual(2, client.SelectedGateway.PortMappings.Count);
        }

        [Test]
        public void GetPortMappings_OfAnEmptyTableIsASuccess()
        {
            FakeGateway gateway = Track(new FakeGateway());
            FakeSsdpResponder ssdp = Track(new FakeSsdpResponder(gateway.Location));
            UpnpClient client = NewClient(ssdp);

            UpnpResult<IReadOnlyList<PortMappingInfo>> list = Wait(client.GetPortMappingsAsync());

            Assert.IsTrue(list.Succeeded, list.ToString());
            Assert.AreEqual(0, list.Value.Count);
        }

        [Test]
        public void GetPortMapping_ReturnsNullWhenThePortIsNotMapped()
        {
            FakeGateway gateway = Track(new FakeGateway());
            FakeSsdpResponder ssdp = Track(new FakeSsdpResponder(gateway.Location));
            UpnpClient client = NewClient(ssdp);

            Wait(client.DiscoverAsync(false));
            UpnpGateway router = client.SelectedGateway;

            UpnpResult<PortMappingInfo> missing = Wait(router.GetPortMappingAsync(UpnpProtocol.Tcp, 9500));
            Assert.IsTrue(missing.Succeeded, missing.ToString());
            Assert.IsNull(missing.Value);

            Wait(router.AddPortMappingAsync(new PortMappingInfo(UpnpProtocol.Tcp, 9500, 9501, "specific")));
            UpnpResult<PortMappingInfo> present = Wait(router.GetPortMappingAsync(UpnpProtocol.Tcp, 9500));
            Assert.IsTrue(present.Succeeded, present.ToString());
            Assert.IsNotNull(present.Value);
            Assert.AreEqual(9500, present.Value.ExternalPort);
            Assert.AreEqual(9501, present.Value.InternalPort);
            Assert.AreEqual("specific", present.Value.Description);
            Assert.AreEqual(UpnpProtocol.Tcp, present.Value.Protocol);
        }

        [Test]
        public void DeletePortMapping_RemovesTheMappingAndIsIdempotent()
        {
            FakeGateway gateway = Track(new FakeGateway());
            FakeSsdpResponder ssdp = Track(new FakeSsdpResponder(gateway.Location));
            UpnpClient client = NewClient(ssdp);

            Wait(client.AddPortMappingAsync(new PortMappingInfo(UpnpProtocol.Tcp, 9600, 9600, "temp")));
            Assert.AreEqual(1, gateway.Mappings.Count);

            UpnpResult first = Wait(client.DeletePortMappingAsync(UpnpProtocol.Tcp, 9600));
            Assert.IsTrue(first.Succeeded, first.ToString());
            Assert.AreEqual(0, gateway.Mappings.Count);

            UpnpResult second = Wait(client.DeletePortMappingAsync(UpnpProtocol.Tcp, 9600));
            Assert.IsTrue(second.Succeeded, "a mapping that is already gone counts as deleted");
            Assert.AreEqual(714, second.UpnpFaultCode);
        }

        [Test]
        public void DeletePortMapping_OnlyRemovesTheMatchingProtocol()
        {
            FakeGateway gateway = Track(new FakeGateway());
            FakeSsdpResponder ssdp = Track(new FakeSsdpResponder(gateway.Location));
            UpnpClient client = NewClient(ssdp);

            Wait(client.AddPortMappingAsync(new PortMappingInfo(UpnpProtocol.Tcp, 9700, 9700, "tcp")));
            Wait(client.AddPortMappingAsync(new PortMappingInfo(UpnpProtocol.Udp, 9700, 9700, "udp")));
            Wait(client.DeletePortMappingAsync(UpnpProtocol.Udp, 9700));

            List<PortMappingInfo> table = gateway.Mappings;
            Assert.AreEqual(1, table.Count);
            Assert.AreEqual(UpnpProtocol.Tcp, table[0].Protocol);
        }

        [Test]
        public void SelectGateway_ChoosesTheTarget()
        {
            FakeGateway first = Track(new FakeGateway());
            FakeGateway second = Track(new FakeGateway());
            FakeSsdpResponder ssdp = Track(new FakeSsdpResponder(first.Location, second.Location));
            UpnpClient client = NewClient(ssdp);

            Assert.AreEqual(2, Wait(client.DiscoverAsync(false)).Value.Count);
            Assert.IsFalse(client.SelectGateway(5));
            Assert.IsFalse(client.SelectGateway(-1));
            Assert.IsFalse(client.SelectGateway((UpnpGateway)null));

            UpnpGateway target = client.Gateways[1];
            Assert.IsTrue(client.SelectGateway(target));
            Wait(client.AddPortMappingAsync(new PortMappingInfo(UpnpProtocol.Tcp, 9800, 9800, "second only")));

            int total = first.Mappings.Count + second.Mappings.Count;
            Assert.AreEqual(1, total);
            Assert.AreEqual(1, (target.Location.Port == first.Port ? first : second).Mappings.Count);
        }

        // ------------------------------------------------------------------------------------------------------
        // All gateways
        // ------------------------------------------------------------------------------------------------------

        [Test]
        public void PerformAll_AddsAndDeletesOnEveryGateway()
        {
            FakeGateway first = Track(new FakeGateway());
            FakeGateway second = Track(new FakeGateway());
            FakeSsdpResponder ssdp = Track(new FakeSsdpResponder(first.Location, second.Location));
            UpnpClient client = NewClient(ssdp);
            PortMappingInfo mapping = new PortMappingInfo(UpnpProtocol.Udp, 9900, 9900, "everywhere");

            UpnpResult added = Wait(client.PerformAllGatewaysAsync(UpnpAction.AddPort, mapping));
            Assert.IsTrue(added.Succeeded, added.ToString());
            StringAssert.Contains("2 of 2", added.Message);
            Assert.AreEqual(1, first.Mappings.Count);
            Assert.AreEqual(1, second.Mappings.Count);

            UpnpResult deleted = Wait(client.PerformAllGatewaysAsync(UpnpAction.DeletePort, mapping));
            Assert.IsTrue(deleted.Succeeded, deleted.ToString());
            Assert.AreEqual(0, first.Mappings.Count);
            Assert.AreEqual(0, second.Mappings.Count);
        }

        [Test]
        public void PerformAll_SucceedsWhenAtLeastOneGatewayAccepts()
        {
            FakeGateway good = Track(new FakeGateway());
            FakeGateway bad = Track(new FakeGateway { ConflictOnAdd = true });
            FakeSsdpResponder ssdp = Track(new FakeSsdpResponder(good.Location, bad.Location));
            UpnpClient client = NewClient(ssdp);

            UpnpResult result = Wait(client.PerformAllGatewaysAsync(UpnpAction.AddPort, new PortMappingInfo(UpnpProtocol.Tcp, 9910, 9910)));

            Assert.IsTrue(result.Succeeded, result.ToString());
            StringAssert.Contains("1 of 2", result.Message);
            Assert.AreEqual(1, good.Mappings.Count);
            Assert.AreEqual(0, bad.Mappings.Count);
        }

        [Test]
        public void PerformAll_FailsWhenEveryGatewayRejects()
        {
            FakeGateway first = Track(new FakeGateway { ConflictOnAdd = true });
            FakeGateway second = Track(new FakeGateway { ConflictOnAdd = true });
            FakeSsdpResponder ssdp = Track(new FakeSsdpResponder(first.Location, second.Location));
            UpnpClient client = NewClient(ssdp);

            UpnpResult result = Wait(client.PerformAllGatewaysAsync(UpnpAction.AddPort, new PortMappingInfo(UpnpProtocol.Tcp, 9920, 9920)));

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(UpnpErrorCode.AddMappingFailed, result.ErrorCode);
            Assert.AreEqual(718, result.UpnpFaultCode);
            StringAssert.Contains("0 of 2", result.Message);
        }

        [Test]
        public void PerformAll_ReportsWhenThereIsNoGateway()
        {
            FakeSsdpResponder ssdp = Track(new FakeSsdpResponder());
            UpnpClient client = NewClient(ssdp, o => o.DiscoveryTimeout = TimeSpan.FromMilliseconds(600));

            UpnpResult result = Wait(client.PerformAllGatewaysAsync(UpnpAction.AddPort, new PortMappingInfo(UpnpProtocol.Tcp, 9930, 9930)));

            Assert.AreEqual(UpnpErrorCode.NoGatewayFound, result.ErrorCode);
        }

        // ------------------------------------------------------------------------------------------------------
        // Failure handling
        // ------------------------------------------------------------------------------------------------------

        [Test]
        public void Request_TimesOutQuicklyWhenTheRouterStaysSilent()
        {
            FakeGateway gateway = Track(new FakeGateway { SilentSoap = true });
            FakeSsdpResponder ssdp = Track(new FakeSsdpResponder(gateway.Location));
            UpnpClient client = NewClient(ssdp, o => o.RequestTimeout = TimeSpan.FromMilliseconds(600));

            Wait(client.DiscoverAsync(false));
            Stopwatch clock = Stopwatch.StartNew();
            UpnpResult result = Wait(client.SelectedGateway.RefreshAsync(true));

            Assert.AreEqual(UpnpErrorCode.RequestTimeout, result.ErrorCode, result.ToString());
            Assert.Less(clock.ElapsedMilliseconds, 2500, "a silent router must not be asked four times in a row");
            Assert.AreEqual(1, Count(gateway.Actions, "GetStatusInfo"));
            Assert.AreEqual(0, Count(gateway.Actions, "GetExternalIPAddress"));
        }

        [Test]
        public void Request_IsRetriedAfterATimeout()
        {
            FakeGateway gateway = Track(new FakeGateway { SilentSoap = true });
            FakeSsdpResponder ssdp = Track(new FakeSsdpResponder(gateway.Location));
            UpnpClient client = NewClient(ssdp, o =>
            {
                o.RequestTimeout = TimeSpan.FromMilliseconds(400);
                o.MaxRetries = 2;
            });

            Wait(client.DiscoverAsync(false));
            UpnpResult result = Wait(client.SelectedGateway.GetExternalIPAddressAsync()).Result;

            Assert.AreEqual(UpnpErrorCode.RequestTimeout, result.ErrorCode);
            Assert.AreEqual(3, Count(gateway.Actions, "GetExternalIPAddress"));
        }

        [Test]
        public void Cancellation_IsReportedAsCanceled()
        {
            FakeGateway gateway = Track(new FakeGateway { SilentSoap = true });
            FakeSsdpResponder ssdp = Track(new FakeSsdpResponder(gateway.Location));
            UpnpClient client = NewClient(ssdp, o => o.RequestTimeout = TimeSpan.FromSeconds(30));

            Wait(client.DiscoverAsync(false));
            using (CancellationTokenSource cts = new CancellationTokenSource())
            {
                cts.CancelAfter(300);
                Stopwatch clock = Stopwatch.StartNew();
                UpnpResult result = Wait(client.SelectedGateway.RefreshAsync(true, cts.Token));

                Assert.AreEqual(UpnpErrorCode.Canceled, result.ErrorCode, result.ToString());
                Assert.Less(clock.ElapsedMilliseconds, 5000);
            }
        }

        [Test]
        public void Cancellation_AlsoStopsTheSearch()
        {
            FakeSsdpResponder ssdp = Track(new FakeSsdpResponder());
            UpnpClient client = NewClient(ssdp, o => o.DiscoveryTimeout = TimeSpan.FromSeconds(30));

            using (CancellationTokenSource cts = new CancellationTokenSource())
            {
                cts.CancelAfter(300);
                Stopwatch clock = Stopwatch.StartNew();
                UpnpResult<IReadOnlyList<UpnpGateway>> result = Wait(client.DiscoverAsync(false, cts.Token));

                Assert.AreEqual(UpnpErrorCode.Canceled, result.ErrorCode, result.ToString());
                Assert.Less(clock.ElapsedMilliseconds, 5000);
            }
        }

        [Test]
        public void Cancellation_EndsPromptlyAtEveryMomentOfTheRequest()
        {
            // The request is stopped while it connects, while it is written and while it waits for the answer. Closing a
            // socket does not wake up a pending connect on every runtime (Mono keeps waiting for it), so a request that
            // only ends when the router answers or when the system gives up shows up here.
            FakeGateway gateway = Track(new FakeGateway { SilentSoap = true });
            FakeSsdpResponder ssdp = Track(new FakeSsdpResponder());
            double[] delaysInMilliseconds = { 0, 0.05, 0.1, 0.2, 0.5, 1, 2, 4 };

            foreach (double delay in delaysInMilliseconds)
            {
                for (int round = 0; round < 4; round++)
                {
                    UpnpClient client = NewClient(ssdp, o => o.RequestTimeout = TimeSpan.FromSeconds(30));
                    Wait(client.AddGatewayAsync(gateway.Location));

                    using (CancellationTokenSource cts = new CancellationTokenSource())
                    {
                        Task<UpnpResult> running = client.AddPortMappingAsync(new PortMappingInfo(UpnpProtocol.Udp, 7777, 7777, "cancel"), cts.Token);

                        Stopwatch wait = Stopwatch.StartNew();
                        while (wait.Elapsed.TotalMilliseconds < delay)
                        {
                            // Spinning on purpose: the delays are shorter than a sleep can be.
                        }

                        // Half of the rounds cancel the token, the other half dispose the client.
                        if (round % 2 == 0)
                        {
                            cts.Cancel();
                        }
                        else
                        {
                            client.Dispose();
                        }

                        string when = "canceled after " + delay + " ms (round " + round + ")";
                        if (!running.Wait(5000))
                        {
                            Assert.Fail("The request did not end when it was " + when + ".");
                        }

                        Assert.AreEqual(UpnpErrorCode.Canceled, running.Result.ErrorCode, when + ": " + running.Result);
                    }
                }
            }
        }

        [Test]
        public void Timeout_EndsPromptlyEvenWhenItHitsWhileTheRequestConnects()
        {
            // Timeouts of a few milliseconds run out while the request is still connecting or writing.
            FakeGateway gateway = Track(new FakeGateway { SilentSoap = true });
            FakeSsdpResponder ssdp = Track(new FakeSsdpResponder());
            int[] timeoutsInMilliseconds = { 1, 2, 3, 5, 8, 13 };

            foreach (int timeout in timeoutsInMilliseconds)
            {
                for (int round = 0; round < 4; round++)
                {
                    UpnpClient client = NewClient(ssdp, o => o.RequestTimeout = TimeSpan.FromSeconds(10));
                    Wait(client.AddGatewayAsync(gateway.Location));
                    client.Options.RequestTimeout = TimeSpan.FromMilliseconds(timeout);

                    Task<UpnpResult> running = client.AddPortMappingAsync(new PortMappingInfo(UpnpProtocol.Tcp, 7777, 7777, "timeout"));

                    string when = "a timeout of " + timeout + " ms (round " + round + ")";
                    if (!running.Wait(5000))
                    {
                        Assert.Fail("The request did not end with " + when + ".");
                    }

                    Assert.AreEqual(UpnpErrorCode.RequestTimeout, running.Result.ErrorCode, when + ": " + running.Result);
                }
            }
        }

        [Test]
        public void ClientState_ShowsTheRunningOperationAndEndsOnDispose()
        {
            FakeGateway gateway = Track(new FakeGateway { SilentSoap = true });
            FakeSsdpResponder ssdp = Track(new FakeSsdpResponder());
            UpnpClient client = NewClient(ssdp, o => o.RequestTimeout = TimeSpan.FromSeconds(30));
            Wait(client.AddGatewayAsync(gateway.Location));

            Task<UpnpResult> running = client.RefreshAsync(true);

            Stopwatch clock = Stopwatch.StartNew();
            while (!client.IsBusy && clock.ElapsedMilliseconds < 3000)
            {
                Thread.Sleep(10);
            }

            Assert.IsTrue(client.IsBusy);
            Assert.AreEqual(UpnpOperation.RefreshingGateway, client.CurrentOperation);

            client.Dispose();
            UpnpResult result = Wait(running);

            Assert.AreEqual(UpnpErrorCode.Canceled, result.ErrorCode);
            Assert.IsFalse(client.IsBusy);
            Assert.AreEqual(UpnpOperation.Idle, client.CurrentOperation);

            UpnpResult<IReadOnlyList<UpnpGateway>> after = Wait(client.DiscoverAsync(false));
            Assert.AreEqual(UpnpErrorCode.Canceled, after.ErrorCode, "a disposed client refuses new work");
        }

        [Test]
        public void Gateway_SerializesRequestsToTheSameRouter()
        {
            FakeGateway gateway = Track(new FakeGateway());
            FakeSsdpResponder ssdp = Track(new FakeSsdpResponder(gateway.Location));
            UpnpClient client = NewClient(ssdp);
            Wait(client.DiscoverAsync(false));
            UpnpGateway router = client.SelectedGateway;

            List<Task<UpnpResult>> adds = new List<Task<UpnpResult>>();
            for (int i = 0; i < 8; i++)
            {
                adds.Add(router.AddPortMappingAsync(new PortMappingInfo(UpnpProtocol.Tcp, 10000 + i, 10000 + i, "parallel " + i)));
            }

            foreach (Task<UpnpResult> add in adds)
            {
                Assert.IsTrue(Wait(add).Succeeded);
            }

            Assert.AreEqual(8, gateway.Mappings.Count);
        }

        // ------------------------------------------------------------------------------------------------------
        // HTTP details
        // ------------------------------------------------------------------------------------------------------

        [Test]
        public void MiniHttp_ReadsABodyThatEndsWithTheConnection()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                Task server = Task.Run(async () =>
                {
                    using (TcpClient client = await listener.AcceptTcpClientAsync().ConfigureAwait(false))
                    {
                        NetworkStream stream = client.GetStream();
                        byte[] buffer = new byte[4096];
                        await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                        byte[] answer = Encoding.ASCII.GetBytes("HTTP/1.0 200 OK\r\nContent-Type: text/xml\r\n\r\n<root>no length header</root>");
                        await stream.WriteAsync(answer, 0, answer.Length).ConfigureAwait(false);
                    }
                });

                HttpReply reply = Wait(MiniHttp.SendAsync(new Uri("http://127.0.0.1:" + port + "/x"), "GET", null, null, TimeSpan.FromSeconds(3), CancellationToken.None));
                Assert.AreEqual(200, reply.StatusCode);
                Assert.AreEqual("<root>no length header</root>", reply.BodyText);
                server.Wait(3000);
            }
            finally
            {
                listener.Stop();
            }
        }

        [Test]
        public void MiniHttp_SkipsInterimResponses()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                Task server = Task.Run(async () =>
                {
                    using (TcpClient client = await listener.AcceptTcpClientAsync().ConfigureAwait(false))
                    {
                        NetworkStream stream = client.GetStream();
                        byte[] buffer = new byte[4096];
                        await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                        byte[] answer = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\nHTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok");
                        await stream.WriteAsync(answer, 0, answer.Length).ConfigureAwait(false);
                    }
                });

                HttpReply reply = Wait(MiniHttp.SendAsync(new Uri("http://127.0.0.1:" + port + "/x"), "GET", null, null, TimeSpan.FromSeconds(3), CancellationToken.None));
                Assert.AreEqual(200, reply.StatusCode);
                Assert.AreEqual("ok", reply.BodyText);
                server.Wait(3000);
            }
            finally
            {
                listener.Stop();
            }
        }

        [Test]
        public void MiniHttp_RefusesHeadersWithLineBreaks()
        {
            List<KeyValuePair<string, string>> headers = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("X-Test", "a\r\nInjected: yes"),
            };

            Task<HttpReply> task = MiniHttp.SendAsync(new Uri("http://127.0.0.1:9/x"), "GET", headers, null, TimeSpan.FromSeconds(1), CancellationToken.None);
            bool threw = false;
            try
            {
                task.Wait(3000);
            }
            catch (AggregateException ex)
            {
                threw = ex.InnerException is ArgumentException;
            }

            Assert.IsTrue(threw, "header injection must be refused");
        }
    }
}
