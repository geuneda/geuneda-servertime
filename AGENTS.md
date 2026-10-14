# Geuneda.ServerTime - AI 에이전트 가이드

> **관련 파일**: `CLAUDE.md`는 이 파일을 감쌉니다 — `CLAUDE.md`가 아니라 `AGENTS.md`를 편집하세요. `README.md`는 사용자 대상 문서입니다.

## 1. 패키지 개요
- **패키지**: `com.geuneda.servertime`
- **Unity**: 6000.3+ (services 와 같음)
- **의존성**: `com.geuneda.core`(Result, ModuleLogger, `IMonotonicClock`), `com.geuneda.services`(`ITimeManipulator`), `com.geuneda.http`(`HttpDateTimeSource`), `com.cysharp.unitask`. services 와 http 가 서로 의존하지 않도록 동기화를 별도 패키지로 뺐습니다(사용자 결정 2026-10-14)
- **출처**: Lucky Defense 의 Percent.Time(로드맵 2-2). NTP 먼저·HTTP 대체(사용자 결정), 단조 시계 기준점, `IsServerTime` 은 따랐습니다. 보완한 것: 왕복 지연 보정(레퍼런스는 NTP 전송 타임스탬프 하나만 쓰고 HTTP 는 보정 없음), 재동기화(레퍼런스는 한 번 성공하면 다시 맞추지 않음), 포그라운드 복귀 재동기화, 지수 백오프(레퍼런스는 5초 고정), 잠자기 포함 단조 시계(레퍼런스는 `Time.unscaledTime`), NTP 응답 검증(originate, 모드, 계층, 윤초 표시), DNS 포함 전체 제한 시간(레퍼런스는 수신에만), 백그라운드를 건넌 표본 버리기

## 2. 구조

| 파일 | 역할 |
|------|------|
| `Runtime/ServerTimeSync.cs` | 원천 순회, 표본 적용(`Apply`), 백그라운드 루프(`RunAsync`, `NextWait`, `WaitAsync`), 일시정지·복귀(`HandlePause`, 일시정지를 먼저 받은 복귀만 재동기화) |
| `Runtime/ServerTimeSyncLifecycle.cs` | `Start` 가 플레이 중에만 만드는 숨은 컴포넌트. `OnApplicationPause(false)` 를 넘김 |
| `Runtime/NtpTimeSource.cs` | DNS·소켓을 스레드 풀에서, 바깥 제한 시간과 취소, 주소별 재시도 |
| `Runtime/NtpPacket.cs` | 요청 생성, 응답 검증, 타임스탬프 변환(두 시대). 순수 함수라 단위 테스트 대상 |
| `Runtime/HttpDateTimeSource.cs` | `HEAD` + `Date` 헤더 |
| `Runtime/IServerTimeSource.cs`, `ServerTimeSample.cs`, `IServerTimeSync.cs`, `ServerTimeSyncOptions.cs`, `ServerTimeResultCodes.cs` | 계약·설정·결과 |

## 3. 규칙

