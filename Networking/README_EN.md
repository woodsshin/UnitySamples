# Custom UNet Networking Layer with Steam P2P Support

Code that extends Unity's legacy networking framework, **UNet (HLAPI)**, to support two transport methods — a dedicated server and **Steam P2P** — under a single unified game logic. This code is excerpted from a previous project; game-specific information and project-specific logic have been stripped out, leaving only **the core parts that demonstrate the networking architecture**.

## Background and Problem Definition

In UNet, calling `SendBytes` / `SendWriter` through a `NetworkConnection` internally loads the message directly into the socket buffer. The problem arises the moment a client connects to the server — the server batches and sends `ObjectSpawnMessage` and `ObjectSpawnSceneMessage` for every `NetworkBehaviour` present in the scene, all at once.

In worlds with a large number of objects (hundreds to thousands of NetworkIdentity instances), this initial spawn traffic instantly exceeds the limit of the engine's internal socket buffer. This leads to the following problems:

- A "no free events for message in the pool" error occurs at the `NetworkTransport` level
- Clients disconnect due to timeout or experience missing object spawns right after connecting
- Steam P2P transport (based on `SteamNetworkingSockets`) has different queuing characteristics from a UDP socket, making bottlenecks more likely to occur

**Goal:** Without touching the engine's transport layer, apply frame-level flow control to the initial spawn traffic by maintaining a custom message queue at the level of inheriting/overriding `NetworkConnection`.

## Architecture Overview

```
NetworkManagerOverride (NetworkManager)
   ├─ Swaps the Connection class in StartHost() depending on whether P2P is used
   │     ├─ SteamNetworkManager.IsUsingP2P == true  → uses SteamNetworkConnection
   │     └─ false (dedicated server, etc.)          → uses CustomNetworkConnection
   │
   ├─ SetNetworkConnectionClass<T>() : the same Connection type must be set on
   │     both NetworkServer / NetworkClient for state to remain symmetric
   │
CustomNetworkConnection (NetworkConnection)
   ├─ SendBytes / SendWriter override
   │     └─ While ReadyForQueueing == true, messages on the ReliableSequenced
   │        channel are not sent immediately but loaded into MsgQueue(List<MessageQueue>)
   ├─ TransportSend override
   │     └─ In queuing mode, skips the actual send and returns as if it succeeded,
   │        preventing the engine from internally triggering its own retry logic
   ├─ FlushBuffer() (coroutine)
   │     └─ At the end of every frame (EndOfFrame), checks the available
   │        send capacity (buffer state) and dequeues and sends only that much
   └─ MessageQueue (IDisposable)
         └─ A value object representing a single queued message.
            Immediately copies the original byte[] for storage (safe even if
            the caller's buffer is reused)

SteamNetworkConnection (CustomNetworkConnection)
   ├─ ForceInitialize() : directly initializes a connection slot targeting
   │     "localhost" without going through UNet's internal init flow
   │     (UNet is unaware of the Steam session)
   ├─ TransportSend override : performs the actual send via Steamworks'
   │     SendP2PPacket, and immediately loopbacks to TransportReceive
   │     when sending to itself
   └─ CloseP2PSession() : waits until the remaining queue is fully drained,
         then closes the session

SteamNetworkClient (NetworkClient)
   └─ Connect() : forces ConnectState to Connected without going through the
         NetworkTransport connection procedure, making UNet treat it as
         "already connected"

SteamNetworkManager (MonoBehaviour)
   └─ Update() : polls the Steamworks P2P packet queue every frame and injects
         received bytes into the corresponding NetworkConnection.TransportReceive
         (acts as a replacement for the UDP socket event loop)

UNETServerController
   ├─ OnP2PSessionRequested() : receives the Steam P2P connection request
   │     callback and decides whether to accept it
   └─ CreateP2PConnectionWithPeer() : after acceptance, creates a
         SteamNetworkConnection and registers it into UNet's connection list
         via NetworkServer.AddExternalConnection()
```

## Key Excerpted Code

### 1. Connection Type Swap — Using a Different Class Depending on Transport Method

```csharp
public override NetworkClient StartHost()
{
    var localClient = base.StartHost();
    if (localClient == null) return null;

    SteamNetworkManager.IsUsingP2P = Settings.CurrentData.P2PHostEnabled;

    if (SteamNetworkManager.IsUsingP2P)
    {
        // Swap in a connection that uses a Steam P2P session (SteamNetworkingSockets)
        NetworkServer.SetNetworkConnectionClass<SteamNetworkConnection>();
        localClient.SetNetworkConnectionClass<SteamNetworkConnection>();
    }
    else
    {
        // Regular UDP-based connection (including spawn traffic queuing)
        NetworkServer.SetNetworkConnectionClass<CustomNetworkConnection>();
        localClient.SetNetworkConnectionClass<CustomNetworkConnection>();
    }
    return localClient;
}
```

