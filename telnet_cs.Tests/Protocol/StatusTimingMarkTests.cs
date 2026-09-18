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
            // (WILL STATUS itself earns the initiation SEND probe, not a DO:
            // the reference probes instead of acknowledging.)
            var (output, stream) = await ReadHandlerOnceAsync(static _ => { },
                255, 253, 5,
                255, 251, 5,
                255, 250, 5, 1, 255, 240);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().HaveCount(4);
            stream.ByteWrites[0].Should().Equal(new byte[] { 255, 251, 5 });
            stream.ByteWrites[1].Should().Equal(new byte[] { 255, 250, 5, 0, 255, 240 });
            stream.ByteWrites[2].Should().Equal(new byte[] { 255, 250, 5, 1, 255, 240 });
            stream.ByteWrites[3].Should().HaveCountGreaterThan(4);
            stream.ByteWrites[3][0].Should().Be((byte)255);
            stream.ByteWrites[3][1].Should().Be((byte)250);
            stream.ByteWrites[3][^1].Should().Be((byte)240);
        }

        [Fact]
        public async Task StatusSend_NoAgreements_IgnoredSilently()
        {
            // A STATUS SEND with no agreement is ignored silently: no IS and
            // no WONT reply.
            var (output, stream) = await ReadHandlerOnceAsync(static _ => { }, 255, 250, 5, 1, 255, 240);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task StatusSend_WhileOursOutstanding_IgnoredSilently()
        {
            // A locally-requested (WantYes) STATUS is not agreed yet, so an
            // early SEND is still ignored silently with no reply.
            var (output, stream) = await ReadHandlerOnceAsync(
              static sut => sut.Negotiation.RequestEnable((int)Options.Status),
              255, 250, 5, 1, 255, 240);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task StatusSend_AfterDoSga_ReportsWillSga()
        {
            var (output, stream) = await ReadHandlerOnceAsync(static _ => { }, 255, 253, 5, 255, 253, 3, 255, 250, 5, 1, 255, 240);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().HaveCount(4);
            stream.ByteWrites[0].Should().Equal(new byte[] { 255, 251, 5 });
            stream.ByteWrites[1].Should().Equal(new byte[] { 255, 250, 5, 0, 255, 240 });
            stream.ByteWrites[2].Should().Equal(new byte[] { 255, 251, 3 });
            stream.ByteWrites[3].Should().Equal(new byte[] { 255, 250, 5, 0, 251, 3, 255, 240 });
        }

        [Fact]
        public async Task StatusSend_AfterWillSga_ReportsDoSga()
        {
            var (output, stream) = await ReadHandlerOnceAsync(static _ => { }, 255, 253, 5, 255, 251, 3, 255, 250, 5, 1, 255, 240);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().HaveCount(4);
            stream.ByteWrites[0].Should().Equal(new byte[] { 255, 251, 5 });
            stream.ByteWrites[1].Should().Equal(new byte[] { 255, 250, 5, 0, 255, 240 });
            stream.ByteWrites[2].Should().Equal(new byte[] { 255, 253, 3 });
            stream.ByteWrites[3].Should().Equal(new byte[] { 255, 250, 5, 0, 253, 3, 255, 240 });
        }

        [Fact]
        public async Task StatusSend_RefusedOption_Omitted()
        {
            // Refused options render as WONT/DONT in the snapshot (previously
            // omitted): TELOPT 92 was refused by us, so the IS frames carry
            // the DONT pair (reference _send_status iterates touched options,
            // False -> WONT/DONT).
            var (output, stream) = await ReadHandlerOnceAsync(static _ => { }, 255, 253, 5, 255, 251, 92, 255, 250, 5, 1, 255, 240);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().HaveCount(4);
            stream.ByteWrites[0].Should().Equal(new byte[] { 255, 251, 5 });
            stream.ByteWrites[1].Should().Equal(new byte[] { 255, 250, 5, 0, 255, 240 });
            stream.ByteWrites[2].Should().Equal(new byte[] { 255, 254, 92 });
            stream.ByteWrites[3].Should().Equal(new byte[] { 255, 250, 5, 0, 254, 92, 255, 240 });
        }

        [Fact]
        public async Task StatusSend_AfterRevoke_OmitsAgain()
        {
            // DO SGA is agreed (WILL SGA), then the DONT revocation is silent
            // (negatives earn no reply, so no WONT) and drops us back to No —
            // the SEND snapshot therefore omits SGA again, exactly like the
            // DO-time snapshot taken before SGA was agreed.
            var (output, stream) = await ReadHandlerOnceAsync(static _ => { }, 255, 253, 5, 255, 253, 3, 255, 254, 3, 255, 250, 5, 1, 255, 240);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().HaveCount(4);
            stream.ByteWrites[0].Should().Equal(new byte[] { 255, 251, 5 });
            stream.ByteWrites[1].Should().Equal(new byte[] { 255, 250, 5, 0, 255, 240 });
            stream.ByteWrites[2].Should().Equal(new byte[] { 255, 251, 3 });
            stream.ByteWrites[3].Should().Equal(new byte[] { 255, 250, 5, 0, 255, 240 });
        }

        [Fact]
        public async Task StatusSend_BothSides_ReportsBoth()
        {
            var (output, stream) = await ReadHandlerOnceAsync(static _ => { }, 255, 253, 5, 255, 253, 3, 255, 251, 3, 255, 250, 5, 1, 255, 240);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().HaveCount(5);
            stream.ByteWrites[0].Should().Equal(new byte[] { 255, 251, 5 });
            stream.ByteWrites[1].Should().Equal(new byte[] { 255, 250, 5, 0, 255, 240 });
            stream.ByteWrites[2].Should().Equal(new byte[] { 255, 251, 3 });
            stream.ByteWrites[3].Should().Equal(new byte[] { 255, 253, 3 });
            stream.ByteWrites[4].Should().Equal(new byte[] { 255, 250, 5, 0, 251, 3, 253, 3, 255, 240 });
        }

        [Fact]
        public async Task StatusSend_StrayIs_IgnoredSilently()
        {
            var (output, stream) = await ReadHandlerOnceAsync(static _ => { }, 255, 250, 5, 0, 255, 240);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().BeEmpty();
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
        public async Task WillTimingMark_Unsolicited_IgnoredSilently()
        {
            var (output, stream) = await ReadHandlerOnceAsync(static _ => { }, 255, 251, 6);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task WillTimingMark_Solicited_PersistsAgreementWithoutReply()
        {
            var stream = new ScriptedStream([255, 251, 6]);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            sut.Negotiation.RequestTimingMark().Should().Be(Commands.Do);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            stream.ByteWrites.Should().BeEmpty();
            sut.Negotiation.IsEnabledByPeer((int)Options.TimingMark).Should().BeTrue();
        }

        [Fact]
        public async Task WillTimingMark_WhileAgreedWithoutOutstanding_IgnoredSilently()
        {
            var stream = new ScriptedStream([255, 251, 6]);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            sut.Negotiation.RequestTimingMark();
            sut.Negotiation.ReceivedWill((int)Options.TimingMark, agree: true);
            sut.Negotiation.IsEnabledByPeer((int)Options.TimingMark).Should().BeTrue();
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            stream.ByteWrites.Should().BeEmpty();
            sut.Negotiation.IsEnabledByPeer((int)Options.TimingMark).Should().BeTrue();
        }

        [Fact]
        public async Task WontTimingMark_Solicited_ClearsOutstandingWithoutReply()
        {
            var stream = new ScriptedStream([255, 252, 6]);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            sut.Negotiation.RequestTimingMark().Should().Be(Commands.Do);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            stream.ByteWrites.Should().BeEmpty();
            sut.Negotiation[(int)Options.TimingMark].Him
              .Should().Be(NegotiationState.SideState.No);
            sut.Negotiation.WasRefusedByPeer((int)Options.TimingMark).Should().BeTrue();
        }

        [Fact]
        public async Task WontTimingMark_Unsolicited_IgnoredSilently()
        {
            var (output, stream) = await ReadHandlerOnceAsync(static _ => { }, 255, 252, 6);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task SendTimingMarkAsync_SendsDo()
        {
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = await Client.CreateAsync(stream, TimeSpan.FromSeconds(30), new CancellationToken());
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
                using var client = await Client.CreateAsync(stream, TimeSpan.FromSeconds(30), new CancellationToken());
                await client.SendTimingMarkAsync();
                await client.SendTimingMarkAsync();
                stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(new byte[] { 255, 253, 6 });
            }
        }

        [Fact]
        public async Task TimingMarkRoundTrip_PeerWill_RecordsAgreement()
        {
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = await Client.CreateAsync(stream, TimeSpan.FromSeconds(30), new CancellationToken());
                await client.SendTimingMarkAsync();
                stream.Enqueue(255, 251, 6);
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(new byte[] { 255, 253, 6 });
                client.Negotiation.IsEnabledByPeer((int)Options.TimingMark).Should().BeTrue();
            }
        }

        [Fact]
        public async Task SendTimingMarkAsync_AfterAgreement_Repings()
        {
            // Every DO TM is answered, so a ping after agreement goes out
            // again instead of being suppressed as already-negotiated.
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = await Client.CreateAsync(stream, TimeSpan.FromSeconds(30), new CancellationToken());
                await client.SendTimingMarkAsync();
                stream.Enqueue(255, 251, 6);
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                await client.SendTimingMarkAsync();
                stream.ByteWrites.Should().HaveCount(2);
                stream.ByteWrites[1].Should().Equal(new byte[] { 255, 253, 6 });
                client.Negotiation[(int)Options.TimingMark].Him
                  .Should().Be(NegotiationState.SideState.WantYes);
            }
        }

        [Fact]
        public async Task TimingMarkRoundTrip_PeerWill_CompletesWithoutReply()
        {
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = await Client.CreateAsync(stream, TimeSpan.FromSeconds(30), new CancellationToken());
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
                using var client = await Client.CreateAsync(stream, TimeSpan.FromSeconds(30), new CancellationToken());
                await client.SendTimingMarkAsync();
                stream.Enqueue(255, 253, 5, 255, 250, 5, 1, 255, 240);
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                stream.ByteWrites.Should().HaveCount(4);
                stream.ByteWrites[1].Should().Equal(new byte[] { 255, 251, 5 });
                stream.ByteWrites[2].Should().Equal(new byte[] { 255, 250, 5, 0, 253, 6, 255, 240 });
                stream.ByteWrites[3].Should().Equal(new byte[] { 255, 250, 5, 0, 253, 6, 255, 240 });
            }
        }

        [Fact]
        public async Task StatusSend_ReportsOutstandingWill()
        {
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = await Client.CreateAsync(
                  stream,
                  TimeSpan.FromSeconds(30),
                  new CancellationToken(),
                  [(Commands.Will, Options.TimingMark)]);
                stream.Enqueue(255, 253, 5, 255, 250, 5, 1, 255, 240);
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                stream.ByteWrites.Should().HaveCount(3);
                stream.ByteWrites[0].Should().Equal(new byte[] { 255, 251, 5 });
                stream.ByteWrites[1].Should().Equal(new byte[] { 255, 250, 5, 0, 255, 240 });
                stream.ByteWrites[2].Should().Equal(new byte[] { 255, 250, 5, 0, 255, 240 });
            }
        }

        [Fact]
        public async Task StatusSend_SeOptionByte_RawUnderIacSe()
        {
            // Under IAC SE framing only IAC itself is escaped: a 240 item
            // byte passes through raw (doubling it would corrupt the parse),
            // and the frame ends IAC SE.
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = await Client.CreateAsync(stream, TimeSpan.FromSeconds(30), new CancellationToken());
                await client.RequestEnableAsync((Options)240);
                stream.Enqueue(255, 253, 5, 255, 250, 5, 1, 255, 240);
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                stream.ByteWrites.Should().HaveCount(4);
                stream.ByteWrites[0].Should().Equal(new byte[] { 255, 253, 240 });
                stream.ByteWrites[1].Should().Equal(new byte[] { 255, 251, 5 });
                stream.ByteWrites[2].Should().Equal(new byte[] { 255, 250, 5, 0, 253, 240, 255, 240 });
                stream.ByteWrites[3].Should().Equal(new byte[] { 255, 250, 5, 0, 253, 240, 255, 240 });
            }
        }

        [Fact]
        public async Task StatusBareSe_TreatedAsData_ConsumedWithSubnegotiation()
        {
            // Source of truth: ~/telnetlib3/telnetlib3/stream_writer.py has no
            // STATUS-specific scan — every IAC inside SB is uniform and a bare
            // 0xF0 is ordinary payload data. The whole frame through IAC SE is
            // one STATUS subnegotiation (here a stray IS), so nothing leaks to
            // text and nothing is answered.
            var (output, stream) = await ReadHandlerOnceAsync(static _ => { }, 255, 250, 5, 0, 240, 65, 66, 255, 240);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task StatusDoubledSe_StaysInPayload()
        {
            // SE SE inside STATUS escapes a literal SE data byte: payload
            // [IS, SE] is still a stray IS, ignored silently with nothing
            // leaked to data. The final IAC SE is the SB terminator.
            var (output, stream) = await ReadHandlerOnceAsync(static _ => { }, 255, 250, 5, 0, 240, 240, 255, 240);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().BeEmpty();
        }
    }
}
