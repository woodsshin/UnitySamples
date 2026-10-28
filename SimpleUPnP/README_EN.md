# Simple UPNP

Open ports on the player's router with one line of code, so that other players can connect to your game
**without manual port forwarding**.

Simple UPNP finds the Internet Gateway Device (the router) with SSDP, reads its manufacturer, status and link
information, and adds, deletes and lists **UDP / TCP port mappings** through the UPnP IGD SOAP services
(`WANIPConnection` / `WANPPPConnection`). It is a pure C# library: no native plugins, no external services,
no accounts. Nothing leaves the local network except the port mappings that you ask the router for.

* Independent of your networking library: it only opens the port on the router, your transport (Unity Transport,
  Mirror, LiteNetLib, your own sockets, a dedicated server on a home PC, ...) listens on that port as usual.
* Drop-in `UpnpManager` component with Inspector settings and UnityEvents, **or** `async`/`await` API,
  **or** the engine independent `UpnpClient` (no `UnityEngine` reference).
* Never throws from the API: every call returns a result with an error code and a readable message.
* Cleans up after itself: ports that the component opened are closed again when the game quits.
* Gateway Tester window (**Window > Simple UPNP > Gateway Tester**) to try your router from the Editor.
* Demo sample, unit tests, full source code.

<p align="center">
  <img src="Image/UPnPTester.PNG" alt="UPnP Tester" width="682"><br>
  <sub>UPnP Tester</sub>
</p>

Inspired by the *SimpleUPNP* plugin for Unreal Engine ([github.com/woodsshin/UPNPSample](https://github.com/woodsshin/UPNPSample)).

## Requirements

* Unity 2022.3 or newer (C# 9).
* Platforms: Windows, macOS, Linux, Android and iOS* players and the Editor. Pure managed code, no native plugins.
* The router must have UPnP switched on (many home routers do by default) and must have a public IP address.

\* iOS needs the *Multicast Networking* entitlement from Apple, see [Platform notes](Assets/com.woodsshin.simpleupnp/Documentation~/SimpleUPNP.md#platform-notes).
WebGL cannot open sockets and is not supported.

## Quick start (no code)

1. **GameObject > Create Empty**, then **Add Component > Simple UPNP > UPNP Manager**.
2. In the *Startup Mappings* list set the protocol and the port of your game (for example **UDP 7777**).
3. Tick **Discover On Start** and **Open Startup Mappings On Start**.
4. Press Play. The Console shows `[Simple UPNP] Added UDP port mapping 7777 -> 192.168.x.x:7777 on <your router>`.
   When you stop the game, the mapping is removed again.

## Quick start (code)

```csharp
using UnityEngine;
using WoodsShin.SimpleUPNP;

public class HostGame : MonoBehaviour
{
    private async void Start()
    {
        UpnpManager upnp = UpnpManager.GetOrCreate();

        // Finds the router if needed, then opens UDP 7777 -> this computer.
        UpnpResult result = await upnp.OpenPortAsync(UpnpProtocol.Udp, 7777, description: "My Game");

        if (result.Succeeded)
        {
            await upnp.RefreshGatewayAsync();   // also reads the public address of the router
            Debug.Log("Players can join at " + upnp.SelectedGateway.ExternalIPAddress + ":7777");
        }
        else
        {
            // Tell the player how to continue, for example "forward the port manually".
            Debug.LogWarning("Could not open the port: " + result);
        }
    }
}
```

`await` returns on the Unity main thread, so you can touch Unity objects right after it.
The port is closed again when the game quits (switch this off with *Close Mappings On Quit*).

## Package contents

| Folder | Contents |
| --- | --- |
| `Runtime/Core` | Engine independent C# library (`UpnpClient`, `UpnpGateway`, SSDP, SOAP). Assembly `WoodsShin.SimpleUPNP.Core`. |
| `Runtime/Unity` | `UpnpManager` component. Assembly `WoodsShin.SimpleUPNP.Runtime`. |
| `Editor` | Gateway Tester window. Assembly `WoodsShin.SimpleUPNP.Editor`. |
| `Tests/Editor` | Unit tests that run against a fake router on your own machine. Assembly `WoodsShin.SimpleUPNP.Tests`. |
| `Samples/Demo` | IMGUI demo. Import it from **Package Manager > Simple UPNP > Samples**. |
| `Documentation~` | The manual: [SimpleUPNP.md](Documentation~/SimpleUPNP.md). |

## Documentation

See [Documentation~/SimpleUPNP.md](Documentation~/SimpleUPNP.md) for the complete manual: Inspector reference,
API reference, error codes, platform notes, security notes and troubleshooting.

## Support

Questions, bug reports and feature requests: please use the contact details on the Asset Store page of this package.
