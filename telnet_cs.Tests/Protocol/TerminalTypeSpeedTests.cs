namespace telnet_cs.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Client;
    using telnet_cs.IO;
    using telnet_cs.Protocol;

    public class TerminalTypeSpeedTests
    {
        private static readonly int[] TypeSend = [255, 250, 24, 1, 255, 240];

        private static readonly int[] SpeedSend = [255, 250, 32, 1, 255, 240];

        private static byte[] TypeIsFrame(string type) =>
          [255, 250, 24, 0, .. Encoding.Latin1.GetBytes(type), 255, 240];

        private static byte[] SpeedIsFrame(string speed) =>
          [255, 250, 32, 0, .. Encoding.Latin1.GetBytes(speed), 255, 240];

        private static async Task<(string Output, ScriptedStream Stream)> ReadHandlerOnceAsync(
          Action<ByteStreamHandler> configure, params int[] reads)
        {
            var stream = new ScriptedStream(reads);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            configure(sut);
            var output = await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
            return (output, stream);
        }

        private static async Task<string> ReadClientOnceAsync(Client client)
        {
            return await client.ReadAsync(TimeSpan.FromMilliseconds(100));
        }

        private static async Task<IReadOnlyList<byte[]>> SendThroughClientAsync(Client client, ScriptedStream stream, int reads)
        {
            for (var i = 0; i < reads; i++)
            {
                stream.Enqueue(TypeSend);
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
            }

            return stream.ByteWrites;
        }

        [Fact]
        public async Task TypeSend_WalksListAcrossReads()
        {
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = new Client(stream, new CancellationToken());
                client.Settings.TerminalTypes.Add("xterm-256color");
                client.Settings.TerminalTypes.Add("xterm");
                client.Settings.TerminalTypes.Add("vt100");
                var writes = await SendThroughClientAsync(client, stream, 3);
                writes.Should().HaveCount(3);
                writes[0].Should().Equal(TypeIsFrame("xterm-256color"));
                writes[1].Should().Equal(TypeIsFrame("xterm"));
                writes[2].Should().Equal(TypeIsFrame("vt100"));
            }
        }

        [Fact]
        public async Task TypeSend_SameTwiceThenWraps()
        {
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = new Client(stream, new CancellationToken());
                client.Settings.TerminalTypes.Add("aaa");
                client.Settings.TerminalTypes.Add("bbb");
                var writes = await SendThroughClientAsync(client, stream, 4);
                writes.Should().HaveCount(4);
                writes[0].Should().Equal(TypeIsFrame("aaa"));
                writes[1].Should().Equal(TypeIsFrame("bbb"));
                writes[2].Should().Equal(TypeIsFrame("bbb"));
                writes[3].Should().Equal(TypeIsFrame("aaa"));
            }
        }

        [Fact]
        public async Task TypeSend_TruncatesTo40Chars()
        {
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = new Client(stream, new CancellationToken());
                client.Settings.TerminalTypes.Add(new string('a', 41));
                var writes = await SendThroughClientAsync(client, stream, 1);
                writes.Should().ContainSingle().Which.Should().Equal(TypeIsFrame(new string('a', 40)));
            }
        }

        [Fact]
        public async Task TypeSend_EmptyListFallsBackToSingle()
        {
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = new Client(stream, new CancellationToken());
                client.Settings.TerminalType = "xterm";
                var writes = await SendThroughClientAsync(client, stream, 2);
                writes.Should().HaveCount(2);
                writes[0].Should().Equal(TypeIsFrame("xterm"));
                writes[1].Should().Equal(TypeIsFrame("xterm"));
            }
        }

        [Fact]
        public async Task TypeSend_EmptyStringFallsBackToUnknown()
        {
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = new Client(stream, new CancellationToken());
                client.Settings.TerminalType = string.Empty;
                var writes = await SendThroughClientAsync(client, stream, 1);
                writes.Should().ContainSingle().Which.Should().Equal(TypeIsFrame("UNKNOWN"));
            }
        }

        [Fact]
        public async Task TypeSend_ListChangeResetsCycle()
        {
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = new Client(stream, new CancellationToken());
                client.Settings.TerminalTypes.Add("aaa");
                client.Settings.TerminalTypes.Add("bbb");
                var first = await SendThroughClientAsync(client, stream, 1);
                first.Should().ContainSingle().Which.Should().Equal(TypeIsFrame("aaa"));
                client.Settings.TerminalTypes.Clear();
                client.Settings.TerminalTypes.Add("zzz");
                stream.Enqueue(TypeSend);
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                stream.ByteWrites.Should().HaveCount(2);
                stream.ByteWrites[1].Should().Equal(TypeIsFrame("zzz"));
            }
        }

        [Fact]
        public void Cycler_Matches_IgnoresCase()
        {
            // RFC 1091 §5: upper and lower case are equivalent in type names.
            var cycler = new TerminalTypeCycler(["XTERM", "vt100"]);
            cycler.Matches(["xterm", "VT100"]).Should().BeTrue();
            cycler.Matches(["xterm", "vt220"]).Should().BeFalse();
        }

        [Fact]
        public async Task TypeSend_CaseOnlyChange_KeepsCycle()
        {
            // A case-only list change is not a list change: the cycle continues
            // instead of resetting.
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = new Client(stream, new CancellationToken());
                client.Settings.TerminalTypes.Add("aaa");
                client.Settings.TerminalTypes.Add("bbb");
                var first = await SendThroughClientAsync(client, stream, 1);
                first.Should().ContainSingle().Which.Should().Equal(TypeIsFrame("aaa"));
                client.Settings.TerminalTypes.Clear();
                client.Settings.TerminalTypes.Add("AAA");
                client.Settings.TerminalTypes.Add("BBB");
                stream.Enqueue(TypeSend);
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                stream.ByteWrites.Should().HaveCount(2);
                stream.ByteWrites[1].Should().Equal(TypeIsFrame("bbb"));
            }
        }

        [Fact]
        public async Task TypeSend_DirectHandlerRepeatsString()
        {
            var (output, stream) = await ReadHandlerOnceAsync(
              static h => h.TerminalType = "xterm",
              [.. TypeSend, .. TypeSend]);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().HaveCount(2);
            stream.ByteWrites[0].Should().Equal(TypeIsFrame("xterm"));
            stream.ByteWrites[1].Should().Equal(TypeIsFrame("xterm"));
        }

        [Fact]
        public async Task SpeedSend_ValidStaysUnchanged()
        {
            var (output, stream) = await ReadHandlerOnceAsync(
              static h => h.TerminalSpeed = "19200,19200", SpeedSend);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(SpeedIsFrame("19200,19200"));
        }

        [Fact]
        public async Task SpeedSend_StripsLeadingZeros()
        {
            var (output, stream) = await ReadHandlerOnceAsync(
              static h => h.TerminalSpeed = "019200,009600", SpeedSend);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(SpeedIsFrame("19200,9600"));
        }

        [Fact]
        public async Task SpeedSend_NonStandardRates_SentVerbatim()
        {
            // RFC 1079 §5 assigns rounding to the receiver for its own local
            // use: the sender transmits its rates unchanged.
            var (output, stream) = await ReadHandlerOnceAsync(
              static h => h.TerminalSpeed = "1000,1000", SpeedSend);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(SpeedIsFrame("1000,1000"));
        }

        [Fact]
        public async Task SpeedSend_ArbitraryRates_SentVerbatim()
        {
            var (output, stream) = await ReadHandlerOnceAsync(
              static h => h.TerminalSpeed = "1337,1919", SpeedSend);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(SpeedIsFrame("1337,1919"));
        }

        [Fact]
        public async Task SpeedSend_HugeRates_SentVerbatim()
        {
            // Rates are opaque decimal text on the wire (RFC 1079 §4), so a
            // value wider than int passes through instead of being rejected.
            var (output, stream) = await ReadHandlerOnceAsync(
              static h => h.TerminalSpeed = "99999999999999999999,1", SpeedSend);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(SpeedIsFrame("99999999999999999999,1"));
        }

        [Fact]
        public void RoundForPadding_RoundsToNearest()
        {
            TerminalSpeedProtocol.RoundForPadding(1000).Should().Be(1200);
        }

        [Fact]
        public void RoundForPadding_TieRoundsUp()
        {
            TerminalSpeedProtocol.RoundForPadding(142).Should().Be(150);
        }

        [Fact]
        public async Task SpeedSend_TrailingPayload_Ignored()
        {
            // The SEND verb carries no payload (RFC 1079 §4): trailing bytes are
            // ignored and the configured speed is answered identically to bare SEND.
            var (output, stream) = await ReadHandlerOnceAsync(
              static h => h.TerminalSpeed = "19200,19200",
              [255, 250, 32, 1, 65, 66, 67, 255, 240]);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(SpeedIsFrame("19200,19200"));
        }

        [Theory]
        [InlineData("abc")]
        [InlineData("9600")]
        [InlineData("9600,4800,2400")]
        [InlineData("")]
        [InlineData("9600,abc")]
        [InlineData(" 9600,9600")]
        public async Task SpeedSend_MalformedSendsNothing(string speed)
        {
            var (output, stream) = await ReadHandlerOnceAsync(h => h.TerminalSpeed = speed, SpeedSend);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().BeEmpty();
        }
    }
}
