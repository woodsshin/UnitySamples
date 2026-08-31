# Custom UNet Networking Layer with Steam P2P Support

Unity의 레거시 네트워킹 프레임워크인 **UNet(HLAPI)** 을 확장하여, 데디케이티드 서버와 **Steam P2P** 두 가지 전송 방식을 단일한 게임 로직으로 지원하도록 설계한 코드입니다. 이전 프로젝트에서 발췌한 코드로, 게임 관련 정보와 프로젝트 고유 로직은 걷어내고 **네트워킹 아키텍처를 보여주는 핵심 부분만** 정리했습니다.

## 배경 및 문제 정의

UNet은 `NetworkConnection`을 통해 `SendBytes` / `SendWriter`를 호출하면 내부적으로 소켓 버퍼에 곧바로 메시지를 적재하는 구조입니다. 문제는 클라이언트가 서버에 접속하는 순간 발생합니다 — 서버가 씬에 존재하는 모든 `NetworkBehaviour`에 대해 `ObjectSpawnMessage`, `ObjectSpawnSceneMessage`를 일괄 처리하여 한꺼번에 전송하기 때문입니다.

오브젝트 수가 많은 월드(수백~수천 개의 NetworkIdentity)에서는 이 초기 스폰 트래픽이 엔진 내부 소켓 버퍼의 한도를 순식간에 초과합니다. 그 결과 다음과 같은 문제로 이어집니다:

- `NetworkTransport` 레벨에서 "no free events for message in the pool" 에러 발생
- 접속 직후 클라이언트가 타임아웃으로 끊기거나 오브젝트 스폰이 누락되는 현상
- Steam P2P 전송(`SteamNetworkingSockets` 기반)은 UDP 소켓과 큐잉 특성이 달라, 병목이 더 쉽게 발생

**목표:** 엔진의 전송 계층은 건드리지 않으면서, `NetworkConnection`을 상속·오버라이드하는 수준에서 자체 메시지 큐를 두어 초기 스폰 트래픽을 프레임 단위로 유량 제어(flow control)하는 것.

## 아키텍처 개요

```
NetworkManagerOverride (NetworkManager)
   ├─ StartHost()에서 P2P 여부에 따라 Connection 클래스를 교체
   │     ├─ SteamNetworkManager.IsUsingP2P == true  → SteamNetworkConnection 사용
   │     └─ false (데디케이티드 서버 등)              → CustomNetworkConnection 사용
   │
   ├─ SetNetworkConnectionClass<T>() : NetworkServer / NetworkClient 양쪽에
   │     동일한 Connection 타입을 지정해야 상태가 대칭적으로 유지됨
   │
CustomNetworkConnection (NetworkConnection)
   ├─ SendBytes / SendWriter 오버라이드
   │     └─ ReadyForQueueing == true인 동안, ReliableSequenced 채널 메시지를
   │        즉시 전송하지 않고 MsgQueue(List<MessageQueue>)에 적재
   ├─ TransportSend 오버라이드
   │     └─ 큐잉 모드에서는 실제 전송을 건너뛰고 성공한 것처럼 반환해,
   │        엔진이 내부적으로 재전송 로직을 타지 않도록 방지
   ├─ FlushBuffer() (코루틴)
   │     └─ 매 프레임 종료 시점(EndOfFrame)마다 전송 가능한 여유(버퍼 상태)를
   │        확인하고, 확보된 만큼만 큐에서 꺼내 실제로 전송
   └─ MessageQueue (IDisposable)
         └─ 큐에 적재된 메시지 한 건을 표현하는 값 객체.
            원본 byte[]를 즉시 복사해 보관(호출부의 버퍼 재사용에도 안전)

SteamNetworkConnection (CustomNetworkConnection)
   ├─ ForceInitialize() : UNet 내부 초기화 흐름을 거치지 않고
   │     "localhost" 대상 커넥션 슬롯을 직접 초기화 (Steam 세션은 UNet이 인지하지 못함)
   ├─ TransportSend 오버라이드 : Steamworks SendP2PPacket으로 실제 송신을 수행하며,
   │     자기 자신에게 보내는 경우는 TransportReceive로 즉시 루프백 처리
   └─ CloseP2PSession() : 잔여 큐가 모두 비워질 때까지 대기한 뒤 세션 종료

SteamNetworkClient (NetworkClient)
   └─ Connect() : NetworkTransport 연결 절차 없이 ConnectState를 강제로
         Connected로 세팅해 UNet이 "이미 연결됨"으로 인식하도록 유도

SteamNetworkManager (MonoBehaviour)
   └─ Update() : 매 프레임 Steamworks P2P 패킷 큐를 폴링하여,
         수신한 바이트를 해당 NetworkConnection.TransportReceive로 주입
         (UDP 소켓 이벤트 루프를 대체하는 역할)

UNETServerController
   ├─ OnP2PSessionRequested() : Steam P2P 접속 요청 콜백을 받아 수락 여부 결정
   └─ CreateP2PConnectionWithPeer() : 수락 후 SteamNetworkConnection을 생성해
         NetworkServer.AddExternalConnection()으로 UNet 커넥션 목록에 편입
```

