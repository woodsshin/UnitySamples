# Steamworks Integration Module

Unity(UNET) 기반 게임에 Steamworks SDK(Steamworks.NET)를 통합한 모듈입니다. **Stats/Achievements, Leaderboard, Matchmaking/Server Browser, Workshop(UGC), P2P Networking, Friends/Avatar** 등 Steamworks의 주요 기능을 지원하며, 모든 비동기 Steam API 호출을 단일한 공통 프레임워크로 추상화하여 일관된 방식으로 처리합니다.

---

## 1. 왜 비동기 태스크 프레임워크가 필요했는가

### 1.1 문제 상황

Steamworks API는 대부분 즉시 결과를 반환하지 않고, `SteamAPICall_t` 핸들을 반환한 뒤 **다음 프레임 이후 어느 시점에** `ISteamUtils.GetAPICallResult()` 또는 콜백을 통해 결과를 확인해야 하는 구조입니다. 여기에 더해 이 프로젝트는 성격이 서로 다른 세 종류의 비동기 작업을 동시에 다뤄야 했습니다.

| 유형 | 예시 | 특징 |
|---|---|---|
| Steam API 콜/콜백 | Stats, Leaderboard, Workshop(UGC) | `SteamAPICall_t` 폴링 또는 `Callback<T>` 이벤트 |
| Steam Matchmaking 인터페이스 콜백 | 서버 브라우저, LAN 검색 | `ISteamMatchmakingServerListResponse` 등 델리게이트 기반, 완료 시점이 개별 서버 응답 수만큼 여러 차례 발생 |
| Raw UDP 소켓 통신 | A2S_INFO, A2S_PLAYER, A2S_RULES 쿼리 | Steamworks가 아닌 순수 소켓 I/O, `BeginReceive`/`EndReceive` 콜백 |

이 세 유형은 완료 판정 방식이 완전히 상이함에도 불구하고, **호출부(UI, 게임 로직) 입장에서는 동일한 방식으로 다룰 수 있어야** 유지보수가 가능합니다. 기능마다 폴링·콜백 처리 로직을 개별적으로 작성하면 다음과 같은 문제가 발생합니다.

- 코드마다 타임아웃, 실패 처리, 델리게이트 호출 패턴이 제각각으로 분산됨
- Steam API가 초기화되지 않았거나 GC에 의해 정리된 시점에 호출되면 `InvalidOperationException`이 발생하는데, 이를 개별 호출부마다 각각 방어해야 함
- 다수의 비동기 요청이 동시에 실행될 경우, Steamworks 콜백 순서와 게임 스레드(Unity 메인 스레드) 간의 경쟁 상태(race condition)가 발생할 수 있음

### 1.2 설계 방향: Command 패턴 + 단일 스레드 직렬 실행 큐

이 문제를 해결하기 위해 모든 비동기 Steam 작업을 **`SteamAsyncTask`라는 공통 추상 클래스로 캡슐화**하고, `SteamAsyncTaskManager`가 이를 **큐에 적재하여 한 번에 하나씩 직렬로 `Update()`에서 처리**하는 구조를 채택했습니다.

```
[호출부] → new SteamAsyncXXX(...) → QueueAsyncTask() → Queue<SteamAsyncTask>
                                                              │
                                     SteamAsyncTaskManager.Update() (매 프레임)
                                                              │
                              ActiveTask.Tick() → IsTaskDone / IsTimeOut 체크
                                                              │
                         FinalizeTask() → TriggerDelegates() → 다음 태스크로 교체
```

이 구조를 채택한 이유는 다음과 같습니다.