The key point is specifying **the same Connection class** on both `NetworkServer` and `NetworkClient`. In UNet, when running as a local host (server and client running simultaneously), the two instances must behave with identical semantics — if only one side is swapped, the consistency between queuing and flush states breaks down, distorting the processing order of spawn messages.

### 2. Message Queuing — Loading into a Custom Queue Instead of Going Straight to the Socket

```csharp
public override bool SendBytes(byte[] bytes, int numBytes, int channelId)
{
    if (UseAdditionalQueue)
    {
        if (ReadyForQueueing && CustomChannels.ReliableSequenced == channelId)
        {
            if (MaxMsgQueueSize < MsgQueue.Count)
            {
                // If the queue accumulates excessively (misconfiguration or
                // critical delay), disconnect to prevent indefinite memory usage
                Disconnect();
                return false;
            }
            MsgQueue.Add(new MessageQueue(this, bytes, numBytes, channelId));
        }
        return true; // Report "success" to the engine; actual sending is fully owned by the queue
    }
    else if (MsgQueue.Count > 0)
    {
        FlushMessage(MsgQueue.Count);
    }
    return base.SendBytes(bytes, numBytes, channelId);
}
```

Queuing was limited to messages destined for the `ReliableSequenced` channel. This is because that channel is precisely where large-volume messages requiring guaranteed ordering, such as object spawn/despawn, are concentrated. The remaining channels (e.g., `StateUpdate`, `Unreliable`) are passed straight through, so gameplay does not experience delay.

### 3. `TransportSend` Override — Suppressing the Actual Send Itself While Queuing

```csharp
public override bool TransportSend(byte[] bytes, int numBytes, int channelId, out byte error)
{
    if (UseAdditionalQueue)
    {
        if (CustomChannels.ReliableSequenced != channelId)
        {
            error = 0;
            return true; // Treat as success without an actual send
        }
    }
    return base.TransportSend(bytes, numBytes, channelId, out error);
}
```

Overriding only `SendBytes` was not sufficient. Because `TransportSend` can be called through a separate path during UNet's internal reliable retry or ACK handling, the same channel filtering had to be applied at this point as well, to prevent the queuing logic and the actual send from colliding with each other.

### 4. Frame-Level Flush — Checking Buffer Headroom and Sending Only That Much

```csharp
IEnumerator FlushBuffer()
{
    yield return Yielders.EndOfFrame;

    while (MsgQueue.Count > 0 || UseAdditionalQueue)
    {
        if (MsgQueue.Count == 0)
        {
            yield return Yielders.EndOfFrame;
            continue;
        }

        // Check the remaining capacity of the transport layer
        // (UDP socket queue or Steam P2P session queue)
        if (!NetworkManagerOverride.IsAvailableMessageQueue(
                this, NetworkManagerOverride.ReservedMessageCount))
        {
            continue; // If there's no headroom, wait until the next frame
        }

        if (!FlushMessage(NetworkManagerOverride.ReservedMessageCount))
        {
            FlushBufferCoroutine = null;
            yield break; // On send failure, delegate to connection termination handling
        }

        yield return Yielders.EndOfFrame;
    }
    FlushBufferCoroutine = null;
}
```

`IsAvailableMessageQueue` branches into two paths (excerpted from the original):

```csharp
if (SteamNetworkManager.IsUsingP2P)
{
    // Steam P2P: query the number of packets queued per session directly via the Steamworks API
    P2PSessionState_t state;
    SteamNetworking.GetP2PSessionState(steamConn.steamId, out state);
    numBufferedMsgs += state.m_nPacketsQueuedForSend;
}
else
{
    // Default UNet transport: query the outgoing queue size of NetworkTransport
    numBufferedMsgs = NetworkTransport.GetOutgoingMessageQueueSize(
        _localClient.connection.connectionId, out error);
}
```

Behind the same "check buffer headroom" interface, the implementation branches to call completely different APIs (Steamworks vs. UNet Transport) depending on the transport method. The core of this design is encapsulating this difference so that the upper-level `FlushBuffer` coroutine logic doesn't need to be aware of it.

### 5. `MessageQueue` — A Value Object for a Message Awaiting Transmission

```csharp
public MessageQueue(CustomNetworkConnection conn, byte[] bytes, int numBytes, int channelId)
{
    this.conn = conn;
    // The original buffer may be reused by the caller, so deep-copy it immediately
    this.bytes = new byte[numBytes];
    Buffer.BlockCopy(bytes, 0, this.bytes, 0, numBytes);
    this.numBytes = numBytes;
    this.channelId = channelId;
}

virtual public bool SendMessage()
{
    if (conn != null && conn.IsValid())
    {
        return conn.BaseSendBytes(ref bytes, numBytes, channelId);
    }
    return false;
}
```

