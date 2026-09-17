# Deterministic Lockstep Multiplayer Framework (Server-Relay, Physics-less Server)

A validation project for a **Deterministic Lockstep** multiplayer architecture built around a **pure C# console UDP server**, with no dependency on Unity's Dedicated Server packages (Netcode for GameObjects, Unity Transport, etc.).

Taking a step beyond the conventional Server-Authoritative + Client-Side Prediction/Reconciliation model, this project has been fully redesigned as an **"Input Relay" architecture in which the server performs no physics simulation whatsoever**. This follows the same conceptual model as the lockstep implementations used by Photon Quantum and Age of Empires-style RTS titles: the server acts as a pure arbiter that only collects and relays inputs, while every physics computation is carried out independently by each client, replaying the identical input sequence in the identical order.

The server is completely decoupled from the Unity runtime. Using the same server instance, a **DOTS/ECS client** and a **Load Test Bot** connect simultaneously and independently reproduce identical simulation results from the same input sequence, validating the architecture's determinism.

> **Project Goal**: To structurally eliminate the entire class of bugs caused by "reconciliation" (prediction-error correction) inherent to Server-Authoritative architectures, and to validate whether a lockstep architecture — one that drastically reduces server load and bandwidth while still guaranteeing perfect synchronization — can be implemented using nothing more than a pure C# server and a custom binary protocol.

---

## 1. Architectural Migration — Server-Authoritative Physics → Deterministic Lockstep

The previous architecture, in which the server directly computed and broadcast position, rotation, health, death, missiles, and score, has been discarded. The server's responsibilities have been reduced to exactly two:

1. Collect each player's `(TargetTick, Throttle, Turn, Fire)` input, keyed by target tick.
2. On every tick, gather "the inputs of every player alive at that tick" and relay them as a single `TickCommit`. For any input that has not yet arrived, the last confirmed input is reused as-is (predicted-input fallback).

```csharp
// DeterministicSampleServer.cs — the server holds no physics fields whatsoever
// (no position, rotation, health, death, etc.)
public class PlayerConnection
{
    public byte Id;
    public IPEndPoint EndPoint;
    public DateTime LastPingTime;
    public long JoinSequence;      // "true connection order" — ascending PlayerId alone cannot reproduce this

    // Fallback cache: the last input actually used for tick commitment
    public float LastThrottle;
    public float LastTurn;
    public bool LastFire;
}
```

| Aspect | Before (Server-Authoritative) | Now (Deterministic Lockstep) |
|---|---|---|
| Physics computation owner | Server | Each client (identical code path for all) |
| Server → client payload | Full position/rotation/health snapshot for every player | Input sequence only (throttle/turn/fire) |
| Source of client-side divergence | Prediction-reconciliation timing/offset absorption logic | Any deviation in input replay order causes immediate divergence (fail-fast) |
| Reconciliation / Snapshot Interpolation | Required (3-stage snapshot/correction/smoothing pipeline) | **Not required** — removed entirely |
| Bandwidth (for N players) | O(N) snapshot broadcast to all clients every tick | O(N) input broadcast to all clients every tick (far fewer fields) |

The server is still implemented on top of `System.Net.Sockets.UdpClient` and has zero references to the Unity API, making it deployable anywhere — Docker, bare-metal Linux, or otherwise.

---

## 2. Protocol Redesign — A Single TickCommit Channel

The three previous broadcast packet types — `ServerState`, `MissileState`, and `ScoreboardState` — have been **consolidated into a single `TickCommit`**. Since the server's only knowledge consists of joins, leaves, and inputs, no other gameplay state exists anywhere in the protocol.

```csharp
// Protocol.cs
public enum PacketType : byte
{
    JoinRequest = 1, JoinResponse = 2, ClientInput = 3,
    TickCommit = 4, Ping = 5, Pong = 6, StartGameRequest = 7
}

public struct TickCommitData
{
    public int Tick;
    public float DeltaTimeSeconds;   // Server-measured dt — clients use this value directly as their physics dt
    public bool GameStarted;         // Edge signal: whether the game started "on this tick"
    public JoinedPlayerInfo[] JoinedPlayers;
    public byte[] LeftPlayerIds;
    public TickPlayerInput[] Inputs;
}
```