## 핵심 발췌 코드

### 1. Connection 타입 스왑 — 전송 방식에 따라 다른 클래스 사용

```csharp
public override NetworkClient StartHost()
{
    var localClient = base.StartHost();
    if (localClient == null) return null;

    SteamNetworkManager.IsUsingP2P = Settings.CurrentData.P2PHostEnabled;

    if (SteamNetworkManager.IsUsingP2P)
    {
        // Steam P2P 세션(SteamNetworkingSockets)을 사용하는 커넥션으로 교체
        NetworkServer.SetNetworkConnectionClass<SteamNetworkConnection>();
        localClient.SetNetworkConnectionClass<SteamNetworkConnection>();
    }
    else
    {
        // 일반 UDP 기반 커넥션(스폰 트래픽 큐잉 포함)
        NetworkServer.SetNetworkConnectionClass<CustomNetworkConnection>();
        localClient.SetNetworkConnectionClass<CustomNetworkConnection>();
    }
    return localClient;
}
```

`NetworkServer`와 `NetworkClient` 양쪽에 **동일한 Connection 클래스**를 지정하는 것이 핵심입니다. UNet은 로컬 호스트(서버와 클라이언트를 동시에 실행하는 상황)에서 두 인스턴스가 동일한 시맨틱으로 동작해야 하는데, 한쪽만 교체할 경우 큐잉·플러시 상태 간 정합성이 깨지면서 스폰 메시지의 처리 순서에 왜곡이 발생합니다.

### 2. 메시지 큐잉 — 소켓으로 직행하지 않고 자체 큐에 적재

```csharp
public override bool SendBytes(byte[] bytes, int numBytes, int channelId)
{
    if (UseAdditionalQueue)
    {
        if (ReadyForQueueing && CustomChannels.ReliableSequenced == channelId)
        {
            if (MaxMsgQueueSize < MsgQueue.Count)
            {
                // 큐가 지나치게 누적되면(설정 오류 또는 치명적 지연) 연결을 종료해
                // 메모리를 무한정 점유하는 상황을 방지
                Disconnect();
                return false;
            }
            MsgQueue.Add(new MessageQueue(this, bytes, numBytes, channelId));
        }
        return true; // 엔진에는 "성공"으로 응답하고, 실제 전송은 큐가 전담
    }
    else if (MsgQueue.Count > 0)
    {
        FlushMessage(MsgQueue.Count);
    }
    return base.SendBytes(bytes, numBytes, channelId);
}
```

큐잉 대상은 `ReliableSequenced` 채널로 향하는 메시지로 한정했습니다. 오브젝트 스폰·디스폰처럼 순서 보장이 요구되는 대용량 메시지가 집중되는 채널이 바로 이 채널이기 때문입니다. 나머지 채널(예: `StateUpdate`, `Unreliable`)은 그대로 흘려보내 게임플레이에 지연이 발생하지 않도록 했습니다.

### 3. `TransportSend` 오버라이드 — 큐잉 중엔 실전송 자체를 억제

```csharp
public override bool TransportSend(byte[] bytes, int numBytes, int channelId, out byte error)
{
    if (UseAdditionalQueue)
    {
        if (CustomChannels.ReliableSequenced != channelId)
        {
            error = 0;
            return true; // 실제 전송 없이 성공 처리
        }
    }
    return base.TransportSend(bytes, numBytes, channelId, out error);
}
```

`SendBytes`만 오버라이드하는 것으로는 충분하지 않았습니다. UNet 내부에서 재전송(reliable retry)이나 ACK 처리 과정에서 `TransportSend`가 별도 경로로 호출될 수 있기 때문에, 이 지점에서도 동일한 채널 필터링을 적용해야 큐잉 로직과 실제 전송이 이중으로 충돌하지 않습니다.

