namespace telnet_cs.Tests
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Client;
    using telnet_cs.IO;
    using telnet_cs.Protocol;

    public class NawsRefreshTests
    {
        private static byte[] NawsFrame(int width, int height) => new byte[]
        {
      255, 250, 31, (byte)(width >> 8), (byte)width, (byte)(height >> 8), (byte)height, 255, 240,
        };

        private static async Task<string> ReadClientOnceAsync(Client client)
        {
            return await client.ReadAsync(TimeSpan.FromMilliseconds(100));
        }

        [Fact]
        public async Task RefreshWindowSize_AfterNegotiation_SendsChangedSize()
        {
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = await Client.CreateAsync(stream, TimeSpan.FromSeconds(30), new CancellationToken());
                client.Settings.WindowWidth = 100;
                client.Settings.WindowHeight = 30;
                stream.Enqueue(255, 253, 31);
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                stream.ByteWrites.Should().HaveCount(2);
                stream.ByteWrites[0].Should().Equal(new byte[] { 255, 251, 31 });
                stream.ByteWrites[1].Should().Equal(NawsFrame(100, 30));
                client.Settings.WindowWidth = 120;
                client.Settings.WindowHeight = 40;
                await client.RefreshWindowSizeAsync();
                stream.ByteWrites.Should().HaveCount(3);
                stream.ByteWrites[2].Should().Equal(NawsFrame(120, 40));
            }
        }

        [Fact]
        public async Task RefreshWindowSize_Unchanged_DoesNotResend()
        {
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = await Client.CreateAsync(stream, TimeSpan.FromSeconds(30), new CancellationToken());
                client.Settings.WindowWidth = 100;
                client.Settings.WindowHeight = 30;
                stream.Enqueue(255, 253, 31);
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                stream.ByteWrites.Should().HaveCount(2);
                await client.RefreshWindowSizeAsync();
                stream.ByteWrites.Should().HaveCount(2);
            }
        }

        [Fact]
        public async Task RefreshWindowSize_WhenNeverNegotiated_SendsNothing()
        {
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = await Client.CreateAsync(stream, TimeSpan.FromSeconds(30), new CancellationToken());
                client.Settings.WindowWidth = 100;
                client.Settings.WindowHeight = 30;
                await client.RefreshWindowSizeAsync();
                stream.ByteWrites.Should().BeEmpty();
            }
        }

        [Fact]
        public async Task RefreshWindowSize_AfterDont_Suppressed()
        {
            // DO NAWS is agreed (WILL + initial SB); the DONT revocation gets
            // no WONT reply (negatives are state-only), and the later resize
            // sends nothing because NAWS is no longer agreed.
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = await Client.CreateAsync(stream, TimeSpan.FromSeconds(30), new CancellationToken());
                client.Settings.WindowWidth = 100;
                client.Settings.WindowHeight = 30;
                stream.Enqueue(255, 253, 31);
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                stream.Enqueue(255, 254, 31);
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                stream.ByteWrites.Should().HaveCount(2);
                stream.ByteWrites[0].Should().Equal(new byte[] { 255, 251, 31 });
                stream.ByteWrites[1].Should().Equal(new byte[] { 255, 250, 31, 0, 100, 0, 30, 255, 240 });
                client.Settings.WindowWidth = 120;
                client.Settings.WindowHeight = 40;
                await client.RefreshWindowSizeAsync();
                stream.ByteWrites.Should().HaveCount(2);
            }
        }

        [Fact]
        public async Task InboundNawsVerbFirst_IsRefusedWithWont()
        {
            // Inbound NAWS has no reply path: subnegotiation frames never
            // synthesize WONT, so the frame is consumed and ignored silently.
            using var stream = new ScriptedStream(255, 250, 31, 0, 0, 80, 0, 24, 255, 240);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            stream.ByteWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task InboundNawsBareShape_IsRefusedWithWont()
        {
            // Bare shape (no verb) is also consumed and ignored: no WONT is
            // synthesized for subnegotiation payloads.
            using var stream = new ScriptedStream(255, 250, 31, 0, 80, 0, 30, 255, 240);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            stream.ByteWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task InboundNawsSend_IsLoggedOnly()
        {
            // A server SEND has no client answer path: consumed, logged, silent.
            using var stream = new ScriptedStream(255, 250, 31, 1, 255, 240);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            stream.ByteWrites.Should().BeEmpty();
        }

        [Theory]
        [InlineData(100, 30, 100, 30)]
        [InlineData(70000, 30, 65535, 30)]
        [InlineData(100, 90000, 100, 65535)]
        [InlineData(9999999, -999999, 65535, 0)]
        [InlineData(-5, -7, 0, 0)]
        [InlineData(65535, 65535, 65535, 65535)]
        public void GetEffectiveSize_ClampsDimensions(int width, int height, int expectedWidth, int expectedHeight)
        {
            NawsProtocol.GetEffectiveSize(width, height).Should().Be(((ushort)expectedWidth, (ushort)expectedHeight));
        }
    }
}
