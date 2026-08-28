# Steamworks Integration Module

A module that integrates the Steamworks SDK (Steamworks.NET) into a Unity (UNET)-based game. It supports core Steamworks features including **Stats/Achievements, Leaderboard, Matchmaking/Server Browser, Workshop (UGC), P2P Networking, and Friends/Avatar**, and abstracts all asynchronous Steam API calls behind a single common framework so they're handled in a consistent way.

---

## 1. Why an Asynchronous Task Framework Was Needed

### 1.1 The Problem

Most of the Steamworks API doesn't return a result immediately — instead it returns a `SteamAPICall_t` handle, and the result has to be checked **at some point after the next frame** via `ISteamUtils.GetAPICallResult()` or a callback. On top of that, this project had to handle three fundamentally different kinds of asynchronous work at the same time.

| Type | Example | Characteristics |
|---|---|---|
| Steam API call/callback | Stats, Leaderboard, Workshop (UGC) | `SteamAPICall_t` polling or `Callback<T>` events |
| Steam Matchmaking interface callback | Server browser, LAN search | Delegate-based, e.g. `ISteamMatchmakingServerListResponse`; completion fires multiple times, once per individual server response |
| Raw UDP socket communication | A2S_INFO, A2S_PLAYER, A2S_RULES queries | Plain socket I/O rather than Steamworks, using `BeginReceive`/`EndReceive` callbacks |

Even though these three types have completely different ways of determining completion, **the calling code (UI, game logic) needs to be able to treat them all the same way** for the codebase to remain maintainable. Writing separate polling/callback handling logic for every feature leads to problems such as:

