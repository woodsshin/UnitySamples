# Deterministic Lockstep Multiplayer Framework (Server-Relay, Physics-less Server)

Unity의 Dedicated Server 패키지(Netcode for GameObjects, Unity Transport 등)에 의존하지 않고, **순수 C# 콘솔 애플리케이션으로 작성한 UDP 서버**를 중심으로 한 **Deterministic Lockstep** 멀티플레이어 아키텍처 검증 프로젝트입니다.

기존 Server-Authoritative + Client-Side Prediction/Reconciliation 구조에서 한 단계 더 나아가, **서버가 물리 시뮬레이션을 전혀 수행하지 않는 "Input Relay" 구조**로 전면 재설계했습니다. Photon Quantum, Age of Empires 계열 RTS가 사용하는 lockstep 모델과 동일한 개념으로, 서버는 입력을 수집·중계하는 순수 중재자 역할만 하고 모든 물리 연산은 각 클라이언트가 동일한 입력 시퀀스를 동일한 순서로 재생해 독립적으로 계산합니다.

서버는 Unity 런타임과 완전히 분리되어 있으며, 동일한 서버에 **DOTS/ECS 클라이언트**와 **부하 테스트 봇(Load Test Bot)** 이 동시에 접속해 같은 입력 시퀀스로부터 각자 독립적으로 동일한 시뮬레이션 결과를 재현함을 검증합니다.

> 프로젝트 목적: Server-Authoritative 구조에서 발생하는 "예측 오차 보정(Reconciliation)"이라는 클래스의 버그 자체를 구조적으로 제거하고, 서버 부하와 대역폭을 극단적으로 낮추면서도 완전한 동기화를 보장하는 lockstep 아키텍처를 순수 C# 서버 + 커스텀 바이너리 프로토콜만으로 구현할 수 있는지 검증합니다.

---

## 1. 아키텍처 전환 — Server-Authoritative Physics → Deterministic Lockstep

기존에 서버가 위치/회전/체력/사망/미사일/스코어를 직접 계산해 브로드캐스트하던 구조를 폐기하고, 서버의 책임을 두 가지로 축소했습니다.

1. 각 플레이어가 보낸 `(TargetTick, Throttle, Turn, Fire)` 입력을 목표 틱(tick)별로 수집한다.
2. 매 틱마다 "그 틱에 살아있는 모든 플레이어의 입력"을 모아 `TickCommit`으로 방송한다. 아직 도착하지 않은 입력은 마지막 확정 입력을 그대로 재사용한다 (predicted-input fallback).

```csharp
// DeterministicSampleServer.cs — 서버는 위치/회전/체력/사망 등 어떤 물리 필드도 갖지 않는다.
public class PlayerConnection
{
    public byte Id;
    public IPEndPoint EndPoint;
    public DateTime LastPingTime;
    public long JoinSequence;      // "실제 접속 순서" — PlayerId 오름차순만으로는 재현 불가

    // fallback용 캐시: 마지막으로 확정에 사용한 입력
    public float LastThrottle;
    public float LastTurn;
    public bool LastFire;
}
```

| 항목 | 기존 (Server-Authoritative) | 현재 (Deterministic Lockstep) |
|---|---|---|
| 물리 연산 주체 | 서버 | 각 클라이언트 (전원 동일 코드 경로) |
| 서버 → 클라이언트 페이로드 | 전체 플레이어 위치/회전/체력 스냅샷 | 입력 시퀀스(throttle/turn/fire)만 |
| 클라이언트 간 오차 발생 원인 | 예측-재조정 타이밍/오프셋 흡수 로직 | 입력 재생 순서가 어긋나면 즉시 발산 (fail-fast) |
| Reconciliation / Snapshot Interpolation | 필요 (스냅/보정/유지 3단 처리) | **불필요** — 삭제됨 |
| 대역폭 (플레이어 N명 기준) | O(N) 스냅샷을 전원에게 매 틱 전송 | O(N) 입력을 전원에게 매 틱 전송 (필드 수가 훨씬 작음) |

서버는 여전히 `System.Net.Sockets.UdpClient`로 구현되며 Unity API를 전혀 참조하지 않아, Docker/리눅스 베어메탈 등 어디에나 배포 가능합니다.

---

## 2. 프로토콜 재설계 — TickCommit 단일 채널

