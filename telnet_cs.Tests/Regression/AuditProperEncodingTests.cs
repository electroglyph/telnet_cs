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
            // F-E1: hyphen/case variants resolve exactly like the provider and
            // the reference (which normalizes '-' -> '_' before lookup).
            TelnetEncodings.RequiresBinaryMode(name).Should().BeTrue();
        }

        [Fact]
        public void AtasciiByteCount_AccountsForFolding()
        {
            // F-E2: the count contract is exact, not "worst case".
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
            // F-E2 (second half): a pending CR joins the next chunk, so the
            // count must include it — otherwise GetBytes overruns the budget.
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
            // F-E3: with a CR pending, an empty non-final chunk stays pending
            // (reference IncrementalEncoder), it does not flush 0x9B early.
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
            // F-E5: a chunk ending mid-surrogate-pair buffers the lead instead
            // of falling back immediately (one-shot GetBytes stays correct).
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
