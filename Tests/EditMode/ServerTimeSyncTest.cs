using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Geuneda.Core;
using Geuneda.ServerTime;
using Geuneda.Services;
using NUnit.Framework;
using UnityEngine.TestTools;

// ReSharper disable once CheckNamespace

namespace Geuneda.ServerTime.Tests
{
	[TestFixture]
	public class ServerTimeSyncTest
	{
		private static readonly DateTime ServerNow = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);

		private FakeClock _clock;
		private TimeService _time;
		private ManualDelay _delay;
		private FakeSource _ntp;
		private FakeSource _http;
		private double _randomValue;
		private ILogSink _originalSink;
		private List<LogEntry> _logs;
		private ServerTimeSync _sync;

		private sealed class ListSink : ILogSink
		{
			private readonly List<LogEntry> _entries;

			public ListSink(List<LogEntry> entries)
			{
				_entries = entries;
			}

			public void Write(in LogEntry entry)
			{
				_entries.Add(entry);
			}
		}

		[SetUp]
		public void SetUp()
		{
			_logs = new List<LogEntry>();
			_originalSink = Log.Sink;
			Log.Sink = new ListSink(_logs);
			_clock = new FakeClock();
			_time = new TimeService(_clock);
			_delay = new ManualDelay();
			_ntp = new FakeSource("ntp");
			_http = new FakeSource("http");
			_randomValue = 1;
		}

		[TearDown]
		public void TearDown()
		{
			_sync?.Dispose();
			Log.Sink = _originalSink;
		}

		private ServerTimeSync Create(ServerTimeSyncOptions options = null, params IServerTimeSource[] sources)
		{
			_sync = new ServerTimeSync(_time, sources.Length == 0 ? new IServerTimeSource[] { _ntp, _http } : sources, options, _clock,
				_delay.Delay, () => _randomValue);
			return _sync;
		}

		private static void Resume(ServerTimeSync sync)
		{
			sync.HandlePause(true);
			sync.HandlePause(false);
		}

		private static Result<ServerTimeSample> Run(UniTask<Result<ServerTimeSample>> task)
		{
			Assert.AreEqual(UniTaskStatus.Succeeded, task.Status, "fake sources complete synchronously");
			return task.GetAwaiter().GetResult();
		}

		[Test]
		public void Constructor_InvalidArguments_Throw()
		{
			var sources = new IServerTimeSource[] { _ntp };

			Assert.Throws<ArgumentNullException>(() => new ServerTimeSync(null, sources));
			Assert.Throws<ArgumentNullException>(() => new ServerTimeSync(_time, null));
			Assert.Throws<ArgumentException>(() => new ServerTimeSync(_time, new IServerTimeSource[0]));
			Assert.Throws<ArgumentException>(() => new ServerTimeSync(_time, new IServerTimeSource[] { _ntp, null }));
			Assert.Throws<ArgumentOutOfRangeException>(() => new ServerTimeSync(_time, sources, new ServerTimeSyncOptions { ResyncInterval = TimeSpan.Zero }));
			Assert.Throws<ArgumentOutOfRangeException>(() => new ServerTimeSync(_time, sources, new ServerTimeSyncOptions { RetryDelay = TimeSpan.Zero }));
			Assert.Throws<ArgumentOutOfRangeException>(() => new ServerTimeSync(_time, sources, new ServerTimeSyncOptions { MaxRoundTrip = TimeSpan.Zero }));
			Assert.Throws<ArgumentOutOfRangeException>(() => new ServerTimeSync(_time, sources,
				new ServerTimeSyncOptions { RetryDelay = TimeSpan.FromMilliseconds(99), MaxRetryDelay = TimeSpan.FromMilliseconds(99) }));
			Assert.Throws<ArgumentOutOfRangeException>(() => new ServerTimeSync(_time, sources, new ServerTimeSyncOptions { ResyncInterval = TimeSpan.FromMilliseconds(999) }));
			Assert.DoesNotThrow(() => new ServerTimeSync(_time, sources, new ServerTimeSyncOptions
			{
				ResyncInterval = ServerTimeSyncOptions.MinResyncInterval,
				RetryDelay = ServerTimeSyncOptions.MinRetryDelay,
				MaxRetryDelay = ServerTimeSyncOptions.MinRetryDelay
			}));
			Assert.Throws<ArgumentOutOfRangeException>(() => new ServerTimeSync(_time, sources,
				new ServerTimeSyncOptions { RetryDelay = TimeSpan.FromSeconds(10), MaxRetryDelay = TimeSpan.FromSeconds(5) }));
		}

