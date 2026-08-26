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
