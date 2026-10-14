using System;

// ReSharper disable once CheckNamespace

namespace Geuneda.ServerTime
{
	/// <summary>
	/// 시간 원천이 한 번 잰 서버 시각입니다. <see cref="ReceivedAt"/> 순간의 서버 시각 추정값과 왕복 지연을 담습니다.
	/// </summary>
	public readonly struct ServerTimeSample
	{
		/// <summary>원천이 받아들이는 가장 이른 서버 시각입니다. 이보다 이르면 응답 오류로 봅니다.</summary>
		internal static readonly DateTime MinValidUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

		/// <summary>원천이 받아들이는 가장 늦은 서버 시각입니다. 보정을 더해도 <see cref="DateTime.MaxValue"/> 를 넘지 않게 합니다.</summary>
		internal static readonly DateTime MaxValidUtc = new DateTime(2200, 1, 1, 0, 0, 0, DateTimeKind.Utc);

		/// <summary><see cref="ReceivedAt"/> 순간의 서버 시각 추정값입니다(<c>Kind</c> 는 <c>Utc</c>).</summary>
		public DateTime ServerUtc { get; }

		/// <summary>응답을 받은 순간의 단조 시계(<c>IMonotonicClock.Elapsed</c>) 값입니다.</summary>
		public TimeSpan ReceivedAt { get; }

		/// <summary>네트워크 왕복 지연입니다(서버 처리 시간을 뺄 수 있으면 뺌). 추정 오차는 대략 이 값의 절반 이내입니다.</summary>
		public TimeSpan RoundTrip { get; }

		/// <exception cref="ArgumentException"><paramref name="serverUtc"/> 의 <c>Kind</c> 가 <c>Utc</c> 가 아닌 경우</exception>
		/// <exception cref="ArgumentOutOfRangeException"><paramref name="roundTrip"/> 이 음수인 경우</exception>
		public ServerTimeSample(DateTime serverUtc, TimeSpan receivedAt, TimeSpan roundTrip)
		{
			if (serverUtc.Kind != DateTimeKind.Utc)
			{
				throw new ArgumentException("Server time must be UTC (DateTimeKind.Utc)", nameof(serverUtc));
			}

			if (roundTrip < TimeSpan.Zero)
			{
				throw new ArgumentOutOfRangeException(nameof(roundTrip), roundTrip, "Round trip must not be negative");
			}

			ServerUtc = serverUtc;
			ReceivedAt = receivedAt;
			RoundTrip = roundTrip;
		}

		public override string ToString()
		{
			return $"{ServerUtc:o} (round trip {RoundTrip.TotalMilliseconds:0} ms)";
		}
	}
}