Since UNet's `NetworkWriter`/buffer is often reused immediately after the call, it must always be copied via `Buffer.BlockCopy` at the moment it's loaded into the queue. Omitting this leads to a defect where, by the time the queue is flushed, a buffer that has already been overwritten by a different message ends up being sent. `IDisposable` is implemented to explicitly release the buffer references of any remaining queue items when the connection closes.

## Steam P2P Integration Points

### Connection Parameter Conversion

`NetworkManagerHudOverride` acts as an adapter that receives Steam's Rich Presence / Join invite callbacks and converts them into UNet connection parameters.

```csharp
// Parse the "+connect steam.<steamid64>:<port>" format to determine
// whether this is an IP-based connection or a Steam P2P connection
int idx = serverIp.IndexOf("steam.");
if (idx != -1)
{
    serverIp = serverIp.Substring("steam.".Length);
    SteamNetworkManager.IsUsingP2P = true;
    ...
}
```

By reusing the same `+connect` syntax as the command line at the moment a friend invite is accepted (`GameRichPresenceJoinRequested_t`), the two entry paths — "direct connection via command-line argument" and "connection via the Steam overlay" — are unified to share the same parsing and connection logic.

The authentication flow strictly follows the standard server authentication flow required by Steamworks: an Auth Blob is issued via Steamworks' `InitiateGameConnection`, delivered to the server as a custom UNet message (`ValidateAuthBlobMessage`), and verified by the server via `SteamGameServer.SendUserConnectAndAuthenticate`.

### A Workaround Design That Replaces UNet's Transport Layer with Steam Sockets

UNet's HLAPI was originally designed on the assumption of its own `NetworkTransport` (LLAPI, UDP-based), so it has no direct point of contact with Steamworks' P2P packet send/receive API. The core of this project is to **construct state so that, from UNet's perspective, a normal connection has been established, while bypassing the layer so that the actual byte transmission is fully handled by the Steamworks P2P API**. Three layers work together organically to achieve this.

**1) Manually Constructing Connection State — `SteamNetworkClient.Connect()`**

```csharp
public void Connect()
{
    // Force UNet's ConnectState to "Connected" without an actual socket connection.
    // This state must be reached for internal initialization to proceed and for
    // the data send/receive path via TransportSend to open
    m_AsyncConnect = ConnectState.Connected;

    // Directly invoke the Connect message to trigger UNet's upper-level logic
    // (handler registration, etc.) to proceed normally
    connection.InvokeHandlerNoData(MsgType.Connect);
}
```

A normal UNet client transitions to `ConnectState.Connected` only after `NetworkTransport.Connect()` succeeds. Here, only the state itself is constructed directly, without an actual socket connection procedure, inducing UNet to proceed normally with the rest of its logic (handler dispatch, Ready handling, etc.) under the assumption that the connection has already been established.

**2) Manually Triggering Connection Initialization — `SteamNetworkConnection.ForceInitialize()`**

```csharp
public void ForceInitialize()
{
    int id = GameManager.IsServer ? 1 : 0;
    foreach (var connection in NetworkServer.connections)
    {
        if (connection.isConnected) id++;
        else break;
    }
    // Initialize only UNet's internal connection slot targeting "localhost",
    // without opening an actual socket
    Initialize("localhost", 0, id, SteamNetworkManager.hostTopology);
    RemoveExternalConnection();
}
```

Normally, `NetworkConnection.Initialize` is called automatically as part of UNet's internal flow, such as via `NetworkServer.AddExternalConnection`. However, because a Steam P2P session is created over a separate channel that UNet is unaware of, this initialization procedure must be invoked directly from code.

**3) Actual Transmission — Overriding `TransportSend` to Delegate to Steamworks**

```csharp
public override bool TransportSend(byte[] bytes, int numBytes, int channelId, out byte error)
{
    if (!SteamNetworkManager.IsUsingP2P || !steamId.IsValid())
        return base.TransportSend(bytes, numBytes, channelId, out error);

    if (steamId.m_SteamID == SteamUser.GetSteamID().m_SteamID)
    {
        // When sending to oneself (the case where the host is also a player),
        // deliver directly to the receive path without going over the network (local loopback)
        TransportReceive(bytes, numBytes, channelId);
        error = 0;
        return true;
    }

    // Map UNet's QoS channel settings (Reliable/Unreliable, etc.) to Steamworks' send type
    EP2PSend eP2PSendType = EP2PSend.k_EP2PSendReliable;
    QosType qos = SteamNetworkManager.hostTopology.DefaultConfig.Channels[channelId].QOS;
    if (qos == QosType.Unreliable || qos == QosType.UnreliableFragmented ||
        qos == QosType.UnreliableSequenced || qos == QosType.StateUpdate)
    {
        eP2PSendType = EP2PSend.k_EP2PSendUnreliable;
    }

    bool result = SteamNetworking.SendP2PPacket(steamId, bytes, (uint)numBytes, eP2PSendType, channelId);
    if (!result && eP2PSendType != EP2PSend.k_EP2PSendUnreliable)
    {
        // If a reliable send fails, treat the connection as unrecoverable and clean it up
        UNETServerController.Instance.RemoveConnection(steamId);
        error = 1;
        return false;
    }
    error = 0;
    return true;
}
```

