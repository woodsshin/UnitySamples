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

**③ 공통 타임아웃 및 생명주기 관리**
`SteamAsyncTask` 기반 클래스에서 `IsTimeOut()`, `ResetTimeOut()`, `SetTimeOut()`을 공통으로 제공합니다. Steam 서버 응답이 지연되거나 P2P 연결이 끊기더라도 시스템이 정지하지 않도록, 기본 2초 타임아웃을 모든 태스크에 일괄 적용하되, Workshop 업로드처럼 처리 시간이 긴 작업은 개별적으로 `SetTimeOut(false)`로 비활성화할 수 있도록 했습니다. 이를 통해 "타임아웃 처리 누락으로 인한 무한 대기"라는 결함을 구조적으로 방지합니다.

**④ 델리게이트 기반 결과 통보를 통한 결합도 최소화**
각 태스크는 `Tick → FinalizeTask → TriggerDelegates`라는 동일한 생명주기를 따르며, 결과는 항상 `(성공 여부, 결과 데이터)` 형태의 델리게이트로 호출부에 통보됩니다. 호출부는 태스크 내부의 폴링 로직을 전혀 알 필요 없이, "요청을 큐에 적재하고 콜백을 등록"하는 것만으로 비동기 처리를 완결할 수 있습니다. 즉, Steamworks의 저수준 콜백 API를 게임 로직으로부터 완전히 은닉하는 어댑터 역할을 수행합니다.

**⑤ 예외 상황에 대한 방어적 설계**
Steamworks 초기화 여부와 무관하게, 게임 실행 중 씬 전환이나 종료 시점에 GC가 Steamworks 객체를 먼저 정리하는 경우가 있어 `IsAPICallCompleted()` 호출 시 `InvalidOperationException`이 발생할 수 있습니다. 이를 각 태스크의 `Tick()`에서 개별적으로 포착하여 `WasSuccessful = false; IsTaskDone = true;`로 안전하게 종료시키는 패턴을 일관되게 적용했습니다 (`SteamAsyncSetServerStats`, `SteamAsyncCreateItem` 등).

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

`SteamAsyncTaskManager`는 게임 전역에서 유일하게 존재하는 싱글턴 `MonoBehaviour`로, `DontDestroyOnLoad`로 씬 전환 중에도 유지됩니다. 큐 접근(`TaskQueue`)과 실행 중인 태스크(`ActiveTask`) 접근에는 각각 별도의 락 오브젝트(`QueueLock`, `TaskLock`)를 사용하여, 다른 스레드(예: UDP 콜백)에서 태스크를 enqueue 하더라도 안전성을 보장하도록 처리했습니다.

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

---

## 3. 기능별 구현

### 3.1 Stats & Achievements

클라이언트용(`SteamAsyncGetStats` / `SteamAsyncSetStats`)과 게임서버용(`SteamAsyncGetServerStats` / `SteamAsyncSetServerStats`) 두 축으로 구성됩니다. 데디케이티드 서버 환경에서는 `SteamGameServerStats`, 일반 클라이언트에서는 `SteamUserStats`를 사용해야 하므로 API 계열 자체가 상이하며, 이를 각각 별도 태스크 클래스로 분리하여 호출부의 혼선을 줄였습니다.

- `StatInfo` 클래스로 스탯 이름·값·증가 여부(Increase)·표시명을 하나의 단위로 캡슐화하여 `List<StatInfo>`를 통째로 요청/응답에 사용 → 다수의 스탯을 단일 태스크로 일괄 처리 가능
- `Increase` 플래그가 설정된 경우 기존 값을 먼저 조회한 뒤 델타를 더하는 방식으로 "누적 증가형 스탯"(킬 수, 플레이타임 등)을 지원
- `int`/`float` 두 데이터 타입만 지원하도록 명시적으로 분기 처리하고, 지원하지 않는 타입은 콘솔 로그로 통지하도록 방어

### 3.2 Leaderboard

`SteamAsyncGetLeaderBoard`(리더보드 핸들 조회/생성) → `SteamAsyncGetLeaderBoardEntries`(순위표 다운로드) → `SteamAsyncSetLeaderBoard`(점수 업로드)의 3단계로 구성되며, 각각이 독립된 태스크이므로 필요한 조합만 선택적으로 큐에 적재하여 사용할 수 있습니다.

- 리더보드는 내림차순 정렬(`k_ELeaderboardSortMethodDescending`), 숫자 표시(`k_ELeaderboardDisplayTypeNumeric`)로 고정 생성
- 다운로드한 엔트리는 `Dictionary<ulong, LeaderboardDetail>`에 SteamID 기준으로 병합되며, 기존 유저는 갱신하고 신규 유저는 추가하는 upsert 방식으로 구현되어 여러 범위(RangeStart~RangeEnd)를 순차 요청해도 결과가 누적됩니다

### 3.3 Matchmaking / Server Browser

인터넷 서버 목록(`SteamAsyncGetServerList`, `ServerListManager` + `SteamLobby`)과 LAN 서버 목록(`SteamAsyncGetLANServerList`)을 분리하여 지원합니다.

