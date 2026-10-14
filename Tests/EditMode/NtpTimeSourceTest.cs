using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Cysharp.Threading.Tasks;
using Geuneda.Core;
using Geuneda.ServerTime;
using NUnit.Framework;
using UnityEngine.TestTools;

// ReSharper disable once CheckNamespace

namespace Geuneda.ServerTime.Tests
{
	/// <summary>
	/// 127.0.0.1 의 가짜 UDP 서버로 실제 소켓, 스레드 풀, 제한 시간 경로를 확인합니다.
	/// </summary>
	public class NtpTimeSourceTest
	{
		private static readonly DateTime ServerNow = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);

		private readonly MonotonicClock _clock = new MonotonicClock();
		private ILogSink _originalSink;
		private LockedSink _sink;

		/// <summary>스레드 풀에서도 쓰므로 잠급니다.</summary>
		private sealed class LockedSink : ILogSink
		{
			private readonly List<LogEntry> _entries = new List<LogEntry>();

			public void Write(in LogEntry entry)
			{
				lock (_entries)
				{
					_entries.Add(entry);
				}
			}

			public bool HasError<T>() where T : Exception
			{
				lock (_entries)
				{
					return _entries.Exists(entry => entry.Level == LogLevel.Error && entry.Exception is T);
				}
			}
		}

		/// <summary>작업 스레드에서 읽히면 <see cref="Entered"/> 를 알리고 <see cref="Gate"/> 가 열릴 때까지 기다렸다가 던지는 시계입니다.</summary>
		private sealed class FailingClock : IMonotonicClock
		{
			public readonly ManualResetEventSlim Entered = new ManualResetEventSlim(false);
			public readonly ManualResetEventSlim Gate = new ManualResetEventSlim(true);

			public TimeSpan Elapsed
			{
				get
				{
					Entered.Set();
					Gate.Wait(TimeSpan.FromSeconds(10));
					throw new InvalidOperationException("clock failure");
				}
			}
		}

		[SetUp]
		public void SetUp()
		{
			_originalSink = Log.Sink;
			_sink = new LockedSink();
			Log.Sink = _sink;
		}

		[TearDown]
		public void TearDown()
		{
			Log.Sink = _originalSink;
		}

		[Test]
		public void Constructor_InvalidArguments_Throw()
		{
			Assert.Throws<ArgumentException>(() => new NtpTimeSource(" "));
			Assert.Throws<ArgumentOutOfRangeException>(() => new NtpTimeSource("localhost", 0));
			Assert.Throws<ArgumentOutOfRangeException>(() => new NtpTimeSource("localhost", 65536));
			Assert.Throws<ArgumentOutOfRangeException>(() => new NtpTimeSource("localhost", 123, TimeSpan.Zero));
			Assert.Throws<ArgumentNullException>(() => new NtpTimeSource().FetchAsync(null, CancellationToken.None));
			Assert.AreEqual("ntp:time.google.com", new NtpTimeSource().Name);
		}

		[UnityTest]
		public IEnumerator Fetch_ValidReply_ReturnsSampleOnMainThread() => UniTask.ToCoroutine(async () =>
		{
			using (var server = new FakeNtpServer(FakeNtpServer.Valid(ServerNow)))
			{
				var source = new NtpTimeSource("127.0.0.1", server.Port, TimeSpan.FromSeconds(2));
				var mainThread = Thread.CurrentThread.ManagedThreadId;
				var before = _clock.Elapsed;

				var result = await source.FetchAsync(_clock, CancellationToken.None);

				Assert.AreEqual(mainThread, Thread.CurrentThread.ManagedThreadId, "returns to the calling main thread");
				Assert.IsTrue(result.IsSuccess, result.ToString());
				var sample = result.Value;
				Assert.Less(sample.RoundTrip, TimeSpan.FromSeconds(1));
				Assert.AreEqual(ServerNow + TimeSpan.FromTicks(sample.RoundTrip.Ticks / 2), sample.ServerUtc);
				Assert.GreaterOrEqual(sample.ReceivedAt, before);
				Assert.LessOrEqual(sample.ReceivedAt, _clock.Elapsed);
			}
		});

