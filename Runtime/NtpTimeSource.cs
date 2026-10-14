using System;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using Geuneda.Core;

// ReSharper disable once CheckNamespace

namespace Geuneda.ServerTime
{
	/// <summary>
	/// NTP 서버(UDP)에서 시각을 가져오는 원천입니다. 서버 부하가 없고 밀리초 해상도입니다.
	/// </summary>
	/// <remarks>
	/// <para>
	/// DNS 조회, 보내기, 받기를 스레드 풀에서 하고 보낸 순간·받은 순간을 그 스레드에서 바로 잽니다. 주소가 여러 개면 소켓 오류가 날 때 다음 주소를 씁니다.
	/// 전체 제한 시간을 넘으면 <c>Timeout</c> 입니다. 응답은 요청에 넣은 무작위 값을 돌려줘야 하고, 서버 모드·버전·계층·윤초 표시를 확인합니다.
	/// 정상 실패(소켓 오류, 제한 시간, 취소)만 결과로 바꾸고 예상하지 못한 예외는 던집니다. 제한 시간·취소로 돌아간 뒤에 끝난 작업의 예외는 Error 로그로 남깁니다.
	/// </para>
	/// <para>
	/// UDP NTP 는 인증이 없어, 네트워크를 장악한 사용자(루팅 기기, 프록시)는 응답을 위조할 수 있습니다. 위조가 문제 되는 값은 서버가 판정합니다.
	/// WebGL 은 UDP 를 쓸 수 없어 바로 <c>NetworkError</c> 로 실패하므로 <see cref="HttpDateTimeSource"/> 를 함께 둡니다.
	/// </para>
	/// </remarks>
	public sealed class NtpTimeSource : IServerTimeSource
	{
		/// <summary>기본 서버입니다. 누구나 쓸 수 있는 Google 공개 NTP 입니다(윤초를 하루에 걸쳐 나눠 반영).</summary>
		public const string DefaultHost = "time.google.com";

		public const int DefaultPort = 123;

		/// <summary>기본 제한 시간(DNS 조회부터 응답까지) 5초입니다.</summary>
		public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

		private const int ReceiveBufferLength = 512;

		private static readonly ModuleLogger Logger = new ModuleLogger("Geuneda.ServerTime.Ntp");

		private readonly string _host;
		private readonly int _port;
		private readonly TimeSpan _timeout;

		/// <inheritdoc />
		public string Name { get; }

		/// <exception cref="ArgumentException"><paramref name="host"/> 가 비었거나 공백인 경우</exception>
		/// <exception cref="ArgumentOutOfRangeException">포트가 1 ~ 65535 밖이거나 제한 시간이 0 이하인 경우</exception>
		public NtpTimeSource(string host = DefaultHost, int port = DefaultPort, TimeSpan? timeout = null)
		{
			if (string.IsNullOrWhiteSpace(host))
			{
				throw new ArgumentException("Host must not be empty", nameof(host));
			}

			if (port < 1 || port > 65535)
			{
				throw new ArgumentOutOfRangeException(nameof(port), port, "Port must be 1 to 65535");
			}

			_timeout = timeout ?? DefaultTimeout;

			if (_timeout <= TimeSpan.Zero || _timeout.TotalMilliseconds > int.MaxValue)
			{
				throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Timeout must be positive");
			}

			_host = host;
			_port = port;
			Name = $"ntp:{host}";
		}

		/// <inheritdoc />
		public UniTask<Result<ServerTimeSample>> FetchAsync(IMonotonicClock clock, CancellationToken cancellationToken)
		{
			if (clock == null)
			{
				throw new ArgumentNullException(nameof(clock));
			}

#if UNITY_WEBGL && !UNITY_EDITOR
			// 스레드 풀과 UDP 가 없어 기다리지 않고 실패해야 다음 원천으로 넘어갑니다
			return UniTask.FromResult(Result<ServerTimeSample>.Fail(ResultCodes.NetworkError, $"{Name}: UDP is not available on WebGL"));
#else
			return FetchCoreAsync(clock, cancellationToken);
#endif
		}

		private async UniTask<Result<ServerTimeSample>> FetchCoreAsync(IMonotonicClock clock, CancellationToken cancellationToken)
		{
			if (cancellationToken.IsCancellationRequested)
			{
				return Result<ServerTimeSample>.Fail(ResultCodes.Cancelled, $"{Name} cancelled");
			}

			var onMainThread = PlayerLoopHelper.IsMainThread;

			try
			{
				using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
				{
					timeout.CancelAfter(_timeout);
					var token = timeout.Token;

					// DNS 조회처럼 막히는 호출이 제한 시간을 넘겨도 기다리지 않습니다. 작업은 끝까지 관찰해 기다림을 멈춘 뒤에 온 예외도 버리지 않습니다.
					// 이어지는 작업은 항상 비동기로 실행합니다. UniTaskCompletionSource 는 잠금을 쥔 채 continuation 을 바로 실행할 수 있어,
					// 취소 콜백이 그 잠금을 기다리는 동안 continuation 이 등록 해제로 그 콜백을 기다리면 교착합니다
					var completion = new TaskCompletionSource<Result<ServerTimeSample>>(TaskCreationOptions.RunContinuationsAsynchronously);
					ObserveAsync(UniTask.RunOnThreadPool(() => Exchange(clock, token), false, token), completion).Forget();
					Result<ServerTimeSample> result;

					using (token.Register(() => completion.TrySetCanceled()))
					{
						try
						{
							result = await completion.Task;
						}
						catch (OperationCanceledException) when (token.IsCancellationRequested)
						{
							result = Result<ServerTimeSample>.Fail(ResultCodes.Cancelled, $"{Name} cancelled");
						}
					}

					return ClassifyCancellation(result, token.IsCancellationRequested, cancellationToken.IsCancellationRequested);
				}
			}
			finally
			{
				// 결과든 예외든 메인 스레드에서 부른 호출자에게는 메인 스레드로 돌려줍니다
				if (onMainThread)
				{
					await UniTask.SwitchToMainThread();
				}
			}
		}

