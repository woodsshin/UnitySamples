# Custom UDP Server-Authoritative Multiplayer Framework

A Server-Authoritative multiplayer architecture validation project built around a **UDP server written as a pure C# console application**, independent of Unity's Dedicated Server ecosystem (Netcode for GameObjects, Unity Transport, etc.).

The server is fully decoupled from the Unity runtime, and this project validates that a **MonoBehaviour client** and a **DOTS/ECS client** can connect to the same server simultaneously and play the same game together. It also includes a custom **Load Test Bot** capable of reproducing sessions of tens to hundreds of concurrent players without any real users.

> **Project Goal**: To validate whether production-grade client-side prediction and reconciliation multiplayer can be implemented using nothing but a pure C# server and a custom binary protocol — without the Netcode dependency, build pipeline constraints, and licensing costs imposed by Unity's Dedicated Server package.

---

## 1. Pure C# Server

| Aspect | Unity Dedicated Server (NGO, etc.) | This Project (Custom C# Server) |
|---|---|---|
| Runtime | Requires a Unity Player build (headless engine initialization overhead) | .NET console app, zero Unity engine dependency |
| Deployment | Requires a platform-specific build pipeline | Deployable to a Linux container with a single `dotnet publish` |
| Tick Rate Control | Bound to Unity's FixedUpdate/PlayerLoop | Full control via a custom `Thread.Sleep`-based loop |
| Scalability | GameObject/Component overhead | Pure data structures based on `ConcurrentDictionary` |
| Scale-Out | Subject to engine licensing/seat constraints | Standard server process with high orchestration flexibility |

The server is implemented using `System.Net.Sockets.UdpClient` and has no reference to the Unity API whatsoever (`CustomServer.cs` contains no `using UnityEngine`). This allows it to be deployed as-is to Docker containers, Linux bare metal, or serverless container environments.

---

## 2. Network Architecture — Server-Authoritative + Client-Side Prediction

This architecture follows a **"server computes the source of truth, client predicts and then corrects against server values"** model, conceptually similar to Photon Fusion 2 and pre-GGPO approaches used in games like Rocket League.

```
[Client]                                         [Server, 60Hz]
   |-- ClientInput(tick, throttle, turn, fire) -->|
   |   (Immediately runs SimulateTankStep         |-- HandleClientInput
   |    locally, rendering the predicted           |   (Runs authoritative simulation
   |    result first)                              |    using identical logic)
   |                                               |
   |<-- ServerState(tick, full player snapshot) --|-- BroadcastServerState
   |   Replay pending input → compute error →     |
   |   3-tier reconciliation: snap/correct/hold   |
```

### 2.1 Server Tick Loop

The server runs simulation and broadcast logic on a dedicated thread at a fixed 60Hz interval.

```csharp
private void ServerLoop()
{
    int serverTick = 0;
    int intervalMs = (int)(1000f / TICK_RATE);
    float dt = 1.0f / TICK_RATE;

    while (_isRunning)
    {
        serverTick++;

        CheckTimeouts();
        CheckRespawns();
        UpdateMissiles(dt);
        BroadcastServerState(serverTick);
        BroadcastMissileState(serverTick);
        BroadcastScoreboardState();

        Thread.Sleep(intervalMs);
    }
}
```

Incoming packets are handled on a separate blocking `UdpClient.Receive` thread, with thread safety ensured via `ConcurrentDictionary<byte, PlayerState>`. This results in a **dual-thread model with separated receive and simulation threads**.

### 2.2 Client-Side Prediction

The client simulates input locally without waiting for a server response, ensuring responsive input handling. The movement formula used here (`SimulateTankStep`) must remain in **bit-identical computation order** with the server's `HandleClientInput`.

```csharp
// Order: 1) Rotate  2) Accelerate/decelerate toward target speed  3) Move  4) Wrap position only at boundaries.
public static void SimulateTankStep(SimulationConfig config, ref float2 pos, ref float rotDeg, ref float speed,
    float throttle, float turn, float dt)
{
    // 1) Rotation
    rotDeg += turn * config.TurnSpeedDeg * dt;
    rotDeg %= 360f;
    if (rotDeg < 0f) rotDeg += 360f;

    // 2) Accelerate/decelerate toward target speed
    float targetSpeed = throttle >= 0f ? throttle * config.MaxForwardSpeed : throttle * config.MaxReverseSpeed;
    float speedDelta = config.Acceleration * dt;
    speed = (speed < targetSpeed)
        ? Mathf.Min(speed + speedDelta, targetSpeed)
        : Mathf.Max(speed - speedDelta, targetSpeed);

    // 3) Forward/reverse movement (0 degrees = +Y, clockwise increase)
    float rotRad = rotDeg * Mathf.Deg2Rad;
    float2 dir = new float2(Mathf.Sin(rotRad), Mathf.Cos(rotRad));
    pos += dir * speed * dt;

    // 4) Screen boundary wrap (re-emerges on the opposite side instead of clamping)
    pos.x = WrapCoordinate(pos.x, config.ServerWorldHalfExtent.x);
    pos.y = WrapCoordinate(pos.y, config.ServerWorldHalfExtent.y);
}
```

This function is **ported with identical logic** to both the MonoBehaviour client (`CustomClient.cs`) and the ECS client (`LocalPlayerFixedStepSystem.cs`), guaranteeing that both client implementations always produce the same prediction results.

### 2.3 Server Reconciliation

Upon receiving a server snapshot, the client discards already-processed pending input and replays only the remaining input to compute the prediction error. Depending on the magnitude of the error, one of three strategies is applied — **snap (immediate correction), correct (gradual offset absorption), or hold (no correction)** — to minimize visually jarring teleport-like pops.

```csharp
// Position: 3-tier handling — snap / correct (offset absorption) / hold
if (posErrorSqrMag > config.SnapThreshold * config.SnapThreshold)
{
    predicted.Position = reconciledPos;   // Immediate snap if error is too large
    offset.Position = float2.zero;
}
else if (posErrorSqrMag > config.ErrorThreshold * config.ErrorThreshold)
{
    predicted.Position = reconciledPos;   // Update the underlying value
    offset.Position -= posError;          // But gradually absorb the visual offset (RenderErrorOffset)
}
// else: if the error is negligible, do nothing (hold prediction)
```

Every frame, `LocalPlayerRenderSmoothingSystem` then Lerps `RenderErrorOffset` toward zero, allowing positional corrections to be smoothly absorbed without any noticeable pop. This is a **Rollback-free Error-Smoothing reconciliation** approach, conceptually equivalent to Fusion 2's Soft/Hard Correction.

### 2.4 Boundary Handling — Interaction Between Wrapped Coordinates and Reconciliation

Instead of clamping at screen boundaries, this project adopts a **wraparound (re-emergence on the opposite side)** approach. In this case, computing positional difference via simple subtraction can misinterpret a wrap-boundary crossing as a massive error. Shortest-path error recalculation logic is identically implemented across the server and both client implementations.

```csharp
private static float WrapCoordinate(float value, float halfExtent)
{
    float range = halfExtent * 2f;
    float shifted = value + halfExtent;      // [-half, +half) -> [0, range)
    float wrapped = shifted % range;
    if (wrapped < 0f) wrapped += range;      // Correct C#'s negative modulo behavior
    return wrapped - halfExtent;             // Back to [-half, +half)
}
```

---

## 3. Remote Player Interpolation (Entity Interpolation)

While the local player is handled via prediction and reconciliation, remote players have no input data to predict against, so they are rendered smoothly by **interpolating between two past snapshots**. Rendering always targets the `Time.time - InterpolationDelay` timestamp, which serves as a buffer to absorb network jitter.

```csharp
float renderTime = Time.time - config.InterpolationDelay;

int index = 0;
while (index < headers.Length && headers[index].LocalTimeStamp < renderTime) index++;

// Interpolate between the two snapshots surrounding the index using factor t
float t = (renderTime - h1.LocalTimeStamp) / (h2.LocalTimeStamp - h1.LocalTimeStamp);
rt.Position = LerpWrapped(s1.Position, s2.Position, t, config.ServerWorldHalfExtent);
rt.RotationDeg = Mathf.LerpAngle(s1.RotationDeg, s2.RotationDeg, t);
```

Because the coordinate system wraps, **shortest-path interpolation (`LerpWrapped`)** is used instead of standard `Lerp`, preventing the visual artifact of an entity diagonally crossing the entire screen when it passes a wrap boundary.

"Unpredictable teleportation" events, such as respawns, are flagged with a separate `JustRespawnedTag` and excluded from the interpolation pipeline, applying an immediate snap instead. Interpolating between a death position and a new spawn position would otherwise produce a teleport-like visual artifact across the screen.

---

## 4. Dual Client Implementations — MonoBehaviour vs. DOTS/ECS

The same binary protocol and the same prediction/reconciliation algorithm were independently implemented across **two distinct Unity architectures**, validating that the protocol design is not coupled to any specific client architecture.

| Layer | MonoBehaviour Implementation | ECS/DOTS Implementation |
|---|---|---|
| Network Thread | `Thread` internal to `CustomClient` | `NetworkConnectionSystem` (InitializationSystemGroup) |
| Input/Prediction | Single `FixedUpdate()` method | `LocalPlayerFixedStepSystem` (FixedStepSimulationSystemGroup) |
| Reconciliation | Inline logic within `Update()` | `ServerStateApplySystem.ReconcileLocalPlayer` |
| Remote Interpolation | `InterpolateRemotePlayers()` | `RemotePlayerInterpolationSystem` |
| Render Sync | Direct assignment to `transform.position` | `RenderTransform2D` → converted to `LocalTransform` by `SyncRenderTransformSystem` |
| State-View Bridge | None (direct GameObject manipulation) | `GameHudBridge` (a Hybrid MonoBehaviour reads the ECS singleton and renders via TMP/OnGUI) |

The ECS version employs a **Hybrid Bridge pattern** that cleanly separates simulation Systems (pure data) from presentation logic (TextMeshPro, OnGUI).

```csharp
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(ServerStateApplySystem))]
public partial class RemotePlayerInterpolationSystem : SystemBase
{
    protected override void OnUpdate()
    {
        // The ECS side only updates RenderTransform2D (pure float2/float data);
        // the actual TextMeshPro/SpriteRenderer objects are synchronized
        // by GameHudBridge.Update(), which polls the EntityManager every frame.
    }
}
```

Both clients can be freely switched via the scene selection screen through `SceneSelector.cs`, and **both can connect to the same server simultaneously, recognizing one another as opposing players**. This empirically demonstrates that the server protocol is fully independent of client implementation choices.

---

## 5. Custom Binary Protocol

Rather than relying on general-purpose serialization formats like JSON or Protobuf, a **minimal-overhead, fixed-layout binary protocol** was custom-designed using `BinaryWriter`/`BinaryReader`. The first byte of each packet identifies its type.

```csharp
public enum PacketType : byte
{
    JoinRequest = 1, JoinResponse = 2, ClientInput = 3, ServerState = 4,
    Ping = 5, Pong = 6, MissileState = 7, ScoreboardState = 8
}

public static byte[] BuildClientInput(byte playerId, int clientTick, float throttle, float turn, bool fire)
{
    using var ms = new MemoryStream();
    using var bw = new BinaryWriter(ms);
    bw.Write((byte)PacketType.ClientInput);
    bw.Write(playerId);
    bw.Write(clientTick);
    bw.Write(throttle);
    bw.Write(turn);
    bw.Write(fire);
    return ms.ToArray();
}
```

The server state broadcast is a variable-length packet proportional to the number of connected players, and field ordering must match exactly between sender and receiver (a deliberate trade-off of low-level serialization without reflection or code generation: no automatic schema validation, requiring manual synchronization).

```csharp
bw.Write((byte)PacketType.ServerState);
bw.Write(serverTick);
bw.Write(_players.Count);
foreach (var kvp in _players)
{
    var p = kvp.Value;
    bw.Write(p.Id); bw.Write(p.X); bw.Write(p.Y);
    bw.Write(p.Rotation); bw.Write(p.CurrentSpeed);
    bw.Write((byte)p.Health); bw.Write(p.IsDead);
}
```

---

## 6. Server-Side Gameplay Logic

All gameplay resolution — movement, firing, hit detection, death, and respawn — is handled exclusively on the server. The client is responsible only for visual prediction and never trusts its own results, following a textbook **Authoritative Server** model.

- **Fire-Rate Limiting**: Rather than detecting an edge transition in client input, the server enforces firing frequency purely via a cooldown (`FIRE_COOLDOWN_SECONDS`) as long as `fire == true` remains active.
- **Collision Detection**: Missile-tank circular collision is computed on the server every tick. The client renders its own predicted missile independently, then reconciles position once the server confirms.
- **Range-Based Expiration**: Since the "out of bounds" condition is meaningless in a wrapped coordinate system, missile lifetime is instead limited by cumulative distance traveled from the firing point.

```csharp
private void UpdateMissiles(float dt)
{
    float collisionDistSqr = (MISSILE_RADIUS + TANK_COLLISION_RADIUS) * (MISSILE_RADIUS + TANK_COLLISION_RADIUS);

    foreach (var kvp in _missiles)
    {
        var missile = kvp.Value;
        missile.X += MathF.Sin(rotRad) * stepDistance;
        missile.Y += MathF.Cos(rotRad) * stepDistance;
        missile.DistanceTraveled += stepDistance;

        if (missile.DistanceTraveled >= MISSILE_MAX_DISTANCE) { _missiles.TryRemove(kvp.Key, out _); continue; }

        foreach (var playerKvp in _players)
        {
            var target = playerKvp.Value;
            if (target.Id == missile.OwnerId || target.IsDead) continue;

            float dx = missile.X - target.X, dy = missile.Y - target.Y;
            if ((dx * dx + dy * dy) <= collisionDistSqr)
            {
                _missiles.TryRemove(kvp.Key, out _);
                ApplyDamage(target, 1, missile.OwnerId);
                break;
            }
        }
    }
}
```

**In-Place Respawn**: On death, position is preserved and only health is reset, ensuring that the client's "immediate snap" reconciliation logic effectively becomes a harmless zero-distance snap. This is a point where client-side edge-case handling and server-side data design complement each other by design.

---

## 7. Load Testing Tool (LoadTestBot)

Without requiring any real Unity client, a **pure .NET console application** spawns a group of bots that communicate with the server using the identical binary protocol, validating server scalability.

```csharp
private static async Task<int> Main(string[] args)
{
    var bots = new List<BotClient>(config.BotCount);
    for (int i = 0; i < config.BotCount; i++)
    {
        var bot = new BotClient(serverEndPoint, stats);
        bots.Add(bot);
        botTasks.Add(bot.RunAsync(cts.Token));

        // Stagger connection timing to avoid a simultaneous connection burst
        if (config.SpawnIntervalMs > 0 && i < config.BotCount - 1)
            await Task.Delay(config.SpawnIntervalMs, cts.Token);
    }
}
```

Each bot runs a 60Hz input-sending loop based on `async/await` and `PeriodicTimer`, using randomly weighted behavior patterns for forward/reverse/turn/idle actions along with burst-fire patterns to closely emulate realistic player traffic.

```csharp
private void PickNewDriveState()
{
    double roll = _random.NextDouble();
    _driveState = roll switch
    {
        < 0.55 => DriveState.Forward,
        < 0.70 => DriveState.Turn,
        < 0.85 => DriveState.Idle,
        _      => DriveState.Reverse
    };
}
```

`LoadTestStats` aggregates sent/received packet and byte counts lock-free using only `Interlocked` operations, while `ConsoleDashboard` uses cursor position control to pin real-time throughput metrics (pkt/s, KB/s) to the top of the console. This design allows a scrolling log stream and a live dashboard to coexist on a single screen without conflict.

```csharp
public void RecordSent(int bytes)
{
    Interlocked.Increment(ref _packetsSent);
    Interlocked.Add(ref _bytesSent, bytes);
}
```

### 7.1 Execution Parameters

```
LoadTestBot [BotCount] [ServerIP] [ServerPort] [Options]
```

| Argument/Option | Description | Default |
|---|---|---|
| `BotCount` | Number of bots (simulated players) to spawn. If omitted, runs at a smoke-test scale by default with no warning. | `10` |
| `ServerIP` | IP address of the target server (hostnames are not supported) | `127.0.0.1` |
| `ServerPort` | Target server port | `9050` |
| `--duration <seconds>` | Test duration in seconds. If omitted, runs indefinitely until manually terminated with Ctrl+C. | Unlimited |
| `--spawn-interval-ms <ms>` | Interval (ms per bot) used to stagger bot connections over time, preventing a simultaneous connection burst | `25` |

**Examples**

```
LoadTestBot                 (Defaults: 10 bots, localhost:9050, unlimited duration)
LoadTestBot 100
LoadTestBot 200 192.168.0.10
LoadTestBot 500 192.168.0.10 9050 --duration 120 --spawn-interval-ms 10
```

> The server's player ID is a `byte` (0–255) with no wraparound handling, so cumulative connections exceeding 254 (including reconnections) may result in ID collisions. Additionally, since no disconnect packet is sent on shutdown, the server keeps bots registered as active players for `TIMEOUT_SECONDS` (3 seconds) after the program exits before cleaning them up.

### 7.2 Load Test Demo Video

A test video demonstrating server throughput and client synchronization behavior as the bot count is scaled sequentially from 10 up to 200.

[![Load Test: 10 -> 200 bots](https://raw.githubusercontent.com/woodsshin/UnitySamples/main/CustomServer/Screenshot/ScreenShot.png)](https://youtu.be/-qsfweI85r4)

---

## 8. Reconnection Watchdog

In the event of a server restart or a transient network disruption, the client automatically recreates its socket and attempts to reconnect after receiving no response for a configured duration (`ConnectionTimeoutSeconds`). Identical watchdog logic is implemented in both the MonoBehaviour and ECS clients.

```csharp
private void UpdateReconnectionWatchdog()
{
    float secondsSinceLastMessage = (nowMs - lastMsgMs) / 1000f;
    if (secondsSinceLastMessage < _connectionTimeoutSeconds) { _reconnectTimer = 0f; return; }

    _reconnectTimer += Time.deltaTime;
    if (_reconnectTimer >= _reconnectIntervalSeconds)
    {
        _reconnectTimer = 0f;
        ConnectToServer(); // Clean up the existing socket/view before reconnecting
    }
}
```

---

## 9. Project Structure

```
Server (Unity-independent)
├── CustomServer.cs         Server main loop, simulation, broadcast logic
├── Protocol.cs              (Bot-only) Packet serialization/deserialization helpers
├── LoadTestStats.cs         Interlocked-based thread-safe statistics
├── BotClient.cs             Individual bot behavior/networking logic
├── ConsoleDashboard.cs       Cursor-control-based real-time console dashboard
└── UnitTest.cs               Load test CLI entry point

Client - MonoBehaviour
└── CustomClient.cs   Prediction/reconciliation/interpolation fully implemented in a single component

Client - ECS/DOTS
├── Components / NetworkComponents.cs             Shared component/singleton definitions
├── Systems / NetworkConnectionSystem.cs          Socket lifecycle, reconnection watchdog
├── Systems / LocalPlayerFixedStepSystem.cs       Input collection + client-side prediction
├── Systems / ServerStateApplySystem.cs           Snapshot application + reconciliation
├── Systems / RemotePlayerInterpolationSystem.cs  Remote interpolation
├── Systems / LocalPlayerRenderSmoothingSystem.cs Reconciliation error smoothing
├── Systems / MissileSystem.cs                    Predicted missiles + server confirmation matching
├── Systems / ScoreboardApplySystem.cs            Kill/death synchronization
├── Systems / UpdateBoundsAndPingSystem.cs        Camera bounds calculation + RTT measurement
├── Systems / RenderSyncSystems.cs                ECS Transform → Entities Graphics sync
├── Authoring / ShipRenderAuthoring.cs / MissileRenderAuthoring.cs   Authoring components for baking
├── Authoring / GameBootstrapAuthoring.cs         Runtime prefab reference registration
├── Authoring / SimulationConfigAuthoring.cs      Server constant mirroring configuration
└── UI / GameHudBridge.cs                         ECS ↔ TextMeshPro/OnGUI bridge

Shared
├── SceneSelector.cs          MonoBehaviour/ECS scene transition UI
└── InputConfigManager.cs     Input System-based key binding management/persistence
```

---

## 10. Build Downloads

| File | Description | Link |
|---|---|---|
| `ClientBuild.zip` | Unity client build (MonoBehaviour/ECS scene selectable) | [Download](https://drive.google.com/file/d/15OwIuR4qvaMoIUMMC-D2Gbs-5OdCS66m/view?usp=sharing) |
| `CustomServer.zip` | UDP server console executable | [Download](https://drive.google.com/file/d/1zSpl6X2qY2FMTBusv9dBUFNJ4JcgrVF2/view?usp=sharing) |
| `LoadTestBot.zip` | Load test bot console executable | [Download](https://drive.google.com/file/d/1aV_XcXkb1NRWe-5OqFGVNWpANZq9kmi4/view?usp=sharing) |

> The `CustomServer` and `LoadTestBot` executables were built with the Single File and Self-Contained deployment options, allowing the C# console programs to run directly on any machine without a pre-installed .NET runtime.

---

## 11. C++ Server Port — Validating Language Independence of the Protocol and Simulation Logic

If Section 1 validated independence from the Unity engine, this section goes a step further and validates whether **the wire protocol and server simulation logic themselves are tied to any specific language or runtime**. The server was rewritten from scratch in C++17, targeting behavior identical to `CustomServer.cs`, and the two implementations are **interchangeable at the protocol level against the same Unity client**.

> Porting goal: keep constants (`Protocol.h`), packet field order, per-tick computation order, and wrap/collision/respawn judgments numerically identical to the C# original. If even one of these drifts, client prediction will keep diverging from server values, surfacing as constant reconciliation snaps.

| Aspect | C# Server (`CustomServer.cs`) | C++ Server (`CustomServer.cpp`) |
|---|---|---|
| Language/Standard | C# / .NET | C++17 |
| Network API | `System.Net.Sockets.UdpClient` | POSIX sockets / Winsock2 (platform-abstracted via `socket_t`) |
| Concurrency Control | `ConcurrentDictionary` | `std::mutex` + `std::unordered_map` (`_Locked` naming convention makes the lock-held precondition explicit) |
| Binary Serialization | `BinaryReader` / `BinaryWriter` | Manual little-endian byte encoding (`BinaryStream.h`) |
| Build/Deployment | `dotnet publish` (Self-Contained bundle) | `make` — native binary with no external runtime dependency |
| Wire Protocol & Tick Computation Order | PacketType 1–8, rotate→accelerate/decelerate→move→wrap→fire | **Fully identical** — the two servers are interchangeable against the same client |

### 11.1 Binary Protocol Compatibility

`BinaryStream.h` reproduces, byte for byte, the wire format produced by .NET's `BinaryReader`/`BinaryWriter`. It matches fixed-width little-endian integers, 32-bit IEEE-754 floating-point (`float`), and even the convention of writing `bool` as a single byte (0/1), so that **the C++ server can parse client packets designed for the C# server as-is**. The 64-bit `double` path (`WriteDouble`/`ReadDouble`) is not implemented, but since every real-valued field in `PlayerState`/`MissileState` is a `float`, this is not a limitation within the scope of the protocol.

```cpp
void WriteSingle(float v)
{
    static_assert(sizeof(float) == 4, "expected 32-bit float");
    uint32_t bits;
    std::memcpy(&bits, &v, 4);   // Extract the IEEE-754 bit pattern as-is
    WriteUInt32Raw(bits);         // Write as 4 little-endian bytes
}
```

x86/x64 is already little-endian, so a raw `memcpy` of the native type would work too, but the byte-level shifting/masking is implemented explicitly so the behavior doesn't implicitly depend on host endianness.

### 11.2 Porting the Concurrency Model — `ConcurrentDictionary` → `std::mutex`

C#'s `ConcurrentDictionary` is thread-safe as a collection in its own right, but C++'s standard library containers are not. This was replaced with a **coarse-grained lock, using a single `std::mutex` to protect `players_`/`missiles_`/`scoreboard_` as a whole**. The design reflects two judgments: every tick needs a consistent snapshot across all players/missiles anyway, and a single mutex doesn't become a bottleneck at this packet volume.

```cpp
while (isRunning_)
{
    serverTick++;
    {
        std::lock_guard<std::mutex> lock(stateMutex_);
        CheckTimeouts_Locked();
        CheckRespawns_Locked();
        UpdateMissiles_Locked(dt);
        BroadcastServerState_Locked(serverTick);
        BroadcastMissileState_Locked(serverTick);
        BroadcastScoreboardState_Locked();
    }
    std::this_thread::sleep_for(intervalMs);
}
```

A naming convention appends `_Locked` to every private method that requires the lock, so the contract — "the caller must already be holding the lock" — is visible from the signature alone. The dual-thread structure itself, with a separate receive thread (`ReceiveLoop`) and tick-loop thread (`ServerLoop`), is identical to the C# original.

### 11.3 Simulation Equivalence — Identical Computation Order, Identical Formulas

The rotate → accelerate/decelerate → move → wrap → fire order in `HandleClientInput` is exactly identical to C#'s `SimulateTankStep`. If even one step in this order changes, client prediction and server results diverge, so the source comments explicitly note that reordering these steps would desync client prediction. The coordinate wrap formula was ported identically as well.

```cpp
float CustomServer::WrapCoordinate(float value, float halfExtent)
{
    float range = halfExtent * 2.0f;
    if (range <= 0.0f) return 0.0f;

    float shifted = value + halfExtent;
    float wrapped = std::fmod(shifted, range);
    if (wrapped < 0.0f) wrapped += range; // fmod keeps the sign of the dividend (same as C#'s %)
    return wrapped - halfExtent;
}
```

C#'s `%` and C++'s `std::fmod` share the same semantics — both follow the sign of the dividend (the left-hand operand) — so the negative-value correction logic was carried over as-is to keep the results from diverging.

### 11.4 Platform-Porting Details — Time Sentinels and Cross-Platform Sockets

These are the points that needed separate attention, specifically because of language/runtime differences, when moving from C# to C++.

**Time Sentinel.** C# uses `DateTime.MinValue` as a sentinel meaning "infinitely far in the past," so the very first fire's cooldown check always passes. `std::chrono::steady_clock`, however, offers no guarantee that a default-constructed `time_point` points to a far-past moment (its epoch can be the boot time, depending on the platform). Porting this literally would mean that, right after process startup, the value could end up small rather than "far in the past," leading to a bug — so a separate sentinel function with an explicit, safe offset was written instead.

```cpp
inline TimePoint FarPast()
{
    // TimePoint::min() itself risks overflow on subtraction here,
    // so an offset with safe headroom is used instead
    return TimePoint::min() + std::chrono::hours(24 * 365 * 10);
}
```

**Cross-Platform Sockets.** The socket API is abstracted behind a `socket_t` type and `#ifdef _WIN32` branches so the same source compiles on both Windows (Winsock2) and POSIX (Linux, etc.). It also handles a well-known Windows UDP pitfall — where a prior `sendto` triggering an ICMP Port Unreachable can cause a subsequent `recvfrom` on a bound, unconnected socket to fail with `WSAECONNRESET` — matching the C# server's `SIO_UDP_CONNRESET` suppression on Windows builds, while leaving it as a documented no-op on Linux builds, where this issue doesn't occur.

### 11.5 Build and Testing

Builds using only the standard library and platform socket headers, with no external package dependencies.

```makefile
CXXFLAGS ?= -std=c++17 -O2 -Wall -Wextra -pthread
```

```
make        # builds the custom_server binary
make run    # builds and runs immediately
```

By default, `main.cpp` waits for Enter to stop the server, just like the C# original, but a test-only path was added: if the `CUSTOM_SERVER_RUN_SECONDS` environment variable is set, the server runs for a fixed duration and then shuts down automatically. This lets an automated test pipeline start up and validate the server without needing to hold open standard input.

---

## Tech Stack
- **Engine**: Unity
- **Client Architecture**: Dual implementation (MonoBehaviour / DOTS-ECS) to compare client-side prediction and reconciliation across both architectures using an identical protocol
- **Networking (Server)**: Pure C# UDP server (`System.Net.Sockets`, no Unity runtime dependency) — Server-Authoritative simulation + custom binary protocol
- **Server Port**: C++17 server port (`std::mutex`, POSIX/Winsock cross-platform sockets) — fully identical wire protocol and simulation computation order to the C# server, validating language/runtime independence
- **Networking Approach Comparison**: A comprehensive evaluation of feasibility, projected cost, and level of technical control when building a custom server, benchmarked against Unity Dedicated Server and Photon-based commercial solutions
- **Load Testing**: Pure .NET console bot client (async/await, `PeriodicTimer`) — Large-scale concurrent connection simulation without any Unity client
- **Genre**: Top-down Arcade Combat (2D spaceship shooter, real-time PvP)