**① Unity 메인 스레드 안전성**
Steamworks.NET의 콜백은 Steam 클라이언트 프로세스와의 통신 결과를 수신하여 처리하는데, `Tick()` 내부에서 `ISteamUtils.IsAPICallCompleted()`로 폴링하는 방식을 채택하면 별도의 스레드 동기화 코드 없이도 Unity의 `Update()` 루프 안에서 안전하게 처리할 수 있습니다. 소켓 콜백(`BeginReceive`)처럼 실제로 다른 스레드에서 실행되는 경우도 있지만, 이 경우에도 태스크 내부 상태(`IsTaskDone`, `WasSuccessful`)만 갱신하고 실제 후처리(`FinalizeTask`, `TriggerDelegates`)는 항상 매니저의 `Update()`에서 실행되도록 하여, 델리게이트를 수신하는 쪽(주로 UI)이 항상 메인 스레드 컨텍스트에서 안전하게 동작하도록 보장했습니다.

**② 단일 처리 방식의 직렬 큐**
`ActiveTask`가 `null`일 때에만 큐에서 다음 태스크를 꺼내 실행하는 구조로, Steam 통계·리더보드 API가 사실상 하나의 요청이 완료되어야 다음 요청을 보장된 순서로 처리할 수 있다는 Steamworks의 특성(예: `RequestUserStats` 완료 후 `SetUserStat`/`GetUserStat` 호출 필요)에 맞춰, **요청 순서와 완료 순서를 일치**시켰습니다. 이를 통해 "통계를 아직 수신하지 못한 상태에서 통계 증가를 시도"하는 등의 순서 오류를 구조적으로 차단할 수 있습니다.

`SteamAsyncTaskManager`의 `Update()`가 바로 이 다이어그램을 그대로 코드로 옮긴 부분으로, `ActiveTask`가 비어 있을 때만 큐에서 다음 태스크를 꺼내고, 완료(`IsTaskDone`) 또는 타임아웃(`IsTimeOut()`) 시점에만 `FinalizeTask → TriggerDelegates`를 호출한 뒤 `ActiveTask`를 비웁니다:

```csharp
// SteamAsyncTaskManager.cs — 매 프레임 하나의 태스크만 직렬로 처리하는 핵심 루프
private void Update()
{
    if (!IsInitialized)
    {
        return;
    }

    // ActiveTask는 Update()(메인 스레드)에서만 읽고 쓰므로 별도 락이 필요 없습니다.
    // TaskQueue는 QueueAsyncTask()를 통해 다른 스레드와 공유되므로, Count 확인과
    // Dequeue를 하나의 lock 블록 안에서 원자적으로 수행해 레이스 컨디션을 방지합니다.
    if (null == ActiveTask)
    {
        lock (QueueLock)
        {
            if (TaskQueue.Count > 0)
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

큐 삽입(`QueueAsyncTask`) 쪽은 다른 스레드(예: UDP `BeginReceive` 콜백)에서도 호출될 수 있으므로 `QueueLock`으로 보호하며, 개발 빌드에서 테스트용 SteamID를 쓰는 경우 통계 오염을 막기 위해 큐잉 자체를 건너뛰는 방어 코드도 포함되어 있습니다:

```csharp
// SteamAsyncTaskManager.cs — 다른 스레드에서의 enqueue도 안전하도록 lock으로 보호
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

**③ 공통 타임아웃 및 생명주기 관리**
`SteamAsyncTask` 기반 클래스에서 `IsTimeOut()`, `ResetTimeOut()`, `SetTimeOut()`을 공통으로 제공합니다. Steam 서버 응답이 지연되거나 P2P 연결이 끊기더라도 시스템이 정지하지 않도록, 기본 2초 타임아웃을 모든 태스크에 일괄 적용하되, Workshop 업로드처럼 처리 시간이 긴 작업은 개별적으로 `SetTimeOut(false)`로 비활성화할 수 있도록 했습니다. 이를 통해 "타임아웃 처리 누락으로 인한 무한 대기"라는 결함을 구조적으로 방지합니다.

