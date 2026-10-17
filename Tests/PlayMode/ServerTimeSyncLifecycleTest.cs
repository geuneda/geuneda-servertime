using System;
using System.Collections;
using System.Threading;
using Cysharp.Threading.Tasks;
using Geuneda.Core;
using Geuneda.ServerTime;
using Geuneda.Services;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

// ReSharper disable once CheckNamespace

namespace Geuneda.ServerTime.Tests
{
	/// <summary>플레이 중에만 만들어지는 숨은 수명 컴포넌트가 실제 Unity 메시지로 동기화를 다루는지 확인합니다.</summary>
	public class ServerTimeSyncLifecycleTest
	{
		private static readonly DateTime ServerNow = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);

		private ServerTimeSync _sync;

		/// <summary>호출 수를 세고, 지정하면 끝나지 않는 요청으로 토큰을 기록하는 원천입니다.</summary>
		private sealed class CountingSource : IServerTimeSource
		{
			public string Name => "counting";
			public int Calls { get; private set; }
			public bool Hang { get; set; }
			public CancellationToken LastToken { get; private set; }

			public UniTask<Result<ServerTimeSample>> FetchAsync(IMonotonicClock clock, CancellationToken cancellationToken)
			{
				Calls++;
				LastToken = cancellationToken;

				return Hang
					? new UniTaskCompletionSource<Result<ServerTimeSample>>().Task
					: UniTask.FromResult(Result<ServerTimeSample>.Ok(new ServerTimeSample(ServerNow, clock.Elapsed, TimeSpan.Zero)));
			}
		}

		[TearDown]
		public void TearDown()
		{
			_sync?.Dispose();
			MainInstaller.Clean<IServerTimeSync>();
		}

		private ServerTimeSync Create(CountingSource source)
		{
			_sync = new ServerTimeSync(new TimeService(), new IServerTimeSource[] { source });
			return _sync;
		}

		[UnityTest]
		public IEnumerator Start_SyncsOnceDuringTheFirstFrames()
		{
			var source = new CountingSource();
			var sync = Create(source);

			sync.Start();
			Assert.IsNotNull(sync.LifecycleObject);

			// 시작 직후 몇 프레임 동안 추가 동기화가 없어야 합니다(2026-10-14 확인: 실행 중에 만든 오브젝트에는 시작 때 OnApplicationPause 가 오지 않음.
			// 그 메시지를 복귀로 보지 않는 가드는 EditMode HandlePause_ResumeWithoutPause_IsIgnored 가 확인)
			for (var i = 0; i < 5; i++)
			{
				yield return null;
			}

			Assert.AreEqual(1, source.Calls);
		}

		[UnityTest]
		public IEnumerator PauseThenResumeMessages_SyncAgain()
		{
			var source = new CountingSource();
			var sync = Create(source);
			sync.Start();
			yield return null;

			sync.LifecycleObject.SendMessage("OnApplicationPause", true);
			sync.LifecycleObject.SendMessage("OnApplicationPause", false);
			yield return null;

			Assert.AreEqual(2, source.Calls);
		}

		[UnityTest]
		public IEnumerator DestroyingTheLifecycleObject_StopsTheRun()
		{
			var source = new CountingSource { Hang = true };
			var sync = Create(source);
			sync.Start();

			Object.Destroy(sync.LifecycleObject);
			yield return null;

			Assert.IsTrue(source.LastToken.IsCancellationRequested);
			Assert.IsNull(sync.Running);
		}

		[UnityTest]
		public IEnumerator ApplicationQuitMessage_StopsTheRunAndDestroysTheObject()
		{
			var source = new CountingSource { Hang = true };
			var sync = Create(source);
			sync.Start();
			var lifecycle = sync.LifecycleObject;

			// 에디터에서는 플레이를 멈출 때 OnApplicationQuit 이 옵니다
			lifecycle.SendMessage("OnApplicationQuit");
			yield return null;

			Assert.IsTrue(source.LastToken.IsCancellationRequested);
			Assert.IsNull(sync.Running);
			Assert.IsTrue(lifecycle == null, "the hidden object is destroyed");
		}

		[UnityTest]
		public IEnumerator CleanDispose_StopsTheRunAndDestroysTheObject()
		{
			var source = new CountingSource { Hang = true };
			var sync = Create(source);
			MainInstaller.Bind<IServerTimeSync>(sync);
			sync.Start();
			var lifecycle = sync.LifecycleObject;

			Assert.IsTrue(MainInstaller.CleanDispose<IServerTimeSync>());
			yield return null;

			Assert.IsTrue(source.LastToken.IsCancellationRequested);
			Assert.IsNull(sync.Running);
			Assert.IsTrue(lifecycle == null, "the hidden object is destroyed");
		}

		[UnityTest]
		public IEnumerator Stop_ThenStart_TheOldObjectsLateDestroyDoesNotStopTheNewRun()
		{
			var source = new CountingSource { Hang = true };
			var sync = Create(source);
			sync.Start();
			var first = sync.LifecycleObject;

			sync.Stop();
			sync.Start();
			yield return null;

			Assert.IsTrue(first == null, "the old object is destroyed at the end of the frame");
			Assert.IsNotNull(sync.Running);
			Assert.IsFalse(source.LastToken.IsCancellationRequested);
			Assert.AreEqual(HideFlags.HideInHierarchy, sync.LifecycleObject.hideFlags);
		}
	}
}