The key is mapping UNet's channel concepts (`QosType.Reliable`, `ReliableSequenced`, `Unreliable`, etc.) onto Steamworks' two send modes (`k_EP2PSendReliable` / `k_EP2PSendUnreliable`). This results in a structure where the upper-level game logic only uses UNet's channel API, without needing to be aware of whether a UDP socket or Steam P2P is being used underneath.

**4) Actual Reception — Reading Steam Packets via Per-Frame Polling and Injecting Them into UNet**

`SteamNetworkManager.Update()` directly polls Steamworks' P2P packet queue every frame, independent of UNet's event loop.

```csharp
for (int chan = 0; chan < channels; chan++)
{
    while (SteamNetworking.IsP2PPacketAvailable(out packetSize, chan))
    {
        CSteamID senderId;
        if (SteamNetworking.ReadP2PPacket(_buffer, packetSize, out packetSize, out senderId, chan))
        {
            NetworkConnection conn = UNETServerController.IsHostingServer()
                ? UNETServerController.GetClient(senderId)   // Server: look up the sender's UNet connection
                : (myClient == null ? null : myClient.connection); // Client: fixed to the server connection

            if (conn != null && packetSize > 0)
            {
                // Feed directly into UNet's normal receive pipeline (handler dispatch, etc.)
                conn.TransportReceive(_buffer, Convert.ToInt32(packetSize), chan);
            }
        }
    }
}
```

In summary, the structure **overrides `TransportSend` to hand off to Steamworks when sending, and polls the Steamworks queue to inject into `TransportReceive` when receiving**. From UNet's point of view, it still only recognizes its own standard send/receive pipeline (serialization, handler dispatch, channel QoS) — the fact that the physical transport layer is Steamworks P2P rather than a UDP socket is completely abstracted away and hidden. To guard against a large burst of packets arriving at once during per-frame polling, the number processed is capped at a certain level (`recvCount > 512`), which also prevents any single frame from taking excessively long.

### Session Creation/Teardown Flow

**Server Side — Deciding Whether to Accept When Steam Notifies a P2P Connection Request via Callback**

```csharp
void OnP2PSessionRequested(P2PSessionRequest_t pCallback)
{
    var member = pCallback.m_steamIDRemote;
    if (NetworkServer.active)
    {
        SteamNetworking.AcceptP2PSessionWithUser(member);
        CreateP2PConnectionWithPeer(member); // Also create the UNet connection slot immediately upon acceptance
    }
}
```

`P2PSessionRequest_t` is the callback through which Steamworks notifies that "this SteamID has requested to open a P2P session." If `AcceptP2PSessionWithUser` is not called here, all packets sent by the other party are dropped. Upon acceptance, a new `SteamNetworkConnection` is created and registered into UNet's connection list via `ForceInitialize()` → `NetworkServer.AddExternalConnection()`, so that from then on it is treated identically to a regular UNet client.

**Connection Termination — Cleaning Up the Steam Session and the UNet Connection Together**

```csharp
public void CloseP2PSession()
{
    if (GameManager.IsServer)
    {
        Reset(); // The server cleans up immediately
    }
    else
    {
        // The client first notifies the server of its intent to disconnect,
        var msg = new DisconnectP2PMessage { SteamId = SteamUser.GetSteamID().m_SteamID };
        SendByChannel(CustomMsgs.RequestDisconnectP2P, msg, CustomChannels.ReliableSequenced);
        // then waits until all packets remaining in the queue have actually been sent before closing the session
        SteamNetworkManager.Instance.FlushLocalP2PPacket(this);
    }
}
```

When a client terminates the connection first, the session is not closed immediately after sending the disconnect message. The coroutine driven by `FlushLocalP2PPacket` waits every frame, checking `m_nPacketsQueuedForSend` via `SteamNetworking.GetP2PSessionState`, until it reaches 0 (i.e., until all packets awaiting transmission have been exhausted), and only then closes the session via `Reset()`. Otherwise, there is a risk that the session gets cut before the final message (such as the disconnect notification) is actually transmitted.

Additionally, on every tick, `ValidateSteamConnection()` verifies the session state reported by Steamworks (`m_bConnectionActive`) separately from UNet's own connection state. This is because, due to the nature of NAT environments, it is more common for Steam P2P for the other party to silently drop off without a response (due to firewalls, network switching, etc.) than with a direct UDP connection, so relying solely on UNet's timeout mechanism can result in delayed detection.

### Special Handling for the Case Where the Host Is Also a Player

Because this uses a listen-server structure (where the host is also the first player), "the connection for the local player" and "connections for remote players" coexist inside the server process. `UNETServerController.GetSteamIDForConnection` explicitly distinguishes between these two cases.

