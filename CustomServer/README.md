# Custom UDP Server-Authoritative Multiplayer Framework

Unity의 Dedicated Server(Netcode for GameObjects, Unity Transport 등)에 의존하지 않고, **순수 C# 콘솔 애플리케이션으로 작성한 UDP 서버**를 중심으로 한 Server-Authoritative 멀티플레이어 아키텍처 검증 프로젝트입니다.

서버는 Unity 런타임과 완전히 분리되어 있으며, 동일한 서버에 **MonoBehaviour 클라이언트**와 **DOTS/ECS 클라이언트**가 동시에 접속해 같은 게임을 플레이할 수 있음을 검증합니다. 또한 실제 유저 없이도 수십~수백 명 규모의 세션을 재현할 수 있는 **자체 부하 테스트 봇(Load Test Bot)** 을 포함합니다.

> 프로젝트 목적: Unity Dedicated Server 패키지가 강제하는 Netcode 종속성, 빌드 파이프라인, 라이선스 비용 없이도 — 순수 C# 서버 + 커스텀 바이너리 프로토콜만으로 프로덕션 수준의 클라이언트 예측/재조정 멀티플레이어를 구현할 수 있는지를 검증하는 프로젝트입니다.

---

## 1. Pure C# 서버

| 항목 | Unity Dedicated Server (NGO 등) | 본 프로젝트 (Custom C# Server) |
|---|---|---|
| 런타임 | Unity Player 빌드 필요 (headless 엔진 초기화 비용 존재) | .NET 콘솔 앱, Unity 엔진 의존성 0 |
| 배포 | 플랫폼별 빌드 파이프라인 필요 | `dotnet publish` 하나로 Linux 컨테이너 배포 가능 |
| 틱레이트 제어 | Unity FixedUpdate/PlayerLoop에 종속 | `Thread.Sleep` 기반 자체 루프로 완전한 제어 |
| 확장성 | GameObject/Component 오버헤드 존재 | `ConcurrentDictionary` 기반 순수 데이터 구조 |
| 스케일 아웃 | 엔진 라이선스/계정 단위 적용 대상 | 표준 서버 프로세스, 오케스트레이션 자유도 높음 |

서버는 `System.Net.Sockets.UdpClient` 으로 구현되어 있으며, Unity API를 전혀 참조하지 않습니다(`CustomServer.cs` 확인 시 `using UnityEngine`이 없음). 이 덕분에 Docker 컨테이너, 리눅스 베어메탈, 서버리스 컨테이너 등 어디에도 그대로 배포할 수 있습니다.

---

## 2. 네트워크 아키텍처 — Server-Authoritative + Client-Side Prediction

Photon Fusion 2, Rocket League의 GGPO 이전 방식과 유사한 **"서버가 유일한 정답(Source of Truth)을 계산하고, 클라이언트는 예측 후 서버 값으로 보정"** 하는 구조입니다.

```
[클라이언트]                                    [서버, 60Hz]
   |-- ClientInput(tick, throttle, turn, fire) -->|
   |   (로컬에서 즉시 SimulateTankStep 실행,      |-- HandleClientInput
   |    예측 결과를 화면에 먼저 반영)              |   (동일 공식으로 authoritative 시뮬레이션)
   |                                              |
   |<-- ServerState(tick, 전체 플레이어 스냅샷) --|-- BroadcastServerState
   |   pending input 재생 → 오차 계산 →           |
   |   스냅/보정/유지 3단 재조정                   |
```

### 2.1 서버 틱 루프

서버는 별도 스레드에서 60Hz 고정 주기로 시뮬레이션과 브로드캐스트를 수행합니다.

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

수신은 별도의 블로킹 `UdpClient.Receive` 스레드에서 처리되고, `ConcurrentDictionary<byte, PlayerState>`로 스레드 안전성을 확보합니다. 즉 **수신 스레드 / 시뮬레이션 스레드가 분리된 이중 스레드 모델**입니다.

### 2.2 클라이언트 예측 (Client-Side Prediction)

클라이언트는 서버 응답을 기다리지 않고 입력을 로컬에서 즉시 시뮬레이션해 반응성을 확보합니다. 이때 사용하는 이동 공식(`SimulateTankStep`)은 서버의 `HandleClientInput`과 **바이트 단위까지 동일한 계산 순서**로 유지해야 합니다.

```csharp
// 순서: 1) 회전  2) 목표 속도로 가감속  3) 이동  4) 위치만 경계 wrap.
public static void SimulateTankStep(SimulationConfig config, ref float2 pos, ref float rotDeg, ref float speed,
    float throttle, float turn, float dt)
{
    // 1) 회전
    rotDeg += turn * config.TurnSpeedDeg * dt;
    rotDeg %= 360f;
    if (rotDeg < 0f) rotDeg += 360f;

    // 2) 목표 속도로 가속/감속
    float targetSpeed = throttle >= 0f ? throttle * config.MaxForwardSpeed : throttle * config.MaxReverseSpeed;
    float speedDelta = config.Acceleration * dt;
    speed = (speed < targetSpeed)
        ? Mathf.Min(speed + speedDelta, targetSpeed)
        : Mathf.Max(speed - speedDelta, targetSpeed);

    // 3) 전진/후진 (0도 = +Y, 시계방향 증가)
    float rotRad = rotDeg * Mathf.Deg2Rad;
    float2 dir = new float2(Mathf.Sin(rotRad), Mathf.Cos(rotRad));
    pos += dir * speed * dt;

    // 4) 화면 경계 wrap (clamp가 아님 — 반대편에서 재등장)
    pos.x = WrapCoordinate(pos.x, config.ServerWorldHalfExtent.x);
    pos.y = WrapCoordinate(pos.y, config.ServerWorldHalfExtent.y);
}
```

이 함수는 MonoBehaviour 클라이언트(`CustomClient.cs`)와 ECS 클라이언트(`LocalPlayerFixedStepSystem.cs`) 양쪽에 **동일한 로직으로 이식**되어 있어, 두 클라이언트 구현체가 항상 같은 예측 결과를 내도록 보장합니다.

### 2.3 서버 재조정 (Reconciliation)

서버 스냅샷이 도착하면, 이미 처리된 pending input을 제거하고 남은 입력만 재생해 예측 오차를 계산합니다. 오차 크기에 따라 **스냅(즉시 이동) / 보정(오프셋 흡수) / 유지** 3단계로 처리해 순간이동처럼 보이는 튐(Pop)을 최소화합니다.

```csharp
// 위치: 스냅 / 보정(오프셋 흡수) / 유지 3단 처리
if (posErrorSqrMag > config.SnapThreshold * config.SnapThreshold)
{
    predicted.Position = reconciledPos;   // 오차가 너무 크면 즉시 스냅
    offset.Position = float2.zero;
}
else if (posErrorSqrMag > config.ErrorThreshold * config.ErrorThreshold)
{
    predicted.Position = reconciledPos;   // 값은 갱신하되
    offset.Position -= posError;          // 화면 표시는 오프셋으로 서서히 흡수 (RenderErrorOffset)
}
// else: 오차가 미미 — 아무것도 하지 않음 (예측 유지)
```

이후 `LocalPlayerRenderSmoothingSystem`이 매 프레임 `RenderErrorOffset`을 0으로 Lerp 감쇠시켜, 위치 보정이 눈에 띄는 튐 없이 부드럽게 흡수되도록 합니다. 이는 **Rollback 없는 Error-Smoothing 재조정** 방식으로, Fusion 2의 Soft/Hard Correction과 개념적으로 동일합니다.

### 2.4 경계 처리 — Wrap 좌표계와 재조정의 상호작용

화면 경계에서 클램프(Clamp) 대신 **반대편 재등장(Wrap)** 방식을 채택했는데, 이 경우 위치 차이를 단순 뺄셈으로 구하면 wrap 경계를 걸친 순간 거대한 오차로 오인될 수 있습니다. 최단 경로 기준으로 오차를 재계산하는 처리가 서버·양쪽 클라이언트 모두에 동일하게 구현되어 있습니다.

```csharp
private static float WrapCoordinate(float value, float halfExtent)
{
    float range = halfExtent * 2f;
    float shifted = value + halfExtent;      // [-half, +half) -> [0, range)
    float wrapped = shifted % range;
    if (wrapped < 0f) wrapped += range;      // C# 음수 % 보정
    return wrapped - halfExtent;             // 다시 [-half, +half)
}
```

> **설계 노트:** 이 상수는 서버 `WORLD_HALF_EXTENT_X/Y`, MonoBehaviour의 `_serverWorldHalfExtent`, ECS `SimulationConfig.ServerWorldHalfExtent` 세 곳에 중복 정의되어 있으며, 코드 주석에서도 반복적으로 "세 값이 정확히 일치해야 재조정이 매 틱 어긋나지 않는다"고 명시합니다. 단일 정답(Source of Truth)을 공유 어셈블리로 분리하지 않고 3중 중복시킨 것은 이 프로젝트가 **의도적 기술 검증용 프로토타입**이며, 실제 프로덕션에서는 Shared Assembly Definition으로 통합해야 할 부채(Tech Debt)임을 인지하고 있습니다.

---

## 3. 원격 플레이어 보간 (Entity Interpolation)

본인 캐릭터는 예측+재조정으로 처리하지만, 원격 플레이어는 예측할 입력 정보가 없으므로 **과거 스냅샷 두 개 사이를 보간**해 부드럽게 표시합니다. 렌더링은 항상 `Time.time - InterpolationDelay` 시점을 목표로 하여, 네트워크 지터를 흡수하는 버퍼 역할을 합니다.

```csharp
float renderTime = Time.time - config.InterpolationDelay;

int index = 0;
while (index < headers.Length && headers[index].LocalTimeStamp < renderTime) index++;

// index 앞뒤 두 스냅샷 사이를 t로 보간
float t = (renderTime - h1.LocalTimeStamp) / (h2.LocalTimeStamp - h1.LocalTimeStamp);
rt.Position = LerpWrapped(s1.Position, s2.Position, t, config.ServerWorldHalfExtent);
rt.RotationDeg = Mathf.LerpAngle(s1.RotationDeg, s2.RotationDeg, t);
```

Wrap 좌표계이므로 일반 `Lerp` 대신 **최단 경로 보간(`LerpWrapped`)** 을 사용해, 경계를 넘나드는 순간 화면을 대각선으로 가로지르는 시각적 오류를 방지합니다.

리스폰처럼 "예측 불가능한 순간이동"은 별도의 `JustRespawnedTag`로 표시해 보간 파이프라인에서 제외하고, 즉시 스냅 처리합니다 — 죽은 위치와 새 스폰 위치를 보간하면 화면을 가로지르는 텔레포트처럼 보이기 때문입니다.

---

## 4. 이중 클라이언트 구현체 — MonoBehaviour vs DOTS/ECS

동일한 바이너리 프로토콜과 동일한 예측/재조정 알고리즘을 **두 가지 상이한 Unity 아키텍처**로 각각 구현해, 프로토콜 설계가 특정 클라이언트 아키텍처에 종속되지 않음을 검증했습니다.

| 계층 | MonoBehaviour 구현 | ECS/DOTS 구현 |
|---|---|---|
| 네트워크 스레드 | `CustomClient` 내부 `Thread` | `NetworkConnectionSystem` (InitializationSystemGroup) |
| 입력/예측 | `FixedUpdate()` 단일 메서드 | `LocalPlayerFixedStepSystem` (FixedStepSimulationSystemGroup) |
| 재조정 | `Update()` 내 인라인 처리 | `ServerStateApplySystem.ReconcileLocalPlayer` |
| 원격 보간 | `InterpolateRemotePlayers()` | `RemotePlayerInterpolationSystem` |
| 렌더 동기화 | `transform.position` 직접 대입 | `RenderTransform2D` → `SyncRenderTransformSystem`가 `LocalTransform`으로 변환 |
| 상태-뷰 브리지 | 없음 (직접 GameObject 조작) | `GameHudBridge` (Hybrid MonoBehaviour가 ECS 싱글톤을 읽어 TMP/OnGUI 렌더) |

ECS 버전은 시뮬레이션 System(순수 데이터)과 표시 로직(TextMeshPro, OnGUI)을 명확히 분리하는 **Hybrid Bridge 패턴**을 사용합니다.

```csharp
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(ServerStateApplySystem))]
public partial class RemotePlayerInterpolationSystem : SystemBase
{
    protected override void OnUpdate()
    {
        // ECS 쪽은 RenderTransform2D(순수 float2/float)만 갱신하고
        // 실제 TextMeshPro/SpriteRenderer는 GameHudBridge.Update()가
        // 매 프레임 EntityManager를 폴링해 동기화한다.
    }
}
```

두 클라이언트는 `SceneSelector.cs`를 통해 씬 선택 화면에서 자유롭게 전환 가능하며, **동일 서버에 동시 접속해 서로를 상대 플레이어로 인식**합니다. 즉 서버 프로토콜이 클라이언트 구현 방식과 완전히 독립적임을 실증합니다.

---

## 5. 커스텀 바이너리 프로토콜

JSON/Protobuf 등 범용 직렬화 대신, `BinaryWriter`/`BinaryReader` 기반의 **최소 오버헤드 고정 바이너리 프로토콜**을 직접 설계했습니다. 패킷 최상위 1바이트가 타입을 식별합니다.

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

서버 상태 브로드캐스트는 접속 인원수에 비례한 가변 길이 패킷이며, 필드 순서가 송수신 양쪽에서 정확히 일치해야 합니다(스키마 자동 검증 없이 수동 동기화 — 리플렉션/코드 생성 없는 저수준 직렬화의 트레이드오프).

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

## 6. 서버 사이드 게임플레이 로직

이동/발사/피격/사망/리스폰 등 모든 판정은 서버에서만 이루어집니다. 클라이언트는 시각적 예측만 담당하며 결과를 신뢰하지 않는 전형적인 **Authoritative Server** 모델입니다.

- **연사 제한**: 클라이언트 입력 엣지 검출 없이 `fire == true`가 유지되는 동안 서버가 쿨다운(`FIRE_COOLDOWN_SECONDS`)만으로 발사 빈도를 강제
- **충돌 판정**: 미사일-탱크 원형 충돌을 서버에서 매 틱 계산, 클라이언트는 예측 미사일을 별도로 굴리다가 서버 확정 시 위치 매칭으로 전환
- **사거리 소멸**: Wrap 좌표계에서는 "경계를 벗어남" 조건이 무의미하므로, 발사 지점부터의 누적 이동 거리로 미사일 수명을 제한

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

**제자리 부활(In-Place Respawn)**: 사망 시 위치를 유지한 채 체력만 리셋해, 클라이언트의 "즉시 스냅" 재조정 로직이 실제로는 이동량 0인 무해한 스냅이 되도록 설계했습니다. 클라이언트 특수 케이스 처리와 서버 데이터 설계가 상호 보완적으로 맞물린 지점입니다.

---

## 7. 부하 테스트 도구 (LoadTestBot)

실제 Unity 클라이언트 없이, **순수 .NET 콘솔 애플리케이션**이 서버와 동일한 바이너리 프로토콜로 통신하는 봇 그룹를 생성해 서버 확장성을 검증합니다.

```csharp
private static async Task<int> Main(string[] args)
{
    var bots = new List<BotClient>(config.BotCount);
    for (int i = 0; i < config.BotCount; i++)
    {
        var bot = new BotClient(serverEndPoint, stats);
        bots.Add(bot);
        botTasks.Add(bot.RunAsync(cts.Token));

        // 동시 접속 폭주를 피하기 위해 접속을 시간축으로 분산
        if (config.SpawnIntervalMs > 0 && i < config.BotCount - 1)
            await Task.Delay(config.SpawnIntervalMs, cts.Token);
    }
}
```

각 봇은 `async/await` + `PeriodicTimer` 기반으로 60Hz 입력 전송 루프를 돌리며, 전진/후진/회전/정지를 랜덤 가중치로 섞은 행동 패턴과 Burst-fire 패턴으로 실제 플레이어 트래픽과 유사합니다.

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

`LoadTestStats`는 `Interlocked` 연산만으로 락 없이 패킷/바이트 송수신량을 집계하며, `ConsoleDashboard`가 커서 위치 제어로 콘솔 상단에 실시간 처리량(pkt/s, KB/s)을 고정 표시합니다 — 로그 스트림과 라이브 대시보드가 한 화면에서 스크롤 없이 공존하는 구조입니다.

```csharp
public void RecordSent(int bytes)
{
    Interlocked.Increment(ref _packetsSent);
    Interlocked.Add(ref _bytesSent, bytes);
}
```

### 7.1 부하 테스트 데모 영상

봇 10개로 시작해 200개까지 순차적으로 늘려가며 서버 처리량과 클라이언트 동기화 상태를 확인한 테스트 영상입니다.

[![Load Test: 10 -> 200 bots](https://img.youtube.com/vi/-qsfweI85r4/maxresdefault.jpg)](https://youtu.be/-qsfweI85r4)

---

## 8. 재연결(Reconnection) 워치독

서버 재시작이나 순간적 네트워크 단절 시, 클라이언트는 일정 시간(`ConnectionTimeoutSeconds`) 응답이 없으면 자동으로 소켓을 재생성하고 재접속을 시도합니다. MonoBehaviour/ECS 양쪽에 동일한 워치독 로직이 구현되어 있습니다.

```csharp
private void UpdateReconnectionWatchdog()
{
    float secondsSinceLastMessage = (nowMs - lastMsgMs) / 1000f;
    if (secondsSinceLastMessage < _connectionTimeoutSeconds) { _reconnectTimer = 0f; return; }

    _reconnectTimer += Time.deltaTime;
    if (_reconnectTimer >= _reconnectIntervalSeconds)
    {
        _reconnectTimer = 0f;
        ConnectToServer(); // 기존 소켓/뷰 정리 후 재접속
    }
}
```

---

## 9. 프로젝트 구조

```
Server (Unity 비의존)
├── CustomServer.cs         서버 메인 루프, 시뮬레이션, 브로드캐스트
├── Protocol.cs              (Bot 전용) 패킷 직렬화/역직렬화 헬퍼
├── LoadTestStats.cs         Interlocked 기반 스레드 안전 통계
├── BotClient.cs             봇 개별 행동/네트워크 로직
├── ConsoleDashboard.cs       커서 제어 기반 실시간 콘솔 대시보드
└── UnitTest.cs               부하 테스트 CLI 엔트리 포인트

Client - MonoBehaviour
└── CustomClient.cs   예측/재조정/보간 전체를 단일 컴포넌트로 구현

Client - ECS/DOTS
├── NetworkComponents.cs             공유 컴포넌트/싱글톤 정의
├── NetworkConnectionSystem.cs       소켓 수명 주기, 재연결 워치독
├── LocalPlayerFixedStepSystem.cs    입력 수집 + 클라이언트 예측
├── ServerStateApplySystem.cs        스냅샷 적용 + 재조정
├── RemotePlayerInterpolationSystem.cs  원격 보간
├── LocalPlayerRenderSmoothingSystem.cs 재조정 오차 스무딩
├── MissileSystem.cs                 예측 미사일 + 서버 확정 매칭
├── ScoreboardApplySystem.cs         킬/데스 동기화
├── UpdateBoundsAndPingSystem.cs     카메라 경계 계산 + RTT 측정
├── RenderSyncSystems.cs             ECS Transform → Entities Graphics 반영
├── ShipRenderAuthoring.cs / MissileRenderAuthoring.cs   베이킹용 Authoring
├── GameBootstrapAuthoring.cs        런타임 프리팹 참조 등록
├── SimulationConfigAuthoring.cs     서버 상수 미러링 설정
└── GameHudBridge.cs                 ECS ↔ TextMeshPro/OnGUI 브리지

Shared
├── SceneSelector.cs          MonoBehaviour/ECS 씬 전환 UI
└── InputConfigManager.cs     Input System 기반 키 바인딩 관리/영속화
```

---

## 10. 빌드 다운로드

| 파일 | 설명 | 링크 |
|---|---|---|
| `ClientBuild.zip` | Unity 클라이언트 빌드 (MonoBehaviour/ECS 씬 선택 가능) | [다운로드](https://drive.google.com/file/d/15OwIuR4qvaMoIUMMC-D2Gbs-5OdCS66m/view?usp=sharing) |
| `CustomServer.zip` | UDP 서버 콘솔 실행 파일 | [다운로드](https://drive.google.com/file/d/1zSpl6X2qY2FMTBusv9dBUFNJ4JcgrVF2/view?usp=sharing) |
| `LoadTestBot.zip` | 부하 테스트 봇 콘솔 실행 파일 | [다운로드](https://drive.google.com/file/d/1aV_XcXkb1NRWe-5OqFGVNWpANZq9kmi4/view?usp=sharing) |

> `CustomServer`와 `LoadTestBot` 실행 파일은 .NET 환경이 설치되어 있지 않은 다른 PC에서도 C# 콘솔 프로그램을 바로 실행하려면 단일 파일(Single File) 및 자체 포함(Self-Contained) 옵션으로 빌드되었습니다.

---

## 기술 스택
- **Engine**: Unity
- **Client Architecture**: MonoBehaviour / DOTS-ECS 이중 구현 — 동일 프로토콜로 두 아키텍처의 클라이언트 예측·재조정 비교
- **Networking (Server)**: 순수 C# UDP 서버 (`System.Net.Sockets`, Unity 런타임 비의존) — Server-Authoritative 시뮬레이션 + 커스텀 바이너리 프로토콜
- **Networking 방식 비교**: Unity Dedicated Server 및 Photon 계열 상용 솔루션 대비, 자체 서버 구축 시의 실현 가능성과 예상 비용, 기술 제어 수준을 종합 검토
- **Load Testing**: 순수 .NET 콘솔 봇 클라이언트 (async/await, `PeriodicTimer`) — Unity 클라이언트 없이 대규모 동시접속 시뮬레이션
- **Genre**: Top-down Arcade Combat (2D 우주선 슈팅, 실시간 PvP)
