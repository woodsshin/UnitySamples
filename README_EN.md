# Unity Portfolio (Steam & Photon)
> <a href="https://drive.google.com/file/d/16IJnSgvC1gbpW9DPIYUcXhM4bqG7w57o/view?usp=sharing" target="_blank"><b>📄 Download / View Full Portfolio (PDF)</b></a>

This repository organizes the networking-related work done throughout Unity-based multiplayer game development, grouped by folder. It spans work across different layers and purposes — from solving flow control issues in legacy engine networking (UNet), to Steamworks SDK integration, to a fully playable prototype built with Photon PUN.

## Folder Structure

### 📁 [Networking](./Networking/README_EN.md)

A custom networking layer built on top of Unity's legacy networking framework, **UNet (HLAPI)**. It abstracts two transport methods — a dedicated server (UDP) and **Steam P2P** — behind the same high-level interface, so a single set of game logic can support both transports.

- A custom message queue that applies frame-level flow control to the burst of object-spawn traffic that occurs on initial connection
- Solved without modifying the engine source, using only overrides of `NetworkConnection`/`NetworkManager` virtual methods
- A low-level design that analyzes UNet's connection state machine and ports the Steam P2P transport layer so that UNet recognizes it as a standard UDP connection
- (Appendix) Also covers chunk streaming and a fall-through-the-floor fix for joining large worlds with 10,000+ objects, plus dedicated server admin features (passwords, kick/ban, remote commands)

### 📁 [Steam](./Steam/README_EN.md)

A module that integrates the **Steamworks SDK (Steamworks.NET)** into a Unity (UNET)-based game. It supports core Steamworks features including Stats/Achievements, Leaderboards, Matchmaking/Server Browser, Workshop (UGC), P2P Networking, and Friends/Avatar.

- Encapsulates three different types of asynchronous operations (Steam API callbacks, Matchmaking delegates, raw UDP sockets) under a common abstract class called `SteamAsyncTask`
- A single-threaded serial execution queue (`SteamAsyncTaskManager`) that guarantees both thread safety and request ordering at the same time
- A structure with shared timeout/lifecycle management and delegate-based result notification, making it easy to extend when adding new Steam features

### 📁 [PhotonPUNPrototype](./PhotonPUNPrototype/README_EN.md)

A multiplayer prototype built on the **Photon PUN (Photon Unity Networking) Lobby sample**, benchmarked against **Fall Guys**. It implements, as an actual playable build, the matchmaking flow from region selection → room creation/joining → game start, along with core casual-racing gameplay such as double jump and checkpoint respawn.

- Includes gameplay screenshots and a downloadable Windows build
- Also documents the design decision process behind transitioning to **Photon Quantum** after validating with PUN, to meet real-service scale requirements of 30+ concurrent users

### 📁 [CustomServer](./CustomServer/README_EN.md)

A project that implements and validates a server-authoritative multiplayer architecture from the ground up, centered on **a UDP server written as a pure C# console application** — with no reliance on Unity's Dedicated Server package or any commercial netcode. Both a MonoBehaviour client and an ECS-DOTS client can connect to the same server at once, and the project covers client-side prediction, reconciliation, and remote interpolation. The server was also ported to C++17, validating that the wire protocol and simulation logic are not tied to any specific language or runtime.

- A custom binary protocol, a 60 Hz server tick loop, and reconciliation over a wrapped coordinate space, all implemented from scratch
- Ported the server to C++17 as well, validating that the C# and C++ implementations are interchangeable at the protocol level for the same client
- Includes a demo video simulating 200 concurrent users with a pure .NET console load-testing bot
- See [CustomServer/README_EN.md](./CustomServer/README_EN.md) for details and build downloads

### 📁 [DeterministicServer](./DeterministicServer/README_EN.md)

A follow-up project that completely redesigns `CustomServer`'s server-authoritative + client prediction/reconciliation structure into a **deterministic lockstep** architecture. The server computes no physics at all, handling only input collection and relay, while every participant — including the DOTS/ECS client — replays the same input sequence in the same order and computes physics independently. This is the same concept used by Photon Quantum and by RTS games.

- Redesigned the custom binary protocol around a single `TickCommit` channel, and removed the fixed-tickrate assumption by broadcasting the server's measured delta time
- Covers finding and fixing the synchronization bugs specific to lockstep — reproducing spawn slots and host assignment from join order (`JoinSequence`), and pinning down a per-tick processing order convention (Join → Leave → Input → Missile → Respawn)
- Simulates large-scale concurrent traffic with a pure .NET console load-testing bot that runs no physics
- See [DeterministicServer/README_EN.md](./DeterministicServer/README_EN.md) for details

## Suggested Reading Order

If you'd like to see the technical difficulty and problem-solving process, we recommend reading **Networking → Steam → CustomServer → DeterministicServer** in that order. If you'd like to see the actual playable result first, start with **PhotonPUNPrototype**.