### 4. 프레임 단위 플러시 — 버퍼 여유를 확인하고 그만큼만 내보냄

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

        // 전송 계층(UDP 소켓 큐 또는 Steam P2P 세션 큐)의 잔여 용량을 확인
        if (!NetworkManagerOverride.IsAvailableMessageQueue(
                this, NetworkManagerOverride.ReservedMessageCount))
        {
            continue; // 여유가 없으면 다음 프레임까지 대기
        }

        if (!FlushMessage(NetworkManagerOverride.ReservedMessageCount))
        {
            FlushBufferCoroutine = null;
            yield break; // 전송 실패 시 연결 종료 처리로 위임
        }

        yield return Yielders.EndOfFrame;
    }
    FlushBufferCoroutine = null;
}
```

`IsAvailableMessageQueue`는 두 갈래로 분기됩니다 (원본에서 발췌):

```csharp
if (SteamNetworkManager.IsUsingP2P)
{
    // Steam P2P: 세션별로 큐잉된 패킷 수를 Steamworks API로 직접 조회
    P2PSessionState_t state;
    SteamNetworking.GetP2PSessionState(steamConn.steamId, out state);
    numBufferedMsgs += state.m_nPacketsQueuedForSend;
}
else
{
    // UNet 기본 전송: NetworkTransport의 아웃고잉 큐 크기 조회
    numBufferedMsgs = NetworkTransport.GetOutgoingMessageQueueSize(
        _localClient.connection.connectionId, out error);
}
```

"버퍼 여유를 확인한다"는 동일한 인터페이스 이면에서, 전송 수단에 따라 완전히 다른 API(Steamworks vs UNet Transport)를 호출하도록 분기했습니다. 상위의 `FlushBuffer` 코루틴 로직이 이 차이를 인지할 필요가 없도록 캡슐화한 것이 설계의 핵심입니다.

### 5. `MessageQueue` — 전송 대기 메시지의 값 객체

```csharp
public MessageQueue(CustomNetworkConnection conn, byte[] bytes, int numBytes, int channelId)
{
    this.conn = conn;
    // 원본 버퍼는 호출부에서 재사용될 수 있으므로 즉시 깊은 복사
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

UNet의 `NetworkWriter`/버퍼는 호출 직후 재사용되는 경우가 많아, 큐에 적재하는 시점에 반드시 `Buffer.BlockCopy`로 복사해 두어야 합니다. 이를 누락하면 큐가 플러시되는 시점엔 이미 다른 메시지로 덮어써진 버퍼를 전송하게 되는 결함으로 이어집니다. `IDisposable`을 구현하여 연결 종료 시 남은 큐 항목의 버퍼 참조를 명시적으로 해제합니다.

## Steam P2P 연동 지점

### 접속 파라미터 변환

`NetworkManagerHudOverride`는 Steam의 Rich Presence / Join 초대 콜백을 수신하여 UNet 접속 파라미터로 변환하는 어댑터 역할을 수행합니다.

```csharp
// "+connect steam.<steamid64>:<port>" 형태를 파싱해
// IP 기반 접속인지 Steam P2P 접속인지 판별
int idx = serverIp.IndexOf("steam.");
if (idx != -1)
{
    serverIp = serverIp.Substring("steam.".Length);
    SteamNetworkManager.IsUsingP2P = true;
    ...
}
```

친구 초대 수락(`GameRichPresenceJoinRequested_t`) 시점에도 커맨드라인과 동일한 `+connect` 문법을 재사용함으로써, "커맨드라인 인자를 통한 직접 접속"과 "Steam 오버레이를 통한 접속"이라는 두 진입 경로가 동일한 파싱·연결 로직을 공유하도록 통일했습니다.

인증 흐름은 Steamworks의 `InitiateGameConnection`으로 Auth Blob을 발급받은 뒤, 이를 UNet 커스텀 메시지(`ValidateAuthBlobMessage`)로 서버에 전달하고, 서버가 `SteamGameServer.SendUserConnectAndAuthenticate`로 검증하는 — Steamworks가 요구하는 표준 서버 인증 플로우를 그대로 준수합니다.

### UNet의 전송 계층을 Steam 소켓으로 대체하는 우회 설계

UNet의 HLAPI는 본래 자체 `NetworkTransport`(LLAPI, UDP 기반)를 전제로 설계되어 있어, Steamworks의 P2P 패킷 송수신 API와는 직접적인 접점이 없습니다. 이 프로젝트의 핵심은 **UNet에게는 정상적인 연결이 성립된 것처럼 상태를 구성해 주고, 실제 바이트 전송은 Steamworks P2P API가 전담하도록 계층을 우회시키는 것**입니다. 이를 위해 세 개의 레이어가 유기적으로 동작합니다.

**1) 연결 상태의 수동 구성 — `SteamNetworkClient.Connect()`**

```csharp
public void Connect()
{
    // 실제 소켓 연결 없이 UNet의 ConnectState를 강제로 "Connected"로 전환.
    // 이 상태가 되어야 내부적으로 초기화가 진행되고 TransportSend를 통한 데이터 송수신 경로가 열림
    m_AsyncConnect = ConnectState.Connected;

    // Connected 메시지를 직접 호출해 UNet 상위 로직(핸들러 등록 등)이 정상적으로 진행되도록 트리거
    connection.InvokeHandlerNoData(MsgType.Connect);
}
```

