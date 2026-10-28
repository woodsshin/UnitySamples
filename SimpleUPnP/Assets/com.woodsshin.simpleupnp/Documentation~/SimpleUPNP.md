# Simple UPNP - Manual

Version 1.0.0 - for Unity 2022.3 and newer

* [1. What it does](#1-what-it-does)
* [2. Installation](#2-installation)
* [3. Getting started](#3-getting-started)
* [4. UPNP Manager component](#4-upnp-manager-component)
* [5. Gateway Tester window](#5-gateway-tester-window)
* [6. Code API](#6-code-api)
* [7. Results, error codes and troubleshooting](#7-results-error-codes-and-troubleshooting)
* [8. Platform notes](#platform-notes)
* [9. Security and good practice](#9-security-and-good-practice)
* [10. Coming from the Unreal Engine plugin](#10-coming-from-the-unreal-engine-plugin)
* [11. Unit tests](#11-unit-tests)

---

## 1. What it does

Home routers block incoming connections. If one player hosts a game, the others cannot connect unless the host
forwards a port on the router by hand. Most routers offer **UPnP** (Universal Plug and Play): a program on the local
network can ask the router to forward a port. Simple UPNP does exactly that:

1. **Discovery** - sends an SSDP `M-SEARCH` on every network adapter and collects the answers.
2. **Description** - downloads the device description of each answer and keeps the devices that are
   Internet Gateway Devices (routers) with a `WANIPConnection` or `WANPPPConnection` service.
3. **Control** - talks to that service with SOAP: `GetStatusInfo`, `GetExternalIPAddress`, `GetConnectionTypeInfo`,
   `GetCommonLinkProperties`, `GetGenericPortMappingEntry`, `AddPortMapping`, `DeletePortMapping`.

What it **cannot** do:

* It cannot help when UPnP is switched off in the router or when the router blocks it.
* It cannot help when the router is not reachable from the internet: behind **another router** (double NAT) or
  behind the network of the provider (**carrier-grade NAT**). The mapping may succeed, but players still cannot
  connect. `UpnpGateway.HasPublicExternalAddress` tells you when this is the case.
* It does not replace a relay or NAT punch-through service. Use both when you need every player to connect.

## 2. Installation

**Asset Store / Package Manager:** open **Window > Package Manager > My Assets**, search for *Simple UPNP*,
then **Download** and **Import**.

The package contains the namespace `WoodsShin.SimpleUPNP` and three assemblies that are referenced automatically:

| Assembly | Contents |
| --- | --- |
| `WoodsShin.SimpleUPNP.Core` | Pure C# library, no `UnityEngine` reference. |
| `WoodsShin.SimpleUPNP.Runtime` | The `UpnpManager` component. |
| `WoodsShin.SimpleUPNP.Editor` | The Gateway Tester window. |

If your project uses assembly definitions, add `WoodsShin.SimpleUPNP.Runtime` (or only `WoodsShin.SimpleUPNP.Core`)
to the references of your assembly definition.

**Sample:** in the Package Manager select *Simple UPNP*, open **Samples** and import **Demo (IMGUI)**.
Add the `UpnpDemo` component to an empty GameObject and press Play.

## 3. Getting started

### 3.1 Without code

1. Create an empty GameObject and add **Simple UPNP > UPNP Manager**.
2. Edit **Startup Mappings**: protocol, external port and internal port of your game.
3. Tick **Discover On Start** and **Open Startup Mappings On Start**.
4. Press Play. The mappings are opened. They are closed again when you stop Play Mode or quit the game.

You can also call the methods from UnityEvents, for example from a *Host Game* button:
`UpnpManager.OpenUdpPort(int)`, `OpenTcpPort(int)`, `CloseUdpPort(int)`, `CloseTcpPort(int)`.
`On Succeeded (string)` and `On Failed (string)` tell you how it went.

### 3.2 With code (`async` / `await`)

```csharp
using UnityEngine;
using WoodsShin.SimpleUPNP;

public class HostGame : MonoBehaviour
{
    private async void Start()
    {
        UpnpManager upnp = UpnpManager.GetOrCreate();

        UpnpResult opened = await upnp.OpenPortAsync(UpnpProtocol.Udp, 7777, description: "My Game");
        if (!opened.Succeeded)
        {
            Debug.LogWarning("UPnP failed: " + opened);        // e.g. "AddMappingFailed (UPnP error 718: ...)"
            return;
        }

        await upnp.RefreshGatewayAsync();
        UpnpGateway router = upnp.SelectedGateway;
        Debug.Log("Public address: " + router.ExternalIPAddress + " (reachable: " + router.HasPublicExternalAddress + ")");
    }
}
```

* Every method returns a result instead of throwing, also when the router does not answer or the call is canceled.
* The code after `await` runs on the main thread again, as with any Unity `async` code.
* Pass a `CancellationToken` to cancel an operation. The result then has `ErrorCode == UpnpErrorCode.Canceled`.
* If no router is known yet, `OpenPortAsync` / `AddPortMappingAsync` search for one first.

### 3.3 Without the component (pure C#)

`UpnpClient` is the engine independent entry point. It works in a console application, a dedicated server build
or a unit test.

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WoodsShin.SimpleUPNP;

public static class Program
{
    public static async Task Main()
    {
        using (UpnpClient client = new UpnpClient())
        {
            UpnpResult<IReadOnlyList<UpnpGateway>> found = await client.DiscoverAsync();
            if (!found.Succeeded)
            {
                Console.WriteLine(found);
                return;
            }

            UpnpGateway router = client.SelectedGateway;
            Console.WriteLine(router.DisplayName + ", public address " + router.ExternalIPAddress);

            await client.AddPortMappingAsync(new PortMappingInfo(UpnpProtocol.Tcp, 25565, 25565, "My Server"));
            // ... run your server ...
            await client.DeletePortMappingAsync(UpnpProtocol.Tcp, 25565);
        }
    }
}
```

Awaiting on a thread with a synchronization context (such as Unity's main thread) resumes on that thread.
All other threads continue on the thread pool. `UpnpClient` and `UpnpGateway` can be used from any thread.

## 4. UPNP Manager component

**Add Component > Simple UPNP > UPNP Manager.** Only one manager can exist: a second one removes itself and logs a warning.
`UpnpManager.Instance` is the active manager, `UpnpManager.GetOrCreate()` creates one when there is none.

### 4.1 Inspector

| Setting | Default | Meaning |
| --- | --- | --- |
| Discover On Start | off | Search for routers in `Start`. |
| Open Startup Mappings On Start | off | Open the **Startup Mappings** in `Start` (after the search). |
| Close Mappings On Quit | on | Delete the mappings that this component opened when the game quits or the component is destroyed. |
| Persist Across Scenes | on | `DontDestroyOnLoad` (only for root objects). |
| Startup Mappings | UDP 7777 | List of mappings for **Open / Close Startup Mappings**. An empty *Internal Client* means: this computer. *Lease Duration* 0 means permanent. |
| Request Timeout Seconds | 5 | How long to wait for one answer of the router. |
| Discovery Timeout Seconds | 3 | How long the search listens for answers. |
| Max Retries | 2 | How often a request is repeated after a timeout or connection error. |
| Fallback To Permanent Lease | on | Many routers refuse mappings with a lease time (UPnP error 725). The request is then repeated as permanent mapping. |
| Quit Cleanup Timeout Seconds | 3 | Quitting waits at most this long for the router while it deletes the mappings. |
| Verbose Logging | off | Log every request and answer (SSDP, SOAP). Turn this on when you report a problem. |
| On Succeeded / On Failed | - | UnityEvents with a text. Raised on the main thread. |

### 4.2 Members

Methods for buttons and UnityEvents (no return value, the result arrives through the events):

`DiscoverGateways()`, `RefreshGateway()`, `OpenStartupMappings()`, `CloseStartupMappings()`,
`OpenTcpPort(int)`, `OpenUdpPort(int)`, `CloseTcpPort(int)`, `CloseUdpPort(int)`.

Asynchronous methods (each takes an optional `CancellationToken` as last parameter):

| Method | Description |
| --- | --- |
| `DiscoverGatewaysAsync(bool refreshSelected = true)` | Search for routers. With `refreshSelected` the first router is also read (status, public address, link properties, port table). |
| `RefreshGatewayAsync(bool includePortMappings = true)` | Read status, public address, link properties and the port table of the selected router. |
| `GetPortMappingsAsync()` | Read the port table of the selected router. |
| `OpenPortAsync(UpnpProtocol protocol, int externalPort, int internalPort = 0, string description = null, int leaseSeconds = 0)` | Open a port to this computer. `internalPort` 0 means the same number as `externalPort`. `leaseSeconds` 0 means permanent. |
| `ClosePortAsync(UpnpProtocol protocol, int externalPort)` | Close a port. Closing a port that is not open succeeds. |
| `AddPortMappingAsync(PortMappingInfo mapping)` | Full control: remote host, internal client, enabled flag, lease. |
| `DeletePortMappingAsync(PortMappingInfo mapping)` | Delete by protocol, external port and remote host. Use `ClosePortAsync` for the usual mapping that has no remote host. |
| `PerformAllGatewaysAsync(UpnpAction action, PortMappingInfo mapping)` | Search again and add or delete the mapping on **every** router that answers. Succeeds when at least one router accepted. |
| `OpenStartupMappingsAsync()` / `CloseStartupMappingsAsync()` | Open / close the *Startup Mappings* list. The result is the first failure, or success when every mapping worked. If no router is found, the rest of the list is skipped instead of searching again for every entry. |

Properties and events:

| Member | Description |
| --- | --- |
| `IsSupported` | False on WebGL. Every operation then returns `UpnpErrorCode.NotSupported`. |
| `Client` | The underlying `UpnpClient`, for everything the component does not wrap. |
| `Gateways`, `SelectedGateway` | The routers found by the last search, and the one that the operations use. Use `Client.SelectGateway(...)` to switch. |
| `IsBusy` | True while an operation is running. |
| `OpenedMappings` | The mappings that this component opened and will close on quit. |
| `LastResult`, `LastOperation` | Outcome and name of the most recent operation. |
| `event OperationFinished(string operation, UpnpResult result)` | Raised on the main thread after every operation. |

### 4.3 Closing ports

`UpnpManager` remembers every mapping it opened successfully. When the game quits (`OnApplicationQuit`) or the
component is destroyed, it asks every known router to delete them and waits at most *Quit Cleanup Timeout Seconds*.

* A port that is left open stays on the router until it is deleted or the router restarts (permanent mappings).
  If your game crashes, the port stays open. Use a **lease** (for example 3600 seconds) together with a timer that
  renews it, when your router supports leases.
* Mobile operating systems may end the app without calling `OnApplicationQuit`. Do not rely on cleanup there.

## 5. Gateway Tester window

**Window > Simple UPNP > Gateway Tester** works in Edit Mode. Use it to check whether your router supports UPnP,
to see what the router reports, and to open and close ports by hand.

* **Search Gateways** - SSDP search, then reads the first router.
* **Refresh** / **Read Port Mappings** - update the information of the selected router.
* **Open Port / Close Port** - one router. **Open / Close on All Gateways** - every router that answers.
* **Use** next to a table entry copies its values into the form.
* Ports opened in the window stay open on the router until you close them.

macOS may ask for permission to find devices on the local network the first time (see [Platform notes](#platform-notes)).

## 6. Code API

All types are in the namespace `WoodsShin.SimpleUPNP`.

### 6.1 `UpnpClient` (implements `IDisposable`)

| Member | Description |
| --- | --- |
| `UpnpClient()` / `UpnpClient(UpnpClientOptions)` | Create a client. |
| `Options` | The settings. Change them before the first operation. |
| `Gateways`, `SelectedGateway`, `SelectGateway(...)` | Routers found by the last search and the one the client level methods use. |
| `IsBusy`, `CurrentOperation` | What the client is doing (`UpnpOperation`). |
| `DiscoverAsync(refreshSelected = true, ct)` | Search. Returns `UpnpResult<IReadOnlyList<UpnpGateway>>`. |
| `AddGatewayAsync(Uri location, ct)` | Add a router whose device description URL you already know (no multicast). |
| `RefreshAsync(includePortMappings = true, ct)` | Read the selected router. |
| `GetPortMappingsAsync(ct)` | Read the port table of the selected router. |
| `AddPortMappingAsync(PortMappingInfo, ct)` | Add a mapping on the selected router. |
| `DeletePortMappingAsync(UpnpProtocol, int externalPort, ct)` / `DeletePortMappingAsync(PortMappingInfo, ct)` | Delete a mapping on the selected router. Use the second form when the mapping has a *remote host*. |
| `PerformAllGatewaysAsync(UpnpAction, PortMappingInfo, ct)` | Add or delete on every router. |
| `Dispose()` | Cancels running operations. The client cannot be used afterwards. |

Methods that need a router search for one when none is selected.

### 6.2 `UpnpGateway`

One router. Instances come from `UpnpClient`. All methods are thread-safe, requests to the same router run one after another.

| Member | Description |
| --- | --- |
| `Location`, `Ssdp` | URL of the device description, headers of the SSDP answer (`SsdpDeviceInfo`). |
| `Manufacturer` | `ManufacturerInfo`: friendly name, manufacturer, model, serial number, ... (after discovery). |
| `Status` | `DeviceStatusInfo`: connection status, uptime, public and local address, connection type (after `RefreshAsync`). |
| `LinkProperties` | `CommonLinkProperties`: access type, upstream / downstream bit rate, link status (after `RefreshAsync`). |
| `PortMappings` | The port table that was read last (`IReadOnlyList<PortMappingInfo>`). |
| `LocalIPAddress`, `ExternalIPAddress` | Address of this computer on the router's network, public address of the router. `"N/A"` while unknown. |
| `HasPublicExternalAddress` | `true` public, `false` private / shared (double NAT, carrier-grade NAT), `null` unknown. |
| `DisplayName`, `LastResult` | Friendly name (or address) and result of the last operation. |
| `RefreshAsync(includePortMappings = true, ct)` | Reads status, public address, connection type, link properties and the port table. |
| `GetPortMappingsAsync(ct)` | Reads the port table (at most `MaxPortMappingEntries`). |
| `GetExternalIPAddressAsync(ct)` | Reads only the public address. |
| `GetPortMappingAsync(protocol, externalPort, remoteHost = "", ct)` | Reads one entry. `Value` is `null` when the entry does not exist. |
| `AddPortMappingAsync(PortMappingInfo, ct)` | Adds a mapping. |
| `DeletePortMappingAsync(PortMappingInfo, ct)` / `(protocol, externalPort, remoteHost = "", ct)` | Deletes a mapping. A missing entry counts as success. |

### 6.3 `PortMappingInfo`

| Field | Default | Meaning |
| --- | --- | --- |
| `Protocol` | `Udp` | `UpnpProtocol.Tcp` or `Udp`. |
| `ExternalPort` | 7777 | Port on the router, 1-65535. |
| `InternalPort` | 7777 | Port on your computer, 1-65535. |
| `InternalClient` | `""` | Address to forward to. Empty = this computer's address on the router's network. |
| `RemoteHost` | `""` | Only accept traffic from this address. Empty = everybody. |
| `Description` | `"Simple UPNP"` | Text that the router shows in its table. |
| `LeaseDuration` | 0 | Seconds the mapping lives. 0 = permanent. |
| `Enabled` | true | Whether the mapping is active. |

`Validate()` returns `null` or a text describing what is wrong. The library validates before it talks to the router.

### 6.4 `UpnpClientOptions`

| Property | Default |
| --- | --- |
| `RequestTimeout` | 5 s |
| `DiscoveryTimeout` | 3 s |
| `DiscoveryGracePeriod` | 600 ms (extra time for more routers after the first answer) |
| `MaxRetries` | 2 (only for timeouts and connection errors, never for answers of the router) |
| `MaxPortMappingEntries` | 256 |
| `FallbackToPermanentLease` | true |
| `SsdpEndPoint` | 239.255.255.250:1900 |
| `MulticastTimeToLive` | 2 |
| `AllowPublicGatewayAddresses` | false (see [Security](#9-security-and-good-practice)) |
| `IncludeLoopbackInterfaces` | false (for tests) |
| `UserAgent` | `SimpleUPNP/1.0 UPnP/1.1` |
| `SearchTargets` | InternetGatewayDevice 1/2, WANIPConnection 1/2, WANPPPConnection 1, `upnp:rootdevice` |
| `Logger` | `Action<UpnpLogLevel, string>`. `UpnpManager` connects it to the Unity console. |

## 7. Results, error codes and troubleshooting

### 7.1 `UpnpResult`

Every operation returns a `UpnpResult` (or `UpnpResult<T>` with a `Value`):

* `Succeeded` / `ErrorCode` - `UpnpErrorCode.Success` or the reason.
* `Message` - a readable explanation.
* `UpnpFaultCode`, `UpnpFaultDescription` - the UPnP error that the router returned (0 if none).
  `UpnpErrors.Describe(code)` explains the common codes.
* `ToString()` combines all of it, which is handy for logs.

| `UpnpErrorCode` | Meaning |
| --- | --- |
| `NoGatewayFound` | Nobody answered the search, or no answer was a router. |
| `GetDeviceListFailed`, `NetworkInitFailed` | The search could not run (sockets, adapters). |
| `RequestFailed`, `RequestTimeout` | The router did not answer, or sent something unusable. |
| `GetStatusFailed`, `GetExternalIpFailed`, `GetConnectionInfoFailed`, `GetCommonLinkPropertiesFailed`, `GetPortMappingEntryFailed` | The router refused to give that information. |
| `AddMappingFailed`, `DeleteMappingFailed` | The router refused to change the table. `UpnpFaultCode` says why. |
| `InvalidArgument` | A port is out of range, the mapping is null, ... Nothing was sent. |
| `Canceled` | You canceled, or the client / component was disposed. |
| `NotSupported` | The platform cannot use UPnP (WebGL). |

### 7.2 UPnP error codes that you will meet

| Code | Meaning and what to do |
| --- | --- |
| 718 | The external port is already mapped, probably to another computer or another instance of your game. Close that mapping or use another port. |
| 725 | The router only supports permanent mappings. With *Fallback To Permanent Lease* the request is repeated with lease 0 automatically. |
| 606 | The router does not allow the action. Port mapping may be disabled in the router settings. |
| 402, 501 | The router rejected the arguments or failed internally. Try again; enable *Verbose Logging* and compare with the router's table. |
| 724, 726, 727 | The router wants the same internal and external port, no remote host, or a wildcard port. Change the mapping. |
| 728 | The router has no free slot in its table. Delete old mappings. |
| 713, 714 | End of the table / no such entry. Handled by the library, you normally never see them. |

### 7.3 Checklist when it does not work

1. **Is UPnP enabled in the router?** Look for *UPnP*, *NAT-PMP* or *Port Mapping* in its settings. Some routers switch it off by default.
2. **Open the Gateway Tester.** If *Search Gateways* finds nothing, the problem is the network, not your game.
3. **Firewall.** Allow your game / the Unity Editor on private networks. A firewall can block the answers of the router.
4. **VPN, virtual adapters, Docker, virtual machines.** Multicast does not leave a NAT'ed virtual network. Use a bridged network or *host* networking, or forward the port by hand.
5. **Is the public address really public?** If `HasPublicExternalAddress` is `false`, there is another router or a carrier-grade NAT in front of yours. Port mapping cannot fix that.
6. **Several routers** (modem + router, mesh, access points). Use *Open on All Gateways* or select the right one with `Client.SelectGateway`.
7. **Turn on Verbose Logging** and read the console: it shows the answers of the router.
8. **Test from outside.** Ask a friend, or use mobile data. Many routers do not loop back to their own public address.

## 8. Platform notes <a name="platform-notes"></a>

| Platform | Notes |
| --- | --- |
| Windows | The first run may show the Windows Firewall prompt: allow *private networks*. |
| macOS | macOS 15 and newer may ask whether the app is allowed to find devices on the local network (this includes the Unity Editor). Allow it, otherwise the search finds nothing. |
| Linux | Dedicated servers in a container need *host* networking (or a bridged network) for the search. |
| Android | Works over Wi-Fi only. Set **Player Settings > Other Settings > Internet Access** to *Require*. The router is reached with plain TCP sockets, so the cleartext HTTP restrictions of Android do not apply. Mobile data has no UPnP router. |
| iOS | Sending the search needs the **Multicast Networking** entitlement (`com.apple.developer.networking.multicast`). Apple must grant it: <https://developer.apple.com/contact/request/networking-multicast>. Also add `NSLocalNetworkUsageDescription` (the text of the *Local Network* permission prompt) to the Info.plist. Without the entitlement, use `UpnpClient.AddGatewayAsync(Uri)` when you know the URL of the router's device description. Test on a real device. |
| WebGL | Not supported: browsers cannot open sockets. Operations return `NotSupported`. |
| Consoles | Not tested. Console platforms provide their own NAT traversal services. |

* **No native code** is used. The package works with Mono and IL2CPP and contains no reflection, so code stripping is safe.
* The router is contacted with plain TCP sockets, so *Allow downloads over HTTP* (Unity) and App Transport Security (iOS) do not apply.
* **Enter Play Mode Options:** *Reload Domain* may be switched off. The component resets its static state on start.
* The library does not use Unity APIs from background threads. Events and awaited code return on the main thread.

## 9. Security and good practice

* **A port mapping is an open door to the internet.** Open only the ports that you need, only while you need them,
  and validate every packet that arrives. Prefer a lease time over a permanent mapping when the router supports it.
* **Close what you open.** The component does this on quit. If you use the pure C# API, delete your mappings yourself.
* **Forged answers are ignored.** A gateway must have a private, shared, link-local or loopback address, and its control URL
  must point to the same host as its device description. This stops answers from the internet that try to make
  your game send requests to other hosts. Set `AllowPublicGatewayAddresses` only if you really need a router that is not on the local network.
* **Text is escaped.** Descriptions with characters like `<` or `&` cannot break the SOAP request.
* **No data leaves your network.** The package sends packets to the multicast address of SSDP and to the routers that answer. It has no analytics and
  contacts no web service.
* Do not expose a service to the internet that has not been written for it (databases, debugging ports, ...).

## 10. Coming from the Unreal Engine plugin

The Unity package follows the idea and the feature set of the Unreal plugin *SimpleUPNP* ([github.com/woodsshin/UPNPSample](https://github.com/woodsshin/UPNPSample)), but it is a new implementation for C# and Unity.

| Unreal (Blueprint) | Unity |
| --- | --- |
| `AddPort` | `UpnpManager.OpenPortAsync` / `AddPortMappingAsync` |
| `RemovePort` | `UpnpManager.ClosePortAsync` / `DeletePortMappingAsync` |
| `GetPortList` | `DiscoverGatewaysAsync` + `RefreshGatewayAsync` / `GetPortMappingsAsync` |
| `GetPortList` with *OnlySearchDevices* | `DiscoverGatewaysAsync(refreshSelected: false)` |
| `PerformAllDevices` (add / delete) | `PerformAllGatewaysAsync(UpnpAction.AddPort / DeletePort, mapping)` |
| `AllowRetry` | `UpnpClientOptions.MaxRetries` |
| `ManufactureInfo`, `DeviceStatusInfo`, `CommonLinkProperties`, `DeviceInfo`, `SimpleUPNPInfo` | `ManufacturerInfo`, `DeviceStatusInfo`, `CommonLinkProperties`, `SsdpDeviceInfo`, `PortMappingInfo` |
| Result / error enum | `UpnpErrorCode`, plus `UpnpFaultCode` for the router's own error |

Differences: results are plain return values, several routers are first class (`Gateways`), every request has a timeout and can be canceled,
and the mapping table is read until the router says that it is complete.

## 11. Unit tests

The package contains tests (`Tests/Editor`). They start a fake router on your own computer, so they need no network.
To run them, add the package to `testables` in `Packages/manifest.json`:

```json
"testables": [ "com.woodsshin.simpleupnp" ]
```

and open **Window > General > Test Runner > EditMode**.

---

*Simple UPNP* - Copyright 2017-present WoodsShin. See `LICENSE.md` and `Third Party Notices.md`.
