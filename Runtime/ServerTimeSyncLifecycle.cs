using System.Threading;
using UnityEngine;

// ReSharper disable once CheckNamespace

namespace Geuneda.ServerTime
{
	/// <summary>
	/// <see cref="ServerTimeSync.Start"/> 가 만드는 숨은 컴포넌트입니다. 앱 일시정지·복귀를 동기화에 넘기고,
	/// 앱이 끝나거나(에디터에서는 플레이를 멈출 때) 파괴되면 자기를 만든 실행을 멈춥니다.
	/// </summary>
	[AddComponentMenu("")]
	internal sealed class ServerTimeSyncLifecycle : MonoBehaviour
	{
		private ServerTimeSync _sync;
		private CancellationTokenSource _run;

		public void Bind(ServerTimeSync sync, CancellationTokenSource run)
		{
			_sync = sync;
			_run = run;
		}

		/// <summary>동기화가 직접 멈출 때 먼저 끊습니다. 프레임 끝에 오는 <c>OnDestroy</c> 가 다음 실행을 멈추지 않게 합니다.</summary>
		public void Unbind()
		{
			_sync = null;
			_run = null;
		}

		private void OnApplicationPause(bool paused)
		{
			_sync?.HandlePause(paused);
		}

		private void OnApplicationQuit()
		{
			End();
		}

		private void OnDestroy()
		{
			End();
		}

		private void End()
		{
			var sync = _sync;
			var run = _run;

			Unbind();
			sync?.EndRun(run);
		}
	}
}