- **결과 규약**: 정상 실패(네트워크, 제한 시간, 취소, 응답 검증, 긴 왕복)는 `Result` 코드, 오용(null, 빈 원천 목록, 잘못된 설정·주소·포트·제한 시간, UTC 가 아닌 표본)은 예외입니다. 공개 메서드는 async 가 아닌 겉 메서드에서 인자를 검사해 바로 던집니다.
- **예외를 숨기지 않음**: 호스트 `CLAUDE.md` 실패 처리 규칙대로 정상 실패 예외(소켓 오류, 취소)만 타입별로 결과로 바꿉니다. 원천이 던진 예외는 `SyncAsync` 가 그대로 던지고(`FetchAsync` 의 `finally` 에서 메인 스레드로 돌아온 뒤), 백그라운드 루프는 Error 로그를 남기고 그 실행을 멈춥니다. `catch (Exception)` 으로 다음 원천으로 넘기거나 재시도하지 마세요(2026-10-14 리뷰: HTTP 대체 성공이 원천 버그를 가리고, 고칠 수 없는 오류를 계속 재시도함).
- **같은 시계**: 표본의 `ReceivedAt` 과 적용 시점의 값은 시간 서비스와 같은 `IMonotonicClock` 이어야 합니다. `Apply` 는 `ServerUtc + (지금 - ReceivedAt)` 을 `SetServerTime` 에 넣습니다. 시계를 서로 다르게 만들 수 있는 API 를 추가하지 마세요.
- **긴 왕복 버리기**: 응답을 기다리는 동안 앱이 백그라운드에 있으면 왕복이 백그라운드 시간만큼 늘고 추정이 그 절반만큼 틀립니다. `MaxRoundTrip` 검사를 빼지 마세요. 맞추는 중에 포그라운드로 돌아오면 `_resumePending` 으로 끝난 뒤 한 번 더 맞춥니다.
- **스레드**: 엔진은 메인 스레드에서만 상태를 바꿉니다(`_wake`, `_resumePending`, `_lastSyncAt` 에 잠금 없음). 원천은 어느 스레드에서 끝나도 되므로 `FetchAsync` 끝에서 `UniTask.SwitchToMainThread()` 합니다. `NtpTimeSource` 는 시각을 정확히 재려고 보내기·받기와 `clock.Elapsed` 를 같은 스레드 풀 작업 안에서 하고, 메인 스레드에서 불렸으면 메인 스레드로 돌아옵니다.
- **NTP 제한 시간**: `Dns.GetHostAddresses` 는 취소할 수 없어, 스레드 풀 작업의 끝을 `UniTaskCompletionSource` 로 옮기고(`ObserveAsync`) 연결된 토큰이 취소되면 그 소스를 취소해 기다리지 않고 돌아옵니다. UniTask 의 `AttachExternalCancellation` 은 쓰지 마세요. 취소가 먼저 끝난 뒤 온 작업 예외를 `TrySetException`(false) 으로 버려 로그에도 남지 않습니다(UniTask 2.5.11 `AttachExternalCancellationSource.RunTask` 확인, 2026-10-14 리뷰). `ObserveAsync` 는 호출자가 아직 기다리면 예외를 그대로 전하고, 이미 돌아갔으면 Error 로그로 남깁니다. 이 완료 소스는 `TaskCompletionSource(RunContinuationsAsynchronously)` 여야 합니다. `UniTaskCompletionSource` 는 `OnCompleted` 가 잠금을 쥔 채 이미 끝난 continuation 을 바로 실행하고 `TrySignalCompletion` 은 상태를 먼저 바꾼 뒤 같은 잠금을 기다리므로, 취소 콜백(다른 스레드)과 await 등록이 겹치면 continuation 의 등록 해제(`CancellationTokenRegistration.Dispose` 가 실행 중인 콜백을 기다림)와 교착합니다(UniTask 2.5.11 소스 확인, 2026-10-14 리뷰 3회차). 정상 취소는 로그하지 않습니다. 작업이 취소 결과로 먼저 끝나는 경우와 취소가 먼저 오는 경우가 경쟁하므로, 실패했고 연결된 토큰이 취소됐으면 `ClassifyCancellation` 에서 호출자 취소(`Cancelled`)와 제한 시간(`Timeout`)을 한 번에 가립니다. 소켓 수신 시간 초과(`SocketError.TimedOut`)도 `Timeout` 입니다. 결과든 예외든 메인 스레드에서 부른 호출자에게는 `finally` 에서 메인 스레드로 돌려줍니다. 람다 안에서 `CancellationTokenSource.Token` 을 읽지 말고 밖에서 읽은 토큰을 넘깁니다(소스가 먼저 Dispose 될 수 있음). 소켓은 토큰 취소 때 Dispose 해서 막힌 `Receive` 를 풉니다.
- **NTP 검증**: 요청의 전송 타임스탬프에 암호학적 난수를 넣고 응답의 originate 와 비교합니다. 연결한 UDP 소켓이라 다른 주소의 데이터그램은 받지 않습니다. 검증을 느슨하게 하지 마세요. 시대 처리는 RFC 4330 3절(최상위 비트 0 이면 2036-02-07 06:28:16 UTC 기준)입니다.
- **HTTP Date**: 초 단위라 0.5초를 더합니다. 상태 코드는 보지 않습니다. 재시도 미들웨어가 있는 클라이언트는 왕복이 늘어 추정이 어긋난다는 점을 README 에 적어 두었습니다.
- **루프**: `Stop` 은 공유 상태(`_running`, `_lifecycleObject`, 복귀 표시)를 먼저 비우고 수명 오브젝트를 끊어 파괴한 뒤 이전 실행의 CTS 를 마지막에 취소합니다. 취소 콜백 안에서 `Stop`·`Start` 를 다시 불러도 바깥 `Stop` 이 새 실행을 지우지 않고, 콜백 예외(`AggregateException`)가 나도 이미 멈춘 상태입니다(2026-10-14 리뷰). CTS 는 루프가 아직 토큰을 읽으므로 Dispose 하지 않습니다(타이머 없는 CTS). `WaitAsync` 는 자기 `_wake` 일 때만 비웁니다(Stop 직후 Start 한 새 루프의 대기를 지우지 않게). 포그라운드 복귀는 재시도 횟수를 0 으로 되돌립니다. 백오프는 틱으로 계산하고, 생성자는 `ResyncInterval` 1초·`RetryDelay` 100ms 미만을 거절합니다. 대기가 0 이 되면 즉시 끝나는 원천과 함께 메인 스레드를 양보하지 않고 돕니다.
- **수명 컴포넌트**: 플레이 중에만 만듭니다(에디트 모드는 `DontDestroyOnLoad` 불가, 일시정지 없음). `HideInHierarchy` 만 붙입니다. DontSave 플래그를 붙이면 플레이를 멈춰도 파괴되지 않을 수 있습니다. 자기를 만든 실행(CTS)을 알고 `OnApplicationQuit`(에디터에서는 플레이 정지)·`OnDestroy` 에서 `EndRun(run)` 을 불러 그 실행이 아직 지금 실행일 때만 멈춥니다. `Stop` 은 파괴 전에 `Unbind` 해서, 프레임 끝에 오는 옛 오브젝트의 `OnDestroy` 가 새 실행을 멈추지 않게 합니다. `HandlePause` 는 `true` 를 먼저 받은 복귀만 재동기화로 봅니다. Unity 문서는 오브젝트가 시작할 때도 `OnApplicationPause(false)` 가 온다고 하므로(앱 시작 직후 첫 장면에서 `Start` 하는 경우) 그 메시지를 복귀로 보지 않기 위한 것입니다. 2026-10-14 에디터 PlayMode 와 Android 에뮬레이터에서는 실행 중에 만든 오브젝트에 이 메시지가 오지 않았습니다(가드를 빼도 PlayMode 테스트가 통과함을 확인). 가드 자체는 EditMode `HandlePause_ResumeWithoutPause_IsIgnored` 가 확인합니다.
- **WebGL**: `NtpTimeSource` 는 스레드 풀·UDP 가 없어 바로 `NetworkError` 입니다(기다리면 다음 원천으로 넘어가지 못함).
- 결과 코드는 6000 블록(core README 범위 표에 등록). 값은 바꾸거나 재사용하지 않습니다.
- static 가변 상태 금지(D2). 인스턴스로 만들어 `MainInstaller` 에 인터페이스로 바인딩합니다.

