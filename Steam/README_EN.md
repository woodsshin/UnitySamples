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

**③ Common Timeout and Lifecycle Management**
The `SteamAsyncTask` base class provides `IsTimeOut()`, `ResetTimeOut()`, and `SetTimeOut()` in common. A default 2-second timeout is applied uniformly to all tasks so the system doesn't stall if a Steam server response is delayed or a P2P connection drops, while longer-running operations like a Workshop upload can individually disable it via `SetTimeOut(false)`. This structurally prevents the defect of "waiting forever because timeout handling was forgotten."

**④ Minimizing Coupling via Delegate-Based Result Notification**
Every task follows the same lifecycle — `Tick → FinalizeTask → TriggerDelegates` — and results are always reported back to the caller as a delegate carrying `(success flag, result data)`. The calling code doesn't need to know anything about the task's internal polling logic; it can complete the asynchronous flow just by "loading a request into the queue and registering a callback." In other words, this acts as an adapter that completely hides Steamworks' low-level callback API from the game logic.

**⑤ Defensive Design Against Edge Cases**
Regardless of whether Steamworks has been initialized, there are cases during scene transitions or shutdown where the GC cleans up Steamworks objects first, which can cause `IsAPICallCompleted()` to throw an `InvalidOperationException`. This is consistently caught inside each task's `Tick()` and safely terminated via `WasSuccessful = false; IsTaskDone = true;` (as seen in `SteamAsyncSetServerStats`, `SteamAsyncCreateItem`, etc.).

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

---

## 3. Feature-by-Feature Implementation

### 3.1 Stats & Achievements

Organized along two axes: client-side (`SteamAsyncGetStats` / `SteamAsyncSetStats`) and game-server-side (`SteamAsyncGetServerStats` / `SteamAsyncSetServerStats`). Because a dedicated server environment must use `SteamGameServerStats` while a regular client must use `SteamUserStats`, the API families themselves differ — so these were split into separate task classes to reduce confusion at the call sites.

- The `StatInfo` class encapsulates a stat's name, value, whether it's an increment (Increase), and display name as a single unit, and a whole `List<StatInfo>` is used for the request/response — allowing multiple stats to be batched into a single task
- When the `Increase` flag is set, the existing value is fetched first and the delta is added on top, supporting "cumulative stats" (kill count, playtime, etc.)
- Explicitly branches to support only the `int`/`float` data types, and logs to the console as a safeguard for any unsupported type

### 3.2 Leaderboard

Made up of three stages — `SteamAsyncGetLeaderBoard` (leaderboard handle lookup/creation) → `SteamAsyncGetLeaderBoardEntries` (rankings download) → `SteamAsyncSetLeaderBoard` (score upload) — each an independent task, so only the needed combination can be selectively queued and used.

- Leaderboards are always created with descending sort order (`k_ELeaderboardSortMethodDescending`) and numeric display (`k_ELeaderboardDisplayTypeNumeric`)
- Downloaded entries are merged into a `Dictionary<ulong, LeaderboardDetail>` keyed by SteamID, implemented as an upsert — updating existing users and adding new ones — so results accumulate correctly even when multiple ranges (RangeStart~RangeEnd) are requested sequentially

### 3.3 Matchmaking / Server Browser

Supports the internet server list (`SteamAsyncGetServerList`, `ServerListManager` + `SteamLobby`) and the LAN server list (`SteamAsyncGetLANServerList`) separately.

- The `ISteamMatchmakingServerListResponse` callback fires individually each time a single server responds (`OnServerResponded`), and `OnRefreshComplete` is fired separately once the entire query finishes. This "multiple partial responses + one completion signal" pattern was naturally integrated into `SteamAsyncTask`'s lifecycle by setting `IsTaskDone` to true only in the completion callback, while accumulating each individual response into a list beforehand
- Filters (`MatchMakingKeyValuePair_t[]`) can narrow the server list by game directory, whether the server is secure, whether players are present, and so on, and app ID verification (`m_nAppID`) filters out servers belonging to other games
- Favorites (`AddToFavorites`/`RemoveToFavorites`) directly use Steam's `SteamMatchmaking` favorites API, so they're tied to the Steam account itself rather than requiring any local client-side storage
- `SteamAsyncGetServerInfo`/`GetPlayers`/`GetRawServerRule` are implemented directly over a plain UDP socket using the [Source Engine Server Query Protocol (A2S)](https://developer.valvesoftware.com/wiki/Server_queries) rather than the Steamworks API — the two-stage handshake of challenge-number request → response parsing is each handled via asynchronous `BeginReceive`/`EndReceive` callbacks, and exposed through the same `SteamAsyncTask` interface

### 3.4 P2P Networking (UNET Integration)

The part that ports Unity's legacy networking (UNET) to run on top of Steam P2P. An adapter pattern was applied to replace UNET's low-level transport layer (`TransportSend`/`TransportReceive`) with Steam P2P packet send/receive.

- `SteamNetworkConnection` inherits from UNET's `NetworkConnection` and overrides `TransportSend()` to delegate the actual transmission to `SteamNetworking.SendP2PPacket()`. It maps the `EP2PSend` type based on the QoS channel setting (Reliable/Unreliable, etc.), converting UNET's channel concept into Steam P2P's transmission mode
- `SteamNetworkManager.Update()` polls incoming packets on every channel every frame (`IsP2PPacketAvailable`) and forwards them to the corresponding connection's `TransportReceive()` — a polling approach was chosen over Steam callbacks so it would naturally mesh with UNET's frame-based processing flow
- `UNETServerController` detects new connection requests via the `P2PSessionRequest_t` callback (`Callback<T>.Create`), and upon acceptance creates a new `SteamNetworkConnection` and registers it as an external UNET server connection (`NetworkServer.AddExternalConnection`). This makes peers who connected via Steam friend invites or matchmaking behave identically to a regular UNET client
- Connection validity is periodically verified via `GetP2PSessionState()` (`ValidateSteamConnection`), preventing the problem of a zombie connection lingering on the UNET side even after the other party has already ended the connection

### 3.5 Workshop (UGC)

Supports the Workshop item publishing flow — `SteamAsyncCreateItem` → file upload (handled externally) → `SteamAsyncSubmitItemUpdate` — as well as a flow for looking up existing items via `SteamAsyncSendQueryUGCRequest`/`SteamAsyncGetUGCDetails`. Since an upload can take a considerable amount of time to complete, both tasks disable the default 2-second timeout via `SetTimeOut(false)`, which sets them apart from the other tasks.

### 3.6 Avatar / Friends

`SteamUtil.GetSteamAvatar()` doesn't use the asynchronous task framework described above, and instead is handled immediately via synchronous APIs (`SteamFriends.GetSmallFriendAvatar`/`GetMediumFriendAvatar` + `ISteamUtils.GetImageRGBA`). This is because avatar images are already cached locally by the Steam client, so no separate polling is needed; to account for the fact that Steam returns images flipped vertically, the image is corrected with `FlipTexture()` before being returned as a `Texture2D`.

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