기존 `ServerState` / `MissileState` / `ScoreboardState` 세 종류의 브로드캐스트 패킷을 **`TickCommit` 하나로 통합**했습니다. 서버가 아는 것은 참가/퇴장/입력뿐이므로, 그 외의 어떤 게임플레이 상태도 프로토콜에 존재하지 않습니다.

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
    public float DeltaTimeSeconds;   // 서버 실측 dt — 클라이언트는 이 값을 그대로 물리 dt로 사용
    public bool GameStarted;         // 엣지 신호: 게임이 "이 틱에" 시작되었는지
    public JoinedPlayerInfo[] JoinedPlayers;
    public byte[] LeftPlayerIds;
    public TickPlayerInput[] Inputs;
}
```

세 프로젝트(서버 / DOTS 클라이언트 / Load Test Bot)가 이 파일을 각자 경로에 물리적으로 복사해 사용하는 구조이므로, 프로토콜 변경 시 세 곳을 수동으로 동일하게 갱신해야 합니다. 컴파일러가 강제하지 않는 대신, 필드 순서 및 정렬 규약(Join은 `JoinSequence` 오름차순, Leave/Input은 `PlayerId` 오름차순)을 코드 전역에서 엄격히 지킵니다.

### 2.1 실측 Delta Time — 고정 틱레이트 가정 제거

서버의 목표 틱레이트(60Hz)는 `Thread.Sleep` 기반 루프의 목표치일 뿐, 스레드 스케줄링 오차로 실제 간격이 미세하게 달라질 수 있습니다. 서버는 이를 매 틱 `Stopwatch`로 실측해 `DeltaTimeSeconds`로 함께 방송하고, 클라이언트는 고정된 `1/60`이 아닌 이 실측값을 그대로 물리 dt로 사용합니다.

```csharp
// DeterministicSampleServer.ServerLoop
float deltaTimeSeconds = tick == 0
    ? (1f / TICK_RATE)
    : (float)tickStopwatch.Elapsed.TotalSeconds;
tickStopwatch.Restart();

BroadcastTickCommit(tick, deltaTimeSeconds, scheduledGameStartThisTick,
    _scheduledJoins, _scheduledLeaves, confirmedInputs, _scheduledLeaveEndPoints);
```

이 값 자체가 서버로부터 전원에게 동일하게 방송되므로, 가변 dt를 사용해도 모든 클라이언트가 정확히 동일한 값으로 누적 계산을 수행해 결정론이 깨지지 않습니다.

---

## 3. Join Sequence — 접속 순서 재현 버그의 발견과 수정

초기 구현에서는 `PlayerId`(byte, 단조 증가) 오름차순을 "실제 접속 순서"로 가정해 스폰 슬롯을 배정했습니다. 그러나 재접속으로 새 ID를 받거나 byte wraparound가 발생하면 `PlayerId` 순서와 실제 접속 순서가 어긋나, 클라이언트마다 스폰 위치 계산 결과가 달라지는 결정론 붕괴 버그가 발견되었습니다.

이를 서버가 명시적으로 관리하는 전역 단조 증가 카운터 `JoinSequence`로 해결했습니다.

```csharp
// JoinedPlayerInfo — Join 처리 순서 규약은 PlayerId가 아니라 반드시 JoinSequence 오름차순
public struct JoinedPlayerInfo
{
    public byte PlayerId;
    public long JoinSequence;
}
```

신규 클라이언트는 `JoinResponse.ExistingPlayers`로 이미 접속해 있던 플레이어 전원의 `(PlayerId, JoinSequence)` 목록을 받아, 자신의 `TickCommit` 처리를 시작하기 전에 이들을 먼저 스폰합니다.

```csharp
// SimulationTickSystem.cs — 신규 접속자 초기 동기화
private void SpawnExistingPlayersOnConnect(JoinedPlayerInfo[] existingPlayers, byte myPlayerId, Entity uiEventSingleton)
{
    var sorted = new List<JoinedPlayerInfo>(existingPlayers);
    sorted.Sort((a, b) => a.JoinSequence.CompareTo(b.JoinSequence));

    foreach (var info in sorted)
        SpawnPlayer(info.PlayerId, info.JoinSequence, myPlayerId, uiEventSingleton);
}
```

이 절차가 없으면 늦게 접속한 클라이언트가 "현재 점유된 스폰 슬롯 집합"을 다른 클라이언트보다 작게 계산해, 동일한 `ComputeSpawnTransform` 로직이 클라이언트마다 다른 결과를 내게 됩니다. `JoinSequence`는 또한 별도의 "방장(Host)" 필드 없이도 매 프레임 재계산만으로 방장 승계 로직을 완전히 대체합니다.

```csharp
// DeterministicSampleServer.cs — 최소 JoinSequence 보유자가 곧 현재 방장
private byte? GetCurrentHostId()
{
    PlayerConnection host = null;
    foreach (var kvp in _players)
        if (host == null || kvp.Value.JoinSequence < host.JoinSequence) host = kvp.Value;
    return host?.Id;
}
```

방장이 퇴장하면 다음 최소값 보유자가 다음 계산에서 자동으로 방장이 되므로, 별도의 승계(succession) 로직이 전혀 필요 없습니다. 서버(`GetCurrentHostId`)와 각 클라이언트(`GameHudBridge.ComputeIsHost`)가 동일한 공식을 각자 독립적으로 계산해 항상 같은 결론에 도달합니다.

---

## 4. 틱 처리 순서 규약 (Lockstep의 핵심 불변식)

Lockstep 결정론이 성립하려면 **모든 클라이언트가 정확히 같은 입력을 정확히 같은 순서로 재생**해야 합니다. 이 프로젝트의 처리 순서 규약은 다음과 같습니다.

```
Join (JoinSequence 오름차순)
  → Leave (PlayerId 오름차순)
  → 이동/발사 판정 (PlayerId 오름차순)
  → 미사일 이동/충돌 판정 (OwnerId 오름차순, 동률이면 FireTick 오름차순)
  → 리스폰 판정 (PlayerId 오름차순)