```csharp
// SteamAsyncTask.cs — 모든 태스크가 상속받는 공통 타임아웃 로직 (기본 2초)
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



**④ 델리게이트 기반 결과 통보를 통한 결합도 최소화**
각 태스크는 `Tick → FinalizeTask → TriggerDelegates`라는 동일한 생명주기를 따르며, 결과는 항상 `(성공 여부, 결과 데이터)` 형태의 델리게이트로 호출부에 통보됩니다. 호출부는 태스크 내부의 폴링 로직을 전혀 알 필요 없이, "요청을 큐에 적재하고 콜백을 등록"하는 것만으로 비동기 처리를 완결할 수 있습니다. 즉, Steamworks의 저수준 콜백 API를 게임 로직으로부터 완전히 은닉하는 어댑터 역할을 수행합니다.

**⑤ 예외 상황에 대한 방어적 설계**
Steamworks 초기화 여부와 무관하게, 게임 실행 중 씬 전환이나 종료 시점에 GC가 Steamworks 객체를 먼저 정리하는 경우가 있어 `IsAPICallCompleted()` 호출 시 `InvalidOperationException`이 발생할 수 있습니다. 이를 각 태스크의 `Tick()`에서 개별적으로 포착하여 `WasSuccessful = false; IsTaskDone = true;`로 안전하게 종료시키는 패턴을 일관되게 적용했습니다 (`SteamAsyncSetServerStats`, `SteamAsyncCreateItem` 등).

```csharp
// SteamAsyncSetServerStats.cs — GC가 Steamworks를 먼저 정리한 경우를 방어
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
// SteamAsyncCreateItem.cs — Tick() 전체를 try-catch로 감싸 Steamworks 미초기화 상태를 방어
public override void Tick()
{
    try
    {
        if (!Initialized)
        {
            Initialized = true;
            CallbackHandle = SteamUGC.CreateItem(ConsumerAppId, FileType);
        }
        // ... 폴링 로직 ...
    }
    catch (System.InvalidOperationException ex)
    {
        // Steamworks is not initialized
        IsTaskDone = true;
        WasSuccessful = false;
    }
}
```


### 1.3 이 구조로 얻은 이점 요약

- **일관성**: Stats, Leaderboard, Workshop 등 기능이 확장되어도 `SteamAsyncTask`를 상속하기만 하면 큐잉·타임아웃·에러 처리를 별도 구현 없이 재사용
- **안전성**: 모든 완료 콜백이 Unity 메인 스레드의 `Update()` 타이밍에 맞춰 호출되어, UI 갱신 등 후속 처리에서 스레드 문제가 발생하지 않음
- **테스트 용이성**: 각 태스크가 독립된 클래스이므로 Steam 클라이언트 없이도 `Tick()`, `FinalizeTask()` 단위로 로직 검증 가능
- **확장성**: 신규 Steam 기능 추가 시 기존 매니저·큐 코드를 수정할 필요 없이 새로운 `SteamAsyncXXX` 클래스만 추가

---

## 2. 아키텍처 개요

### 2.1 핵심 클래스 구조

```
SteamAsyncTask (abstract base)
 ├─ IsTaskDone / WasSuccessful / Initialized (공통 상태)
 ├─ IsTimeOut() / ResetTimeOut() / SetTimeOut()  (공통 타임아웃)
 └─ virtual Tick() / FinalizeTask() / TriggerDelegates()  (생명주기 훅)
       │
       ├─ SteamAsyncSetStats / SteamAsyncGetStats               (클라이언트 Stats)
       ├─ SteamAsyncSetServerStats / SteamAsyncGetServerStats   (게임서버 Stats)
       ├─ SteamAsyncGetLeaderBoard                              (리더보드 조회/생성)
       ├─ SteamAsyncGetLeaderBoardEntries                       (리더보드 엔트리 다운로드)
       ├─ SteamAsyncSetLeaderBoard                               (점수 업로드)
       ├─ SteamAsyncGetServerList / SteamAsyncGetLANServerList  (인터넷/LAN 서버 브라우저)
       ├─ SteamAsyncGetServerInfo / GetPlayers / GetRawServerRule (A2S UDP 쿼리)
       ├─ SteamAsyncCreateItem / SubmitItemUpdate                (Workshop 아이템 생성/업데이트)
       └─ SteamAsyncGetUGCDetails / SendQueryUGCRequest          (Workshop 아이템 조회)

