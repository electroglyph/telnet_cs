namespace telnet_cs.Tests
{
    using System;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Encodings;

    /// <summary>
    /// Audit §5 proper-behavior tests. F-E1–E3/E5 FAIL against current behavior.
    /// </summary>
    public class AuditProperEncodingTests
    {
        [Theory]
        [InlineData("atari-8bit")]
        [InlineData("ATARI-8BIT")]
        [InlineData("Atari_8Bit")]
        public void RequiresBinaryMode_NormalizesName(string name)
        {
            // Source of truth: the encoding name is normalized before the binary check.
            // ~/telnetlib3/telnetlib3/client.py normalizes with
            // encoding.lower().replace("-", "_") before testing FORCE_BINARY_ENCODINGS,
            // and telnet_cs/Encodings/TelnetEncodingProvider.cs does the same
            // (ToLowerInvariant + Replace('-', '_')) before lookup, so
            // GetEncoding("atari-8bit") resolves.
            // Our code: telnet_cs/Encodings/TelnetEncodings.cs RequiresBinaryMode tests
            // the raw name against a set containing "atari_8bit" (case-insensitive but
            // hyphen-sensitive), so hyphen variants resolve yet report false.
            // Proof: each InlineData name must return true; false for "atari-8bit"
            // proves the missing normalization. The underscore variant already passes
            // and guards against over-normalizing. These tests are correct; the fix is
            // to normalize case plus '-' -> '_' before lookup.
            TelnetEncodings.RequiresBinaryMode(name).Should().BeTrue();
        }

        [Fact]
        public void AtasciiByteCount_AccountsForFolding()
        {
            // Source of truth: the byte-count contract is exact. The reference
            // ~/telnetlib3/telnetlib3/encodings/atascii.py _normalize_eol folds
            // "\r\n" -> "\n" (and lone "\r" -> "\n") before charmap encoding, so
            // "a\r\nb" encodes to 3 bytes, and one-shot GetByteCount(string) agrees.
            // Our code: telnet_cs/Encodings/AtasciiEncoding.cs GetByteCount returns
            // count (4) without folding, while GetBytes folds to 3 and silently returns
            // early on a full buffer, risking truncation when callers size from the
            // count.
            // Proof: counted must equal written (3); 4 vs 3 proves the overestimate.
            // This test is correct.
            var encoder = new AtasciiEncoding().GetEncoder();
            char[] chars = "a\r\nb".ToCharArray();
            var bytes = new byte[16];
            int counted = encoder.GetByteCount(chars, 0, chars.Length, flush: false);
            int written = encoder.GetBytes(chars, 0, chars.Length, bytes, 0, flush: false);
            written.Should().Be(3);
            counted.Should().Be(written);
        }

        [Fact]
        public void AtasciiByteCount_AccountsForPendingCr()
        {
            // Source of truth: a pending CR joins the next chunk. atascii.py keeps
            // _pending_cr across IncrementalEncoder.encode calls: a trailing "\r" in a
            // non-final chunk is held, then prepended to the next input and folded, so
            // "a\r" (1 byte emitted, CR held) followed by "b" emits 2 bytes (held LF
            // plus 'b').
            // Our code holds pendingCr in GetBytes but GetByteCount ignores it, so the
            // second chunk counts 1 while GetBytes writes 2, again risking truncation.
            // Proof: after GetBytes("a\r") == 1, GetByteCount("b") must equal
            // GetBytes("b") == 2; 1 vs 2 proves the underestimate. This test is correct.
            var encoder = new AtasciiEncoding().GetEncoder();
            var bytes = new byte[16];
            encoder.GetBytes("a\r".ToCharArray(), 0, 2, bytes, 0, flush: false).Should().Be(1);
            char[] next = "b".ToCharArray();
            int counted = encoder.GetByteCount(next, 0, next.Length, flush: false);
            int written = encoder.GetBytes(next, 0, next.Length, bytes, 0, flush: false);
            written.Should().Be(2);
            counted.Should().Be(written);
        }

        [Fact]
        public void AtasciiEncoder_EmptyNonFinalChunk_EmitsNothing()
        {
            // Source of truth: with a CR pending, an empty non-final chunk stays
            // pending. atascii.py IncrementalEncoder re-adds pending "\r", then strips
            // a trailing "\r" again when final==false, emitting b"" and keeping the
            // pending flag; only "\n" continuation or final flush emits 0x9B.
            // Our code: AtasciiEncoding.cs emits the pending 0x9B on entry even when
            // charCount==0 and flush==false, so a later "\n" double-emits.
            // Proof: GetBytes("a\r") == 1, then GetBytes([], flush:false) must be 0 and
            // GetBytes("\n", flush:true) must be 1 x 0x9B; early 0x9B proves the flush.
            // Rare (zero-length Convert calls) but byte-visible. This test is correct.
            var encoder = new AtasciiEncoding().GetEncoder();
            var bytes = new byte[16];
            encoder.GetBytes("a\r".ToCharArray(), 0, 2, bytes, 0, flush: false).Should().Be(1);
            encoder.GetBytes([], 0, 0, bytes, 0, flush: false).Should().Be(0);
            encoder.GetBytes("\n".ToCharArray(), 0, 1, bytes, 0, flush: true).Should().Be(1);
            bytes[0].Should().Be(0x9B);
        }

        [Fact]
        public void CharmapEncoder_SplitSurrogate_Buffered()
        {
            // Source of truth: the .NET Encoder contract for flush:false buffers an
            // incomplete sequence instead of falling back. One-shot
            // GetBytes("\U0001FB82") correctly yields 0x0D (ATASCII astral cell), so a
            // chunk ending with the lead "\uD83D" must buffer (0 bytes, no throw) and
            // complete to 0x0D when the trail "\uDE02" arrives with flush:true.
            // Our code pairs surrogates only within one chunk; a lone lead falls into
            // scalar fallback and throws via EncoderExceptionFallback, violating the
            // buffering contract (Python str chunks are codepoint-indexed so the case
            // cannot arise there; this half is .NET-only).
            // Proof: writing the lead with flush:false must not throw and must return
            // 0; throwing or returning fallback bytes proves the missing buffer. This
            // test is correct.
            var encoding = new AtasciiEncoding();
            encoding.GetBytes("\U0001FB82").Should().Equal(0x0D);
            var encoder = encoding.GetEncoder();
            var bytes = new byte[16];
            int written = -1;
            Action act = () => { written = encoder.GetBytes("\uD83D".ToCharArray(), 0, 1, bytes, 0, flush: false); };
            act.Should().NotThrow("a split surrogate must buffer, not fall back");
            written.Should().Be(0);
            encoder.GetBytes("\uDE02".ToCharArray(), 0, 1, bytes, 0, flush: true).Should().Be(1);
            bytes[0].Should().Be(0x0D);
        }
    }
}