```

```csharp
// SimulationTickSystem.ProcessOneTick — 순서 자체가 결정론의 일부이므로 절대 바꾸지 않는다
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

    if (!clientState.IsGameStarted) return;   // 로비에서는 Join/Leave만 반영

    var sortedInputs = new List<TickPlayerInput>(commit.Inputs);
    sortedInputs.Sort((a, b) => a.PlayerId.CompareTo(b.PlayerId));
    foreach (var input in sortedInputs)
        ProcessPlayerTick(input, tick, commit.DeltaTimeSeconds, clientState.TotalElapsedSimTime, config, uiEventSingleton);

    ProcessMissiles(tick, commit.DeltaTimeSeconds, clientState.TotalElapsedSimTime, config, uiEventSingleton);
    ProcessRespawns(clientState.TotalElapsedSimTime, config, uiEventSingleton);
}
```

미사일 충돌 판정은 원본 서버 구현에서 `ConcurrentDictionary`의 순회 순서에 암묵적으로 의존했는데, 이는 서버 혼자 실행할 때는 문제가 없었지만(자기 자신과만 일관되면 충분) 여러 클라이언트가 각자 독립적으로 같은 로직을 실행해야 하는 구조에서는 순회 순서 자체가 결정론의 일부가 됩니다. `(OwnerId, FireTick)` 정렬로 명시적으로 고정해, 한 틱에 여러 미사일이 동시에 명중할 수 있는 킬 크레딧 판정까지 전원이 동일한 결과를 내도록 했습니다.

### 4.1 누적 경과 시간 기반 판정 — "N틱 지남"의 폐기

실측 dt 도입으로 "N틱이 지났다"는 사실이 더 이상 일정한 시간 경과를 보장하지 않습니다. 발사 쿨다운과 리스폰 판정은 틱 개수가 아니라, 서버가 방송한 `DeltaTimeSeconds`를 모든 클라이언트가 동일한 순서로 누적한 `TotalElapsedSimTime` 기준으로 이루어집니다.

```csharp
// SimulationTickSystem.ProcessPlayerTick
if (input.Fire && (elapsedTime - lastFire.Value) >= config.FireCooldownSeconds)
{
    EntityManager.SetComponentData(entity, new LastFireElapsedTime { Value = elapsedTime });
    SpawnMissile(input.PlayerId, tick, physics.Position, physics.RotationDeg);
}
```

부동소수점 덧셈 자체는 비결정적일 수 있으나, 모든 클라이언트가 정확히 같은 `DeltaTimeSeconds` 시퀀스를 정확히 같은 순서로 누적하므로 클라이언트 간에는 완전히 동일한 값을 유지합니다.

---

## 5. Join/Leave/게임 시작 — 틱 경계 레이스 컨디션 방어

`JoinRequest`, `StartGameRequest`는 수신 즉시 처리하지 않고 반드시 다음 틱 경계에서 `ServerLoop` 스레드가 순서대로 처리하도록 큐잉합니다. 모든 클라이언트가 정확히 같은 틱에서 같은 이벤트를 관찰해야, "현재 접속자 집합"에 의존하는 스폰 슬롯 계산 등이 전원 동일한 결과를 냅니다.

```csharp
// DeterministicSampleServer.ReceiveLoop — 수신 스레드는 큐잉만 하고 판단하지 않는다
case PacketType.JoinRequest:
    _pendingJoinRequests.Enqueue(remoteEP);
    break;