Because all three projects (server / DOTS client / Load Test Bot) physically copy this file into their own source trees, any protocol change must be manually and identically applied in all three locations. In place of compiler enforcement, field ordering and sort conventions (Joins ordered ascending by `JoinSequence`; Leaves and Inputs ordered ascending by `PlayerId`) are strictly enforced throughout the codebase.

### 2.1 Measured Delta Time — Removing the Fixed Tick-Rate Assumption

The server's target tick rate (60 Hz) is merely the intended cadence of a `Thread.Sleep`-based loop; thread scheduling jitter can cause the actual interval to vary slightly. The server measures this precisely every tick via `Stopwatch` and relays it as `DeltaTimeSeconds`; clients use this measured value directly as their physics dt, rather than a fixed `1/60`.

```csharp
// DeterministicSampleServer.ServerLoop
float deltaTimeSeconds = tick == 0
    ? (1f / TICK_RATE)
    : (float)tickStopwatch.Elapsed.TotalSeconds;
tickStopwatch.Restart();

BroadcastTickCommit(tick, deltaTimeSeconds, scheduledGameStartThisTick,
    _scheduledJoins, _scheduledLeaves, confirmedInputs, _scheduledLeaveEndPoints);
```

Because this value itself is relayed identically from the server to every client, using a variable dt does not break determinism — every client performs the exact same accumulation using the exact same value.

---

## 3. Join Sequence — Discovering and Fixing a Connection-Order Reproduction Bug

The initial implementation assigned spawn slots by assuming that ascending `PlayerId` (a monotonically increasing byte) reflected "true connection order." However, this assumption broke down under reconnection (which issues a new ID) or byte wraparound, causing `PlayerId` order and actual connection order to diverge. This surfaced a bug in which spawn-position computation results differed across clients — a direct violation of the deterministic invariant.

This was resolved by introducing `JoinSequence`, an explicitly server-managed global monotonic counter.

```csharp
// JoinedPlayerInfo — the join-processing order convention is strictly ascending
// JoinSequence, never PlayerId
public struct JoinedPlayerInfo
{
    public byte PlayerId;
    public long JoinSequence;
}
```

A newly connecting client receives the full `(PlayerId, JoinSequence)` list of every already-connected player via `JoinResponse.ExistingPlayers`, and spawns all of them before it begins processing its own `TickCommit` stream.

```csharp
// SimulationTickSystem.cs — initial synchronization for a newly connected client
private void SpawnExistingPlayersOnConnect(JoinedPlayerInfo[] existingPlayers, byte myPlayerId, Entity uiEventSingleton)
{
    var sorted = new List<JoinedPlayerInfo>(existingPlayers);
    sorted.Sort((a, b) => a.JoinSequence.CompareTo(b.JoinSequence));

    foreach (var info in sorted)
        SpawnPlayer(info.PlayerId, info.JoinSequence, myPlayerId, uiEventSingleton);
}
```

Without this step, a late-joining client would compute a smaller "currently occupied spawn slots" set than other clients already had, causing the identical `ComputeSpawnTransform` logic to yield divergent results per client. `JoinSequence` also fully replaces host-succession logic — without any separate "Host" field — through nothing more than a per-frame recomputation.

```csharp
// DeterministicSampleServer.cs — the holder of the minimum JoinSequence is, by definition, the current host
private byte? GetCurrentHostId()
{
    PlayerConnection host = null;
    foreach (var kvp in _players)
        if (host == null || kvp.Value.JoinSequence < host.JoinSequence) host = kvp.Value;
    return host?.Id;
}
```

When the host disconnects, the holder of the next-lowest value automatically becomes host on the very next computation, eliminating the need for any separate succession logic. The server (`GetCurrentHostId`) and every client independently compute the same logic and are guaranteed to always arrive at the same result. On the DOTS client, `GameHudBridge.ComputeIsHost` performs this minimum-`JoinSequence` lookup by iterating its own player collection; on the MonoBehaviour sample client, `CustomClientSample.ComputeIsHost` performs the identical logic over its own collection.

---

## 4. Tick Processing Order Convention (The Core Invariant of Lockstep)

