using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using Geuneda.Core;
using Geuneda.Http;
using Geuneda.ServerTime;
using NUnit.Framework;
using UnityEngine.TestTools;

// ReSharper disable once CheckNamespace

namespace Geuneda.ServerTime.Tests
{
	[TestFixture]
	public class HttpDateTimeSourceTest
	{
		private const string Uri = "https://api.example.com/health";

		private FakeClock _clock;
		private FakeHttpClient _http;

		private sealed class FakeHttpClient : IHttpClient
		{
			private readonly FakeClock _clock;

			public readonly List<HttpRequest> Requests = new List<HttpRequest>();
			public Result<HttpResponse> Next;
			public TimeSpan Latency;

			public FakeHttpClient(FakeClock clock)
			{
				_clock = clock;
			}

			public static Result<HttpResponse> Respond(int status, string date)
			{
				var headers = new Dictionary<string, string>();

				if (date != null)
				{
					headers["date"] = date;
				}

				return Result<HttpResponse>.Ok(new HttpResponse(new System.Uri(Uri), status, null, headers));
			}

			public UniTask<Result<HttpResponse>> SendAsync(HttpRequest request, CancellationToken cancellationToken = default)
			{
				Requests.Add(request);
				_clock.Elapsed += Latency;
				return UniTask.FromResult(Next);
			}

			public UniTask<Result<T>> SendJsonAsync<T>(HttpRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
			public UniTask<Result<HttpResponse>> GetAsync(string uri, CancellationToken cancellationToken = default) => throw new NotSupportedException();
			public UniTask<Result<T>> GetJsonAsync<T>(string uri, CancellationToken cancellationToken = default) => throw new NotSupportedException();
			public UniTask<Result<HttpResponse>> PostJsonAsync(string uri, object body, CancellationToken cancellationToken = default) => throw new NotSupportedException();
			public UniTask<Result<T>> PostJsonAsync<T>(string uri, object body, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		}

		[SetUp]
		public void SetUp()
		{
			_clock = new FakeClock();
			_http = new FakeHttpClient(_clock);
		}

		private Result<ServerTimeSample> Fetch(HttpDateTimeSource source = null)
		{
			return (source ?? new HttpDateTimeSource(_http, Uri)).FetchAsync(_clock, CancellationToken.None).GetAwaiter().GetResult();
		}

		[Test]
		public void Constructor_InvalidArguments_Throw()
		{
			Assert.Throws<ArgumentNullException>(() => new HttpDateTimeSource(null, Uri));
			Assert.Throws<ArgumentException>(() => new HttpDateTimeSource(_http, "/health"));
			Assert.Throws<ArgumentException>(() => new HttpDateTimeSource(_http, "ftp://example.com/"));
			Assert.Throws<ArgumentException>(() => new HttpDateTimeSource(_http, "not a uri"));
			Assert.Throws<ArgumentOutOfRangeException>(() => new HttpDateTimeSource(_http, Uri, TimeSpan.Zero));
			Assert.Throws<ArgumentNullException>(() => new HttpDateTimeSource(_http, Uri).FetchAsync(null, CancellationToken.None));
			Assert.AreEqual("http:api.example.com", new HttpDateTimeSource(_http, Uri).Name);
		}

		[Test]
		public void Fetch_SendsUncachedHeadWithTimeout()
		{
			_http.Next = FakeHttpClient.Respond(200, "Tue, 01 Jan 2030 00:00:00 GMT");

			Fetch(new HttpDateTimeSource(_http, Uri, TimeSpan.FromSeconds(3)));

			var request = _http.Requests[0];
			Assert.AreEqual(HttpMethod.Head, request.Method);
			Assert.AreEqual(new System.Uri(Uri), request.Uri);
			Assert.AreEqual("no-cache", request.Headers["Cache-Control"]);
			Assert.AreEqual(TimeSpan.FromSeconds(3), request.Timeout);
		}

		[Test]
		public void Fetch_DateHeader_AddsHalfSecondAndHalfRoundTrip()
		{
			_http.Next = FakeHttpClient.Respond(200, "Tue, 01 Jan 2030 00:00:00 GMT");
			_http.Latency = TimeSpan.FromMilliseconds(300);
			var receivedAt = _clock.Elapsed + _http.Latency;

			var result = Fetch();

			Assert.IsTrue(result.IsSuccess, result.ToString());
			Assert.AreEqual(new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(650), result.Value.ServerUtc);
			Assert.AreEqual(TimeSpan.FromMilliseconds(300), result.Value.RoundTrip);
			Assert.AreEqual(receivedAt, result.Value.ReceivedAt);
		}

		[Test]
		public void Fetch_ErrorStatusWithDate_StillUsesDate()
		{
			_http.Next = FakeHttpClient.Respond(404, "Tue, 01 Jan 2030 00:00:00 GMT");

			Assert.IsTrue(Fetch().IsSuccess);
		}

		[TestCase(null, TestName = "Fetch_MissingDate_IsInvalidResponse")]
		[TestCase("Tuesday, 01-Jan-30 00:00:00 GMT", TestName = "Fetch_ObsoleteDateFormat_IsInvalidResponse")]
		[TestCase("yesterday", TestName = "Fetch_MalformedDate_IsInvalidResponse")]
		[TestCase("Fri, 01 Jan 2021 00:00:00 GMT", TestName = "Fetch_DateBefore2024_IsInvalidResponse")]
		[TestCase("Fri, 31 Dec 9999 23:59:59 GMT", TestName = "Fetch_DateNearMaxValue_IsInvalidResponseNotAnException")]
		public void Fetch_UnusableDate_IsInvalidResponse(string date)
		{
			_http.Next = FakeHttpClient.Respond(200, date);
			_http.Latency = TimeSpan.FromSeconds(2);

			Assert.AreEqual(ServerTimeResultCodes.InvalidResponse, Fetch().Code);
		}

		[Test]
		public void Sync_DateNearMaxValue_FallsBackAndKeepsRunning()
		{
			_http.Next = FakeHttpClient.Respond(200, "Fri, 31 Dec 9999 23:59:59 GMT");
			_http.Latency = TimeSpan.FromSeconds(2);
			var time = new Geuneda.Services.TimeService(_clock);
			var fallback = new FakeSource("fallback") { Handler = FakeSource.Returns(new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc)) };
			var delay = new ManualDelay();
			var sync = new ServerTimeSync(time, new IServerTimeSource[] { new HttpDateTimeSource(_http, Uri), fallback }, null, _clock, delay.Delay, () => 1);

			sync.Start();

			Assert.AreEqual(1, fallback.Calls);
			Assert.IsTrue(time.IsServerTime);
			Assert.IsNotNull(sync.Running);
			Assert.IsTrue(delay.IsWaiting);
			sync.Stop();
		}