case PacketType.StartGameRequest:
    if (Protocol.TryReadStartGameRequest(data, out byte requesterId))
        _pendingStartGameRequests.Enqueue(requesterId);
    break;
```

```csharp
// DeterministicSampleServer.ServerLoop — 검증은 반드시 이 틱의 최신 스냅샷 기준
// 순서: 1) Join 반영 → 2) Leave 반영 → 3) StartGameRequest 검증
//   (막 타임아웃된 호스트가 보낸 요청을 정확히 거부하려면 Leave 반영 이후여야 함)
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

게임이 이미 시작된 뒤의 신규 접속 시도는 `JoinResponse.GameAlreadyStarted = true`로 즉시 거부하며, 이 경우 플레이어 명단(`PlayerId`, `JoinSequence` 카운터 포함)에 어떤 흔적도 남기지 않습니다.

```csharp
if (_isGameStarted)
{
    SendJoinResponse(clientEP, assignedId: 0, assignedSequence: 0,
        existingPlayers: EmptyExistingPlayers, gameAlreadyStarted: true);
    continue;
}
```

`GameStarted`는 상태가 아니라 엣지(edge) 신호입니다 — 전환이 일어난 딱 한 틱에서만 `true`이고, 클라이언트는 이를 관측한 즉시 영속적인 `IsGameStarted` 상태로 래치(latch)합니다. Join/Leave 목록이 매 틱 빈 리스트로도 계속 전송되는 것과 동일하게, 대역폭 낭비를 피하면서도 신뢰성 있는 상태 전이를 보장하는 설계입니다.

---

## 6. Input Delay — 롤백 없는 지연 흡수

이 lockstep 구조는 **롤백(rollback)을 사용하지 않습니다**. 대신 클라이언트가 로컬에서 다음에 확정될 틱보다 `InputDelayTicks`만큼 미래 시점에 적용될 입력을 미리 서버에 전송해, 입력이 도착하지 않아 fallback(마지막 입력 재사용)이 발동하는 상황 자체를 최소화합니다.

```csharp
// InputCaptureSystem.cs
// "서버가 다음으로 확정할 틱"(LastConfirmedTick + 1)에 InputDelayTicks를 더한다.
int targetTick = clientState.LastConfirmedTick + 1 + config.InputDelayTicks;

if (targetTick == _lastSentTargetTick) return;   // 동일 틱 재전송 방지

byte[] packet = Protocol.BuildClientInput(clientState.MyPlayerId, targetTick, throttle, turn, fireHeld);
```

로컬 PC 환경(지연/유실 거의 없음)을 가정해 작은 값(기본 2틱)으로도 충분하지만, 구조 자체는 Photon Quantum과 동일한 개념으로 네트워크 지연이 큰 환경으로 확장 가능하도록 설계되어 있습니다.

---

## 7. DOTS/ECS 클라이언트 — 물리 진실(Single Source of Truth)의 단순화

기존 `PredictedTankState` / `ServerConfirmedState` / `RenderErrorOffset`으로 이원화되어 있던 구조를 **`TankPhysicsState` 하나로 통합**했습니다. 예측과 확정이 별개로 존재할 필요가 없으므로("전원이 같은 입력을 같은 방식으로 재생한 결과이지 예측이 아니다"), 재조정 오프셋 스무딩·스냅샷 보간 인프라 전체가 삭제되었습니다.

```csharp
// NetworkComponents.cs
/// 유일한 물리 진실. 전원이 같은 입력을 같은 방식으로 재생한 결과이지 예측이 아니므로
/// "Predicted"라는 이름을 쓰지 않는다.
public struct TankPhysicsState : IComponentData
{
    public float2 Position;
    public float RotationDeg;
    public float Speed;
}
```