```csharp
if (NetworkServer.connections.Count >= 1 && conn == NetworkServer.connections[0])
    return SteamUser.GetSteamID(); // The server process's own SteamID

if (myClient != null && conn == myClient.connection)
    return SteamUser.GetSteamID(); // The local client's own SteamID, from the local client's perspective
```

Additionally, in `TransportSend`, when the recipient's SteamID matches the sender's own (the self-send branch described in item 3 above), it loopbacks directly to `TransportReceive` without going through the Steamworks P2P API. This is because routing even local host-to-player communication through the actual network stack (such as Steam relay servers) would incur unnecessary latency and waste session resources.

## Trade-offs Considered in the Design

| Consideration | Choice | Reason |
|---|---|---|
| Engine fork vs. override | Override via inheriting `NetworkConnection`/`NetworkManager` | Modifying the UNet source directly would greatly increase the cost of future Unity updates and maintenance. Solved within the scope of virtual method overrides |
| Which channels to queue | Limited to `ReliableSequenced` | Queuing every channel would delay even real-time-critical state synchronization. Only the channel where spawn bursts occur was selected |
| Handling queue overflow | Immediately `Disconnect()` when the threshold is exceeded | A failure exceeding the configured value (`MaxMsgQueueSize`) was judged to be an unrecoverable scenario in practice; explicit termination was chosen over a zombie connection |
| Unifying the P2P/non-P2P buffer-check approach | Unified interface, with branching only in the internal implementation | Isolates the Steam SDK dependency so that the upper-level logic (the coroutine) doesn't need to be aware of the transport method |

## What This Code Is Intended to Demonstrate

- A design that ports the desired flow-control layer into a third-party networking framework (UNet) whose source cannot be modified, **using nothing but virtual method overrides**
- A structure that abstracts two different transport media (UDP sockets / Steam P2P sessions) behind **the same high-level interface**, so that the game logic never needs to be aware of the transport method
- A low-level design that precisely analyzes UNet's connection state machine and **ports a Steam P2P transport layer** on top of it, working around the engine so that it recognizes it as a standard UDP connection
- Coroutine-based, frame-level flushing that naturally integrates into Unity's main loop for flow control, without polling or a separate thread

---

# Appendix: Large-World Join Streaming & Dedicated Server Admin Features

Where the section above covered "how to apply flow control to spawn traffic on an already-established connection," this appendix covers **"the problems that occur when a client first joins a large world with 10,000+ network objects"** and **"the admin features a dedicated server needs to operate."**

## 5 Problems Covered

1. **Buffer overflow** — sending 10,000+ NetworkIdentities/chunks of data all at once on connect exceeds the transport layer's send queue
2. **Progress loading screen** — with no way to report join progress to the client, users assume the game has frozen
3. **Players falling through the floor** — streaming chunks in arbitrary order can mean the terrain around the player's spawn point arrives late, so gravity calculations start before it exists
4. **Dedicated server admin features** — passwords, kick/ban, server name, remote commands, command-line parameters
5. **Replacing UNet's transport layer with Steam P2P** — delegating actual send/receive to the Steamworks P2P packet API instead of a socket, while reusing UNet's higher-level logic (serialization, channel QoS, handler dispatch) unchanged

---

## 1. Sequential intake — object spawn queue (`SpawnQueue.cs`)

UNet batches `ObjectSpawnMessage`/`ObjectSpawnSceneMessage` on every send tick of `NetworkServer`, but when the number of objects in the scene reaches the thousands-to-tens-of-thousands range, **simply calling `NetworkServer.Spawn()` repeatedly within a single frame** still produces a spike. Symptoms show up as "no free events for message in the pool" errors at the `NetworkTransport` level, timeouts right after connecting, or missing spawned objects.

```csharp
public void Enqueue(IEnumerable<GameObject> objects)
{
    foreach (var go in objects)
    {
        if (go != null) _pending.Enqueue(go);
    }
    _drainRoutine ??= StartCoroutine(DrainRoutine());
}

private IEnumerator DrainRoutine()
{
    while (_pending.Count > 0)
    {
        int spawnedThisFrame = 0;
        while (spawnedThisFrame < objectsPerFrame && _pending.Count > 0)
        {
            GameObject go = _pending.Dequeue();
            if (go == null) continue;

            NetworkIdentity identity = go.GetComponent<NetworkIdentity>();
            if (identity != null && !identity.isServer)
            {
                NetworkServer.Spawn(go);
            }
            spawnedThisFrame++;
        }
        yield return null;
    }
    _drainRoutine = null;
}
```

A cap (`objectsPerFrame`) is placed on how many objects can be spawned in one frame, with the rest carried over to the next. The coroutine keeps itself alive until the queue is drained and then exits, so no separate polling loop is needed.

---

## 2. Chunk streaming — distance-based prioritization (`ChunkStreamManager.cs`)