For lockstep determinism to hold, **every client must replay exactly the same inputs in exactly the same order**. This project's processing-order convention is as follows:

```
Join (ascending JoinSequence)
  → Leave (ascending PlayerId)
  → Movement/fire resolution (ascending PlayerId)
  → Missile movement/collision resolution (ascending OwnerId, ties broken by ascending FireTick)
  → Respawn resolution (ascending PlayerId)
```

```csharp
// SimulationTickSystem.ProcessOneTick — the ordering itself is part of determinism and must never change
private void ProcessOneTick(TickCommitData commit, SimulationConfig config, Entity uiEventSingleton,
    ref ClientStateSingleton clientState)
{
    var sortedJoins = new List<JoinedPlayerInfo>(commit.JoinedPlayers);
    sortedJoins.Sort((a, b) => a.JoinSequence.CompareTo(b.JoinSequence));
    foreach (var info in sortedJoins)
        SpawnPlayer(info.PlayerId, info.JoinSequence, clientState.MyPlayerId, uiEventSingleton);

    var sortedLeaves = new List<byte>(commit.LeftPlayerIds);
    sortedLeaves.Sort();
    foreach (byte playerId in sortedLeaves)
        RemovePlayer(playerId, uiEventSingleton);

    if (!clientState.IsGameStarted) return;   // In the lobby, only Join/Leave are applied

    var sortedInputs = new List<TickPlayerInput>(commit.Inputs);
    sortedInputs.Sort((a, b) => a.PlayerId.CompareTo(b.PlayerId));
    foreach (var input in sortedInputs)
        ProcessPlayerTick(input, tick, commit.DeltaTimeSeconds, clientState.TotalElapsedSimTime, config, uiEventSingleton);

    ProcessMissiles(tick, commit.DeltaTimeSeconds, clientState.TotalElapsedSimTime, config, uiEventSingleton);
    ProcessRespawns(clientState.TotalElapsedSimTime, config, uiEventSingleton);
}
```

Missile collision resolution, in the original server implementation, implicitly depended on `ConcurrentDictionary`'s iteration order. This was harmless when only the server itself ran the logic (self-consistency was sufficient), but once multiple clients each independently execute the same logic, iteration order itself becomes part of determinism. This was explicitly pinned down via a `(OwnerId, FireTick)` sort, ensuring that even kill-credit resolution — where multiple missiles can score a hit on the same tick — produces identical results across every client.

### 4.1 Cumulative-Elapsed-Time-Based Resolution — Retiring "N Ticks Have Passed"

With the introduction of measured dt, "N ticks have elapsed" no longer guarantees a fixed amount of real time has passed. Fire cooldown and respawn resolution are therefore no longer based on tick counts, but on `TotalElapsedSimTime` — the server-relayed `DeltaTimeSeconds` accumulated in identical order by every client.

```csharp
// SimulationTickSystem.ProcessPlayerTick
if (input.Fire && (elapsedTime - lastFire.Value) >= config.FireCooldownSeconds)
{
    EntityManager.SetComponentData(entity, new LastFireElapsedTime { Value = elapsedTime });
    SpawnMissile(input.PlayerId, tick, physics.Position, physics.RotationDeg);
}
```

Floating-point addition itself can be non-deterministic in principle, but because every client accumulates the exact same `DeltaTimeSeconds` sequence in the exact same order, the resulting values remain perfectly identical across all clients.

---

## 5. Join/Leave/Game-Start — Defending Against Tick-Boundary Race Conditions

`JoinRequest` and `StartGameRequest` are never processed the instant they are received. Instead, they are queued and processed, strictly in order, by the `ServerLoop` thread only at the next tick boundary. Every client must observe the same event on exactly the same tick — otherwise computations that depend on "the current set of connected players," such as spawn-slot assignment, would not yield identical results across clients.

```csharp
// DeterministicSampleServer.ReceiveLoop — the receive thread only enqueues; it never makes decisions
case PacketType.JoinRequest:
    _pendingJoinRequests.Enqueue(remoteEP);
    break;

case PacketType.StartGameRequest:
    if (Protocol.TryReadStartGameRequest(data, out byte requesterId))
        _pendingStartGameRequests.Enqueue(requesterId);
    break;
```