일반적인 UNet 클라이언트는 `NetworkTransport.Connect()`가 성공해야 `ConnectState.Connected`로 전환됩니다. 여기서는 실제 소켓 연결 절차 없이 상태만 직접 구성함으로써, UNet이 연결이 이미 성사된 것으로 판단한 채 나머지 로직(핸들러 디스패치, Ready 처리 등)을 정상적으로 이어가도록 유도합니다.

**2) 커넥션 초기화의 수동 트리거 — `SteamNetworkConnection.ForceInitialize()`**

```csharp
public void ForceInitialize()
{
    int id = GameManager.IsServer ? 1 : 0;
    foreach (var connection in NetworkServer.connections)
    {
        if (connection.isConnected) id++;
        else break;
    }
    // 실제 소켓을 개설하지 않고 "localhost"를 대상으로 UNet 내부 커넥션 슬롯만 초기화
    Initialize("localhost", 0, id, SteamNetworkManager.hostTopology);
    RemoveExternalConnection();
}
```

일반적으로 `NetworkConnection.Initialize`는 `NetworkServer.AddExternalConnection` 등 UNet 내부 흐름 속에서 자동으로 호출되지만, Steam P2P 세션은 UNet이 인지하지 못하는 별도 채널로 생성되기 때문에 이 초기화 절차를 코드에서 직접 호출해 주어야 합니다.

**3) 실제 송신 — `TransportSend`를 오버라이드하여 Steamworks로 위임**

```csharp
public override bool TransportSend(byte[] bytes, int numBytes, int channelId, out byte error)
{
    if (!SteamNetworkManager.IsUsingP2P || !steamId.IsValid())
        return base.TransportSend(bytes, numBytes, channelId, out error);

    if (steamId.m_SteamID == SteamUser.GetSteamID().m_SteamID)
    {
        // 자기 자신(호스트가 곧 플레이어인 경우)에게 보내는 경우
        // 네트워크를 경유하지 않고 곧바로 수신 경로로 전달(로컬 루프백)
        TransportReceive(bytes, numBytes, channelId);
        error = 0;
        return true;
    }

    // UNet의 QoS 채널 설정(Reliable/Unreliable 등)을 Steamworks의 전송 타입으로 매핑
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
        // Reliable 전송이 실패하면 복구 불가능한 연결로 간주하고 정리
        UNETServerController.Instance.RemoveConnection(steamId);
        error = 1;
        return false;
    }
    error = 0;
    return true;
}
```

UNet의 채널 개념(`QosType.Reliable`, `ReliableSequenced`, `Unreliable` 등)을 Steamworks의 두 가지 전송 모드(`k_EP2PSendReliable` / `k_EP2PSendUnreliable`)로 매핑하는 것이 핵심입니다. 이를 통해 상위 게임 로직은 UNet 채널 API만을 사용하고, 그 하위에서 UDP 소켓을 쓰는지 Steam P2P를 쓰는지는 의식하지 않아도 되는 구조가 됩니다.

**4) 실제 수신 — 매 프레임 폴링으로 Steam 패킷을 읽어 UNet에 주입**

`SteamNetworkManager.Update()`는 UNet의 이벤트 루프와 무관하게, 매 프레임 Steamworks의 P2P 패킷 큐를 직접 폴링합니다.

```csharp
for (int chan = 0; chan < channels; chan++)
{
    while (SteamNetworking.IsP2PPacketAvailable(out packetSize, chan))
    {
        CSteamID senderId;
        if (SteamNetworking.ReadP2PPacket(_buffer, packetSize, out packetSize, out senderId, chan))
        {
            NetworkConnection conn = UNETServerController.IsHostingServer()
                ? UNETServerController.GetClient(senderId)   // 서버: 발신자의 UNet 커넥션을 조회
                : (myClient == null ? null : myClient.connection); // 클라이언트: 서버 커넥션 고정

            if (conn != null && packetSize > 0)
            {
                // UNet의 정상적인 수신 파이프라인(핸들러 디스패치 등)에 그대로 편입
                conn.TransportReceive(_buffer, Convert.ToInt32(packetSize), chan);
            }
        }
    }
}
```

정리하면, **송신 시엔 `TransportSend`를 재정의하여 Steamworks로 전달하고, 수신 시엔 Steamworks 큐를 폴링하여 `TransportReceive`로 주입하는** 구조입니다. UNet 입장에서는 여전히 자신의 표준 송수신 파이프라인(직렬화, 핸들러 디스패치, 채널 QoS)만을 인식할 뿐, 물리적 전송 계층이 UDP 소켓이 아닌 Steamworks P2P라는 사실은 완전히 추상화되어 가려져 있습니다. 프레임 폴링 중 대량의 패킷이 한 번에 몰릴 경우를 대비해 처리 개수를 일정 수준(`recvCount > 512`)에서 제한함으로써, 단일 프레임이 과도하게 길어지는 상황도 방지합니다.