SteamAsyncTaskManager : MonoBehaviour (Singleton)
 └─ Queue<SteamAsyncTask> 를 매 프레임 하나씩 소비
```

`SteamAsyncTaskManager`는 게임 전역에서 유일하게 존재하는 싱글턴 `MonoBehaviour`로, `DontDestroyOnLoad`로 씬 전환 중에도 유지됩니다. `ActiveTask`는 `Update()`(메인 스레드)에서만 접근하므로 별도의 락이 필요 없고, 다른 스레드(예: UDP 콜백)에서도 접근하는 `TaskQueue`만 단일 락 오브젝트(`QueueLock`)로 보호합니다. `Count` 확인과 `Dequeue`를 같은 lock 블록 안에서 처리해 두 연산 사이의 레이스 컨디션도 차단했습니다.

`SteamAsyncTask` 기반 클래스 자체는 상태값 세 개와 생명주기 훅 세 개만 노출하는 얇은 계약(contract)입니다 — 하위 클래스는 필요한 훅만 골라서 오버라이드하면 되고, 기본 `Tick()`은 아무 폴링도 하지 않고 즉시 완료 처리합니다:

```csharp
// SteamAsyncTask.cs — 모든 SteamAsyncXXX 태스크가 상속하는 공통 base
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

`SteamAsyncTaskManager`의 싱글턴 부분은 `s_instance`가 이미 존재하면 자기 자신을 파괴하는 방식으로 중복 생성을 막고, `DontDestroyOnLoad`로 씬 전환에도 살아남도록 처리합니다:

```csharp
// SteamAsyncTaskManager.cs — 씬 전환에도 유지되는 싱글턴 초기화
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



### 2.2 태스크 실행 생명주기

```
1. QueueAsyncTask(task)         : 큐에 삽입
2. Update()에서 ActiveTask로 승격
3. 매 프레임 ActiveTask.Tick()  : 초기 요청 발사(최초 1회) + 완료 여부 폴링
4. IsTaskDone == true 이거나 IsTimeOut() == true 가 되면
5. FinalizeTask()                : 결과 데이터 가공 (예: Stat 실제 반영, 저장 요청)
6. TriggerDelegates()            : 등록된 델리게이트로 호출부에 결과 통보
7. ActiveTask = null             : 다음 태스크로 이동
```

각 하위 클래스는 이 훅들 가운데 필요한 것만 선택적으로 오버라이드합니다. 예를 들어 `SteamAsyncGetLeaderBoard`는 `FinalizeTask`에서 별도 처리가 없어 비워두는 반면, `SteamAsyncSetServerStats`는 `FinalizeTask`에서 실제 통계 값을 반영한 뒤 `StoreUserStats`를 호출합니다.

가장 단순한 형태인 `SteamAsyncGetLeaderBoard`를 예로 들면, `Tick → FinalizeTask → TriggerDelegates` 세 훅이 실제로 어떻게 오버라이드되는지 한눈에 볼 수 있습니다:

```csharp
// SteamAsyncGetLeaderBoard.cs — Tick에서 초기 요청 발사 + 폴링, FinalizeTask는 비움, TriggerDelegates로 결과 통보
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
    // 이 태스크는 후처리가 필요 없어 비워둠
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

## 3. 기능별 구현

### 3.1 Stats & Achievements

클라이언트용(`SteamAsyncGetStats` / `SteamAsyncSetStats`)과 게임서버용(`SteamAsyncGetServerStats` / `SteamAsyncSetServerStats`) 두 축으로 구성됩니다. 데디케이티드 서버 환경에서는 `SteamGameServerStats`, 일반 클라이언트에서는 `SteamUserStats`를 사용해야 하므로 API 계열 자체가 상이하며, 이를 각각 별도 태스크 클래스로 분리하여 호출부의 혼선을 줄였습니다.