| 삭제된 컴포넌트/시스템 | 사유 |
|---|---|
| `ServerConfirmedState`, `PredictedTankState`, `RenderErrorOffset` | 예측-확정 이원 구조 자체가 사라짐 |
| `RemotePlayerInterpolationSystem`, 스냅샷 버퍼 | 모든 플레이어가 동일 시뮬레이션 결과를 직접 가지므로 보간 근거(위치가 다르게 들어옴) 자체가 없음 |
| `JustRespawnedTag` | 전원이 같은 틱에 같은 리스폰 결과를 계산하므로 "예측과 실제 어긋남을 점프로 봉합"할 상황이 발생하지 않음 |
| `TickRateHz` 설정값 | 고정 틱레이트 가정을 코드베이스 전역에서 제거 (실측 dt로 대체) |

물리 갱신(`SimulationTickSystem`)과 렌더 반영은 별도 System으로 분리되어, 서버 TickCommit이 도착했을 때만 실행되는 가변 프레임 물리와, 매 프레임 최신 확정값을 그대로 복사하는 렌더 동기화가 독립적으로 동작합니다.

```csharp
// PhysicsToRenderSyncSystem.cs
// 여러 틱이 한 프레임에 몰려도 중간 위치는 보간하지 않고 최종 확정값만 반영한다 (확정 사항).
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

`SimulationTickSystem`은 더 이상 Unity의 `FixedStepSimulationSystemGroup`에서 독자적인 60Hz 타이머로 진행하지 않고, `SimulationSystemGroup`(가변 프레임)에서 매 프레임 "이번 프레임에 새로 도착한 `TickCommit`들을 모아 그 개수만큼 순서대로 반복 실행"합니다. 서버가 `TickCommit`을 보내주지 않으면 이 System은 아무 틱도 진행시키지 않습니다.

---

## 8. Load Test Bot — 물리를 계산하지 않는 순수 트래픽 생성기

봇은 서버와 동일한 바이너리 프로토콜로 통신하지만, DOTS 클라이언트가 수행하는 물리 계산을 의도적으로 포기했습니다. Unity `Mathf`와 .NET `MathF`의 삼각함수 구현이 비트 단위까지 동일한 결과를 낸다는 보장이 없어, 이 리스크를 감수하지 않고 봇 자신의 사망 여부 판정 자체를 하지 않는 방향으로 설계했습니다.

```csharp
// BotClient.cs
// 봇은 실제로 죽어있어도 신경 쓰지 않고 계속 자신의 행동 패턴대로 입력을 만들어 보낸다.
// 그 틱에 죽어있다면 각 DOTS 클라이언트의 SimulationTickSystem이 알아서 그 입력을 무시할 뿐이다.
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

봇은 `TickCommit`을 받으면 `Tick` 번호만 추출해 자신의 `TargetTick` 계산에 사용하고, `Join`/`Leave`/`Input` 목록은 파싱만 할 뿐 실질적으로 처리하지 않습니다 — 순수하게 서버 입장에서 "실제 클라이언트와 구분되지 않는 트래픽 패턴"을 만드는 것이 유일한 목적입니다.

```csharp
private void ProcessPacket(byte[] data)
{
    switch (Protocol.PeekType(data))
    {
        case PacketType.TickCommit:
            if (Protocol.TryReadTickCommit(data, out TickCommitData commit))
                _lastConfirmedTick = commit.Tick;   // Tick 번호 외에는 아무것도 사용하지 않는다
            break;
    }
}
```

게임 시작 이후 접속을 시도해 `GameAlreadyStarted = true`로 거부당하면, 재접속을 시도하지 않고 즉시 접속 실패로 집계합니다.

```csharp
catch (InvalidOperationException)
{
    // 서버가 GameAlreadyStarted=true로 거부한 경우. 재접속을 시도하지 않고
    // 접속 실패로 집계 후 그대로 종료한다.
    _stats.RecordJoinFailed();
    return;
}
```

`LoadTestStats`는 `Interlocked` 연산만으로 락 없이 패킷/바이트 송수신량을 집계하며, `ConsoleDashboard`가 커서 위치 제어로 콘솔 상단에 실시간 처리량(pkt/s, KB/s)을 고정 표시합니다.

```csharp
public void RecordSent(int bytes)
{
    Interlocked.Increment(ref _packetsSent);
    Interlocked.Add(ref _bytesSent, bytes);
}
```

