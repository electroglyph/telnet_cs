namespace telnet_cs.Tests
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Client;
    using telnet_cs.IO;

    public class EchoTests
    {
        private static int CountWrites(ScriptedStream stream, byte verb, byte option)
        {
            return stream.ByteWrites.Count(b =>
              b.Length == 3 && b[0] == 255 && b[1] == verb && b[2] == option);
        }

        [Fact]
        public async Task SpontaneousWillEcho_RepliesDoAndTracksPeer()
        {
            using var stream = new ScriptedStream(255, 251, 1);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            CountWrites(stream, 253, 1).Should().Be(1);
            sut.Negotiation.IsEnabledByPeer(1).Should().BeTrue();
        }

        [Fact]
        public async Task WillEcho_SuppressesLocalEchoFlag()
        {
            using var stream = new ScriptedStream(255, 251, 1);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            sut.IsWriteConsole = true;
            sut.LocalEchoEnabled.Should().BeTrue();
            await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
            sut.PeerEchoing.Should().BeTrue();
            sut.LocalEchoEnabled.Should().BeFalse();
        }

        [Fact]
        public async Task ConsoleWrite_SuppressedAfterWillEcho()
        {
            using var stream = new ScriptedStream();
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            sut.IsWriteConsole = true;
            using var sink = new StringWriter();
            var prior = Console.Out;
            try
            {
                Console.SetOut(sink);
                stream.Enqueue(72, 105); // "Hi"
                (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().Be("Hi");
                sink.ToString().Should().Be("Hi");
                stream.Enqueue(255, 251, 1);
                (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
                stream.Enqueue(89, 111); // "Yo"
                (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().Be("Yo");
                sink.ToString().Should().Be("Hi");
            }
            finally { Console.SetOut(prior); }
        }

        [Fact]
        public async Task DoEcho_WithOptIn_RepliesWillAndTracksUs()
        {
            using var stream = new ScriptedStream(255, 253, 1);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            sut.AllowRemoteEcho = true;
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            CountWrites(stream, 251, 1).Should().Be(1);
            sut.Negotiation.IsEnabledByUs(1).Should().BeTrue();
        }

        [Fact]
        public async Task DoEcho_AfterWillEcho_EvenWithOptIn_RepliesWont()
        {
            using var stream = new ScriptedStream();
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            sut.AllowRemoteEcho = true;
            stream.Enqueue(255, 251, 1);
            await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
            stream.Enqueue(255, 253, 1);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            CountWrites(stream, 253, 1).Should().Be(1);
            CountWrites(stream, 252, 1).Should().Be(1);
            CountWrites(stream, 251, 1).Should().Be(0);
        }

        [Fact]
        public async Task WillEcho_AfterAgreedDoEcho_RepliesDont()
        {
            using var stream = new ScriptedStream();
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            sut.AllowRemoteEcho = true;
            stream.Enqueue(255, 253, 1);
            await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
            stream.Enqueue(255, 251, 1);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            CountWrites(stream, 251, 1).Should().Be(1);
            CountWrites(stream, 254, 1).Should().Be(1);
            CountWrites(stream, 253, 1).Should().Be(0);
        }

        [Fact]
        public async Task OptIn_EchoesReceivedBytesBack()
        {
            using var stream = new ScriptedStream();
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            sut.AllowRemoteEcho = true;
            stream.Enqueue(255, 253, 1);
            await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
            stream.Enqueue(65, 66);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().Be("AB");
            stream.ByteWrites.Should().Contain(b => b.SequenceEqual(new byte[] { 65, 66 }));
        }

        [Fact]
        public async Task NoOptIn_DataAroundDoEcho_IsNotEchoedBack()
        {
            using var stream = new ScriptedStream(65, 255, 253, 1, 66);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().Be("AB");
            // Only the WONT reply; the snapshot predates the mid-read agreement.
            stream.ByteWrites.Should().HaveCount(1);
            CountWrites(stream, 252, 1).Should().Be(1);
        }

        [Fact]
        public async Task OptIn_EchoBack_EscapesIac()
        {
            using var stream = new ScriptedStream();
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            sut.AllowRemoteEcho = true;
            stream.Enqueue(255, 253, 1);
            await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
            stream.Enqueue(255, 255);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().Be("\u00ff");
            stream.ByteWrites.Should().Contain(b => b.SequenceEqual(new byte[] { 255, 255 }));
        }

        [Fact]
        public async Task Client_WillEcho_SuppressesAcrossReads()
        {
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = new Client(stream, new CancellationToken());
                stream.Enqueue(255, 251, 1);
                (await client.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().BeEmpty();
                CountWrites(stream, 253, 1).Should().Be(1);
                client.Negotiation.IsEnabledByPeer(1).Should().BeTrue();
                stream.Enqueue(72, 105);
                (await client.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().Be("Hi");
            }
        }

        [Fact]
        public async Task Client_DoEcho_OptIn_EchoesBack()
        {
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = new Client(stream, new CancellationToken());
                client.Settings.AllowRemoteEcho = true;
                stream.Enqueue(255, 253, 1);
                (await client.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().BeEmpty();
                CountWrites(stream, 251, 1).Should().Be(1);
                stream.Enqueue(65);
                (await client.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().Be("A");
                stream.ByteWrites.Should().Contain(b => b.SequenceEqual(new byte[] { 65 }));
            }
        }

        [Fact]
        public async Task StatusSnapshot_ReportsAgreedEcho()
        {
            using var stream = new ScriptedStream();
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            stream.Enqueue(255, 251, 1);
            await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
            // Become the agreed WILL-sender for STATUS (RFC 859 §5 role gate)
            // before asking for the snapshot.
            stream.Enqueue(255, 253, 5);
            await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
            stream.Enqueue(255, 250, 5, 1, 255, 240);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            // STATUS IS uses the RFC 859 bare-SE terminator (no IAC before SE).
            stream.ByteWrites.Should().Contain(b => b.SequenceEqual(new byte[] { 255, 250, 5, 0, 253, 1, 251, 5, 240 }));
        }
    }
}