```csharp
// DeterministicSampleServer.ServerLoop — validation must always be based on this tick's latest snapshot
// Order: 1) Apply Joins → 2) Apply Leaves → 3) Validate StartGameRequest
//   (must occur after Leave application, so that a request from a host that just timed out
//    is correctly rejected)
while (_pendingStartGameRequests.TryDequeue(out byte requesterId))
{
    if (_isGameStarted) continue;

    byte? currentHostId = GetCurrentHostId();
    if (currentHostId.HasValue && currentHostId.Value == requesterId)
    {
        _isGameStarted = true;
        scheduledGameStartThisTick = true;
    }
}
```

Any connection attempt after the game has already started is immediately rejected via `JoinResponse.GameAlreadyStarted = true`, and in this case, no trace is left in the player roster (including the `PlayerId` and `JoinSequence` counter).

```csharp
if (_isGameStarted)
{
    SendJoinResponse(clientEP, assignedId: 0, assignedSequence: 0,
        existingPlayers: EmptyExistingPlayers, gameAlreadyStarted: true);
    continue;
}
```

`GameStarted` is an edge signal, not a state. It is `true` for exactly the one tick on which the transition occurs; upon observing it, the client immediately latches it into a persistent `IsGameStarted` state. This mirrors the way the Join/Leave lists continue to be sent as empty lists on every tick — the design guarantees reliable state transitions while avoiding wasted bandwidth.

---

## 6. Input Delay — Absorbing Latency Without Rollback

This lockstep architecture **does not use rollback**. Instead, each client pre-sends inputs to the server that will apply `InputDelayTicks` ticks into the future relative to the next tick to be confirmed locally, minimizing the situations in which fallback (reusing the last input) is triggered by a late-arriving input.

```csharp
// InputCaptureSystem.cs
// Add InputDelayTicks to "the next tick the server will confirm" (LastConfirmedTick + 1).
int targetTick = clientState.LastConfirmedTick + 1 + config.InputDelayTicks;

if (targetTick == _lastSentTargetTick) return;   // Prevent redundant resend for the same tick

byte[] packet = Protocol.BuildClientInput(clientState.MyPlayerId, targetTick, throttle, turn, fireHeld);
```

Given the local-PC environment assumed here (negligible latency/packet loss), a small default value (2 ticks) is sufficient. However, the underlying architecture follows the same conceptual model as Photon Quantum and is designed to scale to higher-latency network environments.

---

## 7. DOTS/ECS Client — Simplifying the Single Source of Physics Truth

The previous dual-state structure — split across `PredictedTankState`, `ServerConfirmedState`, and `RenderErrorOffset` — has been **consolidated into a single `TankPhysicsState`**. Since prediction and confirmation no longer need to exist as separate concepts ("this is the result of everyone replaying the same input the same way — it is not a prediction"), the entire reconciliation-offset-smoothing and snapshot-interpolation infrastructure has been removed.

```csharp
// NetworkComponents.cs
/// The single source of physics truth. Since this is the result of everyone replaying
/// the same input the same way rather than a prediction, it intentionally avoids
/// the name "Predicted."
public struct TankPhysicsState : IComponentData
{
    public float2 Position;
    public float RotationDeg;
    public float Speed;
}
```

| Removed component/system | Rationale |
|---|---|
| `ServerConfirmedState`, `PredictedTankState`, `RenderErrorOffset` | The prediction/confirmation dual structure no longer exists |
| `RemotePlayerInterpolationSystem`, snapshot buffer | Since every player directly holds the same simulation result, there is no basis for interpolation (positions never arrive divergent in the first place) |
| `JustRespawnedTag` | Since everyone computes the same respawn outcome on the same tick, there is never a situation of "papering over a prediction/actual mismatch with a visual snap" |
| `TickRateHz` setting | Removed the fixed-tick-rate assumption codebase-wide (replaced by measured dt) |

Physics updates (`SimulationTickSystem`) and render synchronization are separated into distinct Systems: variable-frame physics that only runs when a server `TickCommit` arrives, and render synchronization that simply copies the latest confirmed value every frame, operate fully independently of one another.

