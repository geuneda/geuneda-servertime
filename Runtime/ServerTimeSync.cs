using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using Geuneda.Core;
using Geuneda.Services;
using UnityEngine;
using Object = UnityEngine.Object;
using Random = System.Random;

// ReSharper disable once CheckNamespace

namespace Geuneda.ServerTime
{
	/// <summary>
	/// 기본 서버 시간 동기화입니다. 부트스트랩에서 시간 서비스와 원천 목록으로 만들고, <c>MainInstaller</c> 에 <see cref="IServerTimeSync"/> 로 바인딩합니다.
	/// </summary>
	/// <remarks>
	/// <para>
	/// 표본의 서버 시각에 받은 뒤 흐른 단조 시계 시간을 더해 <c>ITimeManipulator.SetServerTime</c> 에 넣습니다. 시간 서비스와 같은 단조 시계를 써야 하므로,
	/// 시간 서비스를 시계를 넘겨 만들었다면 같은 시계를 넘깁니다(기본은 둘 다 core <see cref="MonotonicClock"/>).
	/// </para>
	/// <para>
	/// 원천이 다른 스레드에서 끝나도 메인 스레드로 돌아와 시간 서비스를 바꿉니다. 원천이 결과 대신 던진 예외(버그·오용)는 숨기지 않습니다.
	/// <see cref="SyncAsync"/> 는 그대로 던지고, 백그라운드 동기화는 Error 로그를 남기고 멈춥니다.
	/// 재동기화로 현재 시각이 조금 뒤로 갈 수 있습니다(바뀐 양은 <c>TimeAdjusted</c>).
	/// </para>
	/// <para>
	/// 백그라운드 동기화는 앱이 끝나거나(에디터에서는 플레이를 멈출 때) 숨은 수명 오브젝트가 파괴되면 스스로 멈춥니다.
	/// </para>
	/// </remarks>
	public sealed class ServerTimeSync : IServerTimeSync, IDisposable
	{
		private static readonly ModuleLogger Logger = new ModuleLogger("Geuneda.ServerTime");

		private readonly ITimeManipulator _time;
		private readonly IServerTimeSource[] _sources;
		private readonly IMonotonicClock _clock;
		private readonly TimeSpan _resyncInterval;
		private readonly TimeSpan _retryDelay;
		private readonly TimeSpan _maxRetryDelay;
		private readonly TimeSpan _maxRoundTrip;
		private readonly Func<TimeSpan, CancellationToken, UniTask> _delay;
		private readonly Func<double> _random;

		private CancellationTokenSource _running;
		private CancellationTokenSource _wake;
		private bool _resumePending;
		private bool _paused;
		private TimeSpan? _lastSyncAt;
		private GameObject _lifecycleObject;

		internal CancellationTokenSource Running => _running;
		internal GameObject LifecycleObject => _lifecycleObject;

		/// <param name="time">맞출 시간 서비스</param>
		/// <param name="sources">시도할 원천. 순서대로 시도합니다(예: NTP 다음 HTTPS Date)</param>
		/// <param name="options">null 이면 기본값</param>
		/// <param name="clock">null 이면 core <see cref="MonotonicClock"/>. 시간 서비스와 같은 시계여야 합니다</param>
		/// <exception cref="ArgumentNullException"><paramref name="time"/> 이나 <paramref name="sources"/> 가 null 인 경우</exception>
		/// <exception cref="ArgumentException">원천이 없거나 null 원천이 있는 경우</exception>
		/// <exception cref="ArgumentOutOfRangeException">설정 값이 최솟값보다 작거나 <c>MaxRetryDelay</c> 가 <c>RetryDelay</c> 보다 작은 경우</exception>
		public ServerTimeSync(ITimeManipulator time, IEnumerable<IServerTimeSource> sources, ServerTimeSyncOptions options = null,
			IMonotonicClock clock = null)
			: this(time, sources, options, clock ?? new MonotonicClock(), DefaultDelay, CreateDefaultRandom())
		{
		}