		[TestCase(ResultCodes.NetworkError)]
		[TestCase(ResultCodes.Timeout)]
		[TestCase(ResultCodes.Cancelled)]
		public void Fetch_TransportFailure_KeepsCode(int code)
		{
			_http.Next = Result<HttpResponse>.Fail(code, "transport");

			Assert.AreEqual(code, Fetch().Code);
		}

		[Test]
		public void TryParseHttpDate_ReadsImfFixdateAsUtc()
		{
			Assert.IsTrue(HttpDateTimeSource.TryParseHttpDate(" Sun, 06 Nov 1994 08:49:37 GMT ", out var date));
			Assert.AreEqual(new DateTime(1994, 11, 6, 8, 49, 37, DateTimeKind.Utc), date);
			Assert.AreEqual(DateTimeKind.Utc, date.Kind);
			Assert.IsFalse(HttpDateTimeSource.TryParseHttpDate(null, out _));
		}

		/// <summary>로컬 <see cref="HttpListener"/> 와 실제 UnityWebRequest 로 HEAD 응답의 Date 헤더를 읽습니다.</summary>
		[UnityTest]
		public IEnumerator RealHead_ReadsDateFromLocalServer() => UniTask.ToCoroutine(async () =>
		{
			var port = GetFreePort();
			var prefix = $"http://127.0.0.1:{port}/";
			var listener = new HttpListener();
			listener.Prefixes.Add(prefix);
			listener.Start();
			string method = null;

			_ = Task.Run(async () =>
			{
				try
				{
					var context = await listener.GetContextAsync();
					method = context.Request.HttpMethod;
					context.Response.StatusCode = 204;
					context.Response.Close();
				}
				catch (Exception)
				{
					// 테스트가 끝나 리스너가 닫힘
				}
			});

			try
			{
				var source = new HttpDateTimeSource(new HttpClientBuilder().Build(), prefix + "time");
				var before = DateTime.UtcNow;

				var result = await source.FetchAsync(new MonotonicClock(), CancellationToken.None);

				Assert.IsTrue(result.IsSuccess, result.ToString());
				Assert.AreEqual("HEAD", method);
				// Date 는 초 단위라 0.5초를 더한 추정값은 실제와 1초 안팎 차이 납니다
				Assert.AreEqual(0.5, (result.Value.ServerUtc - before).TotalSeconds, 2.0);
			}
			finally
			{
				listener.Close();
			}
		});

		private static int GetFreePort()
		{
			var listener = new TcpListener(IPAddress.Loopback, 0);
			listener.Start();
			var port = ((IPEndPoint) listener.LocalEndpoint).Port;
			listener.Stop();
			return port;
		}
	}
}