This is the most important part. Simple sequential sending solves the buffer problem, but **the order in which things are sent** can still leave the "falling through the floor" bug in place. If chunks far from the player's spawn point arrive first and the chunks underfoot arrive later, the client's gravity/collision calculations start on terrain that doesn't exist yet.

### Priority ordering

```csharp
private List<ChunkCoord> BuildPriorityOrder(ChunkCoord origin, IReadOnlyList<ChunkCoord> allChunks)
{
    var withDistance = new List<(ChunkCoord coord, int distSq, bool inPriorityRing)>(allChunks.Count);

    foreach (var c in allChunks)
    {
        int dx = c.X - origin.X;
        int dy = c.Y - origin.Y;
        int dz = c.Z - origin.Z;
        int distSq = dx * dx + dy * dy + dz * dz;
        bool inRing = distSq <= priorityRadius * priorityRadius;
        withDistance.Add((c, distSq, inRing));
    }

    // Chunks inside the priority ring always go first, then the rest by distance
    withDistance.Sort((a, b) =>
    {
        if (a.inPriorityRing != b.inPriorityRing)
            return a.inPriorityRing ? -1 : 1;
        return a.distSq.CompareTo(b.distSq);
    });

    var result = new List<ChunkCoord>(withDistance.Count);
    foreach (var entry in withDistance) result.Add(entry.coord);
    return result;
}
```

Rather than a plain distance sort, every chunk inside `priorityRadius` (the terrain immediately surrounding the player) is sent **first, in full**, before the rest streams out in distance order. This prevents cases where similar distance values happen to scramble the ordering between a chunk underfoot and a far-away one.

### Frame-by-frame sending + progress reporting

This uses UNet's `NetworkConnection.SendWriter()` API directly. That means this coroutine has no need to know whether the connection is a regular socket or the `SteamP2PConnection` from section 5 — it only operates against the base `NetworkConnection` class.

```csharp
private IEnumerator StreamChunksRoutine(NetworkConnection conn, StreamState state)
{
    while (state.NextIndex < state.Ordered.Count)
    {
        if (conn == null || !conn.isConnected)
        {
            _activeStreams.Remove(conn);
            yield break;
        }

        int sentThisFrame = 0;
        while (sentThisFrame < maxChunksPerFrame && state.NextIndex < state.Ordered.Count)
        {
            if (!NetworkFlowGate.HasCapacity(conn, reserved: 1))
                break;

            ChunkCoord coord = state.Ordered[state.NextIndex];
            ChunkPayload payload = ChunkDataSource.BuildPayload(coord);

            var chunkWriter = new NetworkWriter();
            chunkWriter.StartMessage(CustomMsgs.ChunkData);
            chunkWriter.Write(coord.X);
            chunkWriter.Write(coord.Y);
            chunkWriter.Write(coord.Z);
            chunkWriter.WriteBytesFull(payload.CompressedVoxelData);
            chunkWriter.FinishMessage();
            conn.SendWriter(chunkWriter, CustomChannels.ReliableSequenced);

            state.NextIndex++;
            state.SentCount++;
            sentThisFrame++;

            if (state.SentCount % 8 == 0 || state.NextIndex == state.Ordered.Count)
            {
                var progressWriter = new NetworkWriter();
                progressWriter.StartMessage(CustomMsgs.ChunkStreamProgress);
                progressWriter.Write(state.SentCount);
                progressWriter.Write(state.TotalCount);
                progressWriter.FinishMessage();
                conn.SendWriter(progressWriter, CustomChannels.Unreliable);
            }

            if (sentThisFrame >= chunksPerTick) break;
        }
        yield return null;
    }

    var completeWriter = new NetworkWriter();
    completeWriter.StartMessage(CustomMsgs.ChunkStreamComplete);
    completeWriter.FinishMessage();
    conn.SendWriter(completeWriter, CustomChannels.ReliableSequenced);

    _activeStreams.Remove(conn);
}
```

Progress messages (`ChunkStreamProgress`) aren't sent per chunk — they're batched in groups of 8 and sent on the `Unreliable` channel. That way progress notifications don't add extra load to the reliable channel we're trying to protect. `NetworkFlowGate.HasCapacity` is set up to branch based on connection type — a point that can be extended to use `GetP2PSessionState` for a `SteamP2PConnection`, or the transport layer's queue-query API for a regular socket.

`NetworkFlowGate.HasCapacity` is the branch point based on connection type — for a regular socket connection, the actual queue depth can be queried via UNet's `NetworkTransport.GetOutgoingMessageQueueSize`, but a `SteamP2PConnection` (section 5) isn't a socket, so `SteamNetworking.GetP2PSessionState`'s `m_nPacketsQueuedForSend` needs to be checked instead. This sample simplifies things by returning `true` in both cases, relying solely on the per-frame caps `chunksPerTick`/`maxChunksPerFrame` as sufficient.

---

## 3. Loading screen + fall-through-the-floor fix (`JoinLoadingScreen.cs`)

