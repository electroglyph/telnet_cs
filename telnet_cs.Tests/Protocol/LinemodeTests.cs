namespace telnet_cs.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Client;
    using telnet_cs.IO;
    using telnet_cs.Protocol;

    public class LinemodeTests
    {
        private static async Task<(string Output, ScriptedStream Stream)> ReadWithStreamAsync(params int[] reads)
        {
            var stream = new ScriptedStream(reads);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            var output = await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
            return (output, stream);
        }

        private static async Task<string> ReadClientOnceAsync(Client client)
        {
            return await client.ReadAsync(TimeSpan.FromMilliseconds(100));
        }

        public static TheoryData<Commands, byte> NewControlCommands => new()
    {
      { Commands.EndOfFile, 236 },
      { Commands.Suspend, 237 },
      { Commands.Abort, 238 },
    };

        [Theory]
        [MemberData(nameof(NewControlCommands))]
        public async Task SendCommand_EofSuspendAbort_WritesIacFramedPair(Commands command, byte code)
        {
            using var stream = new ScriptedStream();
            using var sut = new Client(stream, new CancellationToken());
            await sut.SendCommand(command);
            stream.ByteWrites.Should().ContainSingle(w => w.SequenceEqual(new byte[] { 255, code }));
        }

        [Theory]
        [InlineData(236)] // EOF
        [InlineData(237)] // SUSP
        [InlineData(238)] // ABORT
        public async Task SignalReceived_ConsumedSilently(int code)
        {
            // Decided: out-of-band signals are consumed, never marker text.
            var (output, _) = await ReadWithStreamAsync(255, code);
            output.Should().BeEmpty();
        }

        [Fact]
        public async Task DoLinemode_IsAgreed()
        {
            var (_, stream) = await ReadWithStreamAsync(255, 253, 34);
            stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(new byte[] { 255, 251, 34 });
        }

        [Fact]
        public async Task ModeRequest_IsConfirmedWithAck()
        {
            // MODE is only answered after LINEMODE agreement: DO is agreed with
            // WILL, then the mask is echoed verbatim plus ACK.
            var (output, stream) = await ReadWithStreamAsync(255, 253, 34, 255, 250, 34, 1, 3, 255, 240);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().HaveCount(2);
            stream.ByteWrites[0].Should().Equal(new byte[] { 255, 251, 34 });
            stream.ByteWrites[1].Should().Equal(new byte[] { 255, 250, 34, 1, 7, 255, 240 });
        }

        [Fact]
        public async Task SplitSbLinemodeMode_ReassemblesAcrossReads_AndReplies()
        {
            // Port of test_client_process_chunk_split_sb_linemode: IAC SB
            // LINEMODE MODE split across reads must be stashed, then
            // answered once the mode byte and IAC SE arrive. Agreement comes
            // first so the completed MODE is answered verbatim plus ACK.
            using var stream = new ScriptedStream(255, 253, 34, 255, 250, 34, 1);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            stream.ByteWrites.Should().ContainSingle().Which.Should()
              .Equal(new byte[] { 255, 251, 34 });
            stream.Enqueue(3, 255, 240);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            stream.ByteWrites.Should().HaveCount(2);
            stream.ByteWrites[1].Should().Equal(new byte[] { 255, 250, 34, 1, 7, 255, 240 });
        }

        [Fact]
        public async Task ModeRepeatAcrossReads_RepliesOnce()
        {
            // Agreement first, then the same MODE twice: the first is echoed
            // verbatim plus ACK, the repeat is already in effect and silent.
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = new Client(stream, new CancellationToken());
                stream.Enqueue(255, 253, 34);
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                stream.Enqueue(255, 250, 34, 1, 3, 255, 240);
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                stream.Enqueue(255, 250, 34, 1, 3, 255, 240);
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                stream.ByteWrites.Should().HaveCount(2);
                stream.ByteWrites[0].Should().Equal(new byte[] { 255, 251, 34 });
                stream.ByteWrites[1].Should().Equal(new byte[] { 255, 250, 34, 1, 7, 255, 240 });
            }
        }

        [Fact]
        public async Task ModeUnsupportedBits_AnsweredAsSubset()
        {
            // EDIT | TRAPSIG | SOFT_TAB | LIT_ECHO: the mask is echoed verbatim
            // plus ACK with no subsetting, even for bits without local handling.
            var (_, stream) = await ReadWithStreamAsync(255, 253, 34, 255, 250, 34, 1, 27, 255, 240);
            stream.ByteWrites.Should().HaveCount(2);
            stream.ByteWrites[0].Should().Equal(new byte[] { 255, 251, 34 });
            stream.ByteWrites[1].Should().Equal(new byte[] { 255, 250, 34, 1, 31, 255, 240 });
        }

        [Fact]
        public async Task ModeAck_IsNeverAnswered()
        {
            // Agreement first: MODE without ACK is echoed verbatim plus ACK,
            // then the ACK echoing the agreed mask is already in effect and silent.
            using var stream = new ScriptedStream();
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            stream.Enqueue(255, 253, 34);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            stream.Enqueue(255, 250, 34, 1, 3, 255, 240);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            stream.Enqueue(255, 250, 34, 1, 7, 255, 240);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            stream.ByteWrites.Should().HaveCount(2);
            stream.ByteWrites[0].Should().Equal(new byte[] { 255, 251, 34 });
            stream.ByteWrites[1].Should().Equal(new byte[] { 255, 250, 34, 1, 7, 255, 240 });
        }

        [Fact]
        public async Task ModeTruncated_Ignored()
        {
            var (output, stream) = await ReadWithStreamAsync(255, 250, 34, 1, 255, 240);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task ForwardMaskProposal_RefusedWithWont()
        {
            var (output, stream) = await ReadWithStreamAsync(255, 250, 34, 253, 2, 0, 0, 255, 240);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().BeEmpty();
        }

        public static TheoryData<int> ForwardMaskSilentVerbs => new()
    {
      254, // DONT: accepted (we never forward anyway).
      251, // WILL: unsolicited (we never sent DO), ignored.
      252, // WONT: unsolicited, ignored.
    };

        [Theory]
        [MemberData(nameof(ForwardMaskSilentVerbs))]
        public async Task ForwardMaskNonProposal_Silent(int verb)
        {
            var (output, stream) = await ReadWithStreamAsync(255, 250, 34, verb, 2, 255, 240);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task ForwardMaskTruncated_Ignored()
        {
            var (output, stream) = await ReadWithStreamAsync(255, 250, 34, 253, 255, 240);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task SlcServerValue_AgreedWithAck()
        {
            var (output, stream) = await ReadWithStreamAsync(255, 250, 34, 3, 3, 2, 9, 255, 240);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().ContainSingle().Which.Should()
              .Equal(new byte[] { 255, 250, 34, 3, 3, 130, 9, 255, 240 });
        }

        [Fact]
        public async Task SlcRepeat_Ignored()
        {
            using var stream = new ScriptedStream();
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            stream.Enqueue(255, 250, 34, 3, 3, 2, 9, 255, 240);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            stream.Enqueue(255, 250, 34, 3, 3, 2, 9, 255, 240);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            stream.ByteWrites.Should().ContainSingle().Which.Should()
              .Equal(new byte[] { 255, 250, 34, 3, 3, 130, 9, 255, 240 });
        }

        [Fact]
        public async Task SlcAckedChange_SwitchesSilently()
        {
            using var stream = new ScriptedStream();
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            stream.Enqueue(255, 250, 34, 3, 3, 2, 9, 255, 240);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            // Same level, different value, ACK set: silent switch to 10.
            stream.Enqueue(255, 250, 34, 3, 3, 130, 10, 255, 240);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            // Proves the switch landed: 9 is now the change, agreed with ACK.
            stream.Enqueue(255, 250, 34, 3, 3, 2, 9, 255, 240);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            stream.ByteWrites.Should().HaveCount(2);
            stream.ByteWrites[1].Should().Equal(new byte[] { 255, 250, 34, 3, 3, 130, 9, 255, 240 });
        }

        [Fact]
        public async Task SlcAckedChange_AsServer_IgnoresWithoutStoring()
        {
            // RFC 1184 §5.5 rule 2, server column: a same-level ACKed change
            // with a different value is ignored — no reply and no state
            // change. Same script as SlcAckedChange_SwitchesSilently, but the
            // handler runs the server rules, so the third read finds the row
            // still at 9 and stays silent (one reply total, not two).
            using var stream = new ScriptedStream();
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            sut.ApplyLinemodeAsServer = true;
            stream.Enqueue(255, 250, 34, 3, 3, 2, 9, 255, 240);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            stream.Enqueue(255, 250, 34, 3, 3, 130, 10, 255, 240);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            sut.Linemode.GetEntry(3).Should().Be(new SlcEntry(2, 9, 0));
            stream.Enqueue(255, 250, 34, 3, 3, 2, 9, 255, 240);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            stream.ByteWrites.Should().ContainSingle().Which.Should()
              .Equal(new byte[] { 255, 250, 34, 3, 3, 130, 9, 255, 240 });
        }

        [Fact]
        public async Task SlcCantChange_DisagreesWithoutAck()
        {
            using var stream = new ScriptedStream();
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            sut.Linemode.SetEntry(3, 1, 7);
            stream.Enqueue(255, 250, 34, 3, 3, 2, 9, 255, 240);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            stream.ByteWrites.Should().ContainSingle().Which.Should()
              .Equal(new byte[] { 255, 250, 34, 3, 3, 1, 7, 255, 240 });
        }

        [Fact]
        public async Task SlcUnknownFunction_RefusedAsDefault()
        {
            var (_, stream) = await ReadWithStreamAsync(255, 250, 34, 3, 31, 2, 65, 255, 240);
            stream.ByteWrites.Should().ContainSingle().Which.Should()
              .Equal(new byte[] { 255, 250, 34, 3, 31, 3, 0, 255, 240 });
        }

        [Fact]
        public async Task SlcImportRequest_DroppedSilently()
        {
            // Func 0 is an import request only a server answers; a client drops
            // it silently instead of echoing it back (no ping-pong).
            var (output, stream) = await ReadWithStreamAsync(255, 250, 34, 3, 0, 3, 0, 255, 240);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task SlcImportTriplet_DroppedWhileOthersAnswered()
        {
            var (output, stream) = await ReadWithStreamAsync(255, 250, 34, 3, 0, 3, 0, 3, 2, 5, 255, 240);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().ContainSingle().Which.Should()
              .Equal(new byte[] { 255, 250, 34, 3, 3, 130, 5, 255, 240 });
        }

        [Fact]
        public async Task SlcTruncated_Throws()
        {
            // telnetlib3 _handle_sb_linemode_slc raises ValueError on len%3≠0:
            // the whole buffer is rejected, nothing is answered.
            using var stream = new ScriptedStream();
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            stream.Enqueue(255, 250, 34, 3, 3, 2, 255, 240);
            var act = async () => await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
            (await act.Should().ThrowAsync<InvalidDataException>()).WithMessage("*multiple of 3*");
            stream.ByteWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task SlcDefault_RestoresConfiguredValue()
        {
            // Peer moves IP to ^E, then sends DEFAULT: the row restores to the
            // configured ^C and the reply carries the restored row without ACK.
            using var stream = new ScriptedStream();
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            sut.Linemode.SetEntry(3, 2, 3);
            stream.Enqueue(255, 250, 34, 3, 3, 2, 5, 255, 240);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            stream.Enqueue(255, 250, 34, 3, 3, 3, 99, 255, 240);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            stream.ByteWrites.Should().HaveCount(2);
            stream.ByteWrites[1].Should().Equal(new byte[] { 255, 250, 34, 3, 3, 2, 3, 255, 240 });
            // Proves the restore landed: the configured value is now identical.
            stream.Enqueue(255, 250, 34, 3, 3, 2, 3, 255, 240);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            stream.ByteWrites.Should().HaveCount(2);
        }

        [Fact]
        public async Task SlcDefault_UnsupportedFunction_RefusedAsNoSupport()
        {
            // SYNCH starts at DEFAULT level (unsupported): DEFAULT restores to
            // NOSUPPORT with the default value instead of storing DEFAULT.
            var (output, stream) = await ReadWithStreamAsync(255, 250, 34, 3, 1, 3, 0, 255, 240);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().ContainSingle().Which.Should()
              .Equal(new byte[] { 255, 250, 34, 3, 1, 0, 0, 255, 240 });
        }

        [Fact]
        public async Task ImportRemoteSpecialCharacters_AfterNegotiation_SendsImport()
        {
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = new Client(stream, new CancellationToken());
                stream.Enqueue(255, 253, 34);
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                await client.ImportRemoteSpecialCharactersAsync();
                stream.ByteWrites.Should().HaveCount(2);
                stream.ByteWrites[0].Should().Equal(new byte[] { 255, 251, 34 });
                stream.ByteWrites[1].Should().Equal(new byte[] { 255, 250, 34, 3, 0, 3, 0, 255, 240 });
            }
        }

        [Fact]
        public async Task ImportRemoteSpecialCharacters_WithoutNegotiation_Silent()
        {
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = new Client(stream, new CancellationToken());
                await client.ImportRemoteSpecialCharactersAsync();
                stream.ByteWrites.Should().BeEmpty();
            }
        }

        [Fact]
        public async Task ExportSpecialCharacters_WithEntries_SendsTable()
        {
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = new Client(stream, new CancellationToken());
                stream.Enqueue(255, 253, 34);
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                client.SetLinemodeEntry(3, 2, 9);
                client.SetLinemodeEntry(10, 2, 8);
                await client.ExportSpecialCharactersAsync();
                stream.ByteWrites.Should().HaveCount(2);
                stream.ByteWrites[1].Should().Equal(new byte[] { 255, 250, 34, 3, 3, 2, 9, 10, 2, 8, 255, 240 });
            }
        }

        [Fact]
        public async Task ExportSpecialCharacters_EmptyTable_Silent()
        {
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = new Client(stream, new CancellationToken());
                stream.Enqueue(255, 253, 34);
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                await client.ExportSpecialCharactersAsync();
                stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(new byte[] { 255, 251, 34 });
            }
        }

        [Fact]
        public async Task SendCommand_FlushOut_SendsDoTimingMark()
        {
            // RFC 1184 §5.8: the BRK row carries FLUSHOUT (modifier 2|32), so the
            // sent IAC BRK is followed by IAC DO TIMING-MARK.
            // RFC 1184 §5.5 rule 3: the SLC agreement reply echoes the
            // negotiated FLUSHOUT bit with ACK (2|32|128 = 162).
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = new Client(stream, new CancellationToken());
                stream.Enqueue(255, 253, 34);
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                stream.Enqueue(255, 250, 34, 3, 2, 34, 7, 255, 240);
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                await client.SendCommand(Commands.Break);
                stream.ByteWrites.Should().HaveCount(4);
                stream.ByteWrites[0].Should().Equal(new byte[] { 255, 251, 34 });
                stream.ByteWrites[1].Should().Equal(new byte[] { 255, 250, 34, 3, 2, 162, 7, 255, 240 });
                stream.ByteWrites[2].Should().Equal(new byte[] { 255, 243 });
                stream.ByteWrites[3].Should().Equal(new byte[] { 255, 253, 6 });
            }
        }

        [Fact]
        public async Task SendCommand_FlushIn_WithoutTcp_LogsAndSkips()
        {
            // FLUSHIN on a non-TCP stream: no throw, Synch skipped with a log.
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = new Client(stream, new CancellationToken());
                stream.Enqueue(255, 253, 34);
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                client.SetLinemodeEntry(3, 2, 3, 64);
                var logged = new List<string>();
                client.Settings.Log = logged.Add;
                await client.SendCommand(Commands.InterruptProcess);
                stream.ByteWrites.Should().HaveCount(2);
                stream.ByteWrites[1].Should().Equal(new byte[] { 255, 244 });
                logged.Should().ContainSingle().Which.Should().Contain("Synch");
            }
        }

        [Fact]
        public async Task SendCommand_FlushFlags_WithoutAgreement_SendsNothingExtra()
        {
            // Flags set but LINEMODE never agreed: the gate blocks both halves.
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = new Client(stream, new CancellationToken());
                client.SetLinemodeEntry(2, 2, 7, 32);
                await client.SendCommand(Commands.Break);
                stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(new byte[] { 255, 243 });
            }
        }

        [Fact]
        public async Task SlcForw2_WithoutForw1_RefusedAsNoSupport()
        {
            // RFC 1184 §5.5: lone FORW2 is refused as NOSUPPORT, not accepted.
            var (output, stream) = await ReadWithStreamAsync(255, 250, 34, 3, 18, 2, 5, 255, 240);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().ContainSingle().Which.Should()
              .Equal(new byte[] { 255, 250, 34, 3, 18, 0, 0, 255, 240 });
        }

        [Fact]
        public async Task SlcForw1ThenForw2_AgreedInOrder()
        {
            // Triplets apply sequentially: FORW1 listed first is stored before
            // FORW2 is checked, so both are ACKed.
            var (output, stream) = await ReadWithStreamAsync(255, 250, 34, 3, 17, 2, 5, 18, 2, 6, 255, 240);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().ContainSingle().Which.Should()
              .Equal(new byte[] { 255, 250, 34, 3, 17, 130, 5, 18, 130, 6, 255, 240 });
        }
    }
}