### 8.1 실행 파라미터

```
LoadTestBot [봇개수] [서버IP] [서버포트] [옵션]
```

| 인자/옵션 | 설명 | 기본값 |
|---|---|---|
| `봇개수` | 생성할 봇(가짜 플레이어) 수. 생략 시 경고 없이 기본값으로 스모크 테스트 규모 실행 | `10` |
| `서버IP` | 접속할 서버의 IP 주소 (호스트 이름 미지원) | `127.0.0.1` |
| `서버포트` | 접속할 서버 포트 | `9050` |
| `--duration <초>` | 테스트 지속 시간(초). 생략 시 Ctrl+C로 직접 종료할 때까지 무제한 실행 | 무제한 |
| `--spawn-interval-ms <ms>` | 봇 접속을 시간축으로 분산시키는 간격(ms/봇). 동시 접속 폭주 방지용 | `25` |

**예시**

```
LoadTestBot                 (기본값: 봇 10개, localhost:9050, 무제한)
LoadTestBot 100
LoadTestBot 200 192.168.0.10
LoadTestBot 500 192.168.0.10 9050 --duration 120 --spawn-interval-ms 10
```

> 서버의 플레이어 ID는 `byte`(0~255)이며 래핑 처리가 없어, 254개를 초과하는 누적 접속(재접속 포함)이 발생하면 ID가 겹칠 수 있습니다. 또한 종료 시 접속 종료 패킷을 별도로 보내지 않으므로, 프로그램 종료 후에도 서버는 `TIMEOUT_SECONDS`(3초) 동안 봇들을 살아있는 플레이어로 유지하다 정리합니다.

---

## 9. 재연결(Reconnection) 워치독 — 세션 스코프 상태의 전면 리셋

서버 재시작이나 순간적 네트워크 단절 시, 클라이언트는 일정 시간(`ConnectionTimeoutSeconds`) 응답이 없으면 자동으로 소켓을 재생성하고 재접속을 시도합니다. Lockstep 구조에서는 재연결이 곧 "새로운 틱 시퀀스의 시작"을 의미하므로, `LastConfirmedTick`뿐 아니라 그로부터 파생되는 모든 세션 스코프 값을 함께 리셋해야 합니다.

```csharp
// NetworkConnectionSystem.DisconnectInternal
clientState.MyPlayerId = 0;
clientState.IsConnected = false;
clientState.LastConfirmedTick = -1;

// TotalElapsedSimTime도 함께 0으로 리셋해야 한다. 이 값만 남아있으면 새로 스폰되는
// 플레이어의 발사 쿨다운/리스폰 판정 기준선이 "이전 연결에서 누적된 값"을 기준으로
// 계산되어 완전히 어긋난다.
clientState.TotalElapsedSimTime = 0f;

// IsGameStarted도 동일한 이유로 리셋 — 재연결은 새로운 세션이므로 로비 상태부터
// 다시 관찰해야 한다.
clientState.IsGameStarted = false;
```

원본 구현에서 `TotalElapsedSimTime`만 리셋하고 이 필드를 빠뜨렸다면, 새 세션의 발사 쿨다운/리스폰 판정 기준선이 이전 세션의 누적값과 뒤섞이는 조용한 버그로 이어졌을 지점입니다 — 세션 경계에서 리셋해야 하는 상태를 한 곳에 모아 관리하는 이유이기도 합니다.

재연결이 완료되면, 재연결 감지 로직 자체가 `struct` 타입 컴포넌트의 값 복사 특성으로 인해 낡은 스냅샷을 실수로 덮어쓰지 않도록 명시적으로 최신 상태를 다시 읽어옵니다.

```csharp
// UpdateReconnectionWatchdog — ConnectToServer() 이후 반드시 재동기화해야 하는 이유
// ClientStateSingleton은 struct이므로 GetComponentData는 항상 값 복사본을 반환한다.
// 여기서 다시 읽어오지 않으면, 이 함수를 호출한 OnUpdate()의 낡은 clientState가
// DisconnectInternal이 방금 끝낸 리셋을 그대로 덮어써버린다 — "연결하자마자 계속
// 재접속"의 원인이었다.
clientState = EntityManager.GetComponentData<ClientStateSingleton>(_networkEntity);
```

---

## 10. 프로젝트 구조