- `StatInfo` 클래스로 스탯 이름·값·증가 여부(Increase)·표시명을 하나의 단위로 캡슐화하여 `List<StatInfo>`를 통째로 요청/응답에 사용 → 다수의 스탯을 단일 태스크로 일괄 처리 가능
- `Increase` 플래그가 설정된 경우 기존 값을 먼저 조회한 뒤 델타를 더하는 방식으로 "누적 증가형 스탯"(킬 수, 플레이타임 등)을 지원
- `int`/`float` 두 데이터 타입만 지원하도록 명시적으로 분기 처리하고, 지원하지 않는 타입은 콘솔 로그로 통지하도록 방어

```csharp
// SteamAsyncSetServerStats.cs — Increase 플래그일 때 기존 값을 조회해 델타를 더한 뒤 반영
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
    // ... 동일 패턴을 float에도 반복 적용 ...
}
else
{
    ConsoleDebug.AddText(String.Format("<color=red><b>SteamAsyncSetServerStats unsupported datatype {0} {1}</b></color>", Info.StatName, typeof(float)));
}
```

`FinalizeTask()`에서는 반영한 값을 실제로 Steam 서버에 저장 요청까지 이어서 처리합니다:

```csharp
// SteamAsyncSetServerStats.cs — 값 반영(UpdateStatValue) 후 StoreUserStats로 서버에 커밋
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

`SteamAsyncGetLeaderBoard`(리더보드 핸들 조회/생성) → `SteamAsyncGetLeaderBoardEntries`(순위표 다운로드) → `SteamAsyncSetLeaderBoard`(점수 업로드)의 3단계로 구성되며, 각각이 독립된 태스크이므로 필요한 조합만 선택적으로 큐에 적재하여 사용할 수 있습니다.

- 리더보드는 내림차순 정렬(`k_ELeaderboardSortMethodDescending`), 숫자 표시(`k_ELeaderboardDisplayTypeNumeric`)로 고정 생성
- 다운로드한 엔트리는 `Dictionary<ulong, LeaderboardDetail>`에 SteamID 기준으로 병합되며, 기존 유저는 갱신하고 신규 유저는 추가하는 upsert 방식으로 구현되어 여러 범위(RangeStart~RangeEnd)를 순차 요청해도 결과가 누적됩니다

```csharp
// SteamAsyncGetLeaderBoardEntries.cs — SteamID 기준 upsert로 여러 범위 요청 결과를 하나의 Dictionary에 누적
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

인터넷 서버 목록(`SteamAsyncGetServerList`, `ServerListManager` + `SteamLobby`)과 LAN 서버 목록(`SteamAsyncGetLANServerList`)을 분리하여 지원합니다.

