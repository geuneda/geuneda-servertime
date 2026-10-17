# Geuneda Server Time

서버 시간 동기화 패키지입니다. 시간 원천(NTP, HTTPS `Date` 헤더, 직접 구현)에서 서버 시각을 가져와 왕복 지연을 보정하고, services 의 시간 서비스(`ITimeService`) 기준점으로 넣습니다. 이후 현재 시각은 core 의 단조 시계로 흐르므로 기기 시계를 바꿔도 영향받지 않습니다.

| 기능 | 타입 |
|------|------|
| 동기화 | `IServerTimeSync`, `ServerTimeSync`, `ServerTimeSyncOptions` |
| 시간 원천 | `IServerTimeSource`, `NtpTimeSource`, `HttpDateTimeSource`, `ServerTimeSample` |
| 결과 코드 | `ServerTimeResultCodes`(6000 블록) |

네임스페이스는 `Geuneda.ServerTime` 입니다.

## 설치

`Packages/manifest.json` 에 의존 패키지와 함께 추가합니다. Geuneda 패키지와 UniTask 는 git 의존성이라 함께 넣어야 합니다. Newtonsoft Json, Addressables 는 Unity 레지스트리에서 자동으로 설치됩니다.

```json
{
  "dependencies": {
    "com.cysharp.unitask": "https://github.com/Cysharp/UniTask.git?path=src/UniTask/Assets/Plugins/UniTask",
    "com.geuneda.core": "https://github.com/geuneda/geuneda-core.git",
    "com.geuneda.gamedata": "https://github.com/geuneda/geuneda-gamedata.git",
    "com.geuneda.services": "https://github.com/geuneda/geuneda-services.git",
    "com.geuneda.http": "https://github.com/geuneda/geuneda-http.git",
    "com.geuneda.servertime": "https://github.com/geuneda/geuneda-servertime.git"
  }
}
```

## 사용 예

```csharp
using System.Threading;
using Cysharp.Threading.Tasks;
using Geuneda.Http;
using Geuneda.ServerTime;
using Geuneda.Services;

public static class Bootstrap
{
    public static async UniTask InitializeAsync(CancellationToken cancellationToken)
    {
        var time = new TimeService();
        MainInstaller.Bind<ITimeService>(time);
        MainInstaller.Bind<ITimeManipulator>(time);

        // 재시도 미들웨어 없는 클라이언트(왕복 시간이 늘면 추정이 어긋남)
        var http = new HttpClientBuilder().Build();
        var sync = new ServerTimeSync(time, new IServerTimeSource[]
        {
            new NtpTimeSource(),                                            // time.google.com
            new HttpDateTimeSource(http, "https://api.mygame.com/health")  // NTP 가 막힌 망·WebGL 대체
        });
        MainInstaller.Bind<IServerTimeSync>(sync);

        // 부팅 때 한 번 기다리고(실패해도 진행할지 게임이 정함), 이후는 백그라운드로 맞춤
        var result = await sync.SyncAsync(cancellationToken);

        // 초기화 자체가 취소되면 백그라운드 동기화를 시작하지 않습니다(등록한 바인딩 정리는 초기화 소유자가 함)
        cancellationToken.ThrowIfCancellationRequested();

        if (!result.IsSuccess)
        {
            // 네트워크 실패: 기기 시계 기준으로 진행(time.IsServerTime == false). 아래 Start 가 계속 다시 시도
        }

        sync.Start();
    }

    // initialization: InitializeAsync 에 토큰을 넘긴 소스
    public static void Shutdown(CancellationTokenSource initialization)
    {
        initialization.Cancel();                         // 초기화 중이면 직접 부른 SyncAsync 를 취소하고 Start 하지 않게 함(Dispose 는 이 요청을 취소하지 않음)
        MainInstaller.CleanDispose<IServerTimeSync>();   // Start 가 시작한 백그라운드 동기화 멈춤(진행 중인 요청 취소), 바인딩 제거
    }
}
```