## 4. 테스트

- `Tests/EditMode/` (`Geuneda.ServerTime.Tests.EditMode`)
  - `NtpPacketTest`: 요청 형식, 왕복·서버 처리 보정 계산(틱으로 정확한 값), 버전 3·긴 패킷, 음수 구간 0 처리, 짧은 패킷·클라이언트 모드·버전 2·윤초 표시 3·계층 16·Kiss-o'-Death·originate 불일치·타임스탬프 없음·2024년 이전 거절, 두 시대 변환. 테스트용 `ToTimestamp` 는 올림이어야 `ToDateTime`(내림)과 왕복이 맞습니다. `AddSeconds`/`FromSeconds` 는 Mono 에서 밀리초로 반올림하므로 틱으로 값을 만듭니다
  - `NtpTimeSourceTest`: 127.0.0.1 가짜 UDP 서버(`FakeNtpServer`)로 실제 소켓·스레드 풀 경로, 메인 스레드 복귀, 응답 없음 → `Timeout`(20번 반복. 취소 결과·취소 예외 경쟁은 이 반복으로 재현되지 않아 `ClassifyCancellation` 을 직접 확인), 1ms 제한 시간 100번(교착 회귀, 확률적), 작업 예외가 기다리는 호출자에게 메인 스레드에서 전파, 작업이 시계에 들어온 뒤 호출자가 취소하고 작업이 나중에 던지면 Error 로그(`FailingClock.Entered`/`Gate` 로 순서 고정), originate 불일치·쓰레기 → `InvalidResponse`, 호출자 취소, 미리 취소(보내지 않음), `.invalid` 호스트 → `NetworkError`
  - `HttpDateTimeSourceTest`: 가짜 `IHttpClient`(지연 동안 가짜 시계 진행)로 `HEAD`·헤더·제한 시간, 0.5초 + 왕복 절반, 오류 상태도 사용, 없음·옛 형식·잘못된 형식·2024년 이전 거절, 전송 실패 코드 유지. 로컬 `HttpListener` 와 실제 UnityWebRequest 로 `HEAD` 의 `Date` 읽기
  - `ServerTimeSyncTest`: 가짜 원천·시계·지연(`ManualDelay`, 동기 진행)으로 순서·대체·전부 실패·받은 뒤 경과 더하기·긴 왕복 거절·원천 예외·취소, 스레드 풀에서 끝난 원천의 메인 스레드 적용, 시작 동기화와 재동기화 간격, 수동 동기화 뒤 남은 간격, 지수 백오프와 상한, 무작위 하한, 복구, 대기 중·동기화 중 포그라운드 복귀, 일시정지 없는 복귀 무시(가드), 백오프 초기화, Stop 의 진행 중 요청 취소, Stop 뒤 Start
    - 리뷰 회귀(2026-10-14): 원천 예외가 `SyncAsync` 로 던져지고 다음 원천을 시도하지 않음, 스레드 풀에서 던진 예외도 메인 스레드에서 받음, 루프는 Error 로그 후 멈추고 다시 `Start` 가능, 최소 설정에서도 대기가 0 이 아님, 설정 최솟값 거절, 취소 콜백 안의 `Stop`·`Start` 뒤에도 새 실행을 멈출 수 있음, 콜백 예외 뒤에도 멈춰 있고 다시 시작 가능, 옛 실행의 `EndRun` 이 새 실행을 멈추지 않음
- `Tests/PlayMode/` (`Geuneda.ServerTime.Tests.PlayMode`, `ServerTimeSyncLifecycleTest`): 실제 플레이 모드에서 시작 직후 몇 프레임 동안 한 번만 맞춤, 일시정지·복귀 메시지로 다시 맞춤, 수명 오브젝트 파괴·`OnApplicationQuit` 이 실행을 멈추고 오브젝트를 파괴함, `Stop` 뒤 `Start` 하면 옛 오브젝트의 늦은 파괴가 새 실행을 멈추지 않음, 숨김 플래그가 `HideInHierarchy`
- 기기 확인(2026-10-14): Android 13 에뮬레이터(IL2CPP, High 코드 제거)와 iOS 27 시뮬레이터에서 실제 `time.google.com` NTP, `https://www.google.com` `HEAD` Date, `SyncAsync` 성공과 메인 스레드 복귀, 없는 호스트 `NetworkError`