- `ISteamMatchmakingServerListResponse` 콜백은 서버 하나가 응답할 때마다(`OnServerResponded`) 개별적으로 호출되고, 전체 조회가 종료되면 `OnRefreshComplete`가 별도로 호출되는 구조입니다. 이 "다회 부분 응답 + 단일 완료 신호" 패턴은 `IsTaskDone`을 완료 콜백에서만 true로 설정하고, 그 이전의 개별 응답은 리스트에 누적하는 방식으로 `SteamAsyncTask`의 생명주기 안에 자연스럽게 통합했습니다
- 필터(`MatchMakingKeyValuePair_t[]`)를 통해 게임 디렉토리, 보안 서버 여부, 플레이어 존재 여부 등으로 서버 목록을 좁힐 수 있으며, 앱 ID 검증(`m_nAppID`)을 통해 타 게임의 서버가 혼입되지 않도록 필터링합니다
- 즐겨찾기(`AddToFavorites`/`RemoveToFavorites`)는 Steam의 `SteamMatchmaking` 즐겨찾기 API를 그대로 활용하여, 클라이언트 로컬 저장 없이 Steam 계정에 귀속되도록 처리했습니다
- `SteamAsyncGetServerInfo`/`GetPlayers`/`GetRawServerRule`은 Steamworks API가 아닌 [Source Engine Server Query Protocol(A2S)](https://developer.valvesoftware.com/wiki/Server_queries)을 순수 UDP 소켓으로 직접 구현한 부분으로, 챌린지 넘버 요청 → 응답 파싱의 2단계 핸드셰이크를 각각 `BeginReceive`/`EndReceive` 비동기 콜백으로 처리하여 동일한 `SteamAsyncTask` 인터페이스로 노출합니다

### 3.4 P2P Networking (UNET 통합)

Unity의 레거시 네트워킹(UNET)을 Steam P2P 위에서 동작하도록 이식한 부분입니다. UNET의 저수준 전송 계층(`TransportSend`/`TransportReceive`)을 Steam P2P 패킷 송수신으로 대체하는 어댑터 패턴을 적용했습니다.

- `SteamNetworkConnection`이 UNET의 `NetworkConnection`을 상속하여, `TransportSend()`를 오버라이드해 `SteamNetworking.SendP2PPacket()`으로 실제 전송을 위임합니다. QoS 채널 설정(Reliable/Unreliable 등)에 따라 `EP2PSend` 타입을 매핑하여 UNET의 채널 개념을 Steam P2P의 전송 방식에 맞춰 변환합니다
- `SteamNetworkManager.Update()`에서 매 프레임 모든 채널의 수신 패킷(`IsP2PPacketAvailable`)을 폴링하여 해당 연결의 `TransportReceive()`로 전달합니다 — Steam 콜백이 아닌 폴링 방식을 채택하여 UNET의 프레임 기반 처리 흐름과 자연스럽게 맞물리도록 설계했습니다
- `UNETServerController`가 `P2PSessionRequest_t` 콜백(`Callback<T>.Create`)을 통해 신규 접속 요청을 감지하고, 수락 시 새로운 `SteamNetworkConnection`을 생성하여 UNET 서버의 외부 연결(`NetworkServer.AddExternalConnection`)로 등록합니다. 이를 통해 Steam 친구 초대·매치메이킹으로 접속한 피어가 일반 UNET 클라이언트와 동일하게 동작합니다
- 연결 유효성은 주기적으로 `GetP2PSessionState()`로 검증(`ValidateSteamConnection`)하여, 상대가 이미 연결을 종료했음에도 UNET 측에 좀비 커넥션으로 잔존하는 문제를 방지합니다

### 3.5 Workshop (UGC)

`SteamAsyncCreateItem` → 파일 업로드(외부 처리) → `SteamAsyncSubmitItemUpdate`로 이어지는 워크샵 아이템 게시 플로우와, `SteamAsyncSendQueryUGCRequest`/`SteamAsyncGetUGCDetails`로 기존 아이템을 조회하는 플로우를 지원합니다. 업로드 완료까지 상당한 시간이 소요될 수 있어, 두 태스크 모두 `SetTimeOut(false)`로 기본 2초 타임아웃을 비활성화한 점이 다른 태스크들과 차별화되는 지점입니다.

### 3.6 Avatar / Friends

`SteamUtil.GetSteamAvatar()`는 앞서 설명한 비동기 태스크 프레임워크를 사용하지 않고 동기 API(`SteamFriends.GetSmallFriendAvatar`/`GetMediumFriendAvatar` + `ISteamUtils.GetImageRGBA`)로 즉시 처리됩니다. 아바타 이미지는 Steam 클라이언트가 이미 로컬에 캐시하고 있어 별도 폴링이 불필요하기 때문이며, Steam이 상하 반전된 이미지를 반환하는 특성에 맞춰 `FlipTexture()`로 보정한 뒤 `Texture2D`로 반환합니다.

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