일일 초기화처럼 서버 시각이 필요한 곳은 `ITimeService.IsServerTime` 을 확인하고, `TimeAdjusted` 에서 타이머를 다시 계산합니다.

## 동작

- **원천 순서**: 목록 순서대로 시도해 처음 성공한 표본을 씁니다. 모두 실패하면 첫 실패 코드와 원천별 실패 내용을 돌려줍니다. 원천이 결과 대신 던진 예외(버그·오용)는 숨기지 않습니다. `SyncAsync` 는 그대로 던지고, 백그라운드 동기화는 Error 로그를 남기고 멈춥니다(다시 시도해도 고쳐지지 않으므로).
- **왕복 보정**: 표본은 "응답을 받은 순간의 서버 시각 추정값"과 그 순간의 단조 시계 값입니다. 적용할 때 받은 뒤 흐른 시간을 더해 `ITimeManipulator.SetServerTime` 에 넣습니다. 왕복이 `MaxRoundTrip`(기본 10초)보다 긴 표본은 버립니다. 응답을 기다리는 동안 앱이 백그라운드에 있었던 표본은 추정이 크게 틀리기 때문입니다.
- **백그라운드 동기화(`Start`)**: 아직 맞춘 적 없으면 바로 맞춥니다. 실패하면 `RetryDelay`(기본 5초)부터 두 배씩 `MaxRetryDelay`(기본 5분)까지 기다렸다 다시 시도합니다(대기는 상한의 50~100% 무작위). 성공하면 `ResyncInterval`(기본 30분)마다 다시 맞춥니다. 앱이 포그라운드로 돌아오면(`OnApplicationPause(false)`) 기다리던 중이면 바로, 맞추는 중이면 끝난 뒤 한 번 더 맞추고 재시도 간격을 처음으로 되돌립니다. 설정 최솟값은 `ResyncInterval` 1초, `RetryDelay` 100ms 입니다(대기가 0 이 되어 메인 스레드를 양보하지 않고 도는 것을 막음).
- **멈춤**: `Stop()`/`Dispose()` 로 멈춥니다. `IServerTimeSync` 는 `IDisposable` 이라 `MainInstaller.CleanDispose<IServerTimeSync>()` 로 멈추고 바인딩을 지울 수 있습니다(0.2.0). `Dispose` 는 `Stop` 과 같고 여러 번 불러도 됩니다. `Stop`·`Dispose` 는 `Start` 가 시작한 백그라운드 동기화만 멈춥니다. 직접 부른 `SyncAsync` 는 그 호출에 넘긴 토큰으로 취소하므로, 초기화 중에 종료할 때는 초기화 토큰을 먼저 취소합니다(위 `Shutdown`). 앱이 끝나거나(에디터에서는 플레이를 멈출 때) 숨은 수명 오브젝트가 파괴되면 스스로 멈추므로, 도메인 리로드를 끈 에디터에서도 이전 플레이의 동기화가 남지 않습니다.
- **시계**: 시간 서비스와 동기화가 같은 단조 시계를 써야 합니다. 둘 다 기본 생성자면 core `MonotonicClock` 입니다. 이 시계는 Android·iOS·macOS 에서 기기가 잠든 동안에도 흐르므로, 오프라인으로 돌아와도 서버 시각이 뒤처지지 않습니다.
- **스레드**: 메인 스레드에서 부릅니다. 원천이 다른 스레드에서 끝나도 메인 스레드로 돌아와 시간 서비스를 바꿉니다.
- 재동기화로 현재 시각이 조금 뒤로 갈 수 있습니다(`TimeAdjusted` 의 음수 값).

## 시간 원천

