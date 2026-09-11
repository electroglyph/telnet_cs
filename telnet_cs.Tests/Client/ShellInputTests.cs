// Tests for the §12 pure-logic ports: InputFilter (escape-sequence +
// single-byte translation with prefix hold-back) and LinemodeBuffer
// (RFC 1184 §3.1 local editing). Every assertion pins exact bytes/echoes.
namespace telnet_cs.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Client;

    public class ShellInputTests
    {
        [Fact]
        public void Feed_AtasciiDel_MapsToBackspace()
        {
            InputFilter.CreateAtascii().Feed([0x7F]).Should().Equal(0x7E);
        }

        [Fact]
        public void Feed_AtasciiCrLf_MapsToEol()
        {
            InputFilter.CreateAtascii().Feed([0x0D, 0x0A]).Should().Equal(0x9B, 0x9B);
        }

        [Fact]
        public void Feed_PetsciiDel_MapsToPetsciiDel()
        {
            InputFilter.CreatePetscii().Feed([0x7F, 0x08]).Should().Equal(0x14, 0x14);
        }

        [Fact]
        public void Feed_LoneEscape_HeldUntilFlush()
        {
            var filter = InputFilter.CreateAtascii();
            filter.Feed([0x1B]).Should().BeEmpty();
            filter.HasPending.Should().BeTrue();
            filter.Flush().Should().Equal(0x1B);
            filter.HasPending.Should().BeFalse();
        }

        [Fact]
        public void Feed_SplitArrowSequence_TranslatesAcrossCalls()
        {
            var filter = InputFilter.CreateAtascii();
            filter.Feed([0x1B]).Should().BeEmpty();
            filter.Feed([0x5B, 0x41]).Should().Equal(0x1C);
            filter.HasPending.Should().BeFalse();
        }

        [Fact]
        public void Feed_Ss3Arrow_TranslatesLikeCsi()
        {
            InputFilter.CreateAtascii().Feed([0x1B, 0x4F, 0x42]).Should().Equal(0x1D);
        }

        [Fact]
        public void Feed_AtasciiDeleteSequence_MapsToBackspace()
        {
            InputFilter.CreateAtascii().Feed([0x1B, 0x5B, 0x33, 0x7E]).Should().Equal(0x7E);
        }

        [Fact]
        public void Feed_PetsciiInsertAndHome_Map()
        {
            var filter = InputFilter.CreatePetscii();
            filter.Feed([0x1B, 0x5B, 0x32, 0x7E]).Should().Equal(0x94);
            filter.Feed([0x1B, 0x5B, 0x48]).Should().Equal(0x13);
        }

        [Fact]
        public void Feed_NonSequenceEscape_PassesThrough()
        {
            InputFilter.CreateAtascii().Feed([0x1B, 0x58]).Should().Equal(0x1B, 0x58);
        }

        [Fact]
        public void Ctor_EmptySequenceKey_Throws()
        {
            Action create = () => new InputFilter([new KeyValuePair<byte[], byte[]>([], [1])]);
            create.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void DefaultEscapeDelay_Is350Ms()
        {
            InputFilter.DefaultEscapeDelay.Should().Be(TimeSpan.FromMilliseconds(350));
            new InputFilter().EscapeDelay.Should().Be(TimeSpan.FromMilliseconds(350));
        }

        [Fact]
        public void Linemode_NormalChars_BufferAndEcho()
        {
            var buffer = new LinemodeBuffer();
            buffer.Feed('a').Should().Be(new LinemodeEdit("a", null));
            buffer.Feed('b').Should().Be(new LinemodeEdit("b", null));
            buffer.Length.Should().Be(2);
        }

        [Fact]
        public void Linemode_Cr_SendsBufferedLine()
        {
            var buffer = new LinemodeBuffer();
            buffer.Feed('a');
            buffer.Feed('b');
            var edit = buffer.Feed('\r');
            edit.Echo.Should().Be("\r");
            edit.Data.Should().Equal(Encoding.UTF8.GetBytes("ab\r"));
            buffer.Length.Should().Be(0);
        }

        [Fact]
        public void Linemode_EraseChar_PopsWithEraseEcho()
        {
            var buffer = new LinemodeBuffer();
            buffer.Feed('a');
            buffer.Feed('b');
            buffer.Feed((char)0x7F).Should().Be(new LinemodeEdit("\b \b", null));
            buffer.Length.Should().Be(1);
        }

        [Fact]
        public void Linemode_EraseCharOnEmpty_Swallowed()
        {
            new LinemodeBuffer().Feed((char)0x7F).Should().Be(new LinemodeEdit(string.Empty, null));
        }

        [Fact]
        public void Linemode_EraseLine_ClearsWithRepeatedEraseEcho()
        {
            var buffer = new LinemodeBuffer();
            buffer.Feed('a');
            buffer.Feed('b');
            var edit = buffer.Feed((char)0x15);
            edit.Echo.Should().Be("\b \b\b \b");
            edit.Data.Should().BeNull();
            buffer.Length.Should().Be(0);
        }

        [Fact]
        public void Linemode_EraseWord_VweraseSemantics()
        {
            var buffer = new LinemodeBuffer();
            foreach (char c in "ab cd ")
            {
                buffer.Feed(c);
            }

            var edit = buffer.Feed((char)0x17);
            edit.Echo.Should().Be("\b \b\b \b\b \b");
            edit.Data.Should().BeNull();
            buffer.Length.Should().Be(3);
        }

        [Fact]
        public void Linemode_TrapsigCtrlC_SendsIacIp()
        {
            var slc = new Dictionary<int, int> { [3] = 3 };
            var buffer = new LinemodeBuffer(slc, trapSignal: true);
            var edit = buffer.Feed((char)0x03);
            edit.Echo.Should().BeEmpty();
            edit.Data.Should().Equal(255, 244);
            buffer.Length.Should().Be(0);
        }

        [Fact]
        public void Linemode_WithoutTrapsig_CtrlCBuffers()
        {
            var slc = new Dictionary<int, int> { [3] = 3 };
            var buffer = new LinemodeBuffer(slc);
            var edit = buffer.Feed((char)0x03);
            edit.Echo.Should().Be("\x03");
            edit.Data.Should().BeNull();
            buffer.Length.Should().Be(1);
        }

        [Fact]
        public void Linemode_ForwardMask_FlushesWithTrigger()
        {
            var buffer = new LinemodeBuffer(forwardMask: [(int)'x']);
            buffer.Feed('a');
            var edit = buffer.Feed('x');
            edit.Echo.Should().Be("x");
            edit.Data.Should().Equal(Encoding.UTF8.GetBytes("ax"));
            buffer.Length.Should().Be(0);
        }

        [Fact]
        public void Linemode_CustomEraseCharValue_Honored()
        {
            var slc = new Dictionary<int, int> { [10] = 8 };
            var buffer = new LinemodeBuffer(slc);
            buffer.Feed('a');
            buffer.Feed((char)0x08).Should().Be(new LinemodeEdit("\b \b", null));
            buffer.Length.Should().Be(0);
        }

        [Fact]
        public void Linemode_UnsupportedFunction_NeverMatches()
        {
            var buffer = new LinemodeBuffer(new Dictionary<int, int>());
            var edit = buffer.Feed((char)0x7F);
            edit.Data.Should().BeNull();
            buffer.Length.Should().Be(1);
        }

        [Fact]
        public void Linemode_Flush_ReturnsPendingAndClears()
        {
            var buffer = new LinemodeBuffer();
            buffer.Feed('a');
            buffer.Feed('b');
            buffer.Flush().Should().Equal((byte)'a', (byte)'b');
            buffer.Length.Should().Be(0);
            buffer.Clear();
        }
    }
}