		[Test]
		public void SyncAsync_FirstSourceSucceeds_SetsServerTimeAndSkipsRest()
		{
			_ntp.Handler = FakeSource.Returns(ServerNow);
			var sync = Create();

			var result = Run(sync.SyncAsync());

			Assert.IsTrue(result.IsSuccess);
			Assert.IsTrue(_time.IsServerTime);
			Assert.AreEqual(ServerNow, _time.DateTimeUtcNow);
			Assert.AreEqual(0, _http.Calls);
		}

		[Test]
		public void SyncAsync_FirstSourceFails_FallsBackToNext()
		{
			_http.Handler = FakeSource.Returns(ServerNow);
			var sync = Create();

			var result = Run(sync.SyncAsync());

			Assert.IsTrue(result.IsSuccess);
			Assert.AreEqual(1, _ntp.Calls);
			Assert.AreEqual(ServerNow, _time.DateTimeUtcNow);
		}

		[Test]
		public void SyncAsync_AllFail_ReturnsFirstCodeAndEverySourceMessage()
		{
			_ntp.Handler = FakeSource.Fails(ResultCodes.Timeout);
			_http.Handler = FakeSource.Fails(ServerTimeResultCodes.InvalidResponse);
			var sync = Create();

			var result = Run(sync.SyncAsync());

			Assert.AreEqual(ResultCodes.Timeout, result.Code);
			StringAssert.Contains("ntp:", result.Message);
			StringAssert.Contains("http:", result.Message);
			Assert.IsFalse(_time.IsServerTime);
			Assert.IsTrue(_logs.Exists(entry => entry.Level == LogLevel.Warning));
		}

		[Test]
		public void SyncAsync_AddsTimeElapsedSinceTheSampleWasReceived()
		{
			_ntp.Handler = (clock, _) =>
			{
				var receivedAt = clock.Elapsed;
				_clock.Elapsed += TimeSpan.FromSeconds(2);
				return UniTask.FromResult(Result<ServerTimeSample>.Ok(new ServerTimeSample(ServerNow, receivedAt, TimeSpan.Zero)));
			};
			var sync = Create();

			Run(sync.SyncAsync());

			Assert.AreEqual(ServerNow.AddSeconds(2), _time.DateTimeUtcNow);
		}

		[Test]
		public void SyncAsync_RoundTripTooLong_IsRejectedAndFallsBack()
		{
			_ntp.Handler = FakeSource.Returns(ServerNow.AddHours(1), TimeSpan.FromSeconds(11));
			_http.Handler = FakeSource.Returns(ServerNow, TimeSpan.FromSeconds(10));
			var sync = Create();

			var result = Run(sync.SyncAsync());

			Assert.IsTrue(result.IsSuccess);
			Assert.AreEqual(ServerNow, _time.DateTimeUtcNow);
		}

		[Test]
		public void SyncAsync_OnlyLongRoundTrips_ReturnsRoundTripTooLong()
		{
			_ntp.Handler = FakeSource.Returns(ServerNow, TimeSpan.FromMinutes(5));
			var sync = Create(null, _ntp);

			var result = Run(sync.SyncAsync());

			Assert.AreEqual(ServerTimeResultCodes.RoundTripTooLong, result.Code);
			Assert.IsFalse(_time.IsServerTime);
		}

		[Test]
		public void SyncAsync_SourceThrows_PropagatesWithoutTryingTheNextSource()
		{
			_ntp.Handler = (_, __) => throw new InvalidOperationException("broken source");
			_http.Handler = FakeSource.Returns(ServerNow);
			var sync = Create();

			Assert.Throws<InvalidOperationException>(() => sync.SyncAsync().GetAwaiter().GetResult());
			Assert.AreEqual(0, _http.Calls);
			Assert.IsFalse(_time.IsServerTime);
		}

		[UnityTest]
		public IEnumerator SyncAsync_SourceThrowsOnThreadPool_PropagatesOnMainThread() => UniTask.ToCoroutine(async () =>
		{
			var mainThread = Thread.CurrentThread.ManagedThreadId;
			_ntp.Handler = async (clock, _) =>
			{
				await UniTask.SwitchToThreadPool();
				throw new InvalidOperationException("broken source");
			};
			var sync = Create();

			try
			{
				await sync.SyncAsync();
				Assert.Fail("expected the source exception");
			}
			catch (InvalidOperationException)
			{
				Assert.AreEqual(mainThread, Thread.CurrentThread.ManagedThreadId);
			}
		});

