using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Geuneda.Core;

// ReSharper disable once CheckNamespace

namespace Geuneda.ServerTime
{
	/// <summary>
	/// 시간 원천으로 서버 시각을 가져와 services 의 시간 서비스(<c>ITimeManipulator</c>)를 맞춥니다. 기본 구현은 <see cref="ServerTimeSync"/> 이고,
	/// services 의 <c>MainInstaller</c> 에 이 인터페이스로 바인딩합니다(정리는 <c>MainInstaller.CleanDispose&lt;IServerTimeSync&gt;()</c>).
	/// </summary>
	/// <remarks>
	/// 맞췄는지는 <c>ITimeService.IsServerTime</c>, 시각이 바뀐 것은 <c>ITimeService.TimeAdjusted</c> 로 봅니다. 메인 스레드에서 부릅니다.
	/// <see cref="IDisposable.Dispose"/> 는 <see cref="Stop"/> 과 같고 여러 번 불러도 됩니다.
	/// </remarks>
	public interface IServerTimeSync : IDisposable
	{
		/// <summary>
		/// 원천을 순서대로 시도해 처음 성공한 표본으로 시간 서비스를 맞춥니다. 모두 실패하면 첫 실패 코드와 원천별 실패 내용을 돌려줍니다.
		/// 원천이 결과 대신 던진 예외(버그·오용)는 그대로 던집니다.
		/// 부팅 때 기다렸다가 <see cref="Start"/> 하는 식으로 씁니다.
		/// </summary>
		UniTask<Result<ServerTimeSample>> SyncAsync(CancellationToken cancellationToken = default);

		/// <summary>
		/// 백그라운드 동기화를 시작합니다. 아직 맞춘 적 없으면 바로 맞추고, 실패하면 지수 백오프로 다시 시도하고, 성공하면 재동기화 간격마다,
		/// 앱이 포그라운드로 돌아오면 바로 다시 맞춥니다. 이미 돌고 있으면 아무것도 하지 않습니다.
		/// 앱이 끝나면(에디터에서는 플레이를 멈추면) 스스로 멈추고, 원천이 예외를 던지면 Error 로그를 남기고 멈춥니다.
		/// </summary>
		void Start();

		/// <summary>백그라운드 동기화를 멈춥니다. 진행 중인 요청은 취소됩니다.</summary>
		void Stop();
	}
}
