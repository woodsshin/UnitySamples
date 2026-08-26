# MetarushPUN — Fall Guys-style Multiplayer Prototype

A multiplayer prototype built on Photon's **PUN (Photon Unity Networking) Lobby sample**, benchmarked against **Fall Guys**. On top of Photon PUN's standard matchmaking flow — region selection → room creation/joining → game start — gameplay logic required for a casual racing genre, such as double jump and checkpoint respawn, was added and validated.

---

## Build Download

▶ [Download Windows Build](https://drive.google.com/file/d/1uhwscE3nmQTjUshX3ybWRRV_59VS3g-P/view?usp=sharing)

You can run multiple instances at the same time to test multiplayer locally. (See below for detailed run instructions.)

---

## How to Run

Run the same build as multiple instances simultaneously — one acts as the host and creates a room, while the others join that room — to test multiplayer.

### 1. Select a Server (Region) and Log In

You can run multiple instances at the same time. In each instance, select the Photon region you want to connect to, then log in. **All players must select the same region** in order to meet in the same room.

![Server selection screen](image/SelectServer.png)

### 2. Create and Join a Room

Once logged in and connected to the master server, the menu is displayed. The host player creates a room using **Create Room**, and other players can join that room via **Join Random Room** or **Show Room List**.

![Menu screen](image/Menu.png)

### 3. Start the Game

Once all players have gathered in the room, the host presses the **Start Game** button to start the game. Once the game starts, all clients enter the same map and begin playing.

![Gameplay screen](image/GameStart.png)

---

## Controls and Features

| Input | Action |
|---|---|
| Double-tap Spacebar | Double jump |
| Tab | Change view (3rd person ↔ top-down) |
| Left Shift | Sprint |
| ESC | Options |

**Checkpoints** are placed throughout the map, so if a player falls off the map, their character respawns at the last checkpoint they passed.

---

## Validating for Large-Scale Capacity: Transition to Photon Quantum

After validating the core gameplay with the PUN-based prototype, development moved forward with a transition to **Photon Quantum** to meet the requirement of stably supporting **30+ concurrent users**. Because PUN's state synchronization approach causes bandwidth and computation load to increase sharply at large-scale concurrency, it was determined that Quantum — which is based on deterministic simulation — is better suited for physics-based racing content with a large number of players.

> A build actually developed on Quantum is not currently available, so a video of the gameplay is provided below instead.
>
> 🔗 [https://x.com/PlayMetarush](https://x.com/PlayMetarush)

---

## Tech Stack

- **Engine**: Unity
- **Networking (Prototype)**: Photon PUN (Photon Unity Networking) — based on the Lobby sample
- **Networking (Scale-up)**: Photon Quantum — deterministic simulation-based support for large-scale concurrency
- **Genre**: Casual Multiplayer Racing/Party (benchmarked against Fall Guys)
