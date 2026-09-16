namespace telnet_cs.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.IO;
    using telnet_cs.Protocol;

    public class EnvironmentRequestedOnlyTests
    {
        private static byte[] L(string text) => Encoding.Latin1.GetBytes(text);

        private static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(static p => p)];

        private static IReadOnlyDictionary<string, string> RoleVars() =>
          new Dictionary<string, string>(StringComparer.Ordinal) { ["ROLE"] = "admin" };

        private static byte[] Build(byte[] requested) =>
          EnvironmentProtocol.BuildResponse(0, requested, "bob", "disp:0", RoleVars(), "xterm", "C", "80", "24", "truecolor");

        private static byte[] UserEntry() => Concat([0], L("USER"), [1], L("bob"));

        private static byte[] DisplayEntry() => Concat([0], L("DISPLAY"), [1], L("disp:0"));

        private static byte[] TermEntry() => Concat([0], L("TERM"), [1], L("xterm"));

        private static byte[] LangEntry() => Concat([0], L("LANG"), [1], L("C"));

        private static byte[] ColumnsEntry() => Concat([0], L("COLUMNS"), [1], L("80"));

        private static byte[] LinesEntry() => Concat([0], L("LINES"), [1], L("24"));

        private static byte[] ColorTermEntry() => Concat([0], L("COLORTERM"), [1], L("truecolor"));

        private static byte[] RoleEntry() => Concat([3], L("ROLE"), [1], L("admin"));

        private static byte[] BareFoo() => Concat([0], L("FOO"));

        private static byte[] BareMissing() => Concat([3], L("MISSING"));

        private static byte[][] WellKnownEntries() =>
          [UserEntry(), DisplayEntry(), TermEntry(), LangEntry(), ColumnsEntry(), LinesEntry(), ColorTermEntry()];

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

        private static void ConfigureSb(ByteStreamHandler sut)
        {
            sut.EnvironmentUser = "bob";
            sut.EnvironmentDisplay = "host:0";
            sut.EnvironmentUserVars = new Dictionary<string, string>(StringComparer.Ordinal) { ["ROLE"] = "admin" };
            sut.TerminalType = "xterm";
            sut.WindowWidth = 80;
            sut.WindowHeight = 24;
        }

        private static byte[] ExpectedIsFrame(byte verb, byte option, params byte[][] entries)
        {
            var payload = new List<byte> { verb };
            foreach (var entry in entries)
            {
                payload.AddRange(entry);
            }

            var frame = new List<byte> { 255, 250, option };
            foreach (var b in payload)
            {
                frame.Add(b);
                if (b == 255)
                {
                    frame.Add(b);
                }
            }

            frame.AddRange([255, 240]);
            return [.. frame];
        }

        private static byte[] SbUserEntry() => Concat([0], L("USER"), [1], L("bob"));

        private static byte[] SbTermEntry() => Concat([0], L("TERM"), [1], L("xterm"));

        private static byte[] SbLangEntry() => Concat([0], L("LANG"), [1], L("C"));

        private static byte[] SbColumnsEntry() => Concat([0], L("COLUMNS"), [1], L("80"));

        private static byte[] SbLinesEntry() => Concat([0], L("LINES"), [1], L("24"));

        private static byte[] SbColorTermEntry() =>
          Concat([0], L("COLORTERM"), [1], L(System.Environment.GetEnvironmentVariable("COLORTERM") ?? string.Empty));

        private static byte[] SbRoleEntry() => Concat([3], L("ROLE"), [1], L("admin"));

        private static byte[] SbBareDisplay() => Concat([0], L("DISPLAY"));

        [Fact]
        public void BuildResponse_NamedVarDefined_ReturnsSingleValuedEntry()
        {
            var requested = Concat([0], L("USER"));
            Build(requested).Should().Equal(Concat([0], UserEntry()));
        }

        [Fact]
        public void BuildResponse_NamedVarUndefined_ReturnsBareVarWithoutValue()
        {
            var requested = Concat([0], L("FOO"));
            var payload = Build(requested);
            payload.Should().Equal(Concat([0], BareFoo()));
        }

        [Fact]
        public void BuildResponse_NamedUserVarDefined_ReturnsValuedUserVar()
        {
            var requested = Concat([3], L("ROLE"));
            Build(requested).Should().Equal(Concat([0], RoleEntry()));
        }

        [Fact]
        public void BuildResponse_NamedUserVarUndefined_ReturnsBareUserVarWithoutValue()
        {
            var requested = Concat([3], L("MISSING"));
            var payload = Build(requested);
            payload.Should().Equal(Concat([0], BareMissing()));
        }

        [Fact]
        public void BuildResponse_MultipleNamed_PreservesOrderWithBareAndValuedForms()
        {
            var requested = Concat([0], L("USER"), [0], L("FOO"), [3], L("ROLE"), [3], L("MISSING"));
            Build(requested).Should().Equal(Concat([0], UserEntry(), BareFoo(), RoleEntry(), BareMissing()));
        }

        [Fact]
        public void BuildResponse_BareVar_VolunteersWellKnownOnly()
        {
            var payload = Build([0]);
            payload.Should().Equal(Concat([0, .. WellKnownEntries().SelectMany(static e => e).ToArray()]));
            Encoding.Latin1.GetString(payload).Should().NotContain("ROLE");
        }

        [Fact]
        public void BuildResponse_BareUserVar_VolunteersUserVarsOnly()
        {
            var payload = Build([3]);
            payload.Should().Equal(Concat([0], RoleEntry()));
            Encoding.Latin1.GetString(payload).Should().NotContain("USER");
        }

        [Fact]
        public void BuildResponse_EmptyRequest_VolunteersWellKnownPlusUserVars()
        {
            var payload = Build([]);
            payload.Should().Equal(Concat([0, .. WellKnownEntries().SelectMany(static e => e).ToArray(), .. RoleEntry()]));
        }

        [Fact]
        public void BuildResponse_BareVarPlusBareUserVar_VolunteersAll()
        {
            var payload = Build([0, 3]);
            var empty = Build([]);
            payload.Should().Equal(empty);
            payload.Should().Equal(Concat([0, .. WellKnownEntries().SelectMany(static e => e).ToArray(), .. RoleEntry()]));
        }

        [Fact]
        public void BuildResponse_NamedPlusBareVar_DedupesUserToSingleEntry()
        {
            var requested = Concat([0], L("USER"), [0]);
            var payload = Build(requested);
            payload.Should().Equal(Concat([0, .. WellKnownEntries().SelectMany(static e => e).ToArray()]));
            var text = Encoding.Latin1.GetString(payload);
            int count = 0;
            int at = 0;
            while ((at = text.IndexOf("USER", at, StringComparison.Ordinal)) >= 0)
            {
                count++;
                at += 4;
            }

            count.Should().Be(1);
        }

        [Fact]
        public void BuildResponse_DuplicateNamedVar_DedupesToSingleEntry()
        {
            var requested = Concat([0], L("USER"), [0], L("USER"));
            Build(requested).Should().Equal(Concat([0], UserEntry()));
        }

        [Fact]
        public void BuildResponse_DuplicateNamedUserVar_DedupesToSingleEntry()
        {
            var requested = Concat([3], L("ROLE"), [3], L("ROLE"));
            Build(requested).Should().Equal(Concat([0], RoleEntry()));
        }

        [Fact]
        public void BuildResponse_CrossTypeSameName_KeepsVarAndUserVarDistinct()
        {
            var requested = Concat([0], L("ROLE"), [3], L("ROLE"));
            var bareRoleVar = Concat([0], L("ROLE"));
            Build(requested).Should().Equal(Concat([0], bareRoleVar, RoleEntry()));
        }

        [Fact]
        public void BuildResponse_TrailingEsc_DroppedFromName()
        {
            var requested = new byte[] { 0, (byte)'A', 2 };
            var expected = Concat([0], Concat([0], L("A")));
            Build(requested).Should().Equal(expected);
        }

        [Fact]
        public void BuildResponse_EscEsc_YieldsLiteralEscInName()
        {
            var requested = new byte[] { 0, (byte)'A', 2, 2, (byte)'B' };
            var expectedEntry = new byte[] { 0, (byte)'A', 2, 2, (byte)'B' };
            Build(requested).Should().Equal(Concat([0], expectedEntry));
        }

        [Fact]
        public void BuildResponse_EscValue_YieldsLiteralValueByteInName()
        {
            var requested = new byte[] { 0, (byte)'A', 2, 1, (byte)'B' };
            var expectedEntry = new byte[] { 0, (byte)'A', 2, 1, (byte)'B' };
            Build(requested).Should().Equal(Concat([0], expectedEntry));
        }

        [Fact]
        public void BuildResponse_ValueByte_SkipsValueBytesUntilNextType()
        {
            var requested = Concat([0], L("USER"), [1], L("xyz"), [0], L("FOO"));
            Build(requested).Should().Equal(Concat([0], UserEntry(), BareFoo()));
        }

        [Fact]
        public void BuildResponse_StrayByteBeforeType_IsSkipped()
        {
            var requested = Concat([99], [0], L("USER"));
            Build(requested).Should().Equal(Concat([0], UserEntry()));
        }

        [Fact]
        public async Task SbPath_NamedDisplay_ReturnsBareVarWithoutValue()
        {
            var send = new List<int> { 255, 250, 36, 1, 0 };
            send.AddRange(Encoding.Latin1.GetBytes("DISPLAY").Select(static b => (int)b));
            send.AddRange([255, 240]);
            var (output, stream) = await ReadHandlerOnceAsync(ConfigureSb, [.. send]);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().ContainSingle().Which.Should()
              .Equal(ExpectedIsFrame(0, 36, SbBareDisplay()));
        }

        [Fact]
        public async Task SbPath_BareVar_DoesNotVolunteerDisplayOrUserVars()
        {
            var (output, stream) = await ReadHandlerOnceAsync(ConfigureSb, 255, 250, 36, 1, 0, 255, 240);
            output.Should().BeEmpty();
            var frame = stream.ByteWrites.Should().ContainSingle().Subject;
            frame.Should().Equal(ExpectedIsFrame(0, 36,
              SbUserEntry(), SbTermEntry(), SbLangEntry(), SbColumnsEntry(), SbLinesEntry(), SbColorTermEntry()));
            Encoding.Latin1.GetString(frame).Should().NotContain("DISPLAY");
            Encoding.Latin1.GetString(frame).Should().NotContain("ROLE");
        }

        [Fact]
        public async Task SbPath_NewEnvironNamedUser_AnswersRequestedOnlyOnOption39()
        {
            var send = new List<int> { 255, 250, 39, 1, 0 };
            send.AddRange(Encoding.Latin1.GetBytes("USER").Select(static b => (int)b));
            send.AddRange([255, 240]);
            var (output, stream) = await ReadHandlerOnceAsync(ConfigureSb, [.. send]);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().ContainSingle().Which.Should()
              .Equal(ExpectedIsFrame(0, 39, SbUserEntry()));
        }
    }
}