```csharp
// PhysicsToRenderSyncSystem.cs
// Even if multiple ticks arrive within a single frame, intermediate positions are never
// interpolated — only the final confirmed value is applied (by design).
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(SimulationTickSystem))]
[UpdateBefore(typeof(SyncRenderTransformSystem))]
public partial class PhysicsToRenderSyncSystem : SystemBase
{
    protected override void OnUpdate()
    {
        foreach (var (physics, renderTransform) in
                 SystemAPI.Query<RefRO<TankPhysicsState>, RefRW<RenderTransform2D>>().WithAll<PlayerId>())
        {
            renderTransform.ValueRW.Position = physics.ValueRO.Position;
            renderTransform.ValueRW.RotationDeg = physics.ValueRO.RotationDeg;
        }
    }
}
```

`SimulationTickSystem` no longer advances on its own independent 60 Hz timer within Unity's `FixedStepSimulationSystemGroup`. Instead, it runs within the `SimulationSystemGroup` (variable frame rate), and on each frame it "gathers however many new `TickCommit`s arrived this frame and executes them in order, that many times." If the server does not send a `TickCommit`, this System advances zero ticks.

---

## 8. Load Test Bot — A Pure Traffic Generator That Performs No Physics

The bot communicates with the server using the identical binary protocol, but deliberately forgoes the physics computation that the DOTS client performs. Because there is no guarantee that Unity's `Mathf` and .NET's `MathF` trigonometric implementations produce bit-identical results, the design avoids this risk entirely by having the bot never determine its own death state.

```csharp
// BotClient.cs
// The bot doesn't care whether it's actually dead — it keeps generating and sending
// input according to its own behavior pattern regardless. If it's dead on a given tick,
// each DOTS client's SimulationTickSystem simply ignores that input on its own.
private (float throttle, float turn, bool fire) UpdateBehavior(float dt)
{
    _driveStateTimeLeft -= dt;
    if (_driveStateTimeLeft <= 0f) PickNewDriveState();

    float throttle = _driveState switch
    {
        DriveState.Forward => 1f,
        DriveState.Reverse => -1f,
        _ => 0f
    };
    return (throttle, turn, _isFiring);
}
```

Upon receiving a `TickCommit`, the bot extracts only the `Tick` number for its own `TargetTick` computation; it parses the `Join`/`Leave`/`Input` lists but does not meaningfully process them. Its sole purpose, purely from the server's perspective, is to generate "traffic patterns indistinguishable from an actual client."

```csharp
private void ProcessPacket(byte[] data)
{
    switch (Protocol.PeekType(data))
    {
        case PacketType.TickCommit:
            if (Protocol.TryReadTickCommit(data, out TickCommitData commit))
                _lastConfirmedTick = commit.Tick;   // Nothing but the Tick number is ever used
            break;
    }
}
```

If a connection attempt after the game has already started is rejected with `GameAlreadyStarted = true`, the bot does not attempt to reconnect and immediately records the attempt as a failed connection.

```csharp
catch (InvalidOperationException)
{
    // The server rejected the connection with GameAlreadyStarted=true. No reconnection
    // is attempted; the attempt is recorded as a failed join and the bot terminates.
    _stats.RecordJoinFailed();
    return;
}
```

`LoadTestStats` aggregates sent/received packet and byte counts lock-free, using only `Interlocked` operations, while `ConsoleDashboard` pins a real-time throughput display (pkt/s, KB/s) to the top of the console via cursor-position control.

```csharp
public void RecordSent(int bytes)
{
    Interlocked.Increment(ref _packetsSent);
    Interlocked.Add(ref _bytesSent, bytes);
}
```

### 8.1 Runtime Parameters

```
LoadTestBot [botCount] [serverIP] [serverPort] [options]
```

| Argument/Option | Description | Default |
|---|---|---|
| `botCount` | Number of bots (fake players) to spawn. If omitted, runs a smoke-test-scale default with no warning | `10` |
| `serverIP` | IP address of the server to connect to (hostnames not supported) | `127.0.0.1` |
| `serverPort` | Server port to connect to | `9050` |
| `--duration <seconds>` | Test duration in seconds. If omitted, runs unbounded until manually terminated via Ctrl+C | Unbounded |
| `--spawn-interval-ms <ms>` | Interval (ms per bot) used to stagger bot connections along the time axis, preventing a simultaneous-connection surge | `25` |

