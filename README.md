# Unity Portfolio (Steam & Photon)

Unity 기반 멀티플레이어 게임 개발 과정에서 다룬 네트워킹 관련 작업들을 폴더별로 정리했습니다. 레거시 엔진 네트워킹(UNet)의 유량 제어 문제 해결, Steamworks SDK 통합, 그리고 Photon PUN을 활용한 실제 플레이 가능한 프로토타입까지 — 서로 다른 계층과 목적의 작업을 포함합니다.

## 폴더 구성

### 📁 [Networking](./Networking)

Unity의 레거시 네트워킹 프레임워크인 **UNet(HLAPI)** 을 확장한 커스텀 네트워킹 레이어입니다. 데디케이티드 서버(UDP)와 **Steam P2P** 두 가지 전송 방식을 동일한 상위 인터페이스로 추상화하여 하나의 게임 로직이 두 전송 수단을 모두 지원하도록 설계했습니다.

- 초기 접속 시 대량으로 몰리는 오브젝트 스폰 트래픽을 프레임 단위로 유량 제어(flow control)하는 자체 메시지 큐 구현
- `NetworkConnection`/`NetworkManager` 가상 메서드 오버라이드만으로 엔진 소스 수정 없이 문제 해결
- UNet의 연결 상태 머신을 분석하여, UNet이 표준 UDP 연결로 인식하도록 Steam P2P 전송 계층을 이식하는 저수준 설계
- (부록) 10,000개 이상의 오브젝트를 가진 대형 월드 접속 시의 청크 스트리밍·낙하 버그 해결, 데디케이티드 서버 관리자 기능(비밀번호, 킥/밴, 원격 명령) 설계도 함께 포함

### 📁 [Steam](./Steam)

Unity(UNET) 기반 게임에 **Steamworks SDK(Steamworks.NET)** 를 통합한 모듈입니다. Stats/Achievements, Leaderboard, Matchmaking/Server Browser, Workshop(UGC), P2P Networking, Friends/Avatar 등 Steamworks의 주요 기능을 지원합니다.

- 성격이 다른 세 종류의 비동기 작업(Steam API 콜백, Matchmaking 델리게이트, Raw UDP 소켓)을 `SteamAsyncTask`라는 공통 추상 클래스로 캡슐화
- 단일 스레드 직렬 실행 큐(`SteamAsyncTaskManager`)로 스레드 안전성과 요청 순서 보장을 동시에 확보
- 공통 타임아웃·생명주기 관리와 델리게이트 기반 결과 통보로, 신규 Steam 기능 추가 시 확장이 쉬운 구조

### 📁 [PhotonPUNPrototype](./PhotonPUNPrototype)

**Photon PUN(Photon Unity Networking) Lobby 샘플**을 기반으로 **Fall Guys**를 벤치마킹하여 구현한 멀티플레이어 프로토타입입니다. 리전 선택 → 룸 생성/참가 → 게임 시작으로 이어지는 매치메이킹 흐름과, 더블 점프·체크포인트 리스폰 등 캐주얼 레이싱 장르의 핵심 게임플레이를 실제 플레이 가능한 빌드로 구현했습니다.

- 실행 화면 스크린샷과 다운로드 가능한 Windows 빌드 포함
- 30명 이상의 동시 접속을 요구하는 실서비스 규모에 맞춰, PUN 검증 이후 **Photon Quantum**으로 전환한 설계 판단 과정을 함께 정리

## 읽는 순서 제안

기술적 난이도와 문제 해결 과정을 보고 싶다면 **Networking → Steam** 순으로, 실제 플레이 가능한 결과물을 먼저 보고 싶다면 **PhotonPUNPrototype**부터 확인하는 것을 권장합니다.