```
Server (Unity 비의존)
├── DeterministicSampleServer.cs   서버 메인 루프 (입력 수집/중계 전용, 물리 계산 없음)
├── LoadTestStats.cs                Interlocked 기반 스레드 안전 통계
├── BotClient.cs                    봇 개별 행동/네트워크 로직 (물리 미계산)
├── ConsoleDashboard.cs             커서 제어 기반 실시간 콘솔 대시보드
└── UnitTest.cs                     부하 테스트 CLI 엔트리 포인트

Shared Protocol
└── Protocol.cs                     Game.Networking 네임스페이스 — 서버/클라이언트/봇 3곳에
                                     물리적으로 복사되는 프로토콜 원본 (PacketType, 직렬화 규칙)

Client - DOTS/ECS (전원이 동일 코드 경로로 물리 계산)
├── NetworkComponents.cs            공유 컴포넌트/싱글톤 정의 (TankPhysicsState 등)
├── NetworkConnectionSystem.cs      소켓 수명 주기, 재연결 워치독, 세션 스코프 리셋
├── InputCaptureSystem.cs           입력 수집 + Input Delay 계산 (예측 없음)
├── SimulationTickSystem.cs         TickCommit 재생 → 이동/발사/충돌/사망/리스폰 (Lockstep 핵심)
├── PhysicsToRenderSyncSystem.cs    TankPhysicsState/MissileMotion → RenderTransform2D
├── RenderSyncSystems.cs            RenderTransform2D → Entities Graphics LocalTransform, 색상
├── UpdateBoundsAndPingSystem.cs    카메라 경계 계산 + RTT 측정
├── Authoring / ShipRenderAuthoring.cs / MissileRenderAuthoring.cs   베이킹용 Authoring
├── Authoring / GameBootstrapAuthoring.cs      런타임 프리팹 참조 등록
├── Authoring / SimulationConfigAuthoring.cs   서버와 공유해야 하는 물리 상수 설정
└── UI / GameHudBridge.cs           ECS ↔ TextMeshPro/OnGUI 브리지, 로비/방장 UI
```

---

## 11. 빌드 다운로드

| 파일 | 설명 | 링크 |
|---|---|---|
| `DeterministicClient.zip` | Unity 클라이언트 빌드 (MonoBehaviour/ECS 씬 선택 가능) | [다운로드](https://drive.google.com/file/d/1oEA--eRsbYmHEB8hci3Z4ielES03dLPv/view?usp=sharing) |
| `DeterministicSampleServer.zip` | UDP 서버 콘솔 실행 파일 | [다운로드](https://drive.google.com/file/d/1WNLPCeZiOrBe6HJX1rR7OkO2gCJqJLlK/view?usp=sharing) |
| `DeterministicLoadTestBot.zip` | 부하 테스트 봇 콘솔 실행 파일 | [다운로드](https://drive.google.com/file/d/1jA1U97UGRIRUvt1A37cSBA8jdCXA3oeC/view?usp=sharing) |

> `CustomServer`와 `LoadTestBot` 실행 파일은 .NET 환경이 설치되어 있지 않은 다른 PC에서도 C# 콘솔 프로그램을 바로 실행하려면 단일 파일(Single File) 및 자체 포함(Self-Contained) 옵션으로 빌드되었습니다.

---
## 기술 스택

- **Engine**: Unity (DOTS/ECS)
- **Client Architecture**: Deterministic Lockstep — 서버는 입력만 중계하고, 모든 클라이언트(DOTS 클라이언트 + Load Test Bot)가 동일한 입력 시퀀스를 동일한 순서로 재생해 독립적으로 물리 계산
- **Networking (Server)**: 순수 C# UDP 서버 (`System.Net.Sockets`, Unity 런타임 비의존) — Physics-less Input Relay + 커스텀 바이너리 프로토콜
- **결정론 보장 기법**: 실측 Delta Time 방송, `JoinSequence` 기반 참가 순서 재현, 틱 단위 처리 순서 규약(Join → Leave → Input → Missile → Respawn) 고정, 롤백 없는 Input Delay
- **Load Testing**: 순수 .NET 콘솔 봇 클라이언트 (async/await, `PeriodicTimer`) — 물리 계산 없이 서버 입장에서 실제 클라이언트와 구분되지 않는 트래픽 패턴 생성
- **Genre**: Top-down Arcade Combat (2D 우주선 슈팅, 실시간 PvP)