		internal ServerTimeSync(ITimeManipulator time, IEnumerable<IServerTimeSource> sources, ServerTimeSyncOptions options,
			IMonotonicClock clock, Func<TimeSpan, CancellationToken, UniTask> delay, Func<double> random)
		{
			_time = time ?? throw new ArgumentNullException(nameof(time));
			_sources = sources?.ToArray() ?? throw new ArgumentNullException(nameof(sources));

			if (_sources.Length == 0 || _sources.Any(source => source == null))
			{
				throw new ArgumentException("At least one source is required and sources must not contain null", nameof(sources));
			}

			options ??= new ServerTimeSyncOptions();

			// 대기가 0 이 되면 즉시 끝나는 원천과 함께 메인 스레드를 양보하지 않고 돌 수 있어 최솟값을 둡니다
			if (options.ResyncInterval < ServerTimeSyncOptions.MinResyncInterval || options.RetryDelay < ServerTimeSyncOptions.MinRetryDelay ||
			    options.MaxRoundTrip <= TimeSpan.Zero)
			{
				throw new ArgumentOutOfRangeException(nameof(options),
					$"ResyncInterval must be at least {ServerTimeSyncOptions.MinResyncInterval}, RetryDelay at least {ServerTimeSyncOptions.MinRetryDelay} and MaxRoundTrip positive");
			}

			if (options.MaxRetryDelay < options.RetryDelay)
			{
				throw new ArgumentOutOfRangeException(nameof(options), "MaxRetryDelay must not be less than RetryDelay");
			}

			_resyncInterval = options.ResyncInterval;
			_retryDelay = options.RetryDelay;
			_maxRetryDelay = options.MaxRetryDelay;
			_maxRoundTrip = options.MaxRoundTrip;
			_clock = clock ?? throw new ArgumentNullException(nameof(clock));
			_delay = delay;
			_random = random;
		}

		/// <inheritdoc />
		public async UniTask<Result<ServerTimeSample>> SyncAsync(CancellationToken cancellationToken = default)
		{
			var failures = new List<string>();
			var firstFailureCode = ResultCodes.Cancelled;

			foreach (var source in _sources)
			{
				if (cancellationToken.IsCancellationRequested)
				{
					return Result<ServerTimeSample>.Fail(ResultCodes.Cancelled, "Server time sync cancelled");
				}

				var result = await FetchAsync(source, cancellationToken);

				if (cancellationToken.IsCancellationRequested)
				{
					return Result<ServerTimeSample>.Fail(ResultCodes.Cancelled, "Server time sync cancelled");
				}

				if (result.TryGetValue(out var sample))
				{
					if (sample.RoundTrip <= _maxRoundTrip)
					{
						Apply(source, sample);
						return result;
					}

					result = Result<ServerTimeSample>.Fail(ServerTimeResultCodes.RoundTripTooLong,
						$"round trip {sample.RoundTrip.TotalMilliseconds:0} ms exceeds {_maxRoundTrip.TotalMilliseconds:0} ms");
				}

				Logger.Debug($"Server time source {source.Name} failed: {result}");

				if (failures.Count == 0)
				{
					firstFailureCode = result.Code;
				}

				failures.Add($"{source.Name}: {result}");
			}

			var message = $"All server time sources failed ({string.Join("; ", failures)})";
			Logger.Warning(message);

			return Result<ServerTimeSample>.Fail(firstFailureCode, message);
		}

		/// <inheritdoc />
		public void Start()
		{
			if (_running != null)
			{
				return;
			}

			var run = new CancellationTokenSource();
			_running = run;
			_lifecycleObject = CreateLifecycle(run);
			RunAsync(run).Forget();
		}

