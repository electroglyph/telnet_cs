namespace telnet_cs.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Client;
    using telnet_cs.IO;

    public class AuditCoverageTopUpTests
    {
        [Fact]
        public async Task Utf8TextEncodingDecodesMultibyteRead()
        {
            // "héllo" as UTF-8 bytes; the legacy path would return Latin-1 mojibake.
            using var stream = new ScriptedStream(104, 195, 169, 108, 108, 111);
            using var cts = new CancellationTokenSource();
            using var handler = new ByteStreamHandler(stream, cts, 1);
            handler.TextEncoding = Encoding.UTF8;
            var result = await handler.ReadAsync(TimeSpan.FromMilliseconds(100));
            result.Should().Be("h\u00E9llo");
        }

        [Fact]
        public async Task EtxArrivesAsData()
        {
            // Decided: control bytes are data, never caret notation.
            using var stream = new ScriptedStream(3);
            using var cts = new CancellationTokenSource();
            using var handler = new ByteStreamHandler(stream, cts, 1);
            var result = await handler.ReadAsync(TimeSpan.FromMilliseconds(100));
            result.Should().Be("\x03");
        }

        [Fact]
        public async Task MultiRegexMatchesViaDummyLoginFlow()
        {
            using (GlobalStateGuard.SkipProactive(false))
            {
                using var stream = new DummyByteStream();
                using var client = new Client(stream, TimeSpan.FromSeconds(30), new CancellationToken(), [], skipProactiveNegotiation: false);
                var result = await client.TerminatedReadAsync(
                  new[] { new Regex("Nope>"), new Regex("Account") }, TimeSpan.FromSeconds(5), 1);
                // The match ("Account") ends before the buffer: the cut keeps the
                // match, the trailing ":" is stashed for the next read.
                result.Should().Be("Account");
                (await client.ReadAsync(TimeSpan.FromSeconds(2))).Should().Be(":");
            }
        }

        [Fact]
        public async Task EmptyTerminatorCollectionReadsToTimeout()
        {
            using var stream = new ScriptedStream("AB");
            using var client = new Client(stream, new CancellationToken());
            var result = await client.TerminatedReadAsync(
              new string[0], TimeSpan.FromMilliseconds(50), 1);
            result.Should().Be("AB");
        }

        [Fact]
        public async Task EmptyRegexCollectionReadsToTimeout()
        {
            using var stream = new ScriptedStream("AB");
            using var client = new Client(stream, new CancellationToken());
            var result = await client.TerminatedReadAsync(
              new Regex[0], TimeSpan.FromMilliseconds(50), 1);
            result.Should().Be("AB");
        }

        [Fact]
        public async Task NawsAutoSizeHasRfc1073Shape()
        {
            using var stream = new ScriptedStream(255, 253, 31);
            using var cts = new CancellationTokenSource();
            using var handler = new ByteStreamHandler(stream, cts, 1);
            (await handler.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().BeEmpty();
            stream.ByteWrites.Should().HaveCount(2);
            var naws = stream.ByteWrites[1];
            naws.Should().HaveCount(9);
            naws[0].Should().Be(255);
            naws[1].Should().Be(250);
            naws[2].Should().Be(31);
            naws[7].Should().Be(255);
            naws[8].Should().Be(240);
        }
    }
}
