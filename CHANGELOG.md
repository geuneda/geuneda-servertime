# Changelog
이 패키지의 모든 주요 변경사항은 이 파일에 기록됩니다.

이 형식은 [Keep a Changelog](http://keepachangelog.com/en/1.0.0/)를 기반으로 하며,
이 프로젝트는 [Semantic Versioning](http://semver.org/spec/v2.0.0.html)을 따릅니다.

## [0.2.0] - 2026-10-17

**Changed**:
- **호환되지 않는 변경**: `IServerTimeSync` 가 `IDisposable` 을 상속합니다. 이제 `MainInstaller.CleanDispose<IServerTimeSync>()` 로 백그라운드 동기화를 멈추고(진행 중인 요청 취소, 숨은 수명 오브젝트 파괴) 바인딩을 지울 수 있습니다. 0.1.0 에서는 구현 클래스만 `IDisposable` 이라 이 호출이 컴파일되지 않았습니다. `IServerTimeSync` 를 직접 구현한 클래스(테스트 가짜 포함)는 `Dispose()` 를 구현해야 합니다. `Dispose` 는 전과 같이 `Stop` 과 같고 여러 번 불러도 됩니다. `ServerTimeSync` 의 동작은 바뀌지 않았습니다.

## [0.1.0] - 2026-10-14

**New**:
- 서버 시간 동기화 `IServerTimeSync`/`ServerTimeSync`: 원천을 순서대로 시도해 처음 성공한 표본으로 services 시간 서비스의 기준점(`ITimeManipulator.SetServerTime`)을 맞춤(`SyncAsync`). 모두 실패하면 첫 실패 코드와 원천별 내용. 원천이 던진 예외(버그·오용)는 숨기지 않고 던짐. 왕복이 `MaxRoundTrip`(기본 10초)보다 긴 표본은 버림(응답 대기 중 백그라운드였던 표본)
- 백그라운드 동기화 `Start`/`Stop`: 맞춘 적 없으면 바로, 실패하면 5초부터 두 배씩 5분까지(50~100% 무작위), 성공하면 30분마다, 앱이 포그라운드로 돌아오면 바로(맞추는 중이면 끝난 뒤 한 번 더) 다시 맞추고 재시도 간격 초기화. 원천이 다른 스레드에서 끝나도(예외 포함) 메인 스레드에서 적용. 원천 예외는 Error 로그 후 멈춤. 앱 종료(에디터에서는 플레이 정지)나 숨은 수명 오브젝트 파괴 때 스스로 멈춤. 설정 최솟값 `ResyncInterval` 1초, `RetryDelay` 100ms
- 시간 원천 `IServerTimeSource`와 표본 `ServerTimeSample`(받은 순간의 서버 시각, 그 순간의 단조 시계 값, 왕복)
- `NtpTimeSource`(기본 `time.google.com`): SNTP v4, 스레드 풀에서 DNS·보내기·받기와 시각 측정, 주소별 재시도, 제한 시간 5초(호출자 취소와 제한 시간을 한곳에서 가름, 소켓 수신 시간 초과도 `Timeout`), 정상 실패만 결과로 바꾸고 예상 못 한 예외는 던짐, WebGL 은 바로 `NetworkError`. 요청의 무작위 값(originate) 확인, 서버 모드·버전·계층·윤초 표시·Kiss-o'-Death 검사, 2036년 이후 시대 처리, 서버 처리 시간을 뺀 왕복 보정
- `HttpDateTimeSource`: 자사 서버로 `HEAD`(`Cache-Control: no-cache`)를 보내 `Date` 헤더(IMF-fixdate)를 씀. 0.5초(초 단위 절삭)와 왕복의 절반을 더함. 상태 코드는 보지 않음
- 결과 코드 `ServerTimeResultCodes`(6000 블록: `InvalidResponse` 6001, `RoundTripTooLong` 6002)
