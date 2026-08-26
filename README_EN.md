# Unity Portfolio (Steam & Photon)

This repository organizes the networking-related work done throughout Unity-based multiplayer game development, grouped by folder. It spans work across different layers and purposes — from solving flow control issues in legacy engine networking (UNet), to Steamworks SDK integration, to a fully playable prototype built with Photon PUN.

## Folder Structure

### 📁 [Networking](./Networking/README_EN.md)

A custom networking layer built on top of Unity's legacy networking framework, **UNet (HLAPI)**. It abstracts two transport methods — a dedicated server (UDP) and **Steam P2P** — behind the same high-level interface, so a single set of game logic can support both transports.

- A custom message queue that applies frame-level flow control to the burst of object-spawn traffic that occurs on initial connection
- Solved without modifying the engine source, using only overrides of `NetworkConnection`/`NetworkManager` virtual methods
- A low-level design that analyzes UNet's connection state machine and ports the Steam P2P transport layer so that UNet recognizes it as a standard UDP connection

### 📁 [Steam](./Steam/README_EN.md)

A module that integrates the **Steamworks SDK (Steamworks.NET)** into a Unity (UNET)-based game. It supports core Steamworks features including Stats/Achievements, Leaderboards, Matchmaking/Server Browser, Workshop (UGC), P2P Networking, and Friends/Avatar.

- Encapsulates three different types of asynchronous operations (Steam API callbacks, Matchmaking delegates, raw UDP sockets) under a common abstract class called `SteamAsyncTask`
- A single-threaded serial execution queue (`SteamAsyncTaskManager`) that guarantees both thread safety and request ordering at the same time
- A structure with shared timeout/lifecycle management and delegate-based result notification, making it easy to extend when adding new Steam features

### 📁 [PhotonPUNPrototype](./PhotonPUNPrototype/README_EN.md)

A multiplayer prototype built on the **Photon PUN (Photon Unity Networking) Lobby sample**, benchmarked against **Fall Guys**. It implements, as an actual playable build, the matchmaking flow from region selection → room creation/joining → game start, along with core casual-racing gameplay such as double jump and checkpoint respawn.

- Includes gameplay screenshots and a downloadable Windows build
- Also documents the design decision process behind transitioning to **Photon Quantum** after validating with PUN, to meet real-service scale requirements of 30+ concurrent users

## Suggested Reading Order

If you'd like to see the technical difficulty and problem-solving process, we recommend reading **Networking → Steam** in that order. If you'd like to see the actual playable result first, start with **PhotonPUNPrototype**.