### 세션 생성/해제 흐름

**서버 측 — Steam이 P2P 접속 요청을 콜백으로 통지하면 수락 여부를 판단**

```csharp
void OnP2PSessionRequested(P2PSessionRequest_t pCallback)
{
    var member = pCallback.m_steamIDRemote;
    if (NetworkServer.active)
    {
        SteamNetworking.AcceptP2PSessionWithUser(member);
        CreateP2PConnectionWithPeer(member); // 수락 즉시 UNet 커넥션 슬롯도 함께 생성
    }
}
```

`P2PSessionRequest_t`는 Steamworks가 "해당 SteamID가 P2P 세션 개설을 요청했다"고 통지하는 콜백입니다. 여기서 `AcceptP2PSessionWithUser`를 호출하지 않으면 상대방이 전송한 패킷은 전부 드롭됩니다. 수락과 동시에 `SteamNetworkConnection`을 새로 생성하고 `ForceInitialize()` → `NetworkServer.AddExternalConnection()`을 거쳐 UNet의 커넥션 리스트에 편입시킴으로써, 이후로는 일반 UNet 클라이언트와 동일하게 취급되도록 합니다.

**연결 종료 — Steam 세션과 UNet 커넥션을 함께 정리**

```csharp
public void CloseP2PSession()
{
    if (GameManager.IsServer)
    {
        Reset(); // 서버는 즉시 정리
    }
    else
    {
        // 클라이언트는 서버에 연결 종료 의사를 우선 통지한 뒤,
        var msg = new DisconnectP2PMessage { SteamId = SteamUser.GetSteamID().m_SteamID };
        SendByChannel(CustomMsgs.RequestDisconnectP2P, msg, CustomChannels.ReliableSequenced);
        // 큐에 남은 패킷이 실제로 모두 전송될 때까지 대기한 뒤 세션을 닫음
        SteamNetworkManager.Instance.FlushLocalP2PPacket(this);
    }
}
```

클라이언트가 먼저 연결을 종료할 때는 연결 종료 메시지를 전송한 직후 바로 세션을 닫지 않습니다. `FlushLocalP2PPacket`이 구동하는 코루틴이 `SteamNetworking.GetP2PSessionState`로 `m_nPacketsQueuedForSend`가 0이 될 때까지(전송 대기 중인 패킷이 모두 소진될 때까지) 매 프레임 대기한 뒤에야 `Reset()`으로 세션을 종료합니다. 그렇지 않으면 마지막 메시지(연결 종료 통지 등)가 실제로 전송되기 전에 세션이 끊길 위험이 있기 때문입니다.

또한 매 틱마다 `ValidateSteamConnection()`을 통해 Steamworks가 보고하는 세션 상태(`m_bConnectionActive`)를 UNet의 연결 상태와 별도로 검증합니다. Steam P2P는 NAT 환경 특성상 상대방이 응답 없이 조용히 이탈하는 경우(방화벽, 네트워크 전환 등)가 UDP 다이렉트 연결보다 빈번하여, UNet의 타임아웃 메커니즘만으로는 감지가 지연될 수 있기 때문입니다.

### 호스트가 곧 플레이어인 경우의 특수 처리

리슨 서버(호스트가 곧 첫 번째 플레이어) 구조이기 때문에, 서버 프로세스 내부에 "로컬 플레이어용 커넥션"과 "원격 플레이어용 커넥션"이 공존합니다. `UNETServerController.GetSteamIDForConnection`은 이 두 경우를 명시적으로 구분합니다.

```csharp
if (NetworkServer.connections.Count >= 1 && conn == NetworkServer.connections[0])
    return SteamUser.GetSteamID(); // 서버 프로세스 자신의 SteamID

if (myClient != null && conn == myClient.connection)
    return SteamUser.GetSteamID(); // 로컬 클라이언트 관점에서의 자신의 SteamID
```

또한 `TransportSend`에서 수신자의 SteamID가 자기 자신과 일치하는 경우(위 3번 항목의 self-send 분기), Steamworks P2P API를 경유하지 않고 곧바로 `TransportReceive`로 루프백 처리합니다. 로컬 호스트-플레이어 간 통신까지 실제 네트워크 스택(Steam relay 서버 등)을 경유하게 하면 불필요한 지연과 세션 자원 낭비가 발생하기 때문입니다.

## 설계에서 고려한 트레이드오프