		[Test]
		public void Start_SourceThrows_LogsErrorAndStops()
		{
			_ntp.Handler = (_, __) => throw new InvalidOperationException("broken source");
			var sync = Create(null, _ntp);

			sync.Start();

			Assert.IsTrue(_logs.Exists(entry => entry.Level == LogLevel.Error && entry.Exception is InvalidOperationException));
			Assert.IsNull(sync.Running, "a bug is not retried forever");
			Assert.AreEqual(0, _delay.Requested.Count);

			_ntp.Handler = FakeSource.Returns(ServerNow);
			sync.Start();

			Assert.AreEqual(2, _ntp.Calls);
			Assert.IsTrue(_delay.IsWaiting);
		}

		[Test]
		public void SyncAsync_Cancelled_DoesNotTryNextSourceOrApply()
		{
			using (var cancellation = new CancellationTokenSource())
			{
				_ntp.Handler = (_, token) =>
				{
					cancellation.Cancel();
					return UniTask.FromResult(Result<ServerTimeSample>.Fail(ResultCodes.Cancelled, "cancelled"));
				};
				_http.Handler = FakeSource.Returns(ServerNow);
				var sync = Create();

				var result = Run(sync.SyncAsync(cancellation.Token));

				Assert.AreEqual(ResultCodes.Cancelled, result.Code);
				Assert.AreEqual(0, _http.Calls);
				Assert.IsFalse(_time.IsServerTime);
			}
		}

		[UnityTest]
		public IEnumerator SyncAsync_SourceCompletesOnThreadPool_AppliesOnMainThread() => UniTask.ToCoroutine(async () =>
		{
			var mainThread = Thread.CurrentThread.ManagedThreadId;
			var adjustedOn = -1;
			_time.TimeAdjusted += _ => adjustedOn = Thread.CurrentThread.ManagedThreadId;
			_ntp.Handler = async (clock, _) =>
			{
				await UniTask.SwitchToThreadPool();
				return Result<ServerTimeSample>.Ok(new ServerTimeSample(ServerNow, clock.Elapsed, TimeSpan.Zero));
			};
			var sync = Create();

			var result = await sync.SyncAsync();

			Assert.IsTrue(result.IsSuccess, result.ToString());
			Assert.AreEqual(mainThread, adjustedOn);
			Assert.AreEqual(mainThread, Thread.CurrentThread.ManagedThreadId);
		});

		[Test]
		public void Start_SyncsImmediately_ThenEveryResyncInterval()
		{
			_ntp.Handler = FakeSource.Returns(ServerNow);
			var sync = Create();

			sync.Start();

			Assert.AreEqual(1, _ntp.Calls);
			Assert.IsTrue(_time.IsServerTime);
			CollectionAssert.AreEqual(new[] { TimeSpan.FromMinutes(30) }, _delay.Requested);

			_clock.Elapsed += TimeSpan.FromMinutes(30);
			_delay.Elapse();

			Assert.AreEqual(2, _ntp.Calls);
			Assert.AreEqual(TimeSpan.FromMinutes(30), _delay.Requested[1]);
		}

		[Test]
		public void Start_AfterManualSync_WaitsOnlyTheRemainingInterval()
		{
			_ntp.Handler = FakeSource.Returns(ServerNow);
			var sync = Create();
			Run(sync.SyncAsync());
			_clock.Elapsed += TimeSpan.FromMinutes(10);

			sync.Start();

			Assert.AreEqual(1, _ntp.Calls);
			CollectionAssert.AreEqual(new[] { TimeSpan.FromMinutes(20) }, _delay.Requested);
		}

		[Test]
		public void Start_Failures_BackOffExponentiallyUpToTheCap()
		{
			var sync = Create(null, _ntp);

			sync.Start();

			for (var i = 0; i < 8; i++)
			{
				_delay.Elapse();
			}

			var expected = new[] { 5, 10, 20, 40, 80, 160, 300, 300, 300 };
			Assert.AreEqual(expected.Length, _delay.Requested.Count);

			for (var i = 0; i < expected.Length; i++)
			{
				Assert.AreEqual(TimeSpan.FromSeconds(expected[i]), _delay.Requested[i], $"wait {i}");
			}
		}

		[Test]
		public void Start_RetryJitter_IsHalfToFullOfTheCap()
		{
			_randomValue = 0;
			var sync = Create(null, _ntp);

			sync.Start();

			Assert.AreEqual(TimeSpan.FromSeconds(2.5), _delay.Requested[0]);
		}

