using System;
using System.Globalization;
using System.Threading;
using Cysharp.Threading.Tasks;
using Geuneda.Core;
using Geuneda.Http;

// ReSharper disable once CheckNamespace

namespace Geuneda.ServerTime
{
	/// <summary>
	/// HTTP 응답의 <c>Date</c> 헤더에서 서버 시각을 가져오는 원천입니다. 서버 쪽 작업 없이 자사 서버 주소로 HEAD 요청 하나를 보냅니다.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <c>Date</c> 는 초 단위로 잘린 값이라 정확도는 1~2초입니다(서버가 Date 값을 1초마다 갱신하는 경우 포함). 받은 값에 0.5초와 왕복의 절반을 더해 받은 순간의 서버 시각으로 봅니다.
	/// 상태 코드는 보지 않습니다(404 도 서버 시계의 Date 를 담음). 2xx 가 아닌 응답도 <c>Date</c> 가 있으면 씁니다.
	/// </para>
	/// <para>
	/// 캐시(CDN)를 거치면 오래된 <c>Date</c> 가 올 수 있어 <c>Cache-Control: no-cache</c> 를 보내지만, 캐시하지 않는 API 주소를 쓰는 것이 안전합니다.
	/// HTTPS 주소를 쓰면 TLS 로 응답 위조가 어렵습니다. 재시도 미들웨어가 있는 클라이언트는 실패한 시도까지 왕복에 들어가 추정이 어긋나므로,
	/// 재시도 없는 클라이언트를 넘깁니다(실패하면 <see cref="ServerTimeSync"/> 가 다시 시도함).
	/// </para>
	/// </remarks>
	public sealed class HttpDateTimeSource : IServerTimeSource
	{
		/// <summary>기본 제한 시간 5초입니다.</summary>
		public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

		private static readonly TimeSpan HalfSecond = TimeSpan.FromMilliseconds(500);

		private readonly IHttpClient _http;
		private readonly Uri _uri;
		private readonly TimeSpan _timeout;

		/// <inheritdoc />
		public string Name { get; }

		/// <exception cref="ArgumentNullException"><paramref name="http"/> 가 null 인 경우</exception>
		/// <exception cref="ArgumentException"><paramref name="uri"/> 가 http/https 절대 주소가 아닌 경우</exception>
		/// <exception cref="ArgumentOutOfRangeException">제한 시간이 0 이하인 경우</exception>
		public HttpDateTimeSource(IHttpClient http, string uri, TimeSpan? timeout = null)
		{
			_http = http ?? throw new ArgumentNullException(nameof(http));

			// Unix 에서는 "/x" 도 file 절대 주소로 읽히므로 스킴을 확인합니다
			if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ||
			    (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp))
			{
				throw new ArgumentException($"Not an absolute http(s) URI: {uri}", nameof(uri));
			}

			_timeout = timeout ?? DefaultTimeout;

			if (_timeout <= TimeSpan.Zero)
			{
				throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Timeout must be positive");
			}

			_uri = parsed;
			Name = $"http:{parsed.Host}";
		}

		/// <inheritdoc />
		public UniTask<Result<ServerTimeSample>> FetchAsync(IMonotonicClock clock, CancellationToken cancellationToken)
		{
			if (clock == null)
			{
				throw new ArgumentNullException(nameof(clock));
			}

			return FetchCoreAsync(clock, cancellationToken);
		}

		private async UniTask<Result<ServerTimeSample>> FetchCoreAsync(IMonotonicClock clock, CancellationToken cancellationToken)
		{
			var request = new HttpRequest(HttpMethod.Head, _uri)
				.WithHeader("Cache-Control", "no-cache")
				.WithTimeout(_timeout);

			var sentAt = clock.Elapsed;
			var result = await _http.SendAsync(request, cancellationToken);
			var receivedAt = clock.Elapsed;

			if (!result.TryGetValue(out var response))
			{
				return Result<ServerTimeSample>.Fail(result.Code, $"{Name}: {result.Message}");
			}

			if (!response.Headers.TryGetValue("Date", out var value) || !TryParseHttpDate(value, out var date))
			{
				return Result<ServerTimeSample>.Fail(ServerTimeResultCodes.InvalidResponse,
					$"{Name}: no valid Date header (HTTP {response.StatusCode}, Date '{value}')");
			}

			// 보정을 더하기 전에 범위를 봐야 상한 근처 Date 가 예외 대신 응답 오류가 됩니다
			if (date < ServerTimeSample.MinValidUtc || date > ServerTimeSample.MaxValidUtc)
			{
				return Result<ServerTimeSample>.Fail(ServerTimeResultCodes.InvalidResponse,
					$"{Name}: Date {value} is outside {ServerTimeSample.MinValidUtc:yyyy-MM-dd} ~ {ServerTimeSample.MaxValidUtc:yyyy-MM-dd}");
			}

			var roundTrip = receivedAt - sentAt;
			var serverNow = date + HalfSecond + TimeSpan.FromTicks(roundTrip.Ticks / 2);

			return Result<ServerTimeSample>.Ok(new ServerTimeSample(serverNow, receivedAt, roundTrip));
		}

		/// <summary>RFC 9110 IMF-fixdate(<c>Sun, 06 Nov 1994 08:49:37 GMT</c>)를 UTC 로 읽습니다.</summary>
		internal static bool TryParseHttpDate(string value, out DateTime utc)
		{
			if (value != null && DateTime.TryParseExact(value.Trim(), "r", CultureInfo.InvariantCulture,
				    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utc))
			{
				return true;
			}

			utc = default;
			return false;
		}
	}
}