		/// <inheritdoc />
		public void Stop()
		{
			var run = _running;

			if (run == null)
			{
				return;
			}

			// 취소 콜백이 Stop·Start 를 다시 불러도 새 실행을 건드리지 않도록 공유 상태를 먼저 비우고 이전 실행만 정리합니다
			var lifecycle = _lifecycleObject;
			_running = null;
			_lifecycleObject = null;
			_resumePending = false;
			_paused = false;
			DestroyLifecycle(lifecycle);

			// 루프가 아직 이 토큰을 읽을 수 있어 Dispose 하지 않습니다(타이머가 없는 CTS 라 GC 로 충분).
			// 취소 콜백의 예외는 AggregateException 으로 나가지만 이 실행은 이미 멈춘 상태입니다
			run.Cancel();
		}

		public void Dispose()
		{
			Stop();
		}

		/// <summary>
		/// 앱 일시정지·복귀(<c>OnApplicationPause</c>)를 받습니다. 일시정지 뒤에 돌아오면, 기다리는 중이면 바로 맞추고 맞추는 중이면 끝난 뒤 한 번 더 맞춥니다.
		/// </summary>
		internal void HandlePause(bool paused)
		{
			if (paused)
			{
				_paused = true;
				return;
			}

			// 일시정지를 먼저 받은 복귀만 봅니다. Unity 문서는 오브젝트가 시작할 때도 OnApplicationPause(false) 가 온다고 하므로 그 메시지를 복귀로 보지 않습니다
			var resumed = _paused;
			_paused = false;

			if (!resumed || _running == null)
			{
				return;
			}

			_resumePending = true;
			_wake?.Cancel();
		}

		/// <summary><paramref name="run"/> 이 아직 지금 실행이면 멈춥니다. 수명 오브젝트(앱 종료·파괴)와 루프의 예외 처리가 부릅니다.</summary>
		internal void EndRun(CancellationTokenSource run)
		{
			if (run != null && _running == run)
			{
				Stop();
			}
		}

		/// <summary>
		/// <paramref name="failures"/> 번 연속 실패한 뒤 다음 시도까지 기다릴 시간입니다.
		/// </summary>
		internal TimeSpan NextWait(int failures)
		{
			if (_resumePending)
			{
				return TimeSpan.Zero;
			}

			if (failures > 0)
			{
				// 틱으로 계산해야 작은 설정 값이 밀리초 반올림으로 0 이 되지 않습니다
				var exponential = _retryDelay.Ticks * Math.Pow(2, Math.Min(failures - 1, 30));
				var cap = Math.Min(_maxRetryDelay.Ticks, exponential);

				return TimeSpan.FromTicks((long) (cap * (0.5 + 0.5 * _random())));
			}

			if (!_lastSyncAt.HasValue)
			{
				return TimeSpan.Zero;
			}

			var remaining = _resyncInterval - (_clock.Elapsed - _lastSyncAt.Value);

			return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
		}

		private async UniTaskVoid RunAsync(CancellationTokenSource run)
		{
			var running = run.Token;
			var failures = 0;
			var first = true;

			try
			{
				while (!running.IsCancellationRequested)
				{
					var wait = NextWait(failures);

					// 첫 동기화와 포그라운드 복귀는 바로 하고, 그 밖에는 반복 사이에 꼭 한 번 실행을 양보합니다.
					// 즉시 끝나는 원천과 오래 걸리는 처리기가 겹쳐 재동기화 기한이 매번 지나 있어도 메인 스레드를 붙잡지 않게 합니다
					if (wait <= TimeSpan.Zero && !first && !_resumePending)
					{
						wait = ServerTimeSyncOptions.MinRetryDelay;
					}

					first = false;

					if (wait > TimeSpan.Zero)
					{
						await WaitAsync(wait, running);

						if (running.IsCancellationRequested)
						{
							return;
						}
					}

					if (_resumePending)
					{
						_resumePending = false;
						failures = 0;
					}

					var result = await SyncAsync(running);
					failures = result.IsSuccess ? 0 : failures + 1;
				}
			}
			catch (OperationCanceledException) when (running.IsCancellationRequested)
			{
				// 멈춤
			}
			catch (Exception exception)
			{
				// 원천의 버그·오용은 다시 시도해도 고쳐지지 않으므로 숨기지 않고 멈춥니다
				Logger.Error(exception, "Server time sync stopped by an unexpected exception");
				EndRun(run);
			}
		}

