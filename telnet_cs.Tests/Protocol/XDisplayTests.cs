namespace telnet_cs.Tests
{
    using System;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.IO;
    using telnet_cs.Server;

    public class XDisplayTests
    {
        private static readonly int[] XDisplaySend = [255, 250, 35, 1, 255, 240];

        private static byte[] XDisplayIsFrame(string value)
        {
            return [255, 250, 35, 0, .. Encoding.Latin1.GetBytes(value), 255, 240];
        }

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

        private static ServerSession NewSession(ScriptedStream stream)
        {
            return new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
        }

        private static byte[] OutboundBytes(ScriptedStream stream)
        {
            return stream.ByteWrites.SelectMany(static b => b).ToArray();
        }

        [Fact]
        public async Task PlainData_NeverVolunteersDoXDisplay()
        {
            // No advanced-negotiation trigger on this side: a client handler never
            // emits DO XDISPLAY spontaneously; only an explicit request does.
            var (output, stream) = await ReadHandlerOnceAsync(static _ => { }, (int)'h', (int)'i');
            output.Should().Be("hi");
            stream.ByteWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task XDisplaySend_Configured_AnswersIs()
        {
            // RFC 1096 §4: only the WILL side answers, and only on SEND.
            var (output, stream) = await ReadHandlerOnceAsync(
              static h => h.XDisplayLocation = "host:0", XDisplaySend);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(XDisplayIsFrame("host:0"));
        }

        [Fact]
        public async Task XDisplaySend_Unconfigured_SendsNothing()
        {
            var (output, stream) = await ReadHandlerOnceAsync(static _ => { }, XDisplaySend);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task RequestXDisplayAsync_CollectsAnswer()
        {
            using var stream = new ScriptedStream();
            stream.Enqueue([.. XDisplayIsFrame("sri-nic.arpa:0.0")]);
            using var session = NewSession(stream);
            var value = await session.RequestXDisplayAsync(TimeSpan.FromSeconds(5));
            value.Should().Be("sri-nic.arpa:0.0");
            session.ClientXDisplay.Should().Be("sri-nic.arpa:0.0");
            OutboundBytes(stream).Should().Equal(255, 250, 35, 1, 255, 240);
        }

        [Fact]
        public async Task StrayXDisplayIs_WithoutOutstandingRequest_AnswersWont()
        {
            using var stream = new ScriptedStream();
            stream.Enqueue([.. XDisplayIsFrame("x:0")]);
            using var session = NewSession(stream);
            await session.ReadAsync(TimeSpan.FromSeconds(5));
            session.ClientXDisplay.Should().BeNull();
            session.ClientEffectiveDisplay.Should().BeNull();
            OutboundBytes(stream).Should().Equal(255, 252, 35);
        }

        [Fact]
        public async Task EffectiveDisplay_XDisplayThenEnviron_PicksLastArrived()
        {
            using var stream = new ScriptedStream();
            stream.Enqueue([.. XDisplayIsFrame("x:0")]);
            using var session = NewSession(stream);
            (await session.RequestXDisplayAsync(TimeSpan.FromSeconds(5))).Should().Be("x:0");
            session.ClientEffectiveDisplay.Should().Be("x:0");
            // Interleaved unrelated vars do not disturb the recency rule.
            stream.Enqueue([255, 250, 36, 0,
        0, (byte)'U', (byte)'S', (byte)'E', (byte)'R', 1, (byte)'b', (byte)'o', (byte)'b',
        0, (byte)'D', (byte)'I', (byte)'S', (byte)'P', (byte)'L', (byte)'A', (byte)'Y', 1, (byte)'y', (byte)':', (byte)'0',
        255, 240]);
            (await session.RequestEnvironmentAsync(TimeSpan.FromSeconds(5), [0])).Should().Contain("DISPLAY", "y:0");
            session.ClientEffectiveDisplay.Should().Be("y:0");
        }

        [Fact]
        public async Task EffectiveDisplay_EnvironThenXDisplay_PicksLastArrived()
        {
            using var stream = new ScriptedStream();
            stream.Enqueue([255, 250, 36, 0,
        0, (byte)'D', (byte)'I', (byte)'S', (byte)'P', (byte)'L', (byte)'A', (byte)'Y', 1, (byte)'y', (byte)':', (byte)'0',
        255, 240]);
            using var session = NewSession(stream);
            (await session.RequestEnvironmentAsync(TimeSpan.FromSeconds(5), [0])).Should().Contain("DISPLAY", "y:0");
            session.ClientEffectiveDisplay.Should().Be("y:0");
            stream.Enqueue([.. XDisplayIsFrame("x:0")]);
            (await session.RequestXDisplayAsync(TimeSpan.FromSeconds(5))).Should().Be("x:0");
            session.ClientEffectiveDisplay.Should().Be("x:0");
        }

        [Fact]
        public async Task EffectiveDisplay_SpontaneousInfo_UpdatesRecency()
        {
            using var stream = new ScriptedStream();
            stream.Enqueue([.. XDisplayIsFrame("x:0")]);
            using var session = NewSession(stream);
            (await session.RequestXDisplayAsync(TimeSpan.FromSeconds(5))).Should().Be("x:0");
            // INFO is only honored from a WILL-agreed peer (RFC 1408): the
            // WILL here earns the DO reply, then the INFO updates recency.
            stream.Enqueue([255, 251, 36, 255, 250, 36, 2,
        0, (byte)'D', (byte)'I', (byte)'S', (byte)'P', (byte)'L', (byte)'A', (byte)'Y', 1, (byte)'z', (byte)':', (byte)'1',
        255, 240]);
            await session.ReadAsync(TimeSpan.FromSeconds(5));
            session.ClientEffectiveDisplay.Should().Be("z:1");
        }
    }
}
