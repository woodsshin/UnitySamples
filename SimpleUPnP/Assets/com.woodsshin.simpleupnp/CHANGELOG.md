# Changelog

All notable changes to this package are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this package adheres to [Semantic Versioning](https://semver.org/).

## [1.0.0] - 2026-10-27

First release.

### Added
- SSDP discovery of Internet Gateway Devices on every IPv4 network adapter.
- Device description parsing, selection of the `WANIPConnection` / `WANPPPConnection` service.
- Manufacturer, connection status, public address and common link properties of the router.
- Add, delete, read one and list UDP / TCP port mappings. Automatic repeat as permanent mapping when the router
  answers with UPnP error 725. Deleting a mapping that does not exist counts as success.
- `PerformAllGatewaysAsync`: add or delete a mapping on every router of the network.
- `UpnpGateway.HasPublicExternalAddress`: detects double NAT and carrier-grade NAT.
- `UpnpManager` component: Inspector settings, UnityEvents, `async` API, startup mappings, closes the ports it
  opened when the game quits.
- Engine independent `UpnpClient` (assembly `WoodsShin.SimpleUPNP.Core`, no `UnityEngine` reference).
- Gateway Tester window (**Window > Simple UPNP > Gateway Tester**).
- Demo sample (IMGUI) and a manual.
- Unit tests that run against a fake router on the local machine.
