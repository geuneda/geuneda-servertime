using System;
using Geuneda.ServerTime;
using NUnit.Framework;

// ReSharper disable once CheckNamespace

namespace Geuneda.ServerTime.Tests
{
	[TestFixture]
	public class NtpPacketTest
	{
		private const ulong Nonce = 0x0123456789ABCDEFUL;

		// AddSeconds·FromSeconds 는 런타임에 따라 밀리초로 반올림하므로 틱으로 만듭니다
		private static readonly DateTime ServerReceive = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(5_000_000);
		private static readonly DateTime ServerTransmit = ServerReceive.AddTicks(625_000);
		private static readonly TimeSpan SentAt = TimeSpan.FromTicks(1_000_000_000);
		private static readonly TimeSpan ReceivedAt = SentAt + TimeSpan.FromTicks(3_125_000);

		[Test]
		public void CreateRequest_IsVersion4ClientWithNonceInTransmitField()
		{
			var request = NtpPacket.CreateRequest(Nonce);

			Assert.AreEqual(48, request.Length);
			Assert.AreEqual(0x23, request[0]);
			Assert.AreEqual(Nonce, NtpPacket.ReadUInt64(request, 40));

			for (var i = 1; i < 40; i++)
			{
				Assert.AreEqual(0, request[i], $"byte {i}");
			}
		}

		[Test]
		public void TryRead_ValidReply_RemovesServerProcessingAndAddsHalfRoundTrip()
		{
			var reply = NtpTestPackets.Reply(Nonce, ServerReceive, ServerTransmit);

			Assert.IsTrue(NtpPacket.TryRead(reply, reply.Length, Nonce, SentAt, ReceivedAt, out var sample, out var error), error);

			// 왕복 312.5ms - 서버 처리 62.5ms = 250ms, 받은 순간의 서버 시각은 전송 시각 + 125ms
			Assert.AreEqual(TimeSpan.FromMilliseconds(250), sample.RoundTrip);
			Assert.AreEqual(ServerTransmit.AddTicks(1_250_000), sample.ServerUtc);
			Assert.AreEqual(DateTimeKind.Utc, sample.ServerUtc.Kind);
			Assert.AreEqual(ReceivedAt, sample.ReceivedAt);
		}

		[Test]
		public void TryRead_AcceptsVersion3AndLongerPackets()
		{
			var reply = NtpTestPackets.Reply(Nonce, ServerReceive, ServerTransmit, header: 0x1C);
			var padded = new byte[68];
			Array.Copy(reply, padded, reply.Length);

			Assert.IsTrue(NtpPacket.TryRead(padded, padded.Length, Nonce, SentAt, ReceivedAt, out _, out var error), error);
		}

		[Test]
		public void TryRead_NegativeIntervals_AreClampedToZero()
		{
			// 서버 전송 시각이 수신 시각보다 앞서고, 서버 처리 시간이 우리가 잰 왕복보다 긴 응답
			var reply = NtpTestPackets.Reply(Nonce, ServerTransmit.AddSeconds(1), ServerTransmit);

			Assert.IsTrue(NtpPacket.TryRead(reply, reply.Length, Nonce, SentAt, SentAt, out var sample, out var error), error);
			Assert.AreEqual(TimeSpan.Zero, sample.RoundTrip);
			Assert.AreEqual(ServerTransmit, sample.ServerUtc);
		}

		[TestCase(47, TestName = "TryRead_Rejects_ShortPacket")]
		public void TryRead_Rejects_ShortPacket(int length)
		{
			var reply = NtpTestPackets.Reply(Nonce, ServerReceive, ServerTransmit);

			Assert.IsFalse(NtpPacket.TryRead(reply, length, Nonce, SentAt, ReceivedAt, out _, out var error));
			StringAssert.Contains("Short", error);
		}

		[TestCase(0x23, 2, "mode", TestName = "TryRead_Rejects_ClientMode")]
		[TestCase(0x14, 2, "version", TestName = "TryRead_Rejects_Version2")]
		[TestCase(0xE4, 2, "not synchronized", TestName = "TryRead_Rejects_LeapIndicator3")]
		[TestCase(0x24, 16, "not synchronized", TestName = "TryRead_Rejects_Stratum16")]
		[TestCase(0x24, 0, "Kiss-o'-Death from server (RATE)", TestName = "TryRead_Rejects_KissOfDeath")]
		public void TryRead_RejectsBadHeader(int header, int stratum, string expected)
		{
			var reply = NtpTestPackets.Reply(Nonce, ServerReceive, ServerTransmit, (byte) header, (byte) stratum);

			Assert.IsFalse(NtpPacket.TryRead(reply, reply.Length, Nonce, SentAt, ReceivedAt, out _, out var error));
			StringAssert.Contains(expected, error);
		}

		[Test]
		public void TryRead_Rejects_OriginateMismatch()
		{
			var reply = NtpTestPackets.Reply(Nonce + 1, ServerReceive, ServerTransmit);

			Assert.IsFalse(NtpPacket.TryRead(reply, reply.Length, Nonce, SentAt, ReceivedAt, out _, out var error));
			StringAssert.Contains("Originate", error);
		}

		[Test]
		public void TryRead_Rejects_MissingTimestamps()
		{
			var reply = NtpTestPackets.Reply(Nonce, ServerReceive, ServerTransmit);
			NtpPacket.WriteUInt64(reply, 40, 0);

			Assert.IsFalse(NtpPacket.TryRead(reply, reply.Length, Nonce, SentAt, ReceivedAt, out _, out var error));
			StringAssert.Contains("Missing", error);
		}

		[Test]
		public void TryRead_Rejects_TimeBefore2024()
		{
			var old = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
			var reply = NtpTestPackets.Reply(Nonce, old, old);

			Assert.IsFalse(NtpPacket.TryRead(reply, reply.Length, Nonce, SentAt, ReceivedAt, out _, out var error));
			StringAssert.Contains("before 2024", error);
		}

		[Test]
		public void ToDateTime_HandlesBothEras()
		{
			// Unix 1704067200 + 2208988800 = NTP 3913056000 (2024-01-01)
			Assert.AreEqual(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), NtpPacket.ToDateTime(3913056000UL << 32));
			Assert.AreEqual(new DateTime(2036, 2, 7, 6, 28, 15, DateTimeKind.Utc).AddSeconds(0.5), NtpPacket.ToDateTime((0xFFFFFFFFUL << 32) | 0x80000000UL));
			// 최상위 비트가 0 이면 2036-02-07 06:28:16 UTC 부터 다시 셉니다
			Assert.AreEqual(new DateTime(2036, 2, 7, 6, 28, 17, DateTimeKind.Utc), NtpPacket.ToDateTime(1UL << 32));
		}

		[Test]
		public void TestPackets_RoundTripTimestamps()
		{
			Assert.AreEqual(ServerTransmit, NtpPacket.ToDateTime(NtpTestPackets.ToTimestamp(ServerTransmit)));
			var era1 = new DateTime(2040, 6, 1, 12, 0, 0, DateTimeKind.Utc).AddTicks(1_234_567);
			Assert.AreEqual(era1, NtpPacket.ToDateTime(NtpTestPackets.ToTimestamp(era1)));
		}
	}
}