**Examples**

```
LoadTestBot                 (defaults: 10 bots, localhost:9050, unbounded)
LoadTestBot 100
LoadTestBot 200 192.168.0.10
LoadTestBot 500 192.168.0.10 9050 --duration 120 --spawn-interval-ms 10
```

> The server's player ID is a `byte` (0–255) with no wraparound handling, so if cumulative connections (including reconnections) exceed 254, ID collisions can occur. In addition, no explicit disconnect packet is sent on bot termination, so the server keeps terminated bots registered as live players for `TIMEOUT_SECONDS` (3 seconds) before cleaning them up.

---

### 8.2 Load Test Demo Video

A test video confirming deterministic state consistency across 10 bots. Positions are displayed to four decimal places for verification purposes.

[![Deterministic PlayTest: 10 bots](https://raw.githubusercontent.com/woodsshin/UnitySamples/main/DeterministicServer/Screenshot/ScreenShot.png)](https://youtu.be/SoECz6Zym2s)

---

## 9. Disconnection Handling — Immediate EntryScene Transition Without Retry

If no response is received from the server for longer than `ConnectionTimeoutSeconds` (based on `TimeSinceLastServerMessageMs`), the client closes the socket immediately and transitions to `EntryScene`, without any retry attempt.

```csharp
// NetworkConnectionSystem.CheckConnectionLost
private void CheckConnectionLost(ref ClientStateSingleton clientState, SimulationConfig config, NetworkConnectionData netData)
{
    if (netData.UdpClient == null) return;
    if (clientState.IsConnectionLost) return;

    long lastMsgMs = Interlocked.Read(ref netData.TimeSinceLastServerMessageMs);
    long nowMs = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
    float secondsSinceLastMessage = (nowMs - lastMsgMs) / 1000f;

    if (secondsSinceLastMessage < config.ConnectionTimeoutSeconds) return;

    clientState.IsConnectionLost = true;
    clientState.SceneTransitionCountdownSeconds = 0f;   // Unlike the 3-second wait on rejection, this transition is immediate

    if (netData.UdpClient != null)
    {
        try { netData.UdpClient.Close(); } catch { }
        netData.UdpClient = null;
    }
}
```

The identical decision logic (close the `UdpClient`, do not retry, transition to `EntryScene`) is implemented identically in both the DOTS client (`NetworkConnectionSystem.CheckConnectionLost`) and the MonoBehaviour sample client (`CustomClientSample.UpdateConnectionLossCheck`).

Whenever a new connection begins (at the moment `ConnectToServer` is invoked, and also at System/component shutdown), `DisconnectInternal` resets all session-scoped state back to its initial values. In a lockstep architecture, a new connection inherently means "the start of a new tick sequence," so not only `LastConfirmedTick` but every value derived from it must be reset together.

```csharp
// NetworkConnectionSystem.DisconnectInternal
clientState.MyPlayerId = 0;
clientState.IsConnected = false;
clientState.LastConfirmedTick = -1;

// TotalElapsedSimTime must also be reset to 0 here. If this value alone were left
// stale, the fire-cooldown/respawn resolution baseline for a newly spawned player
// would be computed relative to "the value accumulated during the previous
// connection," causing it to be completely out of alignment.
clientState.TotalElapsedSimTime = 0f;

// IsGameStarted is reset for the same reason — a new connection is a new session,
// and must be observed starting from the lobby state once again.
clientState.IsGameStarted = false;
```

This is precisely the point at which a bug would have emerged had only `TotalElapsedSimTime` been reset while these other fields were overlooked: the new session's fire-cooldown/respawn baseline would become entangled with the previous session's accumulated values. This is also why all state that must be reset at a session boundary is deliberately centralized in one place. `DisconnectInternal` is not part of any retry loop — it is called from exactly two locations: as the first step of `ConnectToServer` (cleanup before a new connection begins), and at System/component shutdown.

---

## 10. Project Structure

```
Server (Unity-independent)
├── DeterministicSampleServer.cs   Main server loop (input collection/relay only — no physics computation)
├── LoadTestStats.cs                Thread-safe statistics based on Interlocked operations
├── BotClient.cs                    Per-bot behavior/networking logic (no physics computation)
├── ConsoleDashboard.cs             Real-time console dashboard driven by cursor control
└── UnitTest.cs                     CLI entry point for the load test

Shared Protocol
└── Protocol.cs                     Game.Networking namespace — the canonical protocol
                                     definition (PacketType, serialization rules), physically
                                     copied into all three of server/client/bot

Client - DOTS/ECS (physics computed via an identical code path for every client)
├── NetworkComponents.cs            Shared component/singleton definitions (TankPhysicsState, etc.)
├── NetworkConnectionSystem.cs      Socket lifecycle, disconnect detection (transitions to
                                     EntryScene with no retry), session-scoped state reset
├── InputCaptureSystem.cs           Input capture + Input Delay computation (no prediction)
├── SimulationTickSystem.cs         TickCommit replay → movement/fire/collision/death/respawn
                                     (the core of lockstep)
├── PhysicsToRenderSyncSystem.cs    TankPhysicsState/MissileMotion → RenderTransform2D
├── RenderSyncSystems.cs            RenderTransform2D → Entities Graphics LocalTransform, color
├── UpdateBoundsAndPingSystem.cs    Camera bounds computation + RTT measurement
├── Authoring / ShipRenderAuthoring.cs / MissileRenderAuthoring.cs   Baking-time authoring
├── Authoring / GameBootstrapAuthoring.cs      Runtime prefab reference registration
├── Authoring / SimulationConfigAuthoring.cs   Physics constants that must be shared with the server
└── UI / GameHudBridge.cs           ECS ↔ TextMeshPro/OnGUI bridge, lobby/host UI

Client - MonoBehaviour (reproduces the same protocol/lockstep logic as DOTS in a single class)
└── CustomClient.cs                 CustomClientSample — a version that consolidates connection,
                                     simulation, render synchronization, and OnGUI HUD into a
                                     single MonoBehaviour. Rather than splitting state across ECS
                                     components, physics and score (PlayerRuntimeState) are held
                                     as class fields — but the processing order, sort conventions,
                                     and resolution logic are exactly identical to the DOTS client
```

---

## 11. Build Downloads

| File | Description | Link |
|---|---|---|
| `DeterministicClient.zip` | Unity client build (MonoBehaviour/ECS scene selectable) | [Download](https://drive.google.com/file/d/1oEA--eRsbYmHEB8hci3Z4ielES03dLPv/view?usp=sharing) |
| `DeterministicSampleServer.zip` | UDP server console executable | [Download](https://drive.google.com/file/d/1WNLPCeZiOrBe6HJX1rR7OkO2gCJqJLlK/view?usp=sharing) |
| `DeterministicLoadTestBot.zip` | Load test bot console executable | [Download](https://drive.google.com/file/d/1jA1U97UGRIRUvt1A37cSBA8jdCXA3oeC/view?usp=sharing) |

> The `DeterministicSampleServer` and `LoadTestBot` executables are built as Single File / Self-Contained, so they can be run directly as C# console applications on any PC — including ones without the .NET runtime installed.

---
## Tech Stack

- **Engine**: Unity (DOTS/ECS)
- **Client Architecture**: Deterministic Lockstep. The server only relays input; every client (the DOTS client and the MonoBehaviour sample client alike) independently computes physics by replaying the identical input sequence in the identical order. The Load Test Bot does not participate in this physics computation, and instead only generates traffic using the identical protocol.
- **Networking (Server)**: Pure C# UDP server (`System.Net.Sockets`, no dependency on the Unity runtime) — a physics-less Input Relay design paired with a custom binary protocol
- **Determinism Guarantee Techniques**: Measured delta-time relay, `JoinSequence`-based connection-order reproduction, a fixed per-tick processing-order convention (Join → Leave → Input → Missile → Respawn), rollback-free Input Delay
- **Load Testing**: A pure .NET console bot client (async/await, `PeriodicTimer`) — generates traffic patterns indistinguishable from real clients from the server's perspective, without performing any physics computation
- **Genre**: Top-down arcade combat (2D spaceship shooter, real-time PvP)
