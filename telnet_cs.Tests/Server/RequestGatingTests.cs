namespace telnet_cs.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Protocol;
    using telnet_cs.Server;

    public class RequestGatingTests
    {
        private static byte[] OutboundBytes(ScriptedStream stream)
        {
            return stream.ByteWrites.SelectMany(static b => b).ToArray();
        }

        private static int CountSubsequence(byte[] haystack, byte[] needle)
        {
            int count = 0;
            for (int i = 0; i + needle.Length <= haystack.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    count++;
                }
            }

            return count;
        }

        private static bool ContainsSubsequence(byte[] haystack, byte[] needle)
        {
            return CountSubsequence(haystack, needle) > 0;
        }

        private static int[] TtypeIsFrame(string value)
        {
            return [255, 250, 24, 0, .. Encoding.Latin1.GetBytes(value).Select(static b => (int)b), 255, 240];
        }

        private static int[] TspeedIsFrame(string value)
        {
            return [255, 250, 32, 0, .. Encoding.Latin1.GetBytes(value).Select(static b => (int)b), 255, 240];
        }

        private static int[] XDisplayIsFrame(string value)
        {
            return [255, 250, 35, 0, .. Encoding.Latin1.GetBytes(value).Select(static b => (int)b), 255, 240];
        }

        private static List<string> CaptureLog(TelnetServerOptions options, List<string> logs)
        {
            options.Log = msg => { lock (logs) logs.Add(msg); };
            return logs;
        }

        [Fact]
        public async Task RequestTerminalTypesAsync_WithoutWill_ReturnsEmptyAndSendsNothing()
        {
            using var stream = new ScriptedStream();
            var logs = new List<string>();
            var options = new TelnetServerOptions();
            CaptureLog(options, logs);
            using var session = new ServerSession(stream, options, CancellationToken.None);
            (await session.RequestTerminalTypesAsync(TimeSpan.FromMilliseconds(200))).Should().BeEmpty();
            stream.ByteWrites.Should().BeEmpty();
            lock (logs) logs.Should().ContainSingle(m => m.Contains("TTYPE", StringComparison.Ordinal));
        }

        [Fact]
        public async Task RequestTerminalSpeedAsync_WithoutWill_ReturnsNullAndSendsNothing()
        {
            using var stream = new ScriptedStream();
            var logs = new List<string>();
            var options = new TelnetServerOptions();
            CaptureLog(options, logs);
            using var session = new ServerSession(stream, options, CancellationToken.None);
            (await session.RequestTerminalSpeedAsync(TimeSpan.FromMilliseconds(200))).Should().BeNull();
            stream.ByteWrites.Should().BeEmpty();
            lock (logs) logs.Should().ContainSingle(m => m.Contains("TSPEED", StringComparison.Ordinal));
        }

        [Fact]
        public async Task RequestXDisplayAsync_WithoutWill_ReturnsNullAndSendsNothing()
        {
            using var stream = new ScriptedStream();
            var logs = new List<string>();
            var options = new TelnetServerOptions();
            CaptureLog(options, logs);
            using var session = new ServerSession(stream, options, CancellationToken.None);
            (await session.RequestXDisplayAsync(TimeSpan.FromMilliseconds(200))).Should().BeNull();
            stream.ByteWrites.Should().BeEmpty();
            lock (logs) logs.Should().ContainSingle(m => m.Contains("XDISPLOC", StringComparison.Ordinal));
        }

        [Fact]
        public async Task RequestEnvironmentAsync_WithoutWill_ReturnsEmptyAndSendsNothing()
        {
            using var stream = new ScriptedStream();
            var logs = new List<string>();
            var options = new TelnetServerOptions();
            CaptureLog(options, logs);
            using var session = new ServerSession(stream, options, CancellationToken.None);
            (await session.RequestEnvironmentAsync(TimeSpan.FromMilliseconds(200))).Should().BeEmpty();
            stream.ByteWrites.Should().BeEmpty();
            lock (logs) logs.Should().ContainSingle(m => m.Contains("OLD_ENVIRON", StringComparison.Ordinal));
        }

        [Fact]
        public async Task RequestNewEnvironmentAsync_WithoutWill_ReturnsEmptyAndSendsNothing()
        {
            using var stream = new ScriptedStream();
            var logs = new List<string>();
            var options = new TelnetServerOptions();
            CaptureLog(options, logs);
            using var session = new ServerSession(stream, options, CancellationToken.None);
            (await session.RequestNewEnvironmentAsync(TimeSpan.FromMilliseconds(200))).Should().BeEmpty();
            stream.ByteWrites.Should().BeEmpty();
            lock (logs) logs.Should().ContainSingle(m => m.Contains("NEW_ENVIRON", StringComparison.Ordinal));
        }

        [Fact]
        public async Task RequestCharsetAsync_WithoutAgreement_ReturnsNullAndSendsNothing()
        {
            using var stream = new ScriptedStream();
            var logs = new List<string>();
            var options = new TelnetServerOptions();
            CaptureLog(options, logs);
            using var session = new ServerSession(stream, options, CancellationToken.None);
            (await session.RequestCharsetAsync(TimeSpan.FromMilliseconds(200))).Should().BeNull();
            stream.ByteWrites.Should().BeEmpty();
            lock (logs) logs.Should().ContainSingle(m => m.Contains("CHARSET", StringComparison.Ordinal));
        }

        [Fact]
        public async Task RequestCharsetAsync_WithPeerWill_SendsSingleRequestAndResolves()
        {
            using var stream = new ScriptedStream();
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            session.Negotiation.ReceivedWill((int)Options.CharacterSet, agree: true);
            stream.Enqueue(255, 250, 42, 2, 85, 84, 70, 45, 56, 255, 240);
            (await session.RequestCharsetAsync(TimeSpan.FromSeconds(5))).Should().Be("UTF-8");
            var outbound = OutboundBytes(stream);
            ContainsSubsequence(outbound, [255, 250, 42, 1]).Should().BeTrue();
            stream.ByteWrites.Count(static b => b.Length >= 4 && b[0] == 255 && b[1] == 250 && b[2] == 42 && b[3] == 1).Should().Be(1);
        }

        [Fact]
        public async Task RequestCharsetAsync_WithUsSideAgreement_SendsSingleRequestAndResolves()
        {
            using var stream = new ScriptedStream();
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            session.Negotiation.ReceivedDo((int)Options.CharacterSet, agree: true);
            stream.Enqueue(255, 250, 42, 2, 85, 84, 70, 45, 56, 255, 240);
            (await session.RequestCharsetAsync(TimeSpan.FromSeconds(5))).Should().Be("UTF-8");
            var outbound = OutboundBytes(stream);
            ContainsSubsequence(outbound, [255, 250, 42, 1]).Should().BeTrue();
            stream.ByteWrites.Count(static b => b.Length >= 4 && b[0] == 255 && b[1] == 250 && b[2] == 42 && b[3] == 1).Should().Be(1);
        }

        [Fact]
        public async Task RequestTerminalTypesAsync_WithWill_SendsSendAndResolvesPrequeuedChain()
        {
            using var stream = new ScriptedStream();
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            session.Negotiation.ReceivedWill((int)Options.TerminalType, agree: true);
            stream.Enqueue([.. TtypeIsFrame("XTERM"), .. TtypeIsFrame("XTERM")]);
            (await session.RequestTerminalTypesAsync(TimeSpan.FromSeconds(5))).Should().Equal("XTERM");
            var outbound = OutboundBytes(stream);
            ContainsSubsequence(outbound, [255, 250, 24, 1, 255, 240]).Should().BeTrue();
        }

        [Fact]
        public async Task RequestTerminalSpeedAsync_WithWill_SendsSingleSendAndResolves()
        {
            var options = new TelnetServerOptions { RequestTerminalSpeed = false };
            using var stream = new ScriptedStream();
            using var session = new ServerSession(stream, options, CancellationToken.None);
            session.Negotiation.ReceivedWill((int)Options.TerminalSpeed, agree: true);
            stream.Enqueue([.. TspeedIsFrame("9600,9600")]);
            (await session.RequestTerminalSpeedAsync(TimeSpan.FromSeconds(5))).Should().Be("9600,9600");
            CountSubsequence(OutboundBytes(stream), [255, 250, 32, 1, 255, 240]).Should().Be(1);
        }

        [Fact]
        public async Task RequestXDisplayAsync_WithWill_SendsSingleSendAndResolves()
        {
            using var stream = new ScriptedStream();
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            session.Negotiation.ReceivedWill((int)Options.XDisplay, agree: true);
            stream.Enqueue([.. XDisplayIsFrame("host:0")]);
            (await session.RequestXDisplayAsync(TimeSpan.FromSeconds(5))).Should().Be("host:0");
            CountSubsequence(OutboundBytes(stream), [255, 250, 35, 1, 255, 240]).Should().Be(1);
        }

        [Fact]
        public async Task RequestEnvironmentAsync_WithWill_SendsSingleSendAndResolves()
        {
            using var stream = new ScriptedStream();
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            session.Negotiation.ReceivedWill((int)Options.OldEnvironment, agree: true);
            stream.Enqueue(255, 250, 36, 0, 0, 65, 1, 66, 255, 240);
            var env = await session.RequestEnvironmentAsync(TimeSpan.FromSeconds(5));
            env.Should().ContainSingle(kv => kv.Key == "A" && kv.Value == "B");
            CountSubsequence(OutboundBytes(stream), [255, 250, 36, 1, 255, 240]).Should().Be(1);
        }

        [Fact]
        public async Task RequestNewEnvironmentAsync_WithWill_SendsSingleSendAndResolves()
        {
            using var stream = new ScriptedStream();
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            session.Negotiation.ReceivedWill((int)Options.NewEnvironment, agree: true);
            stream.Enqueue(255, 250, 39, 0, 0, 65, 1, 66, 255, 240);
            var env = await session.RequestNewEnvironmentAsync(TimeSpan.FromSeconds(5));
            env.Should().ContainSingle(kv => kv.Key == "A" && kv.Value == "B");
            CountSubsequence(OutboundBytes(stream), [255, 250, 39, 1, 255, 240]).Should().Be(1);
        }

        [Fact]
        public async Task RequestXDisplayAsync_OverlappingWithAgreement_SharesSingleSend()
        {
            using var stream = new ScriptedStream();
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            session.Negotiation.ReceivedWill((int)Options.XDisplay, agree: true);
            var first = session.RequestXDisplayAsync(TimeSpan.FromMilliseconds(500));
            await Task.Delay(100);
            var second = session.RequestXDisplayAsync(TimeSpan.FromMilliseconds(300));
            await Task.WhenAll(first, second);
            CountSubsequence(OutboundBytes(stream), [255, 250, 35, 1, 255, 240]).Should().Be(1);
        }
    }
}
