using System.Threading;
using Cysharp.Threading.Tasks;
using Geuneda.Core;

// ReSharper disable once CheckNamespace

namespace Geuneda.ServerTime
{
	/// <summary>
	/// 서버 시각을 한 번 가져오는 원천입니다. 기본 구현은 <see cref="NtpTimeSource"/> 와 <see cref="HttpDateTimeSource"/> 이고,
	/// 게임 서버 API 나 실시간 RPC 로 시각을 받으려면 직접 구현합니다.
	/// </summary>
	public interface IServerTimeSource
	{
		/// <summary>로그에 쓰는 이름입니다(예: <c>ntp:time.google.com</c>).</summary>
		string Name { get; }

		/// <summary>
		/// 서버 시각을 가져옵니다. 요청을 보내기 직전과 응답을 받은 직후에 <paramref name="clock"/> 을 읽어 왕복 지연을 보정한 표본을 만듭니다.
		/// </summary>
		/// <remarks>
		/// 정상 실패는 <c>Result</c> 코드(<c>NetworkError</c>, <c>Timeout</c>, <c>Cancelled</c>, <see cref="ServerTimeResultCodes.InvalidResponse"/>)로 돌려줍니다.
		/// 버그·오용은 결과로 숨기지 않고 예외로 던집니다(<see cref="ServerTimeSync.SyncAsync"/> 도 그대로 던지고, 백그라운드 동기화는 Error 로그 후 멈춤).
		/// 제한 시간은 원천이 정합니다. 어느 스레드에서 끝나도 됩니다(<see cref="ServerTimeSync"/> 가 메인 스레드로 돌아옴).
		/// </remarks>
		UniTask<Result<ServerTimeSample>> FetchAsync(IMonotonicClock clock, CancellationToken cancellationToken);
	}
}