| 원천 | 정확도 | 비고 |
|------|--------|------|
| `NtpTimeSource(host = "time.google.com")` | 수십 ms | UDP 123. DNS 조회부터 응답까지 제한 시간 5초. 요청에 무작위 값을 넣어 응답이 돌려주는지 확인하고, 서버 모드·버전·계층·윤초 표시를 검사합니다. 서버 처리 시간을 빼고 왕복의 절반을 더합니다. 인증이 없어 네트워크를 장악한 사용자는 위조할 수 있고, 일부 망은 UDP 123 을 막으며, WebGL 은 쓸 수 없습니다 |
| `HttpDateTimeSource(http, uri)` | 1~2초 | 자사 서버 주소로 `HEAD` 를 보내고 응답의 `Date` 헤더를 씁니다(서버 작업 없음). 상태 코드는 보지 않습니다. `Cache-Control: no-cache` 를 보내지만 캐시하지 않는 API 주소를 씁니다. HTTPS 면 위조가 어렵습니다 |
| 직접 구현 `IServerTimeSource` | 서버에 따름 | 게임 서버 API·실시간 RPC 가 밀리초 시각을 주면 보낸 직전·받은 직후의 `clock.Elapsed` 로 표본을 만듭니다 |

직접 구현 예:

```csharp
public sealed class TimeResponse
{
    public long UnixMs;
}

public sealed class ApiTimeSource : IServerTimeSource
{
    private readonly IHttpClient _http;

    public ApiTimeSource(IHttpClient http) => _http = http;

    public string Name => "api:time";

    public async UniTask<Result<ServerTimeSample>> FetchAsync(IMonotonicClock clock, CancellationToken cancellationToken)
    {
        var sentAt = clock.Elapsed;
        var result = await _http.GetJsonAsync<TimeResponse>("https://api.mygame.com/time", cancellationToken);
        var receivedAt = clock.Elapsed;

        if (!result.TryGetValue(out var response))
        {
            return Result<ServerTimeSample>.Fail(result.Code, result.Message);
        }

        var roundTrip = receivedAt - sentAt;
        var serverNow = DateTimeOffset.FromUnixTimeMilliseconds(response.UnixMs).UtcDateTime + TimeSpan.FromTicks(roundTrip.Ticks / 2);

        return Result<ServerTimeSample>.Ok(new ServerTimeSample(serverNow, receivedAt, roundTrip));
    }
}
```

## 결과 코드

| 코드 | 이름 | 뜻 |
|------|------|----|
| 2 | `ResultCodes.Cancelled` | 호출자 취소 |
| 3 | `ResultCodes.Timeout` | 원천 제한 시간 초과 |
| 4 | `ResultCodes.NetworkError` | DNS 실패, 연결·전송 실패 |
| 6001 | `ServerTimeResultCodes.InvalidResponse` | NTP 응답 검증 실패, `Date` 헤더 없음·형식 오류, 2024년 이전 시각 |
| 6002 | `ServerTimeResultCodes.RoundTripTooLong` | 왕복이 `MaxRoundTrip` 보다 김 |

## 치트와 신뢰

- 기기 시계 조작: 현재 시각은 서버 기준점 + 단조 시계라 영향받지 않습니다. 동기화 로그(Info)에 기기 시계와의 차이를 남깁니다.
- 동기화 전(`IsServerTime == false`)에는 생성할 때의 기기 시계 기준입니다. 보상·일일 초기화처럼 조작이 문제 되는 판정은 서버 시각을 받은 뒤에 하거나 서버가 합니다.
- NTP 응답 위조와 HTTPS 인증서를 심은 프록시는 막지 못합니다. 위조가 문제 되는 값은 서버가 판정합니다.

## 요구 사항

- Unity 6000.3 이상
- `com.geuneda.core` 0.4.0, `com.geuneda.services` 5.0.0, `com.geuneda.http` 0.1.0, `com.cysharp.unitask` 2.5.10. 어셈블리 `Geuneda.ServerTime`

변경 이력은 [CHANGELOG.md](CHANGELOG.md) 를 참고하세요.

## 라이선스

MIT. [LICENSE.md](LICENSE.md) 를 참고하세요.