- `ISteamMatchmakingServerListResponse` 콜백은 서버 하나가 응답할 때마다(`OnServerResponded`) 개별적으로 호출되고, 전체 조회가 종료되면 `OnRefreshComplete`가 별도로 호출되는 구조입니다. 이 "다회 부분 응답 + 단일 완료 신호" 패턴은 `IsTaskDone`을 완료 콜백에서만 true로 설정하고, 그 이전의 개별 응답은 리스트에 누적하는 방식으로 `SteamAsyncTask`의 생명주기 안에 자연스럽게 통합했습니다
- 필터(`MatchMakingKeyValuePair_t[]`)를 통해 게임 디렉토리, 보안 서버 여부, 플레이어 존재 여부 등으로 서버 목록을 좁힐 수 있으며, 앱 ID 검증(`m_nAppID`)을 통해 타 게임의 서버가 혼입되지 않도록 필터링합니다
- 즐겨찾기(`AddToFavorites`/`RemoveToFavorites`)는 Steam의 `SteamMatchmaking` 즐겨찾기 API를 그대로 활용하여, 클라이언트 로컬 저장 없이 Steam 계정에 귀속되도록 처리했습니다
- `SteamAsyncGetServerInfo`/`GetPlayers`/`GetRawServerRule`은 Steamworks API가 아닌 [Source Engine Server Query Protocol(A2S)](https://developer.valvesoftware.com/wiki/Server_queries)을 순수 UDP 소켓으로 직접 구현한 부분으로, 챌린지 넘버 요청 → 응답 파싱의 2단계 핸드셰이크를 각각 `BeginReceive`/`EndReceive` 비동기 콜백으로 처리하여 동일한 `SteamAsyncTask` 인터페이스로 노출합니다

```csharp
// SteamAsyncGetServerList.cs — 개별 서버 응답은 누적만, IsTaskDone은 OnRefreshComplete에서만 true로 설정
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
// SteamAsyncGetPlayers.cs — A2S_PLAYER: 챌린지 넘버를 먼저 받아온 뒤 그 값을 그대로 실어 재요청하는 2단계 핸드셰이크
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


### 3.4 P2P Networking (UNET 통합)

Unity의 레거시 네트워킹(UNET)을 Steam P2P 위에서 동작하도록 이식한 부분입니다. UNET의 저수준 전송 계층(`TransportSend`/`TransportReceive`)을 Steam P2P 패킷 송수신으로 대체하는 어댑터 패턴을 적용했습니다.

- `SteamNetworkConnection`이 UNET의 `NetworkConnection`을 상속하여, `TransportSend()`를 오버라이드해 `SteamNetworking.SendP2PPacket()`으로 실제 전송을 위임합니다. QoS 채널 설정(Reliable/Unreliable 등)에 따라 `EP2PSend` 타입을 매핑하여 UNET의 채널 개념을 Steam P2P의 전송 방식에 맞춰 변환합니다

  ```csharp
  // SteamNetworkConnection.cs — UNET 채널의 QoS 설정을 Steam P2P 전송 타입으로 매핑
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

- `SteamNetworkManager.Update()`에서 매 프레임 모든 채널의 수신 패킷(`IsP2PPacketAvailable`)을 폴링하여 해당 연결의 `TransportReceive()`로 전달합니다 — Steam 콜백이 아닌 폴링 방식을 채택하여 UNET의 프레임 기반 처리 흐름과 자연스럽게 맞물리도록 설계했습니다

  ```csharp
  // SteamNetworkManager.cs — 매 프레임 모든 채널을 폴링해 UNET TransportReceive로 전달
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

- `UNETServerController`가 `P2PSessionRequest_t` 콜백(`Callback<T>.Create`)을 통해 신규 접속 요청을 감지하고, 수락 시 새로운 `SteamNetworkConnection`을 생성하여 UNET 서버의 외부 연결(`NetworkServer.AddExternalConnection`)로 등록합니다. 이를 통해 Steam 친구 초대·매치메이킹으로 접속한 피어가 일반 UNET 클라이언트와 동일하게 동작합니다

  ```csharp
  // UNETServerController.cs — P2P 세션 요청 수락 후 UNET 외부 연결로 등록
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

  클라이언트 쪽에서는 `SteamNetworkClient.Connect()`가 실제 UNET handshake 없이 `ConnectState`를 곧바로 `Connected`로 세팅해 UNET을 "속이는" 방식으로 P2P 연결을 완성합니다:

  ```csharp
  // SteamNetworkClient.cs — UNET의 정상 접속 절차를 생략하고 상태를 강제로 Connected로 전환
  public void Connect()
  {
      // Connect to localhost and trick UNET by setting ConnectState state to "Connected",
      // which triggers some initialization and allows data to pass through TransportSend
      m_AsyncConnect = ConnectState.Connected;

      // send Connected message
      connection.InvokeHandlerNoData(MsgType.Connect);
  }
  ```

- 연결 유효성은 주기적으로 `GetP2PSessionState()`로 검증(`ValidateSteamConnection`)하여, 상대가 이미 연결을 종료했음에도 UNET 측에 좀비 커넥션으로 잔존하는 문제를 방지합니다

  ```csharp
  // UNETServerController.cs — P2P 세션이 이미 끊겼는데 UNET에는 남아있는 좀비 커넥션을 정리
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

- 정상 종료 시에는 서버/클라이언트 역할에 따라 처리가 갈립니다. 서버는 즉시 `Reset()`으로 세션을 닫지만, 클라이언트는 큐에 쌓인 패킷이 전부 전송될 때까지 기다린 뒤(`FlushLocalP2PPacket`) 세션을 닫아, 마지막 메시지가 유실되지 않도록 합니다.

  ```csharp
  // SteamNetworkConnection.cs — 서버는 즉시 종료, 클라이언트는 disconnect 메시지 전송 후 큐 flush를 기다림
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
  // SteamNetworkManager.cs — 전송 큐가 빌 때까지 프레임 단위로 대기한 뒤에야 세션을 닫음
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

`SteamAsyncCreateItem` → 파일 업로드(외부 처리) → `SteamAsyncSubmitItemUpdate`로 이어지는 워크샵 아이템 게시 플로우와, `SteamAsyncSendQueryUGCRequest`/`SteamAsyncGetUGCDetails`로 기존 아이템을 조회하는 플로우를 지원합니다. 업로드 완료까지 상당한 시간이 소요될 수 있어, 두 태스크 모두 `SetTimeOut(false)`로 기본 2초 타임아웃을 비활성화한 점이 다른 태스크들과 차별화되는 지점입니다.

```csharp
// SteamAsyncSubmitItemUpdate.cs — 생성자에서 곧바로 기본 타임아웃을 비활성화
public SteamAsyncSubmitItemUpdate(UGCUpdateHandle_t ugcUpdateHandle, WorkShopItemDetail itemDetail, string changeNote)
{
    UGCUpdateHandle = ugcUpdateHandle;
    ItemDetail = itemDetail;
    ChangeNote = changeNote;

    // disable timeout
    base.SetTimeOut(false);
}
```

`SteamAsyncCreateItem`의 `FinalizeTask()`는 생성된 아이템의 `PublishedFileId_t`를 호출부가 전달한 `WorkShopItemDetail`에 되채워 넣어, 이후 `SubmitItemUpdate` 단계로 그대로 이어질 수 있도록 합니다:

```csharp
// SteamAsyncCreateItem.cs — 생성 결과를 다음 단계(SubmitItemUpdate)에서 쓸 수 있도록 ItemDetail에 반영
public override void FinalizeTask()
{
    if (WasSuccessful)
    {
        ItemDetail.PublishedField = CallbackResults.m_nPublishedFileId;
    }
}
```


### 3.6 Avatar / Friends

`SteamUtil.GetSteamAvatar()`는 앞서 설명한 비동기 태스크 프레임워크를 사용하지 않고 동기 API(`SteamFriends.GetSmallFriendAvatar`/`GetMediumFriendAvatar` + `ISteamUtils.GetImageRGBA`)로 즉시 처리됩니다. 아바타 이미지는 Steam 클라이언트가 이미 로컬에 캐시하고 있어 별도 폴링이 불필요하기 때문이며, Steam이 상하 반전된 이미지를 반환하는 특성에 맞춰 `FlipTexture()`로 보정한 뒤 `Texture2D`로 반환합니다.

```csharp
// SteamUtil.cs — 동기 API로 즉시 avatar 이미지를 가져온 뒤 상하 반전을 보정
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

## 4. 사용 예시

```csharp
// 통계 조회 요청
var task = new SteamAsyncGetStats(SteamUser.GetSteamID(), statDictionary);
task.OnGetStat += (parent, success, steamId, stats) =>
{
    if (success)
    {
        // UI 갱신 등 후속 처리
    }
};
SteamAsyncTaskManager.QueueAsyncTask(task);
```

호출부는 태스크를 생성하여 큐에 적재한 뒤 델리게이트만 등록하면 되며, 폴링·타임아웃·스레드 안전성은 프레임워크가 전담합니다.

---

## 5. 기술 스택

- **Engine**: Unity (UNET 레거시 네트워킹)
- **Steam SDK**: Steamworks.NET
- **Language**: C#
- **Networking**: Steam P2P Packet Relay, Source Engine A2S UDP Query Protocol
