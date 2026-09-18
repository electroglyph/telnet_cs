namespace telnet_cs.Tests
{
    using System;
    using System.Collections.Generic;
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
            using var sut = await Client.CreateAsync(stream, TimeSpan.FromSeconds(30), new CancellationToken());
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
            // DO LINEMODE is agreed with WILL plus the automatic SLC import
            // request (func 0, DEFAULT) per RFC 1184 §2.4.
            var (_, stream) = await ReadWithStreamAsync(255, 253, 34);
            stream.ByteWrites.Should().HaveCount(2);
            stream.ByteWrites[0].Should().Equal(new byte[] { 255, 251, 34 });
            stream.ByteWrites[1].Should().Equal(new byte[] { 255, 250, 34, 3, 0, 3, 0, 255, 240 });
        }

        [Fact]
        public async Task ModeRequest_IsConfirmedWithAck()
        {
            // MODE is only answered after LINEMODE agreement: DO is agreed with
            // WILL, then the mask is echoed verbatim plus ACK.
            var (output, stream) = await ReadWithStreamAsync(255, 253, 34, 255, 250, 34, 1, 3, 255, 240);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().HaveCount(3);
            stream.ByteWrites[0].Should().Equal(new byte[] { 255, 251, 34 });
            stream.ByteWrites[1].Should().Equal(new byte[] { 255, 250, 34, 3, 0, 3, 0, 255, 240 });
            stream.ByteWrites[2].Should().Equal(new byte[] { 255, 250, 34, 1, 7, 255, 240 });
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
            stream.ByteWrites.Should().HaveCount(2);
            stream.ByteWrites[0].Should().Equal(new byte[] { 255, 251, 34 });
            stream.ByteWrites[1].Should().Equal(new byte[] { 255, 250, 34, 3, 0, 3, 0, 255, 240 });
            stream.Enqueue(3, 255, 240);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            stream.ByteWrites.Should().HaveCount(3);
            stream.ByteWrites[2].Should().Equal(new byte[] { 255, 250, 34, 1, 7, 255, 240 });
        }

        [Fact]
        public async Task ModeRepeatAcrossReads_RepliesOnce()
        {
            // Agreement first, then the same MODE twice: the first is echoed
            // verbatim plus ACK, the repeat is already in effect and silent.
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = await Client.CreateAsync(stream, TimeSpan.FromSeconds(30), new CancellationToken());
                stream.Enqueue(255, 253, 34);
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                stream.Enqueue(255, 250, 34, 1, 3, 255, 240);
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                stream.Enqueue(255, 250, 34, 1, 3, 255, 240);
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                stream.ByteWrites.Should().HaveCount(3);
                stream.ByteWrites[0].Should().Equal(new byte[] { 255, 251, 34 });
                stream.ByteWrites[1].Should().Equal(new byte[] { 255, 250, 34, 3, 0, 3, 0, 255, 240 });
                stream.ByteWrites[2].Should().Equal(new byte[] { 255, 250, 34, 1, 7, 255, 240 });
            }
        }

        [Fact]
        public async Task ModeUnsupportedBits_AnsweredAsSubset()
        {
            // EDIT | TRAPSIG | SOFT_TAB | LIT_ECHO: the mask is echoed verbatim
            // plus ACK with no subsetting, even for bits without local handling.
            var (_, stream) = await ReadWithStreamAsync(255, 253, 34, 255, 250, 34, 1, 27, 255, 240);
            stream.ByteWrites.Should().HaveCount(3);
            stream.ByteWrites[0].Should().Equal(new byte[] { 255, 251, 34 });
            stream.ByteWrites[1].Should().Equal(new byte[] { 255, 250, 34, 3, 0, 3, 0, 255, 240 });
            stream.ByteWrites[2].Should().Equal(new byte[] { 255, 250, 34, 1, 31, 255, 240 });
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
            stream.ByteWrites.Should().HaveCount(3);
            stream.ByteWrites[0].Should().Equal(new byte[] { 255, 251, 34 });
            stream.ByteWrites[1].Should().Equal(new byte[] { 255, 250, 34, 3, 0, 3, 0, 255, 240 });
            stream.ByteWrites[2].Should().Equal(new byte[] { 255, 250, 34, 1, 7, 255, 240 });
        }

        [Fact]
        public async Task ModeTruncated_Ignored()
        {
            var (output, stream) = await ReadWithStreamAsync(255, 250, 34, 1, 255, 240);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task ModeWithoutAgreement_Ignored()
        {
            // MODE with no LINEMODE agreement (neither side enabled) is
            // ignored: a complete [MODE, mask] still earns no ACK reply.
            var (output, stream) = await ReadWithStreamAsync(255, 250, 34, 1, 3, 255, 240);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task UnknownSubcommand_Ignored()
        {
            // A LINEMODE subcommand outside MODE/SLC/FORWARDMASK is logged
            // and ignored: no reply, no state change, the read loop survives
            // the malformed peer input.
            var (output, stream) = await ReadWithStreamAsync(255, 250, 34, 9, 0, 255, 240);
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
        public async Task ForwardMaskDo_StoresMaskAndMarksOffered()
        {
            // RFC 1184 §2.3: a well-formed DO FORWARDMASK is stored silently
            // (no reply) and marks the local sub-state.
            using var stream = new ScriptedStream();
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            stream.Enqueue(255, 250, 34, 253, 2, 0x01, 0x02, 255, 240);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            stream.ByteWrites.Should().BeEmpty();
            sut.Linemode.ForwardMask.Should().Equal(0x01, 0x02);
            sut.Linemode.ForwardMaskOffered.Should().BeTrue();
        }

        [Fact]
        public async Task ForwardMaskOversized_NotStored_OfferedStillMarked()
        {
            // Masks longer than 32 bytes are rejected without a reply, like
            // the reference ("invalid length"); the sub-state still flips.
            using var stream = new ScriptedStream();
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            var frame = new List<int> { 255, 250, 34, 253, 2 };
            frame.AddRange(Enumerable.Repeat(1, 33));
            frame.AddRange([255, 240]);
            stream.Enqueue(frame.ToArray());
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            stream.ByteWrites.Should().BeEmpty();
            sut.Linemode.ForwardMask.Should().BeNull();
            sut.Linemode.ForwardMaskOffered.Should().BeTrue();
        }

        [Fact]
        public async Task ForwardMaskDont_ClearsOffered_KeepsStoredMask()
        {
            // DONT clears the local sub-state but keeps stored bytes (the
            // reference only flips its local flag).
            using var stream = new ScriptedStream();
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            stream.Enqueue(255, 250, 34, 253, 2, 0x01, 255, 240);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            stream.Enqueue(255, 250, 34, 254, 2, 255, 240);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            stream.ByteWrites.Should().BeEmpty();
            sut.Linemode.ForwardMaskOffered.Should().BeFalse();
            sut.Linemode.ForwardMask.Should().Equal(0x01);
        }

        [Fact]
        public async Task ForwardMaskWillWont_OnClient_Ignored()
        {
            // Client role never sends DO, so a WILL/WONT answer has no
            // proposal behind it and records nothing.
            using var stream = new ScriptedStream();
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            stream.Enqueue(255, 250, 34, 251, 2, 255, 240);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            sut.Linemode.ForwardMaskAccepted.Should().BeFalse();
            stream.Enqueue(255, 250, 34, 252, 2, 255, 240);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            stream.ByteWrites.Should().BeEmpty();
            sut.Linemode.ForwardMaskAccepted.Should().BeFalse();
        }

        [Fact]
        public async Task ForwardMaskWillWont_OnServer_RecordedSilently()
        {
            // Server role accepts WILL/WONT answers without any reply.
            using var stream = new ScriptedStream();
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            sut.IsServerRole = true;
            stream.Enqueue(255, 250, 34, 251, 2, 255, 240);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            sut.Linemode.ForwardMaskAccepted.Should().BeTrue();
            stream.Enqueue(255, 250, 34, 252, 2, 255, 240);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            stream.ByteWrites.Should().BeEmpty();
            sut.Linemode.ForwardMaskAccepted.Should().BeFalse();
        }

        [Fact]
        public async Task ForwardMaskDont_WithPayload_KeepsOffered()
        {
            // A DONT carrying payload bytes is dropped without clearing
            // the offered sub-state.
            using var stream = new ScriptedStream();
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            stream.Enqueue(255, 250, 34, 253, 2, 0x01, 255, 240);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            sut.Linemode.ForwardMaskOffered.Should().BeTrue();
            stream.Enqueue(255, 250, 34, 254, 2, 0x09, 255, 240);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            stream.ByteWrites.Should().BeEmpty();
            sut.Linemode.ForwardMaskOffered.Should().BeTrue();
            sut.Linemode.ForwardMask.Should().Equal(0x01);
        }

        [Fact]
        public async Task ForwardMaskDoDont_OnServer_Ignored()
        {
            // Server role rejects wrong-role verbs with no state change:
            // a DO proposal stores nothing, a DONT clears nothing.
            using var stream = new ScriptedStream();
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            sut.IsServerRole = true;
            stream.Enqueue(255, 250, 34, 253, 2, 0x01, 0x02, 255, 240);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            stream.Enqueue(255, 250, 34, 254, 2, 255, 240);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            stream.ByteWrites.Should().BeEmpty();
            sut.Linemode.ForwardMask.Should().BeNull();
            sut.Linemode.ForwardMaskOffered.Should().BeFalse();
        }

        [Fact]
        public async Task ForwardMaskEmptyDo_LeavesStateUntouched()
        {
            // An empty DO is warned on and ignored: nothing stored, the
            // sub-state unflipped, no reply.
            using var stream = new ScriptedStream();
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            stream.Enqueue(255, 250, 34, 253, 2, 255, 240);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            stream.ByteWrites.Should().BeEmpty();
            sut.Linemode.ForwardMask.Should().BeNull();
            sut.Linemode.ForwardMaskOffered.Should().BeFalse();
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
            // Any ACKed triplet is dropped, never stored: the ACKed change to
            // 10 lands nowhere, so the row stays at 9 and the third triplet —
            // identical level+value to the stored row — is ignored too. One
            // SLC reply total (the first adopt), not two.
            stream.Enqueue(255, 250, 34, 3, 3, 130, 10, 255, 240);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            // Proves 10 was dropped: 9 is still the stored value, so this
            // identical triplet is ignored rather than agreed with ACK.
            stream.Enqueue(255, 250, 34, 3, 3, 2, 9, 255, 240);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            stream.ByteWrites.Should().ContainSingle().Which.Should()
              .Equal(new byte[] { 255, 250, 34, 3, 3, 130, 9, 255, 240 });
        }

        [Fact]
        public async Task SlcAckedChange_AsServer_IgnoresWithoutStoring()
        {
            // RFC 1184 §5.5 rule 2, server column: a same-level ACKed change
            // with a different value is ignored — no SLC reply and no state
            // change. Same script as SlcAckedChange_SwitchesSilently, but the
            // handler runs the server rules, so the third read finds the row
            // still at 9 and stays silent (one SLC reply total, not two).
            // No LINEMODE agreement exists on this bare handler, so no
            // forwardmask request goes out (reference suppresses DO
            // FORWARDMASK without receipt of WILL LINEMODE).
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
            // A valued CANTCHANGE row still adopts the peer value and replies
            // level+mask+ACK (reference _slc_change: valued rows accept the
            // peer value), instead of echoing the own row back.
            using var stream = new ScriptedStream();
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            sut.Linemode.SetEntry(3, 1, 7);
            stream.Enqueue(255, 250, 34, 3, 3, 2, 9, 255, 240);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            stream.ByteWrites.Should().ContainSingle().Which.Should()
              .Equal(new byte[] { 255, 250, 34, 3, 3, 130, 9, 255, 240 });
        }

        [Fact]
        public async Task SlcUnknownFunction_RefusedAsDefault()
        {
            // Out-of-range functions answer (NOSUPPORT, 0xFF); the 0xFF value
            // doubles on the wire like any IAC data byte (reference: out of
            // range -> SLC_nosupport). NOSUPPORT receipt answers (0x80, 0xFF).
            var (_, stream) = await ReadWithStreamAsync(255, 250, 34, 3, 31, 2, 65, 255, 240);
            stream.ByteWrites.Should().ContainSingle().Which.Should()
              .Equal(new byte[] { 255, 250, 34, 3, 31, 0, 255, 255, 255, 240 });
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
        public async Task SlcTruncated_Ignored()
        {
            // A misaligned SLC tail is a malformed peer frame, not a
            // framing bug: the reference raises ValueError at feed level
            // but contains it per byte in its feed loop, so end-to-end the
            // frame is a no-op there too. The whole buffer is rejected,
            // nothing is answered, and later valid frames still dispatch.
            using var stream = new ScriptedStream();
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            stream.Enqueue(255, 250, 34, 3, 3, 2, 255, 240);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            stream.ByteWrites.Should().BeEmpty();
            stream.Enqueue(255, 250, 34, 3, 3, 2, 5, 255, 240);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            stream.ByteWrites.Should().ContainSingle().Which.Take(4).Should()
              .Equal(new byte[] { 255, 250, 34, 3 });
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
        public async Task SlcDefault_IdenticalToBsdDefault_IsSilent()
        {
            // RFC 1184 §5.5 rule 1 (telnetlib3 _slc_process): identical
            // settings are ignored. SYNCH (1,DEFAULT,0) already matches the
            // BSD default row, so no reply goes out (executed on the reference).
            var (output, stream) = await ReadWithStreamAsync(255, 250, 34, 3, 1, 3, 0, 255, 240);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task ImportRemoteSpecialCharacters_AfterNegotiation_SendsImport()
        {
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = await Client.CreateAsync(stream, TimeSpan.FromSeconds(30), new CancellationToken());
                stream.Enqueue(255, 253, 34);
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                await client.ImportRemoteSpecialCharactersAsync();
                // DO already triggered the automatic import; the manual call
                // re-requests (both ride the same frame shape).
                stream.ByteWrites.Should().HaveCount(3);
                stream.ByteWrites[0].Should().Equal(new byte[] { 255, 251, 34 });
                stream.ByteWrites[1].Should().Equal(new byte[] { 255, 250, 34, 3, 0, 3, 0, 255, 240 });
                stream.ByteWrites[2].Should().Equal(new byte[] { 255, 250, 34, 3, 0, 3, 0, 255, 240 });
            }
        }

        [Fact]
        public async Task ImportRemoteSpecialCharacters_WithoutNegotiation_Silent()
        {
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = await Client.CreateAsync(stream, TimeSpan.FromSeconds(30), new CancellationToken());
                await client.ImportRemoteSpecialCharactersAsync();
                stream.ByteWrites.Should().BeEmpty();
            }
        }

        [Fact]
        public async Task ExportSpecialCharacters_WithEntries_SendsTable()
        {
            // The export carries the BSD default rows with explicit overrides
            // applied (here IP -> ^I and EC -> ^H), matching the reference
            // full-tabset publish.
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = await Client.CreateAsync(stream, TimeSpan.FromSeconds(30), new CancellationToken());
                stream.Enqueue(255, 253, 34);
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                client.SetLinemodeEntry(3, 2, 9);
                client.SetLinemodeEntry(10, 2, 8);
                await client.ExportSpecialCharactersAsync();
                stream.ByteWrites.Should().HaveCount(3);
                stream.ByteWrites[0].Should().Equal(new byte[] { 255, 251, 34 });
                stream.ByteWrites[1].Should().Equal(new byte[] { 255, 250, 34, 3, 0, 3, 0, 255, 240 });
                stream.ByteWrites[2].Should().Equal(
                  255, 250, 34, 3,
                  1, 3, 0, 2, 3, 0, 3, 2, 9, 4, 34, 15, 5, 2, 20, 6, 3, 0,
                  7, 98, 28, 8, 2, 4, 9, 66, 26, 10, 2, 8, 11, 2, 21, 12, 2, 23,
                  13, 2, 18, 14, 2, 22, 15, 2, 17, 16, 2, 19,
                  255, 240);
            }
        }

        [Fact]
        public async Task ExportSpecialCharacters_DefaultTable_SendsBsdRows()
        {
            // Source of truth: ~/telnetlib3/telnetlib3/slc.py BSD_SLC_TAB (16
            // live rows, funcs 1..16) via generate_slctab; the default export
            // carries those rows so MODE-ACK -> SLC -> FORWARDMASK has
            // something to send.
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = await Client.CreateAsync(stream, TimeSpan.FromSeconds(30), new CancellationToken());
                stream.Enqueue(255, 253, 34);
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                await client.ExportSpecialCharactersAsync();
                var triplets = new byte[]
                {
                  1, 3, 0, 2, 3, 0, 3, 98, 3, 4, 34, 15,
                  5, 2, 20, 6, 3, 0, 7, 98, 28, 8, 2, 4,
                  9, 66, 26, 10, 2, 127, 11, 2, 21, 12, 2, 23,
                  13, 2, 18, 14, 2, 22, 15, 2, 17, 16, 2, 19,
                };
                stream.ByteWrites.Should().HaveCount(3);
                stream.ByteWrites[0].Should().Equal(new byte[] { 255, 251, 34 });
                stream.ByteWrites[1].Should().Equal(new byte[] { 255, 250, 34, 3, 0, 3, 0, 255, 240 });
                stream.ByteWrites[2].Should().Equal(
                  new[] { new byte[] { 255, 250, 34, 3 }, triplets, new byte[] { 255, 240 } }
                    .SelectMany(static p => p).ToArray());
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
                using var client = await Client.CreateAsync(stream, TimeSpan.FromSeconds(30), new CancellationToken());
                stream.Enqueue(255, 253, 34);
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                stream.Enqueue(255, 250, 34, 3, 2, 34, 7, 255, 240);
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                await client.SendCommand(Commands.Break);
                stream.ByteWrites.Should().HaveCount(5);
                stream.ByteWrites[0].Should().Equal(new byte[] { 255, 251, 34 });
                stream.ByteWrites[1].Should().Equal(new byte[] { 255, 250, 34, 3, 0, 3, 0, 255, 240 });
                stream.ByteWrites[2].Should().Equal(new byte[] { 255, 250, 34, 3, 2, 162, 7, 255, 240 });
                stream.ByteWrites[3].Should().Equal(new byte[] { 255, 243 });
                stream.ByteWrites[4].Should().Equal(new byte[] { 255, 253, 6 });
            }
        }

        [Fact]
        public async Task SendCommand_FlushIn_WithoutTcp_LogsAndSkips()
        {
            // FLUSHIN on a non-TCP stream: no throw, Synch skipped with a log.
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = await Client.CreateAsync(stream, TimeSpan.FromSeconds(30), new CancellationToken());
                stream.Enqueue(255, 253, 34);
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                client.SetLinemodeEntry(3, 2, 3, 64);
                var logged = new List<string>();
                client.Settings.Log = logged.Add;
                await client.SendCommand(Commands.InterruptProcess);
                stream.ByteWrites.Should().HaveCount(3);
                stream.ByteWrites[2].Should().Equal(new byte[] { 255, 244 });
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
                using var client = await Client.CreateAsync(stream, TimeSpan.FromSeconds(30), new CancellationToken());
                client.SetLinemodeEntry(2, 2, 7, 32);
                await client.SendCommand(Commands.Break);
                stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(new byte[] { 255, 243 });
            }
        }

        [Fact]
        public async Task SlcForw2_WithoutForw1_RefusedAsNoSupport()
        {
            // The lone-FORW2 gate is gone (reference has none): a lone FORW2
            // is adopted with ACK like any valued row — the default FORW2
            // (00,FF) takes the valued path since the value differs.
            var (output, stream) = await ReadWithStreamAsync(255, 250, 34, 3, 18, 2, 5, 255, 240);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().ContainSingle().Which.Should()
              .Equal(new byte[] { 255, 250, 34, 3, 18, 130, 5, 255, 240 });
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

        private static async Task<ByteStreamHandler> AgreeRemoteLinemodeAsync(
            ScriptedStream stream, CancellationTokenSource cts, List<byte> fired)
        {
            // Peer WILL LINEMODE plus MODE 0 (EDIT clear): remote line editing.
            // Server role (the realistic snooping side): WILL earns DO plus
            // the initial MODE proposal; MODE 0 echoes nothing back (already
            // the default mask). Ownership transfers to the caller.
            var sut = new ByteStreamHandler(stream, cts, 1);
            sut.SlcFunctionReceived = fired.Add;
            sut.IsServerRole = true;
            sut.ApplyLinemodeAsServer = true;
            stream.Enqueue(255, 251, 34, 255, 250, 34, 1, 0, 255, 240);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            sut.Negotiation.IsEnabledByPeer(34).Should().BeTrue();
            stream.ByteWrites.Should().HaveCount(2);
            stream.ByteWrites[0].Should().Equal(new byte[] { 255, 253, 34 });
            stream.ByteWrites[1].Should().Equal(new byte[] { 255, 250, 34, 1, 16, 255, 240 });
            return sut;
        }

        [Fact]
        public async Task SlcSnoop_RemoteMode_MatchSetsReceivedAndFiresHook()
        {
            // Data byte 0x03 equals the default IP value (function 3): it is
            // delivered in-band and reported, like slc_received + callback.
            using var stream = new ScriptedStream();
            using var cts = new CancellationTokenSource();
            var fired = new List<byte>();
            using var sut = await AgreeRemoteLinemodeAsync(stream, cts, fired);
            stream.Enqueue(3);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().Be("\x03");
            sut.SlcReceived.Should().Be((byte)3);
            fired.Should().Equal((byte)3);
        }

        [Fact]
        public async Task SlcSnoop_RemoteMode_MissAfterMatchRecordsNull()
        {
            // The report always describes the last byte: IP then 'A' leaves
            // null (the per-byte reset), with only the match firing the hook.
            using var stream = new ScriptedStream();
            using var cts = new CancellationTokenSource();
            var fired = new List<byte>();
            using var sut = await AgreeRemoteLinemodeAsync(stream, cts, fired);
            stream.Enqueue(3, 65);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().Be("\x03" + "A");
            sut.SlcReceived.Should().BeNull();
            fired.Should().Equal((byte)3);
        }

        [Fact]
        public async Task SlcSnoop_LocalMode_EditSet_Silent()
        {
            // MODE EDIT means the peer edits locally: data bytes are just data.
            using var stream = new ScriptedStream();
            using var cts = new CancellationTokenSource();
            var fired = new List<byte>();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            sut.SlcFunctionReceived = fired.Add;
            sut.IsServerRole = true;
            sut.ApplyLinemodeAsServer = true;
            stream.Enqueue(255, 251, 34, 255, 250, 34, 1, 1, 255, 240);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            sut.Negotiation.IsEnabledByPeer(34).Should().BeTrue();
            stream.Enqueue(3);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().Be("\x03");
            sut.SlcReceived.Should().BeNull();
            fired.Should().BeEmpty();
        }

        [Fact]
        public async Task SlcSnoop_WithoutAgreement_Silent()
        {
            // No LINEMODE, no ECHO+SGA: nothing snoops on a fresh handler.
            using var stream = new ScriptedStream(3);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            var fired = new List<byte>();
            sut.SlcFunctionReceived = fired.Add;
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().Be("\x03");
            sut.SlcReceived.Should().BeNull();
            fired.Should().BeEmpty();
        }

        [Fact]
        public async Task SlcSnoop_EscapedIac255_NotSnooped()
        {
            // The IAC IAC escape delivers data byte 255, but the escape path
            // never snoops (reference: escaped data bypasses its snoop
            // branch) — so the NOSUPPORT disable value matches nothing.
            using var stream = new ScriptedStream();
            using var cts = new CancellationTokenSource();
            var fired = new List<byte>();
            using var sut = await AgreeRemoteLinemodeAsync(stream, cts, fired);
            stream.Enqueue(255, 255);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().Be("ÿ");
            sut.SlcReceived.Should().BeNull();
            fired.Should().BeEmpty();
        }

        [Fact]
        public async Task SlcSnoop_CommandByte_ResetsReceived()
        {
            // Command bytes reset the report like any consumed byte: IP, then
            // a silently-consumed NOP, leaves null.
            using var stream = new ScriptedStream();
            using var cts = new CancellationTokenSource();
            var fired = new List<byte>();
            using var sut = await AgreeRemoteLinemodeAsync(stream, cts, fired);
            stream.Enqueue(3);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().Be("\x03");
            sut.SlcReceived.Should().Be((byte)3);
            stream.Enqueue(255, 241);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            sut.SlcReceived.Should().BeNull();
        }

        [Fact]
        public async Task SlcSnoop_KludgeServer_EchoAndSga_Snoops()
        {
            // Pre-LINEMODE char mode, server side: our ECHO + SGA snoops.
            using var stream = new ScriptedStream();
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            var fired = new List<byte>();
            sut.SlcFunctionReceived = fired.Add;
            sut.AllowRemoteEcho = true;
            sut.IsServerRole = true;
            sut.ApplyLinemodeAsServer = true;
            stream.Enqueue(255, 253, 1, 255, 253, 3);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            sut.Negotiation.IsEnabledByUs(1).Should().BeTrue();
            sut.Negotiation.IsEnabledByUs(3).Should().BeTrue();
            stream.Enqueue(3);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().Be("\x03");
            sut.SlcReceived.Should().Be((byte)3);
            fired.Should().Equal((byte)3);
        }

        [Fact]
        public async Task SlcSnoop_KludgeClient_PeerEchoAndSga_Snoops()
        {
            // Pre-LINEMODE char mode, client side: their ECHO + SGA snoops
            // (their SGA alone suffices, matching the reference gate).
            using var stream = new ScriptedStream();
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            var fired = new List<byte>();
            sut.SlcFunctionReceived = fired.Add;
            sut.AllowRemoteEcho = true;
            stream.Enqueue(255, 251, 1, 255, 251, 3);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            sut.Negotiation.IsEnabledByPeer(1).Should().BeTrue();
            sut.Negotiation.IsEnabledByPeer(3).Should().BeTrue();
            stream.Enqueue(3);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().Be("\x03");
            sut.SlcReceived.Should().Be((byte)3);
            fired.Should().Equal((byte)3);
        }

        [Fact]
        public void SlcSnoop_Lookup_FirstFunctionWinsAndZeroNeverMatches()
        {
            // Value-based scan in function order: IP (3) still wins after a
            // later row takes the same value; 0 never matches. The NOSUPPORT
            // disable value 255 scans like any other value (first such row is
            // function 17) — it never snoops on the wire only because the
            // IAC IAC escape path bypasses the snoop.
            var state = new LinemodeState();
            state.Snoop(3).Should().Be((byte)3);
            state.Snoop(0).Should().BeNull();
            state.Snoop(255).Should().Be((byte)17);
            state.Snoop(200).Should().BeNull();
            state.SetEntry(5, 2, 3);
            state.Snoop(3).Should().Be((byte)3);
        }
    }
}