		[Test]
		public void Start_MinimumOptions_AlwaysWaitBeforeRetrying()
		{
			// 즉시 끝나는 실패 원천과 최소 설정에서도 대기가 0 이 되지 않아 Start 가 돌아옵니다
			_randomValue = 0;
			var sync = Create(new ServerTimeSyncOptions
			{
				ResyncInterval = ServerTimeSyncOptions.MinResyncInterval,
				RetryDelay = ServerTimeSyncOptions.MinRetryDelay,
				MaxRetryDelay = ServerTimeSyncOptions.MinRetryDelay
			}, _ntp);

			sync.Start();

			Assert.AreEqual(1, _ntp.Calls);
			Assert.AreEqual(TimeSpan.FromMilliseconds(50), _delay.Requested[0]);
		}

		[Test]
		public void Start_TimeAdjustedHandlerTime_IsNotCountedAgainstTheInterval()
		{
			_ntp.Handler = FakeSource.Returns(ServerNow);
			_time.TimeAdjusted += _ => _clock.Elapsed += TimeSpan.FromSeconds(1);
			var sync = Create(new ServerTimeSyncOptions { ResyncInterval = TimeSpan.FromSeconds(1) }, _ntp);

			sync.Start();

			CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(1) }, _delay.Requested);
		}

		[Test]
		public void Start_ResyncAlwaysOverdue_StillYieldsBetweenIterations()
		{
			// 읽을 때마다 1초씩 가는 시계라 성공할 때마다 재동기화 기한(1초)이 이미 지나 있습니다
			var clock = new TickingClock();
			var time = new TimeService(clock);
			var source = new FakeSource("sync") { Handler = FakeSource.Returns(ServerNow) };
			_sync = new ServerTimeSync(time, new IServerTimeSource[] { source },
				new ServerTimeSyncOptions { ResyncInterval = ServerTimeSyncOptions.MinResyncInterval }, clock, _delay.Delay, () => 1);

			_sync.Start();

			Assert.AreEqual(1, source.Calls, "Start returns instead of looping");
			CollectionAssert.AreEqual(new[] { ServerTimeSyncOptions.MinRetryDelay }, _delay.Requested);

			_delay.Elapse();

			Assert.AreEqual(2, source.Calls);
			Assert.IsTrue(_delay.IsWaiting);
		}

		[Test]
		public void Start_RecoversAfterFailures_ThenWaitsResyncInterval()
		{
			var sync = Create(null, _ntp);
			sync.Start();
			_ntp.Handler = FakeSource.Returns(ServerNow);

			_delay.Elapse();

			Assert.IsTrue(_time.IsServerTime);
			Assert.AreEqual(TimeSpan.FromMinutes(30), _delay.Requested[1]);
		}

		[Test]
		public void HandleResume_WhileWaiting_SyncsImmediately()
		{
			_ntp.Handler = FakeSource.Returns(ServerNow);
			var sync = Create();
			sync.Start();

			Resume(sync);

			Assert.AreEqual(2, _ntp.Calls);
			Assert.IsTrue(_delay.IsWaiting);
			Assert.AreEqual(2, _delay.Requested.Count);
		}

		[Test]
		public void HandleResume_DuringFetch_SyncsAgainAfterItEnds()
		{
			var pending = new UniTaskCompletionSource<Result<ServerTimeSample>>();
			_ntp.Handler = (_, __) => pending.Task;
			var sync = Create(null, _ntp);
			sync.Start();

			Resume(sync);
			Assert.AreEqual(1, _ntp.Calls, "the fetch in flight is not duplicated");

			_ntp.Handler = FakeSource.Returns(ServerNow);
			pending.TrySetResult(Result<ServerTimeSample>.Fail(ResultCodes.Timeout, "slow"));

			Assert.AreEqual(2, _ntp.Calls, "a fetch that spanned the pause is followed by a fresh one");
			Assert.IsTrue(_time.IsServerTime);
		}

		[Test]
		public void HandleResume_ResetsBackoff()
		{
			var sync = Create(null, _ntp);
			sync.Start();
			_delay.Elapse();
			_delay.Elapse();
			Assert.AreEqual(TimeSpan.FromSeconds(20), _delay.Requested[2]);

			Resume(sync);

			Assert.AreEqual(4, _ntp.Calls);
			Assert.AreEqual(TimeSpan.FromSeconds(5), _delay.Requested[3]);
		}

		[Test]
		public void HandlePause_ResumeWithoutPause_IsIgnored()
		{
			// Unity 문서는 오브젝트가 시작할 때도 OnApplicationPause(false) 가 온다고 합니다
			_ntp.Handler = FakeSource.Returns(ServerNow);
			var sync = Create();
			sync.Start();

			sync.HandlePause(false);

			Assert.AreEqual(1, _ntp.Calls);
			Assert.AreEqual(1, _delay.Requested.Count);
		}

		[Test]
		public void HandleResume_WhenNotRunning_DoesNothing()
		{
			_ntp.Handler = FakeSource.Returns(ServerNow);
			var sync = Create();

			Resume(sync);
			Assert.AreEqual(0, _ntp.Calls);

			sync.Start();

			Assert.AreEqual(1, _ntp.Calls, "a resume before Start does not trigger an extra sync");
			CollectionAssert.AreEqual(new[] { TimeSpan.FromMinutes(30) }, _delay.Requested);
		}

		[Test]
		public void Stop_CancelsTheFetchInFlightAndTheLoop()
		{
			var pending = new UniTaskCompletionSource<Result<ServerTimeSample>>();
			_ntp.Handler = (_, token) =>
			{
				token.Register(() => pending.TrySetResult(Result<ServerTimeSample>.Fail(ResultCodes.Cancelled, "cancelled")));
				return pending.Task;
			};
			var sync = Create(null, _ntp);
			sync.Start();

			sync.Stop();

			Assert.IsTrue(_ntp.LastToken.IsCancellationRequested);
			Assert.AreEqual(1, _ntp.Calls);
			Assert.AreEqual(0, _delay.Requested.Count);
			Assert.IsFalse(_delay.IsWaiting);
		}

		[Test]
		public void Stop_FromACancellationCallback_KeepsTheNewRunStoppable()
		{
			var sync = Create(null, _ntp);
			var pending = new UniTaskCompletionSource<Result<ServerTimeSample>>();
			_ntp.Handler = (_, token) =>
			{
				token.Register(() =>
				{
					pending.TrySetResult(Result<ServerTimeSample>.Fail(ResultCodes.Cancelled, "cancelled"));
					sync.Stop();
					sync.Start();
				});
				_ntp.Handler = FakeSource.Returns(ServerNow);
				return pending.Task;
			};
			sync.Start();

			sync.Stop();

			Assert.IsNotNull(sync.Running, "the run started inside the callback survives the outer Stop");
			Assert.IsTrue(_delay.IsWaiting);

			sync.Stop();

			Assert.IsNull(sync.Running);
			Assert.IsFalse(_delay.IsWaiting);
		}

		[Test]
		public void Stop_CallbackThrows_RunIsStillStoppedAndRestartable()
		{
			var sync = Create(null, _ntp);
			_ntp.Handler = (_, token) =>
			{
				token.Register(() => throw new InvalidOperationException("callback failure"));
				return new UniTaskCompletionSource<Result<ServerTimeSample>>().Task;
			};
			sync.Start();

			Assert.Throws<AggregateException>(() => sync.Stop());
			Assert.IsNull(sync.Running);

			_ntp.Handler = FakeSource.Returns(ServerNow);
			sync.Start();

			Assert.IsNotNull(sync.Running);
			Assert.IsTrue(_time.IsServerTime);
		}

		[Test]
		public void EndRun_FromAnOldRun_DoesNotStopTheCurrentRun()
		{
			_ntp.Handler = FakeSource.Returns(ServerNow);
			var sync = Create(null, _ntp);
			sync.Start();
			var first = sync.Running;
			sync.Stop();
			sync.Start();

			sync.EndRun(first);

			Assert.IsNotNull(sync.Running);
			Assert.IsTrue(_delay.IsWaiting);

			sync.EndRun(sync.Running);

			Assert.IsNull(sync.Running);
			Assert.IsFalse(_delay.IsWaiting);
		}

		[Test]
		public void Stop_ThenStart_RunsASingleNewLoop()
		{
			_ntp.Handler = FakeSource.Returns(ServerNow);
			var sync = Create(null, _ntp);
			sync.Start();
			sync.Start();
			Assert.AreEqual(1, _ntp.Calls);

			sync.Stop();
			Assert.IsFalse(_delay.IsWaiting);
			_clock.Elapsed += TimeSpan.FromMinutes(30);
			sync.Start();

			Assert.AreEqual(2, _ntp.Calls);
			Assert.IsTrue(_delay.IsWaiting);

			Resume(sync);
			Assert.AreEqual(3, _ntp.Calls, "resume wakes the new loop's wait");
		}
	}
}