		private async UniTask WaitAsync(TimeSpan wait, CancellationToken running)
		{
			var wake = CancellationTokenSource.CreateLinkedTokenSource(running);
			_wake = wake;

			try
			{
				await _delay(wait, wake.Token);
			}
			catch (OperationCanceledException) when (wake.IsCancellationRequested)
			{
				// 멈췄거나 포그라운드로 돌아옴
			}
			finally
			{
				// Stop 뒤 바로 Start 한 새 루프의 대기를 지우지 않게 자기 것일 때만 비웁니다
				if (_wake == wake)
				{
					_wake = null;
				}

				wake.Dispose();
			}
		}

		private async UniTask<Result<ServerTimeSample>> FetchAsync(IServerTimeSource source, CancellationToken cancellationToken)
		{
			try
			{
				return await source.FetchAsync(_clock, cancellationToken);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				return Result<ServerTimeSample>.Fail(ResultCodes.Cancelled, $"{source.Name} cancelled");
			}
			finally
			{
				// 원천이 다른 스레드에서 끝났어도(예외 포함) 시간 서비스(Unity API)와 호출자는 메인 스레드로 돌아옵니다
				await UniTask.SwitchToMainThread();
			}
		}

		private void Apply(IServerTimeSource source, ServerTimeSample sample)
		{
			var elapsed = _clock.Elapsed;
			var serverNow = sample.ServerUtc + (elapsed - sample.ReceivedAt);

			_time.SetServerTime(serverNow);
			// TimeAdjusted 처리기가 쓴 시간은 재동기화 간격에 넣지 않습니다
			_lastSyncAt = _clock.Elapsed;

			Logger.Info($"Server time synced from {source.Name} (round trip {sample.RoundTrip.TotalMilliseconds:0} ms, " +
			            $"device clock offset {(DateTime.UtcNow - serverNow).TotalSeconds:0.###} s)");
		}

		private GameObject CreateLifecycle(CancellationTokenSource run)
		{
			// 에디트 모드에는 앱 일시정지가 없고 DontDestroyOnLoad 도 쓸 수 없습니다
			if (!Application.isPlaying)
			{
				return null;
			}

			// DontSave 플래그를 붙이지 않아야 플레이를 멈출 때 함께 파괴됩니다
			var lifecycleObject = new GameObject(nameof(ServerTimeSyncLifecycle)) { hideFlags = HideFlags.HideInHierarchy };
			Object.DontDestroyOnLoad(lifecycleObject);
			lifecycleObject.AddComponent<ServerTimeSyncLifecycle>().Bind(this, run);

			return lifecycleObject;
		}

		private static void DestroyLifecycle(GameObject lifecycleObject)
		{
			if (lifecycleObject == null)
			{
				return;
			}

			// 파괴는 프레임 끝에 일어나므로, 먼저 끊어야 늦게 오는 OnDestroy 가 새 실행을 멈추지 않습니다
			if (lifecycleObject.TryGetComponent<ServerTimeSyncLifecycle>(out var lifecycle))
			{
				lifecycle.Unbind();
			}

			if (Application.isPlaying)
			{
				Object.Destroy(lifecycleObject);
			}
			else
			{
				Object.DestroyImmediate(lifecycleObject);
			}
		}

		private static UniTask DefaultDelay(TimeSpan delay, CancellationToken cancellationToken)
		{
			return UniTask.Delay(delay, DelayType.Realtime, PlayerLoopTiming.Update, cancellationToken);
		}

		private static Func<double> CreateDefaultRandom()
		{
			var random = new Random();

			return random.NextDouble;
		}
	}
}