| 고려사항 | 선택 | 이유 |
|---|---|---|
| 엔진 포크 vs 오버라이드 | `NetworkConnection`/`NetworkManager` 상속을 통한 오버라이드 | UNet 소스 자체를 수정하면 이후 Unity 업데이트·유지보수 비용이 커짐. 가상 메서드 오버라이드 범위 내에서 해결 |
| 큐잉 대상 채널 | `ReliableSequenced`로 한정 | 전체 채널을 큐잉하면 실시간성이 중요한 상태 동기화까지 지연됨. 스폰 폭주가 발생하는 채널만 선별 |
| 큐 오버플로우 처리 | 임계값 초과 시 즉시 `Disconnect()` | 설정값(`MaxMsgQueueSize`)을 초과하는 실패는 정상적으로 복구 불가능한 시나리오로 판단, 좀비 연결 대신 명시적 종료를 채택 |
| P2P/비-P2P 버퍼 확인 방식 통일 | 인터페이스는 통일하고 내부 구현만 분기 | 상위 로직(코루틴)이 전송 수단을 인지할 필요가 없도록 하여 Steam SDK 의존성을 격리 |

## 이 코드에서 보여주고자 한 것

- 소스 수정이 불가능한 서드파티 네트워킹 프레임워크(UNet)에, **가상 메서드 오버라이드만으로 원하는 유량 제어 계층을 이식**한 설계
- 서로 다른 전송 매체(UDP 소켓 / Steam P2P 세션)를 **동일한 상위 인터페이스**로 추상화하여, 게임 로직이 전송 방식을 의식하지 않도록 만든 구조
- UNet의 연결 상태 머신을 정밀하게 파악하고, 그 위에 **Steam P2P 전송 계층을 이식**하여 엔진이 표준 UDP 연결로 인식하도록 우회시킨 저수준 설계
- 코루틴 기반의 프레임 단위 플러시로, 폴링이나 별도 스레드 없이 Unity의 메인 루프에 자연스럽게 편입되는 유량 제어

---

# 부록: 대형 월드 접속 스트리밍 & 데디케이티드 서버 관리자 기능

위 섹션이 "이미 접속한 연결에서 스폰 트래픽을 어떻게 유량 제어할 것인가"를 다뤘다면, 아래는 **"10,000개 이상의 네트워크 오브젝트를 가진 대형 월드에 클라이언트가 처음 접속할 때 발생하는 문제들"** 과 **"데디케이티드 서버 운영에 필요한 관리자 기능"** 을 다루고 있습니다.

## 다루는 문제 5가지

1. **버퍼 오버플로우** — 접속 시점에 10,000개 이상의 NetworkIdentity/청크 데이터를 한 번에 전송하면 전송 계층의 송신 큐를 초과함
2. **프로그레스 로딩 스크린** — 접속 중 진행률을 클라이언트에 알려줄 방법이 없으면 사용자는 멈춘 것으로 오인함
3. **플레이어가 지면 아래로 떨어지는 문제** — 청크를 임의 순서로 스트리밍하면 플레이어 스폰 위치 주변 지형이 늦게 도착해 중력 계산이 먼저 시작됨
4. **데디케이티드 서버 관리자 기능** — 비밀번호, 킥/밴, 서버 이름, 원격 명령, 커맨드라인 파라미터
5. **UNet 전송 계층을 Steam P2P로 대체** — 소켓이 아닌 Steamworks P2P 패킷 API로 실제 송수신을 위임하면서, UNet 상위 로직(직렬화, 채널 QoS, 핸들러 디스패치)은 그대로 재사용

---

## 1. 순차적 수신 — 오브젝트 스폰 큐 (`SpawnQueue.cs`)

UNet은 `NetworkServer`의 송신 주기마다 `ObjectSpawnMessage`/`ObjectSpawnSceneMessage`를 배칭하지만, 씬에 존재하는 오브젝트 수가 수천~수만 개 규모면 **단일 프레임 안에서 `NetworkServer.Spawn()`을 반복 호출하는 것 자체**가 여전히 스파이크를 만듭니다. 증상은 `NetworkTransport` 레벨의 "no free events for message in the pool" 에러, 접속 직후 타임아웃, 또는 오브젝트 누락으로 나타납니다.

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

한 프레임에 스폰할 오브젝트 수(`objectsPerFrame`)를 상한선으로 두고, 나머지는 다음 프레임으로 넘깁니다. 큐가 비워질 때까지 코루틴이 자기 자신을 유지하다가 종료되는 구조라 별도의 폴링 루프가 필요 없습니다.

---

## 2. 청크 스트리밍 — 거리 기반 우선순위 (`ChunkStreamManager.cs`)

