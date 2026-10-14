using System;
using System.Text;

// ReSharper disable once CheckNamespace

namespace Geuneda.ServerTime
{
	/// <summary>
	/// SNTP(RFC 4330) 요청을 만들고 응답을 검증해 표본으로 바꿉니다.
	/// </summary>
	/// <remarks>
	/// 요청의 전송 타임스탬프 자리에 무작위 값(nonce)을 넣고, 서버가 그 값을 originate 자리에 돌려줬는지 확인합니다(경로 밖 위조와 늦게 온 다른 응답을 거름).
	/// 서버 시각은 네 타임스탬프로 계산합니다. 서버 처리 시간(T3 - T2)을 왕복에서 빼고, 응답을 받은 순간의 서버 시각을 T3 + 왕복 / 2 로 봅니다.
	/// </remarks>
	internal static class NtpPacket
	{
		public const int Length = 48;

		private const byte ClientRequestHeader = 0x23; // LI 0, VN 4, Mode 3(client)
		private const int ServerMode = 4;
		private const int UnsynchronizedLeap = 3;
		private const int OriginateOffset = 24;
		private const int ReceiveOffset = 32;
		private const int TransmitOffset = 40;

		// 최상위 비트가 1 이면 1900년 기준(1968 ~ 2036), 0 이면 2036-02-07 06:28:16 UTC 기준(2036 ~ 2104)입니다(RFC 4330 3절)
		private static readonly DateTime Era0 = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);
		private static readonly DateTime Era1 = new DateTime(2036, 2, 7, 6, 28, 16, DateTimeKind.Utc);

		public static byte[] CreateRequest(ulong nonce)
		{
			var packet = new byte[Length];

			packet[0] = ClientRequestHeader;
			WriteUInt64(packet, TransmitOffset, nonce);

			return packet;
		}

		/// <summary>
		/// 응답을 검증하고 받은 순간의 서버 시각과 왕복 지연을 계산합니다. 맞지 않으면 false 와 이유를 돌려줍니다.
		/// </summary>
		/// <param name="sentAt">요청을 보내기 직전의 단조 시계 값(T1)</param>
		/// <param name="receivedAt">응답을 받은 직후의 단조 시계 값(T4)</param>
		public static bool TryRead(byte[] buffer, int length, ulong nonce, TimeSpan sentAt, TimeSpan receivedAt,
			out ServerTimeSample sample, out string error)
		{
			sample = default;

			if (length < Length)
			{
				error = $"Short NTP packet ({length} bytes)";
				return false;
			}

			var leap = buffer[0] >> 6;
			var version = (buffer[0] >> 3) & 7;
			var mode = buffer[0] & 7;
			var stratum = buffer[1];

			if (mode != ServerMode)
			{
				error = $"Not a server reply (mode {mode})";
				return false;
			}

			if (version < 3 || version > 4)
			{
				error = $"Unsupported NTP version {version}";
				return false;
			}

			if (stratum == 0)
			{
				error = $"Kiss-o'-Death from server ({Encoding.ASCII.GetString(buffer, 12, 4)})";
				return false;
			}

			if (leap == UnsynchronizedLeap || stratum > 15)
			{
				error = $"Server clock is not synchronized (leap {leap}, stratum {stratum})";
				return false;
			}

			if (ReadUInt64(buffer, OriginateOffset) != nonce)
			{
				error = "Originate timestamp does not match the request";
				return false;
			}

			var receiveTimestamp = ReadUInt64(buffer, ReceiveOffset);
			var transmitTimestamp = ReadUInt64(buffer, TransmitOffset);

			if (receiveTimestamp == 0 || transmitTimestamp == 0)
			{
				error = "Missing server timestamps";
				return false;
			}

			var serverReceive = ToDateTime(receiveTimestamp);
			var serverTransmit = ToDateTime(transmitTimestamp);
			var processing = Max(serverTransmit - serverReceive, TimeSpan.Zero);
			var roundTrip = Max(receivedAt - sentAt - processing, TimeSpan.Zero);
			var serverNow = serverTransmit + TimeSpan.FromTicks(roundTrip.Ticks / 2);

			if (serverNow < ServerTimeSample.MinValidUtc)
			{
				error = $"Server time {serverNow:o} is before {ServerTimeSample.MinValidUtc:yyyy-MM-dd}";
				return false;
			}

			sample = new ServerTimeSample(serverNow, receivedAt, roundTrip);
			error = null;
			return true;
		}

		internal static DateTime ToDateTime(ulong timestamp)
		{
			var seconds = (uint) (timestamp >> 32);
			var fraction = (uint) timestamp;
			var era = (seconds & 0x80000000u) != 0 ? Era0 : Era1;
			var ticks = seconds * TimeSpan.TicksPerSecond + (long) (((ulong) fraction * TimeSpan.TicksPerSecond) >> 32);

			return era.AddTicks(ticks);
		}

		internal static ulong ReadUInt64(byte[] buffer, int offset)
		{
			ulong value = 0;

			for (var i = 0; i < 8; i++)
			{
				value = (value << 8) | buffer[offset + i];
			}

			return value;
		}

		internal static void WriteUInt64(byte[] buffer, int offset, ulong value)
		{
			for (var i = 7; i >= 0; i--)
			{
				buffer[offset + i] = (byte) value;
				value >>= 8;
			}
		}

		private static TimeSpan Max(TimeSpan a, TimeSpan b)
		{
			return a > b ? a : b;
		}
	}
}