		/// <summary>
		/// 스레드 풀 작업의 끝을 <paramref name="completion"/> 에 옮깁니다. 호출자가 아직 기다리면 예외를 그대로 전하고,
		/// 이미 제한 시간·취소로 돌아갔으면(<c>TrySetException</c> 이 false) Error 로그로 남깁니다.
		/// </summary>
		private async UniTaskVoid ObserveAsync(UniTask<Result<ServerTimeSample>> work, TaskCompletionSource<Result<ServerTimeSample>> completion)
		{
			try
			{
				completion.TrySetResult(await work);
			}
			catch (OperationCanceledException)
			{
				completion.TrySetCanceled();
			}
			catch (Exception exception)
			{
				if (!completion.TrySetException(exception))
				{
					Logger.Error(exception, $"{Name} failed after the caller stopped waiting");
				}
			}
		}

		/// <summary>
		/// 작업이 취소된 결과로 먼저 끝나든 취소 예외가 먼저 오든, 실패했고 연결된 토큰이 취소됐으면 호출자 취소(<c>Cancelled</c>)와
		/// 제한 시간(<c>Timeout</c>)을 여기서 한 번에 가립니다. 성공한 표본은 그대로 둡니다.
		/// </summary>
		internal Result<ServerTimeSample> ClassifyCancellation(Result<ServerTimeSample> result, bool linkedCancelled, bool callerCancelled)
		{
			if (result.IsSuccess || !linkedCancelled)
			{
				return result;
			}

			return callerCancelled
				? Result<ServerTimeSample>.Fail(ResultCodes.Cancelled, $"{Name} cancelled")
				: Result<ServerTimeSample>.Fail(ResultCodes.Timeout, $"{Name} timed out after {_timeout.TotalSeconds:0.###} s");
		}

		private Result<ServerTimeSample> Exchange(IMonotonicClock clock, CancellationToken token)
		{
			try
			{
				var addresses = Dns.GetHostAddresses(_host);
				SocketException lastError = null;

				foreach (var address in addresses)
				{
					if (address.AddressFamily != AddressFamily.InterNetwork && address.AddressFamily != AddressFamily.InterNetworkV6)
					{
						continue;
					}

					try
					{
						return ExchangeWith(new IPEndPoint(address, _port), clock, token);
					}
					catch (SocketException exception) when (!token.IsCancellationRequested)
					{
						lastError = exception;
					}
				}

				return lastError == null
					? Result<ServerTimeSample>.Fail(ResultCodes.NetworkError, $"{Name}: no IPv4 or IPv6 address")
					: FromSocketError(lastError);
			}
			catch (Exception exception) when (token.IsCancellationRequested &&
			                                  (exception is SocketException || exception is ObjectDisposedException || exception is OperationCanceledException))
			{
				// 취소가 소켓을 닫아 막힌 호출이 풀렸습니다. 호출자 취소인지 제한 시간인지는 FetchCoreAsync 가 가립니다
				return Result<ServerTimeSample>.Fail(ResultCodes.Cancelled, $"{Name} cancelled");
			}
			catch (SocketException exception)
			{
				return FromSocketError(exception);
			}
		}

		private Result<ServerTimeSample> FromSocketError(SocketException exception)
		{
			var code = exception.SocketErrorCode == SocketError.TimedOut ? ResultCodes.Timeout : ResultCodes.NetworkError;

			return Result<ServerTimeSample>.Fail(code, $"{Name}: {exception.SocketErrorCode} {exception.Message}");
		}

		private Result<ServerTimeSample> ExchangeWith(IPEndPoint endpoint, IMonotonicClock clock, CancellationToken token)
		{
			using (var socket = new Socket(endpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp))
			using (token.Register(socket.Dispose))
			{
				// 연결한 UDP 소켓은 그 주소에서 온 데이터그램만 받습니다
				socket.ReceiveTimeout = (int) _timeout.TotalMilliseconds;
				socket.Connect(endpoint);

				var nonce = CreateNonce();
				var request = NtpPacket.CreateRequest(nonce);
				var buffer = new byte[ReceiveBufferLength];

				var sentAt = clock.Elapsed;
				socket.Send(request);
				var length = socket.Receive(buffer);
				var receivedAt = clock.Elapsed;

				return NtpPacket.TryRead(buffer, length, nonce, sentAt, receivedAt, out var sample, out var error)
					? Result<ServerTimeSample>.Ok(sample)
					: Result<ServerTimeSample>.Fail(ServerTimeResultCodes.InvalidResponse, $"{Name} ({endpoint.Address}): {error}");
			}
		}

		private static ulong CreateNonce()
		{
			var bytes = new byte[8];

			using (var random = RandomNumberGenerator.Create())
			{
				random.GetBytes(bytes);
			}

			return BitConverter.ToUInt64(bytes, 0);
		}
	}
}