가장 중요한 부분입니다. 단순히 순차 전송만 하면 버퍼 문제는 해결되지만, **어떤 순서로 보내느냐**에 따라 "지면 아래로 떨어지는 버그"가 남아있을 수 있습니다. 플레이어 스폰 위치에서 먼 청크가 먼저 도착하고 발밑 청크가 나중에 오면, 클라이언트의 중력/충돌 계산이 아직 존재하지 않는 지형 위에서 시작됩니다.

### 우선순위 정렬

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

    // 우선순위 링 안쪽을 항상 먼저, 그 다음은 거리순
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

단순 거리순 정렬이 아니라, `priorityRadius` 안의 청크(플레이어를 즉시 둘러싼 지형)를 **항상 먼저** 전부 보내고, 그 다음에야 나머지를 거리순으로 흘려보냅니다. 거리값이 우연히 비슷해서 발밑 청크와 먼 청크의 정렬 순서가 뒤섞이는 경우를 방지하기 위함입니다.

### 프레임 단위 전송 + 진행률 보고

UNet의 `NetworkConnection.SendWriter()` API를 그대로 사용합니다. 이렇게 하면 이 코루틴은 연결이 일반 소켓 기반인지, 5번 섹션의 `SteamP2PConnection`인지 전혀 알 필요가 없습니다 — `NetworkConnection` 기본 클래스만 보고 작동합니다.

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

진행률 메시지(`ChunkStreamProgress`)는 매 청크마다 보내지 않고 8개 단위로 배칭해서 `Unreliable` 채널로 보냅니다. 이렇게 하면 진행률 알림 자체가 우리가 보호하려는 reliable 채널에 추가 부하를 주지 않습니다. `NetworkFlowGate.HasCapacity`는 연결 타입에 따라 분기하도록 준비되어 있습니다 — `SteamP2PConnection`이면 `GetP2PSessionState`로, 일반 소켓이면 전송 계층의 큐 조회 API로 확장할 수 있는 지점입니다.

`NetworkFlowGate.HasCapacity`는 연결 타입에 따라 분기하는 지점입니다 — 일반 소켓 연결이면 UNet의 `NetworkTransport.GetOutgoingMessageQueueSize`로 실제 큐 깊이를 조회할 수 있지만, `SteamP2PConnection`(5번 섹션)은 소켓이 아니므로 대신 `SteamNetworking.GetP2PSessionState`의 `m_nPacketsQueuedForSend`를 확인해야 합니다. 이 샘플에서는 두 경우 모두 `chunksPerTick`/`maxChunksPerFrame`의 프레임당 상한만으로 충분하다고 보고 `true`를 반환하도록 단순화했습니다.

---

## 3. 로딩 스크린 + 낙하 버그 수정 (`JoinLoadingScreen.cs`)

로딩 UI는 부수 효과일 뿐이고, **실제로 낙하 버그를 고치는 것은 진행 중에 플레이어의 물리 시뮬레이션을 멈춰두는 것**입니다.

```csharp
private void OnStreamBegin(ChunkStreamBeginMessage msg)
{
    _totalChunks = msg.TotalChunks;
    _receivedChunks = 0;
    SetVisible(true);
    SetPlayerSimulationEnabled(false);   // ← 여기가 핵심
    UpdateBar(0, _totalChunks);
}

private void OnStreamComplete(ChunkStreamCompleteMessage msg)
{
    UpdateBar(_totalChunks, _totalChunks);
    SetPlayerSimulationEnabled(true);    // 스트리밍 완료 후에만 재개
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

우선순위 스트리밍(2번)이 "발밑 지형을 먼저 보낸다"면, 이 부분은 "그 지형이 다 도착하기 전까지는 애초에 중력 계산을 아예 하지 않는다"는 이중 안전장치입니다. 네트워크 지연으로 우선순위 청크 전송이 예상보다 늦어지는 경우까지 커버합니다.

---

## 4. 데디케이티드 서버 관리자 기능

### 4-1. 커맨드라인 파라미터 파싱 (`DedicatedServerConfig.cs`)

`-file start <저장이름>`, `-logFile "경로"`, `-settings Key Value Key Value ...` 형태의 인자를 파싱합니다. 

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

`-settings` 블록은 다음 플래그가 나오기 전까지(`-`로 시작하는 토큰을 만나기 전까지) Key/Value 쌍을 계속 소비합니다:

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
            // ... 나머지 키는 코드 참고
            default:
                UnityEngine.Debug.LogWarning($"[DedicatedServerConfig] Unknown setting '{key}', ignoring.");
                break;
        }
        i += 2;
    }
    return i;
}
```

알 수 없는 키는 서버를 크래시시키지 않고 경고만 남긴 뒤 넘어갑니다 — 오타 하나로 전체 서버 기동이 실패하는 것을 방지합니다.