		[UnityTest]
		public IEnumerator Fetch_WorkerThrowsBeforeTimeout_PropagatesOnMainThread() => UniTask.ToCoroutine(async () =>
		{
			var source = new NtpTimeSource("127.0.0.1", NtpTimeSource.DefaultPort, TimeSpan.FromSeconds(5));
			var mainThread = Thread.CurrentThread.ManagedThreadId;

			try
			{
				await source.FetchAsync(new FailingClock(), CancellationToken.None);
				Assert.Fail("expected the clock exception");
			}
			catch (InvalidOperationException)
			{
				Assert.AreEqual(mainThread, Thread.CurrentThread.ManagedThreadId);
			}
		});

		[UnityTest]
		public IEnumerator Fetch_WorkerThrowsAfterTheCallerStoppedWaiting_IsLoggedNotLost() => UniTask.ToCoroutine(async () =>
		{
			var clock = new FailingClock();
			clock.Gate.Reset();
			var source = new NtpTimeSource("127.0.0.1", NtpTimeSource.DefaultPort, TimeSpan.FromSeconds(10));

			using (var cancellation = new CancellationTokenSource())
			{
				var fetch = source.FetchAsync(clock, cancellation.Token);

				// 작업이 시계에 들어온 것을 확인한 뒤 취소해야, 작업이 시작도 못 하고 끝나는 경우와 섞이지 않습니다
				var stopwatch = Stopwatch.StartNew();

				while (!clock.Entered.IsSet && stopwatch.Elapsed < TimeSpan.FromSeconds(5))
				{
					await UniTask.Delay(10, DelayType.Realtime);
				}

				Assert.IsTrue(clock.Entered.IsSet, "the worker reached the clock");
				cancellation.Cancel();
				var result = await fetch;

				Assert.AreEqual(ResultCodes.Cancelled, result.Code, result.ToString());
				Assert.IsFalse(_sink.HasError<InvalidOperationException>());
			}

			// 호출자가 돌아간 뒤 작업이 던집니다
			clock.Gate.Set();
			var waited = Stopwatch.StartNew();

			while (!_sink.HasError<InvalidOperationException>() && waited.Elapsed < TimeSpan.FromSeconds(5))
			{
				await UniTask.Delay(20, DelayType.Realtime);
			}

			Assert.IsTrue(_sink.HasError<InvalidOperationException>(), "the late exception is logged instead of being dropped");
		});

		[UnityTest]
		public IEnumerator Fetch_ManyVeryShortTimeouts_AllReturnTimeout() => UniTask.ToCoroutine(async () =>
		{
			// 제한 시간 콜백과 기다림 등록이 겹치는 순서를 여러 번 만듭니다(교착하면 이 테스트가 끝나지 않음)
			using (var server = new FakeNtpServer(_ => null))
			{
				var source = new NtpTimeSource("127.0.0.1", server.Port, TimeSpan.FromMilliseconds(1));

				for (var i = 0; i < 100; i++)
				{
					var result = await source.FetchAsync(_clock, CancellationToken.None);

					Assert.AreEqual(ResultCodes.Timeout, result.Code, $"attempt {i}: {result}");
				}
			}
		});

		[UnityTest]
		public IEnumerator Fetch_NoReply_TimesOut() => UniTask.ToCoroutine(async () =>
		{
			using (var server = new FakeNtpServer(_ => null))
			{
				var source = new NtpTimeSource("127.0.0.1", server.Port, TimeSpan.FromMilliseconds(300));
				var stopwatch = Stopwatch.StartNew();

				var result = await source.FetchAsync(_clock, CancellationToken.None);

				Assert.AreEqual(ResultCodes.Timeout, result.Code, result.ToString());
				Assert.Less(stopwatch.Elapsed, TimeSpan.FromSeconds(2));
				Assert.AreEqual(1, server.Requests);
			}
		});

		[Test]
		public void ClassifyCancellation_SeparatesTimeoutFromCallerCancellation()
		{
			var source = new NtpTimeSource("127.0.0.1");
			var cancelledResult = Result<ServerTimeSample>.Fail(ResultCodes.Cancelled, "worker saw the closed socket");
			var sample = Result<ServerTimeSample>.Ok(new ServerTimeSample(ServerNow, TimeSpan.Zero, TimeSpan.Zero));
			var networkError = Result<ServerTimeSample>.Fail(ResultCodes.NetworkError, "unreachable");

			// 제한 시간이 소켓을 닫아 작업이 취소 결과로 먼저 끝난 경우
			Assert.AreEqual(ResultCodes.Timeout, source.ClassifyCancellation(cancelledResult, true, false).Code);
			Assert.AreEqual(ResultCodes.Cancelled, source.ClassifyCancellation(cancelledResult, true, true).Code);
			Assert.AreEqual(ResultCodes.Timeout, source.ClassifyCancellation(networkError, true, false).Code);
			Assert.AreEqual(ResultCodes.NetworkError, source.ClassifyCancellation(networkError, false, false).Code);
			Assert.IsTrue(source.ClassifyCancellation(sample, true, true).IsSuccess, "a finished sample is kept");
		}

