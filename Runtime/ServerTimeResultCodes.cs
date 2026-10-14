// ReSharper disable once CheckNamespace

namespace Geuneda.ServerTime
{
	/// <summary>
	/// servertime 패키지의 결과 코드입니다(core 결과 코드 범위 표의 6000 블록).
	/// </summary>
	/// <remarks>
	/// 연결 실패, 제한 시간, 취소는 core 공용 코드(<c>NetworkError</c>, <c>Timeout</c>, <c>Cancelled</c>)를 씁니다.
	/// </remarks>
	public static class ServerTimeResultCodes
	{
		/// <summary>이 패키지 코드 블록의 시작입니다. 코드로는 쓰지 않습니다.</summary>
		public const int RangeStart = 6000;

		/// <summary>응답에서 쓸 수 있는 시각을 얻지 못했습니다(NTP 응답 검증 실패, Date 헤더 없음·형식 오류, 2024년 이전 시각).</summary>
		public const int InvalidResponse = RangeStart + 1;

		/// <summary>
		/// 왕복 시간이 <see cref="ServerTimeSyncOptions.MaxRoundTrip"/> 보다 길어 표본을 버렸습니다.
		/// 응답을 기다리는 동안 앱이 백그라운드에 있었거나 네트워크가 아주 느린 경우입니다.
		/// </summary>
		public const int RoundTripTooLong = RangeStart + 2;
	}
}