### 4-2. 비밀번호 게이트 / 킥 / 밴 (`DedicatedServerAdmin.cs`)

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

`TryAuthenticateJoin`은 `NetworkConnection` 기본 타입만 받으므로, 5번 섹션에서 만든 `SteamP2PServerController.ShouldAcceptConnection()`에 이 메서드를 그대로 연결해 소켓 접속과 Steam P2P 접속 모두에 동일한 인증 로직을 적용할 수 있습니다. 밴 목록은 connectionId가 아니라 안정적인 식별자(스팀ID, IP 등 프로젝트에 맞는 값)로 관리합니다. connectionId는 재접속할 때마다 바뀌므로 밴 키로 쓸 수 없습니다.

### 4-3. 원격 명령 실행 (`serverrun` 방식)

Stationeers는 클라이언트 콘솔에서 `serverrun <명령>`을 입력하면, 서버와 클라이언트에 동일하게 설정된 `ServerAuthSecret`으로 인증 후 서버에서 명령을 실행하는 방식을 씁니다. 이 패턴을 일반화했습니다:

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

## 데디케이티드 서버 파라미터 전체 목록

### 최상위 플래그

| 플래그 | 값 | 설명 |
|---|---|---|
| `-file start` | `<stationname> [worldid] [difficulty] [startcondition] [startlocation]` | 지정한 저장(station)을 불러오거나, 없으면 새 월드를 생성. `stationname`만 필수, 나머지는 선택이지만 하나를 넣으려면 그 앞의 모든 선택 인자도 함께 넣어야 함 |
| `-logFile` | `"path"` | `output_log.txt` 대신 사용할 커스텀 로그 파일 경로 |
| `-settings` | 아래 표 | 서버 설정값 일괄 지정. 예: `-settings ServerName "MyServer"` |

### `-settings` 키 목록

| 키 | 값 | 설명 |
|---|---|---|
| `ServerVisible` | `true` / `false` | 인게임 서버 목록에 노출 여부 |
| `GamePort` | `27016` 등 | 플레이어 접속 포트 |
| `UpdatePort` | `27015` 등 | 스팀 업데이트 포트 |
| `UPNPEnabled` | `true` / `false` | UPnP(자동 포트포워딩) 사용 여부, 라우터 지원 필요 |
| `ServerName` | 문자열 | 서버 이름 |
| `ServerPassword` | 문자열 | 서버 접속 비밀번호 |
| `ServerAuthSecret` | 문자열 | 관리자 원격 명령(`serverrun`) 인증용 시크릿 |
| `ServerMaxPlayers` | `1`–`20` | 최대 플레이어 슬롯 (20 초과 비권장) |
| `AutoSave` | `true` / `false` | 자동 저장 여부 |
| `SaveInterval` | `300` 등 (초) | 자동 저장 주기, 60초 미만 비권장 |
| `AutoPauseServer` | `true` / `false` | 접속자가 없을 때 자동 일시정지 여부 |
| `UseSteamP2P` | `true` / `false` | Steam P2P 접속 허용 여부 (데디케이티드 서버에서는 비활성 권장) |
| `StartLocalHost` | `true` / `false` | 접속 가능하려면 반드시 필요한 내부 값, 변경 비권장 |
| `LocalIpAddress` | `0.0.0.0` 등 | (Linux) 서버가 바인딩할 네트워크 인터페이스 |

---

## 부록 아키텍처 요약

```
DedicatedServerConfig.ParseFromArgs(args)
   └─ -file / -logFile / -settings 파싱

DedicatedServerAdmin
   ├─ TryAuthenticateJoin()   : 비밀번호 · 밴 · 정원 체크  ← SteamP2PServerController.ShouldAcceptConnection()에 연결 가능
   ├─ Kick() / Ban()          : 안정적 식별자 기반 강제 퇴장
   └─ TryExecuteRemoteCommand(): ServerAuthSecret 인증 후 명령 디스패치

SpawnQueue
   └─ 씬 오브젝트 일괄 스폰을 프레임당 상한으로 분산

ChunkStreamManager
   ├─ BuildPriorityOrder()    : 스폰 위치 우선순위 링 + 거리순 정렬
   └─ StreamChunksRoutine()   : 프레임당 상한 + 진행률 브로드캐스트 (conn.SendWriter 경유 → SteamP2PConnection.TransportSend)

JoinLoadingScreen (클라이언트)
   ├─ ChunkStreamBeginMessage    → 로딩 화면 표시 + 플레이어 시뮬레이션 정지
   ├─ ChunkStreamProgressMessage → 프로그레스 바 갱신
   └─ ChunkStreamCompleteMessage → 시뮬레이션 재개 + 로딩 화면 숨김

```