The loading UI is just a side effect — **what actually fixes the fall-through bug is pausing the player's physics simulation while streaming is in progress.**

```csharp
private void OnStreamBegin(ChunkStreamBeginMessage msg)
{
    _totalChunks = msg.TotalChunks;
    _receivedChunks = 0;
    SetVisible(true);
    SetPlayerSimulationEnabled(false);   // ← this is the key part
    UpdateBar(0, _totalChunks);
}

private void OnStreamComplete(ChunkStreamCompleteMessage msg)
{
    UpdateBar(_totalChunks, _totalChunks);
    SetPlayerSimulationEnabled(true);    // Only resume after streaming completes
    SetVisible(false);
}

private void SetPlayerSimulationEnabled(bool enabled)
{
    var localPlayer = NetworkClient.localPlayer;
    if (localPlayer == null) return;

    if (localPlayer.TryGetComponent<Rigidbody>(out var rb))
    {
        rb.isKinematic = !enabled;
    }
    if (localPlayer.TryGetComponent<PlayerMotor>(out var motor))
    {
        motor.enabled = enabled;
    }
}
```

If priority streaming (item 2) is "send the terrain underfoot first," this is the second line of defense: "don't run gravity calculations at all until that terrain has actually arrived." This also covers cases where network latency delays priority chunk delivery longer than expected.

---

## 4. Dedicated Server Admin Features

### 4-1. Command-line parameter parsing (`DedicatedServerConfig.cs`)

Parses arguments in the form `-file start <savename>`, `-logFile "path"`, and `-settings Key Value Key Value ...`.

```csharp
public static DedicatedServerConfig ParseFromArgs(string[] args)
{
    var config = new DedicatedServerConfig();

    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "-file":
                if (i + 2 < args.Length && args[i + 1] == "start")
                {
                    config.SaveName = args[i + 2];
                    i += 2;
                }
                break;

            case "-logFile":
                if (i + 1 < args.Length) config.LogFilePath = args[++i];
                break;

            case "-settings":
                i = ParseSettingsBlock(args, i + 1, config) - 1;
                break;
        }
    }
    return config;
}
```

The `-settings` block keeps consuming Key/Value pairs until the next flag appears (i.e. until it hits a token starting with `-`):

```csharp
private static int ParseSettingsBlock(string[] args, int startIndex, DedicatedServerConfig config)
{
    int i = startIndex;
    while (i + 1 < args.Length && !args[i].StartsWith("-"))
    {
        string key = args[i];
        string value = args[i + 1];

        switch (key)
        {
            case "ServerVisible": config.ServerVisible = ParseBool(value); break;
            case "GamePort": config.GamePort = ParseInt(value, config.GamePort); break;
            case "ServerName": config.ServerName = value; break;
            case "ServerPassword": config.ServerPassword = value; break;
            case "ServerAuthSecret": config.ServerAuthSecret = value; break;
            case "ServerMaxPlayers": config.ServerMaxPlayers = ParseInt(value, config.ServerMaxPlayers); break;
            // ... see the code for remaining keys
            default:
                UnityEngine.Debug.LogWarning($"[DedicatedServerConfig] Unknown setting '{key}', ignoring.");
                break;
        }
        i += 2;
    }
    return i;
}
```

An unrecognized key doesn't crash the server — it just logs a warning and moves on, so a single typo can't take down the whole server startup.

### 4-2. Password gate / kick / ban (`DedicatedServerAdmin.cs`)

```csharp
public bool TryAuthenticateJoin(NetworkConnection conn, string identity, string passwordAttempt, out string rejectReason)
{
    if (_bannedIdentities.Contains(identity))
    {
        rejectReason = "You are banned from this server.";
        return false;
    }

    if (!string.IsNullOrEmpty(Config.ServerPassword) && Config.ServerPassword != passwordAttempt)
    {
        rejectReason = "Incorrect server password.";
        return false;
    }

    if (NetworkServer.connections.Count >= Config.ServerMaxPlayers)
    {
        rejectReason = "Server is full.";
        return false;
    }

    _connectionIdentities[conn.connectionId] = identity;
    rejectReason = null;
    return true;
}

public void Kick(NetworkConnection conn, string reason = "Kicked by admin")
{
    SendNotice(conn, reason);
    conn.Disconnect();
}

public void Ban(string identity, string reason = "Banned by admin")
{
    _bannedIdentities.Add(identity);
    SaveBanList();

    var conn = FindConnectionByIdentity(identity);
    if (conn != null) Kick(conn, reason);
}

private void SendNotice(NetworkConnection conn, string message)
{
    var writer = new NetworkWriter();
    writer.StartMessage(CustomMsgs.AdminNotice);
    writer.Write(message);
    writer.FinishMessage();
    conn.SendWriter(writer, Channels.DefaultReliable);
}
```

