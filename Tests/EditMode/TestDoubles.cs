using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Geuneda.Core;
using Geuneda.ServerTime;

// ReSharper disable once CheckNamespace

namespace Geuneda.ServerTime.Tests
{
	internal sealed class FakeClock : IMonotonicClock
	{
		public TimeSpan Elapsed { get; set; } = TimeSpan.FromHours(3);
	}

	/// <summary>읽을 때마다 1초씩 가는 시계입니다.</summary>
	internal sealed class TickingClock : IMonotonicClock
	{
		private TimeSpan _elapsed = TimeSpan.FromHours(1);

		public TimeSpan Elapsed => _elapsed += TimeSpan.FromSeconds(1);
	}

	/// <summary>호출마다 <see cref="Handler"/> 를 부르는 원천입니다. 기본은 실패입니다.</summary>
	internal sealed class FakeSource : IServerTimeSource
	{
		public string Name { get; }
		public int Calls { get; private set; }
		public CancellationToken LastToken { get; private set; }
		public Func<IMonotonicClock, CancellationToken, UniTask<Result<ServerTimeSample>>> Handler { get; set; }

		public FakeSource(string name)
		{
			Name = name;
			Handler = Fails(ResultCodes.NetworkError);
		}

		public UniTask<Result<ServerTimeSample>> FetchAsync(IMonotonicClock clock, CancellationToken cancellationToken)
		{
			Calls++;
			LastToken = cancellationToken;
			return Handler(clock, cancellationToken);
		}

		/// <summary>지금 받은 것처럼 서버 시각 <paramref name="serverUtc"/> 표본을 돌려줍니다.</summary>
		public static Func<IMonotonicClock, CancellationToken, UniTask<Result<ServerTimeSample>>> Returns(DateTime serverUtc, TimeSpan roundTrip = default)
		{
			return (clock, _) => UniTask.FromResult(Result<ServerTimeSample>.Ok(new ServerTimeSample(serverUtc, clock.Elapsed, roundTrip)));
		}

		public static Func<IMonotonicClock, CancellationToken, UniTask<Result<ServerTimeSample>>> Fails(int code)
		{
			return (_, __) => UniTask.FromResult(Result<ServerTimeSample>.Fail(code, $"fail {code}"));
		}
	}

	/// <summary>대기를 기록하고 테스트가 끝낼 때까지 기다리게 하는 지연입니다. 취소되면 바로 끝납니다.</summary>
	internal sealed class ManualDelay
	{
		private UniTaskCompletionSource _pending;

		public readonly List<TimeSpan> Requested = new List<TimeSpan>();

		public bool IsWaiting => _pending != null;

		public UniTask Delay(TimeSpan wait, CancellationToken cancellationToken)
		{
			var pending = new UniTaskCompletionSource();
			Requested.Add(wait);
			_pending = pending;
			cancellationToken.Register(() =>
			{
				if (_pending == pending)
				{
					_pending = null;
				}

				pending.TrySetCanceled(cancellationToken);
			});

			return pending.Task;
		}

		/// <summary>지금 기다리는 대기를 끝냅니다. 이어지는 루프는 이 호출 안에서 동기로 돕니다.</summary>
		public void Elapse()
		{
			var pending = _pending;
			_pending = null;
			pending.TrySetResult();
		}
	}
}
