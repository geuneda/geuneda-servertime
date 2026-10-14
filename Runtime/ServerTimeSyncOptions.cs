using System;

// ReSharper disable once CheckNamespace

namespace Geuneda.ServerTime
{
	/// <summary><see cref="ServerTimeSync"/> 설정입니다.</summary>
	public sealed class ServerTimeSyncOptions
	{
		/// <summary><see cref="ResyncInterval"/> 의 최솟값 1초입니다.</summary>
		public static readonly TimeSpan MinResyncInterval = TimeSpan.FromSeconds(1);

		/// <summary><see cref="RetryDelay"/> 의 최솟값 100ms 입니다. 대기가 0 이 되어 메인 스레드를 양보하지 않고 도는 것을 막습니다.</summary>
		public static readonly TimeSpan MinRetryDelay = TimeSpan.FromMilliseconds(100);

		/// <summary>성공한 뒤 다시 맞추는 간격입니다. 기본 30분, 최소 1초입니다(기기 시계 오차는 보통 하루 수 초 이내).</summary>
		public TimeSpan ResyncInterval { get; set; } = TimeSpan.FromMinutes(30);

		/// <summary>모든 원천이 실패한 뒤 첫 재시도까지의 대기 상한입니다. 실패할 때마다 두 배가 되고, 실제 대기는 상한의 50~100% 무작위입니다. 기본 5초, 최소 100ms 입니다.</summary>
		public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(5);

		/// <summary>재시도 대기 상한의 최댓값입니다. 기본 5분입니다.</summary>
		public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(5);

		/// <summary>
		/// 받아들이는 최대 왕복 시간입니다. 넘으면 표본을 버리고(<see cref="ServerTimeResultCodes.RoundTripTooLong"/>) 다음 원천으로 넘어갑니다.
		/// 응답을 기다리는 동안 앱이 백그라운드에 있었던 표본은 추정이 크게 틀리므로 버리기 위한 것입니다. 기본 10초입니다.
		/// </summary>
		public TimeSpan MaxRoundTrip { get; set; } = TimeSpan.FromSeconds(10);
	}
}
