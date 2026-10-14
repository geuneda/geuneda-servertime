using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Geuneda.ServerTime;

// ReSharper disable once CheckNamespace

namespace Geuneda.ServerTime.Tests
{
	/// <summary>테스트용 NTP 응답을 만듭니다.</summary>
	internal static class NtpTestPackets
	{
		private static readonly DateTime Era0 = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);
		private static readonly DateTime Era1 = new DateTime(2036, 2, 7, 6, 28, 16, DateTimeKind.Utc);

		public static byte[] Reply(ulong originate, DateTime receive, DateTime transmit, byte header = 0x24, byte stratum = 2)
		{
			var packet = new byte[NtpPacket.Length];

			packet[0] = header;
			packet[1] = stratum;

			if (stratum == 0)
			{
				Encoding.ASCII.GetBytes("RATE").CopyTo(packet, 12);
			}

			NtpPacket.WriteUInt64(packet, 24, originate);
			NtpPacket.WriteUInt64(packet, 32, ToTimestamp(receive));
			NtpPacket.WriteUInt64(packet, 40, ToTimestamp(transmit));

			return packet;
		}

		public static ulong ToTimestamp(DateTime utc)
		{
			var era = utc < Era1 ? Era0 : Era1;
			var ticks = (utc - era).Ticks;
			var seconds = (ulong) (ticks / TimeSpan.TicksPerSecond);
			// 올림이라야 NtpPacket.ToDateTime(내림)으로 되돌렸을 때 같은 틱이 됩니다
			var fraction = (((ulong) (ticks % TimeSpan.TicksPerSecond) << 32) + TimeSpan.TicksPerSecond - 1) / TimeSpan.TicksPerSecond;

			return (seconds << 32) | fraction;
		}
	}

	/// <summary>
	/// 127.0.0.1 의 UDP NTP 서버입니다. 받은 요청마다 <see cref="Respond"/> 가 돌려준 바이트를 답합니다(null 이면 답하지 않음).
	/// </summary>
	internal sealed class FakeNtpServer : IDisposable
	{
		private readonly UdpClient _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
		private int _requests;

		public int Port => ((IPEndPoint) _udp.Client.LocalEndPoint).Port;

		public Func<byte[], byte[]> Respond { get; set; }

		public int Requests => Volatile.Read(ref _requests);

		public FakeNtpServer(Func<byte[], byte[]> respond)
		{
			Respond = respond;
			Task.Run(ReceiveLoop);
		}

		/// <summary>요청의 전송 타임스탬프를 originate 에 넣고 서버 시각 <paramref name="now"/> 를 답하는 정상 서버입니다.</summary>
		public static Func<byte[], byte[]> Valid(DateTime now)
		{
			return request => NtpTestPackets.Reply(NtpPacket.ReadUInt64(request, 40), now, now);
		}

		public void Dispose()
		{
			_udp.Close();
		}

		private async Task ReceiveLoop()
		{
			while (true)
			{
				UdpReceiveResult received;

				try
				{
					received = await _udp.ReceiveAsync();
				}
				catch (Exception)
				{
					return;
				}

				Interlocked.Increment(ref _requests);
				var reply = Respond(received.Buffer);

				if (reply != null)
				{
					try
					{
						await _udp.SendAsync(reply, reply.Length, received.RemoteEndPoint);
					}
					catch (Exception)
					{
						return;
					}
				}
			}
		}
	}
}
