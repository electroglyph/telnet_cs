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

    public class StatusTimingMarkTests
    {
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

        [Fact]
        public async Task StatusSend_WhenAgreed_AnswersIsWithoutRenegotiating()
        {
            // RFC 859 motivation: a status query must not trigger renegotiation —
            // answering SEND emits exactly the IS snapshot, no new WILL/DO/WONT/DONT.
            var (output, stream) = await ReadHandlerOnceAsync(static _ => { },
                255, 253, 5,
                255, 251, 5,
                255, 250, 5, 1, 255, 240);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().HaveCount(3);
            stream.ByteWrites[0].Should().Equal(new byte[] { 255, 251, 5 });
            stream.ByteWrites[1].Should().Equal(new byte[] { 255, 253, 5 });
            stream.ByteWrites[2].Should().HaveCountGreaterThan(4);
            stream.ByteWrites[2][0].Should().Be((byte)255);
            stream.ByteWrites[2][1].Should().Be((byte)250);
            stream.ByteWrites[2][^1].Should().Be((byte)240);
        }

        [Fact]
        public async Task StatusSend_NoAgreements_GetsWont()
        {
            // RFC 859 §5: only the WILL-sender answers SEND. Nothing agreed → WONT.
            var (output, stream) = await ReadHandlerOnceAsync(static _ => { }, 255, 250, 5, 1, 255, 240);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(new byte[] { 255, 252, 5 });
        }

        [Fact]
        public async Task StatusSend_WhileOursOutstanding_GetsWont()
        {
            // Strict Yes-only: a locally-requested (WantYes) STATUS is not the
            // agreed WILL-sender yet, so an early SEND still earns WONT.
            var (output, stream) = await ReadHandlerOnceAsync(
              static sut => sut.Negotiation.RequestEnable((int)Options.Status),
              255, 250, 5, 1, 255, 240);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(new byte[] { 255, 252, 5 });
        }

        [Fact]
        public async Task StatusSend_AfterDoSga_ReportsWillSga()
        {
            var (output, stream) = await ReadHandlerOnceAsync(static _ => { }, 255, 253, 5, 255, 253, 3, 255, 250, 5, 1, 255, 240);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().HaveCount(3);
            stream.ByteWrites[0].Should().Equal(new byte[] { 255, 251, 5 });
            stream.ByteWrites[1].Should().Equal(new byte[] { 255, 251, 3 });
            stream.ByteWrites[2].Should().Equal(new byte[] { 255, 250, 5, 0, 251, 3, 251, 5, 240 });
        }

        [Fact]
        public async Task StatusSend_AfterWillSga_ReportsDoSga()
        {
            var (output, stream) = await ReadHandlerOnceAsync(static _ => { }, 255, 253, 5, 255, 251, 3, 255, 250, 5, 1, 255, 240);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().HaveCount(3);
            stream.ByteWrites[0].Should().Equal(new byte[] { 255, 251, 5 });
            stream.ByteWrites[1].Should().Equal(new byte[] { 255, 253, 3 });
            stream.ByteWrites[2].Should().Equal(new byte[] { 255, 250, 5, 0, 253, 3, 251, 5, 240 });
        }

        [Fact]
        public async Task StatusSend_RefusedOption_Omitted()
        {
            // TELOPT 92 stays refused; this harness agrees nothing else, so
            // only the WILL STATUS self-entry joins the IS.
            var (output, stream) = await ReadHandlerOnceAsync(static _ => { }, 255, 253, 5, 255, 251, 92, 255, 250, 5, 1, 255, 240);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().HaveCount(3);
            stream.ByteWrites[0].Should().Equal(new byte[] { 255, 251, 5 });
            stream.ByteWrites[1].Should().Equal(new byte[] { 255, 254, 92 });
            stream.ByteWrites[2].Should().Equal(new byte[] { 255, 250, 5, 0, 251, 5, 240 });
        }

        [Fact]
        public async Task StatusSend_AfterRevoke_OmitsAgain()
        {
            var (output, stream) = await ReadHandlerOnceAsync(static _ => { }, 255, 253, 5, 255, 253, 3, 255, 254, 3, 255, 250, 5, 1, 255, 240);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().HaveCount(4);
            stream.ByteWrites[0].Should().Equal(new byte[] { 255, 251, 5 });
            stream.ByteWrites[1].Should().Equal(new byte[] { 255, 251, 3 });
            stream.ByteWrites[2].Should().Equal(new byte[] { 255, 252, 3 });
            stream.ByteWrites[3].Should().Equal(new byte[] { 255, 250, 5, 0, 251, 5, 240 });
        }

        [Fact]
        public async Task StatusSend_BothSides_ReportsBoth()
        {
            var (output, stream) = await ReadHandlerOnceAsync(static _ => { }, 255, 253, 5, 255, 253, 3, 255, 251, 3, 255, 250, 5, 1, 255, 240);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().HaveCount(4);
            stream.ByteWrites[0].Should().Equal(new byte[] { 255, 251, 5 });
            stream.ByteWrites[1].Should().Equal(new byte[] { 255, 251, 3 });
            stream.ByteWrites[2].Should().Equal(new byte[] { 255, 253, 3 });
            stream.ByteWrites[3].Should().Equal(new byte[] { 255, 250, 5, 0, 251, 3, 253, 3, 251, 5, 240 });
        }

        [Fact]
        public async Task StatusSend_StrayIs_GetsWont()
        {
            var (output, stream) = await ReadHandlerOnceAsync(static _ => { }, 255, 250, 5, 0, 255, 240);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(new byte[] { 255, 252, 5 });
        }

        [Fact]
        public async Task DoTimingMark_GetsWill()
        {
            var (output, stream) = await ReadHandlerOnceAsync(static _ => { }, 255, 253, 6);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(new byte[] { 255, 251, 6 });
        }

        [Fact]
        public async Task DoTimingMark_AfterData_DataDeliveredWithMarkAnswered()
        {
            var (output, stream) = await ReadHandlerOnceAsync(static _ => { }, 104, 105, 255, 253, 6);
            output.Should().Be("hi");
            stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(new byte[] { 255, 251, 6 });
        }

        [Fact]
        public async Task WillTimingMark_GetsDo()
        {
            var (output, stream) = await ReadHandlerOnceAsync(static _ => { }, 255, 251, 6);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(new byte[] { 255, 253, 6 });
        }

        [Fact]
        public async Task SendTimingMarkAsync_SendsDo()
        {
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = new Client(stream, new CancellationToken());
                await client.SendTimingMarkAsync();
                stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(new byte[] { 255, 253, 6 });
            }
        }

        [Fact]
        public async Task SendTimingMarkAsync_TwiceWhileOutstanding_SendsOnce()
        {
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = new Client(stream, new CancellationToken());
                await client.SendTimingMarkAsync();
                await client.SendTimingMarkAsync();
                stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(new byte[] { 255, 253, 6 });
            }
        }

        [Fact]
        public async Task TimingMarkRoundTrip_PeerWill_CompletesWithoutReply()
        {
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = new Client(stream, new CancellationToken());
                await client.SendTimingMarkAsync();
                stream.Enqueue(255, 251, 6);
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(new byte[] { 255, 253, 6 });
            }
        }

        [Fact]
        public async Task StatusSend_ReportsOutstandingDo()
        {
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = new Client(stream, new CancellationToken());
                await client.SendTimingMarkAsync();
                stream.Enqueue(255, 253, 5, 255, 250, 5, 1, 255, 240);
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                stream.ByteWrites.Should().HaveCount(3);
                stream.ByteWrites[1].Should().Equal(new byte[] { 255, 251, 5 });
                stream.ByteWrites[2].Should().Equal(new byte[] { 255, 250, 5, 0, 251, 5, 253, 6, 240 });
            }
        }

        [Fact]
        public async Task StatusSend_ReportsOutstandingWill()
        {
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = new Client(
                  stream,
                  TimeSpan.FromSeconds(30),
                  new CancellationToken(),
                  [(Commands.Will, Options.TimingMark)]);
                stream.Enqueue(255, 253, 5, 255, 250, 5, 1, 255, 240);
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                stream.ByteWrites.Should().HaveCount(3);
                stream.ByteWrites[0].Should().Equal(new byte[] { 255, 251, 6 });
                stream.ByteWrites[1].Should().Equal(new byte[] { 255, 251, 5 });
                stream.ByteWrites[2].Should().Equal(new byte[] { 255, 250, 5, 0, 251, 5, 251, 6, 240 });
            }
        }

        [Fact]
        public async Task StatusSend_SeOptionByte_Doubled()
        {
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = new Client(stream, new CancellationToken());
                await client.RequestEnableAsync((Options)240);
                stream.Enqueue(255, 253, 5, 255, 250, 5, 1, 255, 240);
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                stream.ByteWrites.Should().HaveCount(3);
                stream.ByteWrites[0].Should().Equal(new byte[] { 255, 253, 240 });
                stream.ByteWrites[1].Should().Equal(new byte[] { 255, 251, 5 });
                stream.ByteWrites[2].Should().Equal(new byte[] { 255, 250, 5, 0, 251, 5, 253, 240, 240, 240 });
            }
        }

        [Fact]
        public async Task StatusBareSe_TerminatesScanAndPreservesTrailingBytes()
        {
            // RFC 859 inner framing: a bare SE ends a STATUS payload, so the
            // bytes after it (here "AB") belong to the subsequent stream.
            // Payload [IS] is a stray IS and earns WONT.
            var (output, stream) = await ReadHandlerOnceAsync(static _ => { }, 255, 250, 5, 0, 240, 65, 66, 255, 240);
            output.Should().Be("AB");
            stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(new byte[] { 255, 252, 5 });
        }

        [Fact]
        public async Task StatusDoubledSe_StaysInPayload()
        {
            // SE SE inside STATUS escapes a literal SE data byte (RFC 859):
            // payload [IS, SE] is still a stray IS, and nothing leaks to data.
            var (output, stream) = await ReadHandlerOnceAsync(static _ => { }, 255, 250, 5, 0, 240, 240, 255, 240);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(new byte[] { 255, 252, 5 });
        }
    }
}