Since `TryAuthenticateJoin` only takes the base `NetworkConnection` type, this method can be wired directly into `SteamP2PServerController.ShouldAcceptConnection()` from section 5, applying the same authentication logic to both socket connections and Steam P2P connections. The ban list is keyed by a stable identifier (Steam ID, IP, or whatever fits the project) rather than `connectionId` — since `connectionId` changes on every reconnect, it can't be used as a ban key.

### 4-3. Remote command execution (the `serverrun` approach)

Stationeers uses a pattern where typing `serverrun <command>` in the client console authenticates against a `ServerAuthSecret` configured identically on both server and client, then executes the command on the server. This pattern is generalized here:

```csharp
public bool TryExecuteRemoteCommand(string providedSecret, string command, out string result)
{
    if (string.IsNullOrEmpty(Config.ServerAuthSecret) || providedSecret != Config.ServerAuthSecret)
    {
        result = "Unauthorized.";
        return false;
    }
    result = DispatchCommand(command);
    return true;
}

private string DispatchCommand(string command)
{
    var parts = command.Split(' ', 2);
    string verb = parts[0].ToLowerInvariant();
    string arg = parts.Length > 1 ? parts[1] : string.Empty;

    switch (verb)
    {
        case "say": BroadcastChat(arg); return $"Broadcasted: {arg}";
        case "kick": return KickByIdentity(arg) ? $"Kicked {arg}" : $"No connected player matching '{arg}'";
        case "ban": Ban(arg); return $"Banned {arg}";
        case "unban": Unban(arg); return $"Unbanned {arg}";
        case "setname": SetServerName(arg); return $"Server name set to '{arg}'";
        case "setpassword": SetServerPassword(arg); return "Server password updated.";
        case "help": return "Commands: say <msg>, kick <id>, ban <id>, unban <id>, setname <name>, setpassword <pw>";
        default: return $"Unknown command '{verb}'. Try 'help'.";
    }
}
```

---

## Full List of Dedicated Server Parameters

### Top-level flags

| Flag | Value | Description |
|---|---|---|
| `-file start` | `<stationname> [worldid] [difficulty] [startcondition] [startlocation]` | Loads the specified save (station), or creates a new world if it doesn't exist. Only `stationname` is required; the rest are optional, but including one means every optional argument before it must also be included |
| `-logFile` | `"path"` | Custom log file path to use instead of `output_log.txt` |
| `-settings` | see table below | Sets server configuration values in bulk. e.g. `-settings ServerName "MyServer"` |

### `-settings` key list

| Key | Value | Description |
|---|---|---|
| `ServerVisible` | `true` / `false` | Whether the server is listed in the in-game server browser |
| `GamePort` | e.g. `27016` | Port players connect to |
| `UpdatePort` | e.g. `27015` | Steam update port |
| `UPNPEnabled` | `true` / `false` | Whether to use UPnP (automatic port forwarding); requires router support |
| `ServerName` | string | Server name |
| `ServerPassword` | string | Server connection password |
| `ServerAuthSecret` | string | Secret used to authenticate admin remote commands (`serverrun`) |
| `ServerMaxPlayers` | `1`–`20` | Maximum player slots (going above 20 is not recommended) |
| `AutoSave` | `true` / `false` | Whether to auto-save |
| `SaveInterval` | e.g. `300` (seconds) | Auto-save interval; going below 60 seconds is not recommended |
| `AutoPauseServer` | `true` / `false` | Whether to auto-pause when no one is connected |
| `UseSteamP2P` | `true` / `false` | Whether to allow Steam P2P connections (recommended to disable on a dedicated server) |
| `StartLocalHost` | `true` / `false` | Internal value required for the server to be reachable; changing it is not recommended |
| `LocalIpAddress` | e.g. `0.0.0.0` | (Linux) the network interface the server binds to |

---

## Appendix Architecture Summary

```
DedicatedServerConfig.ParseFromArgs(args)
   └─ Parses -file / -logFile / -settings

DedicatedServerAdmin
   ├─ TryAuthenticateJoin()    : checks password · ban list · capacity  ← can be wired into SteamP2PServerController.ShouldAcceptConnection()
   ├─ Kick() / Ban()           : forced removal keyed by a stable identifier
   └─ TryExecuteRemoteCommand(): dispatches commands after ServerAuthSecret authentication

SpawnQueue
   └─ Spreads bulk scene-object spawning across a per-frame cap

ChunkStreamManager
   ├─ BuildPriorityOrder()    : priority ring around the spawn point + distance sort
   └─ StreamChunksRoutine()   : per-frame cap + progress broadcast (via conn.SendWriter → SteamP2PConnection.TransportSend)

JoinLoadingScreen (client)
   ├─ ChunkStreamBeginMessage    → shows the loading screen + pauses player simulation
   ├─ ChunkStreamProgressMessage → updates the progress bar
   └─ ChunkStreamCompleteMessage → resumes simulation + hides the loading screen

```