		[UnityTest]
		public IEnumerator Fetch_RepeatedTimeouts_AreNeverReportedAsCancelled() => UniTask.ToCoroutine(async () =>
		{
			// 제한 시간이 소켓을 닫아 작업이 취소 결과로 먼저 끝나는 경우와 취소 예외가 먼저 오는 경우가 섞입니다
			using (var server = new FakeNtpServer(_ => null))
			{
				var source = new NtpTimeSource("127.0.0.1", server.Port, TimeSpan.FromMilliseconds(50));

				for (var i = 0; i < 20; i++)
				{
					var result = await source.FetchAsync(_clock, CancellationToken.None);

					Assert.AreEqual(ResultCodes.Timeout, result.Code, $"attempt {i}: {result}");
				}
			}
		});

		[UnityTest]
		public IEnumerator Fetch_WrongOriginate_IsInvalidResponse() => UniTask.ToCoroutine(async () =>
		{
			using (var server = new FakeNtpServer(request => NtpTestPackets.Reply(NtpPacket.ReadUInt64(request, 40) ^ 1, ServerNow, ServerNow)))
			{
				var source = new NtpTimeSource("127.0.0.1", server.Port, TimeSpan.FromSeconds(2));

				var result = await source.FetchAsync(_clock, CancellationToken.None);

				Assert.AreEqual(ServerTimeResultCodes.InvalidResponse, result.Code, result.ToString());
				StringAssert.Contains("Originate", result.Message);
			}
		});

		[UnityTest]
		public IEnumerator Fetch_GarbageReply_IsInvalidResponse() => UniTask.ToCoroutine(async () =>
		{
			using (var server = new FakeNtpServer(_ => new byte[] { 1, 2, 3 }))
			{
				var source = new NtpTimeSource("127.0.0.1", server.Port, TimeSpan.FromSeconds(2));

				var result = await source.FetchAsync(_clock, CancellationToken.None);

				Assert.AreEqual(ServerTimeResultCodes.InvalidResponse, result.Code, result.ToString());
			}
		});

		[UnityTest]
		public IEnumerator Fetch_CallerCancels_IsCancelledPromptly() => UniTask.ToCoroutine(async () =>
		{
			using (var server = new FakeNtpServer(_ => null))
			using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
			{
				var source = new NtpTimeSource("127.0.0.1", server.Port, TimeSpan.FromSeconds(10));
				var stopwatch = Stopwatch.StartNew();

				var result = await source.FetchAsync(_clock, cancellation.Token);

				Assert.AreEqual(ResultCodes.Cancelled, result.Code, result.ToString());
				Assert.Less(stopwatch.Elapsed, TimeSpan.FromSeconds(3));
			}
		});

		[Test]
		public void Fetch_AlreadyCancelled_ReturnsCancelledWithoutSending()
		{
			using (var server = new FakeNtpServer(FakeNtpServer.Valid(ServerNow)))
			{
				var source = new NtpTimeSource("127.0.0.1", server.Port);

				var task = source.FetchAsync(_clock, new CancellationToken(true));

				Assert.AreEqual(UniTaskStatus.Succeeded, task.Status);
				Assert.AreEqual(ResultCodes.Cancelled, task.GetAwaiter().GetResult().Code);
				Assert.AreEqual(0, server.Requests);
			}
		}

		[UnityTest]
		public IEnumerator Fetch_UnresolvableHost_IsNetworkError() => UniTask.ToCoroutine(async () =>
		{
			// .invalid 는 해석되지 않도록 예약된 최상위 도메인입니다(RFC 6761)
			var source = new NtpTimeSource("geuneda-servertime-test.invalid", NtpTimeSource.DefaultPort, TimeSpan.FromSeconds(5));

			var result = await source.FetchAsync(_clock, CancellationToken.None);

			Assert.IsTrue(result.Code == ResultCodes.NetworkError || result.Code == ResultCodes.Timeout, result.ToString());
		});
	}
}