- Timeout handling, failure handling, and delegate invocation patterns end up scattered and inconsistent across the codebase
- Calling the Steam API before it's initialized, or after it's been cleaned up by the GC, throws an `InvalidOperationException` — something every individual call site would otherwise have to guard against on its own
- When multiple asynchronous requests run at the same time, race conditions can occur between the order of Steamworks callbacks and the game thread (Unity's main thread)

### 1.2 Design Direction: Command Pattern + Single-Threaded Serial Execution Queue

To solve this, every asynchronous Steam operation was **encapsulated in a common abstract class called `SteamAsyncTask`**, and `SteamAsyncTaskManager` was designed to **load these into a queue and process them one at a time, serially, inside `Update()`**.

```
[Caller] → new SteamAsyncXXX(...) → QueueAsyncTask() → Queue<SteamAsyncTask>
                                                              │
                                     SteamAsyncTaskManager.Update() (every frame)
                                                              │
                              ActiveTask.Tick() → checks IsTaskDone / IsTimeOut
                                                              │
                         FinalizeTask() → TriggerDelegates() → advance to the next task
```

The reasons for adopting this structure are as follows.

**① Unity Main-Thread Safety**
Steamworks.NET's callbacks receive and handle the results of communication with the Steam client process. By polling with `ISteamUtils.IsAPICallCompleted()` inside `Tick()`, this can be handled safely within Unity's `Update()` loop without any separate thread-synchronization code. There are also cases that genuinely run on a different thread, such as socket callbacks (`BeginReceive`) — but even in those cases, only the task's internal state (`IsTaskDone`, `WasSuccessful`) is updated there, while the actual post-processing (`FinalizeTask`, `TriggerDelegates`) always runs inside the manager's `Update()`. This guarantees that the side receiving the delegate (mainly the UI) always operates safely within the main-thread context.

**② A Serial Queue with a Single Processing Path**
The structure only dequeues and runs the next task when `ActiveTask` is `null`. This matches a characteristic of Steamworks where, for example, the Stats/Leaderboard APIs effectively require one request to complete before the next can be guaranteed to process in order (e.g., `SetUserStat`/`GetUserStat` need to be called after `RequestUserStats` completes) — so **request order and completion order are kept aligned**. This structurally blocks ordering bugs such as "trying to increment a stat before it has even been received."

`SteamAsyncTaskManager`'s `Update()` is the code that directly implements the diagram above: it only dequeues the next task while `ActiveTask` is empty, and calls `FinalizeTask → TriggerDelegates` only once the task is done (`IsTaskDone`) or has timed out (`IsTimeOut()`), before clearing `ActiveTask`:

```csharp
// SteamAsyncTaskManager.cs — the core loop that processes exactly one task at a time, serially
private void Update()
{
    if (!IsInitialized)
    {
        return;
    }

    lock (TaskLock)
    {
        if (null == ActiveTask && TaskQueue.Count > 0)
        {
            lock (QueueLock)
            {
                ActiveTask = TaskQueue.Dequeue();
            }
        }
    }

    if (ActiveTask != null)
    {
        ActiveTask.Tick();
        if (ActiveTask.IsTaskDone || ActiveTask.IsTimeOut())
        {
            // Finish work and trigger delegates
            ActiveTask.FinalizeTask();
            ActiveTask.TriggerDelegates();
            ActiveTask = null;
        }
    }
}
```

Enqueuing (`QueueAsyncTask`) can also be called from a different thread (e.g., a UDP `BeginReceive` callback), so it's protected with `QueueLock`. There's also a guard that skips the enqueue entirely in development builds using a test Steam ID, to avoid polluting stats:

```csharp
// SteamAsyncTaskManager.cs — guards enqueuing from other threads with a lock
public static void QueueAsyncTask(SteamAsyncTask Task)
{
#if !DEDICATED_SERVER && DEVELOPMENT_BUILD
    // custom steamid doesn't update steam stats
    if (SteamUser.GetSteamTestId())
    {
        return;
    }
#endif
    lock (QueueLock)
    {
        TaskQueue.Enqueue(Task);
    }
}
```

**③ Common Timeout and Lifecycle Management**
The `SteamAsyncTask` base class provides `IsTimeOut()`, `ResetTimeOut()`, and `SetTimeOut()` in common. A default 2-second timeout is applied uniformly to all tasks so the system doesn't stall if a Steam server response is delayed or a P2P connection drops, while longer-running operations like a Workshop upload can individually disable it via `SetTimeOut(false)`. This structurally prevents the defect of "waiting forever because timeout handling was forgotten."

```csharp
// SteamAsyncTask.cs — common timeout logic inherited by every task (defaults to 2 seconds)
static float REQUEST_TIMEOUT = 2f;

// when disable timeout, set false
virtual public void SetTimeOut(bool timeout)
{
    checkTime = timeout;
}

virtual public void ResetTimeOut()
{
    resetTimeOut = true;
}

virtual public bool IsTimeOut()
{
    if (!checkTime)
    {
        return false;
    }

    if (resetTimeOut)
    {
        reqTime = Time.realtimeSinceStartup;
        resetTimeOut = false;
    }

    // reset timeout
    if (Time.realtimeSinceStartup - reqTime > REQUEST_TIMEOUT)
    {
        WasSuccessful = false;
        IsTaskDone = true;
        return true;
    }
    else
    {
        return false;
    }
}
```



**④ Minimizing Coupling via Delegate-Based Result Notification**
Every task follows the same lifecycle — `Tick → FinalizeTask → TriggerDelegates` — and results are always reported back to the caller as a delegate carrying `(success flag, result data)`. The calling code doesn't need to know anything about the task's internal polling logic; it can complete the asynchronous flow just by "loading a request into the queue and registering a callback." In other words, this acts as an adapter that completely hides Steamworks' low-level callback API from the game logic.

**⑤ Defensive Design Against Edge Cases**
Regardless of whether Steamworks has been initialized, there are cases during scene transitions or shutdown where the GC cleans up Steamworks objects first, which can cause `IsAPICallCompleted()` to throw an `InvalidOperationException`. This is consistently caught inside each task's `Tick()` and safely terminated via `WasSuccessful = false; IsTaskDone = true;` (as seen in `SteamAsyncSetServerStats`, `SteamAsyncCreateItem`, etc.).

```csharp
// SteamAsyncSetServerStats.cs — guards against the GC having cleaned up Steamworks first
bool FailedCall = false;
try
{
    IsTaskDone = SteamGameServerUtils.IsAPICallCompleted(CallbackHandle, out FailedCall);
}
catch (InvalidOperationException ex)
{
    // GC destroyed SteamWorks
    WasSuccessful = false;
    IsTaskDone = true;
    return;
}
```

```csharp
// SteamAsyncCreateItem.cs — wraps the entire Tick() in a try-catch to guard against Steamworks not being initialized
public override void Tick()
{
    try
    {
        if (!Initialized)
        {
            Initialized = true;
            CallbackHandle = SteamUGC.CreateItem(ConsumerAppId, FileType);
        }
        // ... polling logic ...
    }
    catch (System.InvalidOperationException ex)
    {
        // Steamworks is not initialized
        IsTaskDone = true;
        WasSuccessful = false;
    }
}
```



### 1.3 Summary of Benefits Gained From This Structure

- **Consistency**: Even as features like Stats, Leaderboard, and Workshop expand, simply inheriting from `SteamAsyncTask` reuses queuing, timeout, and error handling without any separate implementation
- **Safety**: Every completion callback fires on the timing of Unity's main-thread `Update()`, so downstream processing such as UI updates never runs into threading issues
- **Testability**: Since each task is an independent class, logic can be verified at the `Tick()`/`FinalizeTask()` level even without a Steam client
- **Extensibility**: Adding a new Steam feature only requires adding a new `SteamAsyncXXX` class, with no need to modify the existing manager/queue code

---

## 2. Architecture Overview

### 2.1 Core Class Structure

```
SteamAsyncTask (abstract base)
 ├─ IsTaskDone / WasSuccessful / Initialized (common state)
 ├─ IsTimeOut() / ResetTimeOut() / SetTimeOut()  (common timeout)
 └─ virtual Tick() / FinalizeTask() / TriggerDelegates()  (lifecycle hooks)
       │
       ├─ SteamAsyncSetStats / SteamAsyncGetStats               (client Stats)
       ├─ SteamAsyncSetServerStats / SteamAsyncGetServerStats   (game-server Stats)
       ├─ SteamAsyncGetLeaderBoard                              (leaderboard lookup/creation)
       ├─ SteamAsyncGetLeaderBoardEntries                       (leaderboard entry download)
       ├─ SteamAsyncSetLeaderBoard                               (score upload)
       ├─ SteamAsyncGetServerList / SteamAsyncGetLANServerList  (internet/LAN server browser)
       ├─ SteamAsyncGetServerInfo / GetPlayers / GetRawServerRule (A2S UDP query)
       ├─ SteamAsyncCreateItem / SubmitItemUpdate                (Workshop item creation/update)
       └─ SteamAsyncGetUGCDetails / SendQueryUGCRequest          (Workshop item lookup)

SteamAsyncTaskManager : MonoBehaviour (Singleton)
 └─ Consumes the Queue<SteamAsyncTask> one item per frame
```

`SteamAsyncTaskManager` is the single globally-existing singleton `MonoBehaviour` in the game, kept alive across scene transitions via `DontDestroyOnLoad`. Access to the queue (`TaskQueue`) and access to the currently running task (`ActiveTask`) each use their own separate lock object (`QueueLock`, `TaskLock`), so that safety is guaranteed even if a task is enqueued from a different thread (e.g., a UDP callback).

The `SteamAsyncTask` base class itself is a thin contract that exposes just three state fields and three lifecycle hooks — subclasses override only the hooks they need, and the default `Tick()` does no polling at all and completes immediately:

```csharp
// SteamAsyncTask.cs — the common base inherited by every SteamAsyncXXX task
public class SteamAsyncTask
{
    public bool IsTaskDone { get; protected set; }
    public bool WasSuccessful { get; protected set; }
    protected bool Initialized;

    public SteamAsyncTask()
    {
        IsTaskDone = false;
        WasSuccessful = false;
        Initialized = false;
        resetTimeOut = true;
        checkTime = true;
    }

    virtual public void Tick()
    {
        IsTaskDone = true;
    }

    virtual public void FinalizeTask()
    {
    }

    virtual public void TriggerDelegates()
    {
    }
}
```

The singleton part of `SteamAsyncTaskManager` prevents duplicate instances by destroying itself if `s_instance` already exists, and uses `DontDestroyOnLoad` to survive scene transitions:

```csharp
// SteamAsyncTaskManager.cs — singleton initialization that survives scene transitions
private void Awake()
{
    // Only one instance of SteamAsyncTaskManager at a time!
    if (s_instance != null)
    {
        Destroy(gameObject);
        return;
    }
    s_instance = this;

    IsInitialized = true;
    // We want our SteamAsyncTaskManager Instance to persist across scenes.
    DontDestroyOnLoad(gameObject);
}
```



### 2.2 Task Execution Lifecycle

```
1. QueueAsyncTask(task)         : inserted into the queue
2. Promoted to ActiveTask inside Update()
3. ActiveTask.Tick() every frame  : fires the initial request (once) + polls for completion
4. Once IsTaskDone == true or IsTimeOut() == true
5. FinalizeTask()                : post-processes the result data (e.g., actually applying a stat, issuing a save request)
6. TriggerDelegates()            : notifies the caller of the result via the registered delegate
7. ActiveTask = null             : moves on to the next task
```

Each subclass selectively overrides only the hooks it needs. For example, `SteamAsyncGetLeaderBoard` leaves `FinalizeTask` empty since it requires no additional processing, whereas `SteamAsyncSetServerStats` applies the actual stat value inside `FinalizeTask` and then calls `StoreUserStats`.

Taking `SteamAsyncGetLeaderBoard` — the simplest case — as an example, you can see exactly how the three hooks `Tick → FinalizeTask → TriggerDelegates` get overridden in practice:

```csharp
// SteamAsyncGetLeaderBoard.cs — Tick fires the initial request and polls, FinalizeTask is left empty, TriggerDelegates reports the result
public override void Tick()
{
    if (!Initialized)
    {
        CallbackHandle = SteamUserStats.FindOrCreateLeaderboard(LeaderboardName,
            ELeaderboardSortMethod.k_ELeaderboardSortMethodDescending,
            ELeaderboardDisplayType.k_ELeaderboardDisplayTypeNumeric);
        Initialized = true;
    }

    if (CallbackHandle != SteamAPICall_t.Invalid)
    {
        bool FailedCall = false;
        IsTaskDone = SteamUtils.IsAPICallCompleted(CallbackHandle, out FailedCall);

        if (IsTaskDone)
        {
            bool FailedResult;
            int SizeCallback = System.Runtime.InteropServices.Marshal.SizeOf(typeof(LeaderboardFindResult_t));
            IntPtr Callback = Marshal.AllocHGlobal(SizeCallback);

            bool SuccessCallResult = SteamUtils.GetAPICallResult(CallbackHandle, Callback, SizeCallback, LeaderboardFindResult_t.k_iCallback, out FailedResult);
            WasSuccessful = SuccessCallResult && !FailedCall && !FailedResult;

            if (WasSuccessful)
            {
                CallbackResults = (LeaderboardFindResult_t)Marshal.PtrToStructure(Callback, typeof(LeaderboardFindResult_t));
                if (0 == CallbackResults.m_bLeaderboardFound)
                {
                    WasSuccessful = false;
                }
            }
        }
    }
}

public override void FinalizeTask()
{
    // left empty — this task needs no post-processing
}

public override void TriggerDelegates()
{
    if (null != OnGetLeaderboard)
    {
        OnGetLeaderboard(this, WasSuccessful, CallbackResults.m_hSteamLeaderboard.m_SteamLeaderboard);
    }
}
```



---

## 3. Feature-by-Feature Implementation

### 3.1 Stats & Achievements

Organized along two axes: client-side (`SteamAsyncGetStats` / `SteamAsyncSetStats`) and game-server-side (`SteamAsyncGetServerStats` / `SteamAsyncSetServerStats`). Because a dedicated server environment must use `SteamGameServerStats` while a regular client must use `SteamUserStats`, the API families themselves differ — so these were split into separate task classes to reduce confusion at the call sites.

- The `StatInfo` class encapsulates a stat's name, value, whether it's an increment (Increase), and display name as a single unit, and a whole `List<StatInfo>` is used for the request/response — allowing multiple stats to be batched into a single task
- When the `Increase` flag is set, the existing value is fetched first and the delta is added on top, supporting "cumulative stats" (kill count, playtime, etc.)
- Explicitly branches to support only the `int`/`float` data types, and logs to the console as a safeguard for any unsupported type

```csharp
// SteamAsyncSetServerStats.cs — when Increase is set, fetches the old value and adds the delta before applying it
if (Info.Value.GetType() == typeof(int))
{
    int IntegerValue = (int)Info.Value;
    if (Info.Increase)
    {
        // get old value
        int Value = 0;
        if (SteamGameServerStats.GetUserStat(SteamID, Info.StatName, out Value))
        {
            // increase count
            IntegerValue += Value;
            Info.Value = IntegerValue;
        }
    }
    // set new stat
    if (!SteamGameServerStats.SetUserStat(SteamID, Info.StatName, IntegerValue))
    {
        //ConsoleDebug.AddText(String.Format("<color=red><b>Failed to set {0} stats</b></color>", Info.StatName));
    }
}
else if (Info.Value.GetType() == typeof(float))
{
    // ... same pattern repeated for float ...
}
else
{
    ConsoleDebug.AddText(String.Format("<color=red><b>SteamAsyncSetServerStats unsupported datatype {0} {1}</b></color>", Info.StatName, typeof(float)));
}
```

`FinalizeTask()` follows through by actually committing the applied values to Steam's servers:

```csharp
// SteamAsyncSetServerStats.cs — applies the values (UpdateStatValue) then commits them via StoreUserStats
public override void FinalizeTask()
{
    if (WasSuccessful)
    {
        WasSuccessful = UpdateStatValue();

        // reqeust to store stat to steam server
        CallbackHandle = SteamGameServerStats.StoreUserStats(SteamID);
    }
}
```


### 3.2 Leaderboard

Made up of three stages — `SteamAsyncGetLeaderBoard` (leaderboard handle lookup/creation) → `SteamAsyncGetLeaderBoardEntries` (rankings download) → `SteamAsyncSetLeaderBoard` (score upload) — each an independent task, so only the needed combination can be selectively queued and used.

- Leaderboards are always created with descending sort order (`k_ELeaderboardSortMethodDescending`) and numeric display (`k_ELeaderboardDisplayTypeNumeric`)
- Downloaded entries are merged into a `Dictionary<ulong, LeaderboardDetail>` keyed by SteamID, implemented as an upsert — updating existing users and adding new ones — so results accumulate correctly even when multiple ranges (RangeStart~RangeEnd) are requested sequentially

```csharp
// SteamAsyncGetLeaderBoardEntries.cs — upserts by SteamID so results from multiple range requests accumulate into one Dictionary
for (int EntryIdx = 0; EntryIdx < CallbackResults.m_cEntryCount; ++EntryIdx)
{
    LeaderboardEntry_t LeaderboardEntry;
    if (SteamUserStats.GetDownloadedLeaderboardEntry(CallbackResults.m_hSteamLeaderboardEntries, EntryIdx, out LeaderboardEntry, null, 0))
    {
        if (LeaderboardDictionary.ContainsKey(LeaderboardEntry.m_steamIDUser.m_SteamID))
        {
            LeaderboardDictionary[LeaderboardEntry.m_steamIDUser.m_SteamID].TotalScore = LeaderboardEntry.m_nScore;
            LeaderboardDictionary[LeaderboardEntry.m_steamIDUser.m_SteamID].Rank = LeaderboardEntry.m_nGlobalRank;
            LeaderboardDictionary[LeaderboardEntry.m_steamIDUser.m_SteamID].UGCHandle = LeaderboardEntry.m_hUGC.m_UGCHandle;
        }
        else
        {
            LeaderboardDetail Detail = new LeaderboardDetail();
            Detail.SteamId = LeaderboardEntry.m_steamIDUser.m_SteamID;
            Detail.TotalScore = LeaderboardEntry.m_nScore;
            Detail.Rank = LeaderboardEntry.m_nGlobalRank;
            Detail.UGCHandle = LeaderboardEntry.m_hUGC.m_UGCHandle;

            LeaderboardDictionary.Add(Detail.SteamId, Detail);
        }
    }
}
```


### 3.3 Matchmaking / Server Browser

Supports the internet server list (`SteamAsyncGetServerList`, `ServerListManager` + `SteamLobby`) and the LAN server list (`SteamAsyncGetLANServerList`) separately.

- The `ISteamMatchmakingServerListResponse` callback fires individually each time a single server responds (`OnServerResponded`), and `OnRefreshComplete` is fired separately once the entire query finishes. This "multiple partial responses + one completion signal" pattern was naturally integrated into `SteamAsyncTask`'s lifecycle by setting `IsTaskDone` to true only in the completion callback, while accumulating each individual response into a list beforehand
- Filters (`MatchMakingKeyValuePair_t[]`) can narrow the server list by game directory, whether the server is secure, whether players are present, and so on, and app ID verification (`m_nAppID`) filters out servers belonging to other games
- Favorites (`AddToFavorites`/`RemoveToFavorites`) directly use Steam's `SteamMatchmaking` favorites API, so they're tied to the Steam account itself rather than requiring any local client-side storage
- `SteamAsyncGetServerInfo`/`GetPlayers`/`GetRawServerRule` are implemented directly over a plain UDP socket using the [Source Engine Server Query Protocol (A2S)](https://developer.valvesoftware.com/wiki/Server_queries) rather than the Steamworks API — the two-stage handshake of challenge-number request → response parsing is each handled via asynchronous `BeginReceive`/`EndReceive` callbacks, and exposed through the same `SteamAsyncTask` interface

```csharp
// SteamAsyncGetServerList.cs — individual server responses just accumulate; IsTaskDone is only ever set true in OnRefreshComplete
private void OnServerResponded(HServerListRequest hRequest, int iServer)
{
    base.ResetTimeOut();

    ++respondedServerCount;
    gameserveritem_t pServer = SteamMatchmakingServers.GetServerDetails(hRequest, iServer);

    if (pServer != null)
    {
        if (pServer.m_nAppID == Steamworks.AppId_t.myowngame.m_AppId)
        {
            ServerList.Add(pServer);
        }
    }
}

private void OnRefreshComplete(HServerListRequest hRequest, EMatchMakingServerResponse response)
{
    if (respondedServerCount > 0)
    {
        WasSuccessful = true;
    }
    IsTaskDone = true;
    ReleaseRequest();
}
```

```csharp
// SteamAsyncGetPlayers.cs — A2S_PLAYER: a two-stage handshake that first fetches a challenge number, then re-sends it verbatim
private void responseChallengeNumber(IAsyncResult res)
{
    byte[] received = udpClient.EndReceive(res, ref receivedFrom);
    if (received.Length == chellengePacketSize && received[0] == 0xff && received[1] == 0xff
        && received[2] == 0xff && received[3] == 0xff && received[4] == 0x41)
    {
        // copy challenge number
        byte[] requestRules = { 0xff, 0xff, 0xff, 0xff, 0x55, 0x00, 0x00, 0x00, 0x00 };
        requestRules[5] = received[5];
        requestRules[6] = received[6];
        requestRules[7] = received[7];
        requestRules[8] = received[8];

        int sentSize = udpClient.Send(requestRules, received.Length, receivedFrom);
        if (sentSize > 0)
        {
            udpClient.BeginReceive(new AsyncCallback(recvPlayers), null);
        }
        // ...
    }
}
```


### 3.4 P2P Networking (UNET Integration)

The part that ports Unity's legacy networking (UNET) to run on top of Steam P2P. An adapter pattern was applied to replace UNET's low-level transport layer (`TransportSend`/`TransportReceive`) with Steam P2P packet send/receive.

- `SteamNetworkConnection` inherits from UNET's `NetworkConnection` and overrides `TransportSend()` to delegate the actual transmission to `SteamNetworking.SendP2PPacket()`. It maps the `EP2PSend` type based on the QoS channel setting (Reliable/Unreliable, etc.), converting UNET's channel concept into Steam P2P's transmission mode

  ```csharp
  // SteamNetworkConnection.cs — maps a UNET channel's QoS setting to the corresponding Steam P2P send type
  public override bool TransportSend(byte[] bytes, int numBytes, int channelId, out byte error)
  {
      if (!SteamNetworkManager.IsUsingP2P || !steamId.IsValid())
      {
          return base.TransportSend(bytes, numBytes, channelId, out error);
      }

      if (steamId.m_SteamID == SteamUser.GetSteamID().m_SteamID)
      {
          // sending to self. short circuit
          TransportReceive(bytes, numBytes, channelId);
          error = 0;
          return true;
      }

      EP2PSend eP2PSendType = EP2PSend.k_EP2PSendReliable;

      QosType qos = SteamNetworkManager.hostTopology.DefaultConfig.Channels[channelId].QOS;
      if (qos == QosType.Unreliable || qos == QosType.UnreliableFragmented || qos == QosType.UnreliableSequenced || qos == QosType.StateUpdate)
      {
          eP2PSendType = EP2PSend.k_EP2PSendUnreliable;
      }

      // Send packet to peer through Steam
      bool result = SteamNetworking.SendP2PPacket(steamId, bytes, (uint)numBytes, eP2PSendType, channelId);
      if (!result)
      {
          // ignore unreliable packets
          if (eP2PSendType == EP2PSend.k_EP2PSendUnreliable)
          {
              error = 0;
              return true;
          }

          // disconnect p2p client
          UNETServerController.Instance.RemoveConnection(steamId);
          error = 1;
          return false;
      }

      error = 0;
      return true;
  }
  ```

- `SteamNetworkManager.Update()` polls incoming packets on every channel every frame (`IsP2PPacketAvailable`) and forwards them to the corresponding connection's `TransportReceive()` — a polling approach was chosen over Steam callbacks so it would naturally mesh with UNET's frame-based processing flow

  ```csharp
  // SteamNetworkManager.cs — polls every channel each frame and forwards packets into UNET's TransportReceive
  for (int chan = 0; chan < channels; chan++)
  {
      while (SteamNetworking.IsP2PPacketAvailable(out packetSize, chan))
      {
          if (_bufferSize < packetSize)
          {
              _buffer = new byte[packetSize];
          }

          CSteamID senderId;

          if (SteamNetworking.ReadP2PPacket(_buffer, packetSize, out packetSize, out senderId, chan))
          {
              ++recvCount;
              NetworkConnection conn;

              if (UNETServerController.IsHostingServer())
              {
                  // We are the server, one of our clients will handle this packet
                  conn = UNETServerController.GetClient(senderId);
              }
              else
              {
                  // We are a client, we only have one connection (the server).
                  conn = myClient == null ? null : myClient.connection;
              }

              if (conn != null && packetSize > 0)
              {
                  // Handle Steam packet through UNET
                  conn.TransportReceive(_buffer, Convert.ToInt32(packetSize), chan);
                  // avoid freezing while receiving massive packets
                  if (recvCount > 512)
                  {
                      break;
                  }
              }
          }
      }
  }
  ```

- `UNETServerController` detects new connection requests via the `P2PSessionRequest_t` callback (`Callback<T>.Create`), and upon acceptance creates a new `SteamNetworkConnection` and registers it as an external UNET server connection (`NetworkServer.AddExternalConnection`). This makes peers who connected via Steam friend invites or matchmaking behave identically to a regular UNET client

  ```csharp
  // UNETServerController.cs — accepts the P2P session request, then registers it as a UNET external connection
  void OnP2PSessionRequested(P2PSessionRequest_t pCallback)
  {
      var member = pCallback.m_steamIDRemote;

      if (NetworkServer.active)
      {
          // Accept the connection if this user is in the lobby
          SteamNetworking.AcceptP2PSessionWithUser(member);

          CreateP2PConnectionWithPeer(member);
      }
  }

  public void CreateP2PConnectionWithPeer(CSteamID peer)
  {
      SteamNetworking.SendP2PPacket(peer, null, 0, EP2PSend.k_EP2PSendReliable);

      if (GetClient(peer) == null)
      {
          // create new connnection for this client and connect them to server
          var newConn = new SteamNetworkConnection(peer);
          newConn.ForceInitialize();

          NetworkServer.AddExternalConnection(newConn);
          AddConnection(newConn);
      }
  }
  ```

  On the client side, `SteamNetworkClient.Connect()` completes the P2P connection by "tricking" UNET — forcing its `ConnectState` straight to `Connected` instead of going through UNET's normal handshake:

  ```csharp
  // SteamNetworkClient.cs — skips UNET's normal connection procedure and forces the state to Connected
  public void Connect()
  {
      // Connect to localhost and trick UNET by setting ConnectState state to "Connected",
      // which triggers some initialization and allows data to pass through TransportSend
      m_AsyncConnect = ConnectState.Connected;

      // send Connected message
      connection.InvokeHandlerNoData(MsgType.Connect);
  }
  ```

- Connection validity is periodically verified via `GetP2PSessionState()` (`ValidateSteamConnection`), preventing the problem of a zombie connection lingering on the UNET side even after the other party has already ended the connection

  ```csharp
  // UNETServerController.cs — cleans up a zombie connection where the P2P session has already ended but UNET still holds it
  public bool ValidateSteamConnection(CSteamID steamId)
  {
      if (GameManager.IsServer && GameManager.GameState == GameState.Running)
      {
          P2PSessionState_t ConnectionState;
          if (!SteamNetworking.GetP2PSessionState(steamId, out ConnectionState))
          {
              RemoveConnection(steamId);
              return false;
          }
          // disconnected from host
          if (ConnectionState.m_bConnectionActive == 0 && ConnectionState.m_bConnecting == 0)
          {
              RemoveConnection(steamId);
              return false;
          }
      }

      return true;
  }
  ```

- On a clean disconnect, the handling differs by role. The server closes the session immediately via `Reset()`, while the client waits until every packet still queued for sending has actually gone out (`FlushLocalP2PPacket`) before closing the session, so the final message isn't lost.

  ```csharp
  // SteamNetworkConnection.cs — the server closes immediately; the client sends a disconnect message and waits for its send queue to flush
  public void CloseP2PSession()
  {
      if (GameManager.IsServer)
      {
          Reset();
      }
      else
      {
          var msg = new DisconnectP2PMessage()
          {
              SteamId = SteamUser.GetSteamID().m_SteamID
          };

          SendByChannel(CustomMsgs.RequestDisconnectP2P, msg, CustomChannels.ReliableSequenced);
          SteamNetworkManager.Instance.FlushLocalP2PPacket(this);
      }
  }
  ```

  ```csharp
  // SteamNetworkManager.cs — waits frame-by-frame until the send queue is empty before closing the session
  IEnumerator Flush(SteamNetworkConnection steamConnection)
  {
      bool Success = false;
      P2PSessionState_t ConnectionState;
      do
      {
          yield return Yielders.EndOfFrame;
          Success = SteamNetworking.GetP2PSessionState(steamConnection.steamId, out ConnectionState);
      } while (Success && ConnectionState.m_bConnectionActive == 1 && ConnectionState.m_nPacketsQueuedForSend > 0);

      steamConnection.Reset();
  }
  ```



### 3.5 Workshop (UGC)

Supports the Workshop item publishing flow — `SteamAsyncCreateItem` → file upload (handled externally) → `SteamAsyncSubmitItemUpdate` — as well as a flow for looking up existing items via `SteamAsyncSendQueryUGCRequest`/`SteamAsyncGetUGCDetails`. Since an upload can take a considerable amount of time to complete, both tasks disable the default 2-second timeout via `SetTimeOut(false)`, which sets them apart from the other tasks.

```csharp
// SteamAsyncSubmitItemUpdate.cs — disables the default timeout right in the constructor
public SteamAsyncSubmitItemUpdate(UGCUpdateHandle_t ugcUpdateHandle, WorkShopItemDetail itemDetail, string changeNote)
{
    UGCUpdateHandle = ugcUpdateHandle;
    ItemDetail = itemDetail;
    ChangeNote = changeNote;

    // disable timeout
    base.SetTimeOut(false);
}
```

`SteamAsyncCreateItem`'s `FinalizeTask()` writes the newly created item's `PublishedFileId_t` back into the caller-supplied `WorkShopItemDetail`, so the flow can carry straight through into the `SubmitItemUpdate` stage:

```csharp
// SteamAsyncCreateItem.cs — writes the creation result back into ItemDetail so the next stage (SubmitItemUpdate) can use it
public override void FinalizeTask()
{
    if (WasSuccessful)
    {
        ItemDetail.PublishedField = CallbackResults.m_nPublishedFileId;
    }
}
```


### 3.6 Avatar / Friends

`SteamUtil.GetSteamAvatar()` doesn't use the asynchronous task framework described above, and instead is handled immediately via synchronous APIs (`SteamFriends.GetSmallFriendAvatar`/`GetMediumFriendAvatar` + `ISteamUtils.GetImageRGBA`). This is because avatar images are already cached locally by the Steam client, so no separate polling is needed; to account for the fact that Steam returns images flipped vertically, the image is corrected with `FlipTexture()` before being returned as a `Texture2D`.

```csharp
// SteamUtil.cs — fetches the avatar image synchronously and corrects for the vertical flip
static public Texture2D GetSteamAvatar(CSteamID SteamID, bool SmallerAvatar)
{
    int FriendAvatar = SmallerAvatar
        ? SteamFriends.GetSmallFriendAvatar(SteamID)
        : SteamFriends.GetMediumFriendAvatar(SteamID);

    uint ImageWidth, ImageHeight;
    bool success = SteamUtils.GetImageSize(FriendAvatar, out ImageWidth, out ImageHeight);

    if (success && ImageWidth > 0 && ImageHeight > 0)
    {
        byte[] Image = new byte[ImageWidth * ImageHeight * 4];
        Texture2D returnTexture = new Texture2D((int)ImageWidth, (int)ImageHeight, TextureFormat.RGBA32, false, false);
        success = SteamUtils.GetImageRGBA(FriendAvatar, Image, (int)(ImageWidth * ImageHeight * 4));
        if (success)
        {
            returnTexture.LoadRawTextureData(Image);
            returnTexture.Apply();
            //NOTE: texture loads upside down, so we flip it to normal...
            returnTexture = FlipTexture(returnTexture);
        }
        return returnTexture;
    }
    return null;
}
```


---

## 4. Usage Example

```csharp
// Requesting stats
var task = new SteamAsyncGetStats(SteamUser.GetSteamID(), statDictionary);
task.OnGetStat += (parent, success, steamId, stats) =>
{
    if (success)
    {
        // Follow-up processing such as updating the UI
    }
};
SteamAsyncTaskManager.QueueAsyncTask(task);
```

The caller only needs to create a task, load it into the queue, and register a delegate — polling, timeout handling, and thread safety are all handled entirely by the framework.

---

## 5. Tech Stack

- **Engine**: Unity (UNET legacy networking)
- **Steam SDK**: Steamworks.NET
- **Language**: C#
- **Networking**: Steam P2P Packet Relay, Source Engine A2S UDP Query Protocol
