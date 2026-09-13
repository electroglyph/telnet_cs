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
    using telnet_cs.Client;
    using telnet_cs.IO;
    using telnet_cs.Protocol;

    public class EnvironmentTests
    {
        private static byte[] L(string text) => Encoding.Latin1.GetBytes(text);

        private static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(static p => p)];

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

        private static void ConfigureFull(ByteStreamHandler sut)
        {
            sut.EnvironmentUser = "bob";
            sut.EnvironmentDisplay = "host:0";
            sut.EnvironmentUserVars = new Dictionary<string, string>(StringComparer.Ordinal) { ["ROLE"] = "admin" };
            // Deterministic session parameters (not statics or the console probe).
            sut.TerminalType = "xterm";
            sut.WindowWidth = 80;
            sut.WindowHeight = 24;
        }

        private static byte[] ExpectedIsFrame(byte verb, params byte[][] entries) =>
          ExpectedIsFrame(verb, 36, entries);

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

        private static byte[] UserEntry() => Concat([0], L("USER"), [1], L("bob"));
        private static byte[] DisplayEntry() => Concat([0], L("DISPLAY"), [1], L("host:0"));
        private static byte[] RoleEntry() => Concat([3], L("ROLE"), [1], L("admin"));
        private static byte[] TermEntry() => Concat([0], L("TERM"), [1], L("xterm"));
        private static byte[] LangCEntry() => Concat([0], L("LANG"), [1], L("C"));
        private static byte[] ColumnsEntry() => Concat([0], L("COLUMNS"), [1], L("80"));
        private static byte[] LinesEntry() => Concat([0], L("LINES"), [1], L("24"));
        private static byte[] ColorTermEntry() => Concat([0], L("COLORTERM"), [1], L(System.Environment.GetEnvironmentVariable("COLORTERM") ?? string.Empty));
        private static byte[][] SystemEntries() => [TermEntry(), LangCEntry(), ColumnsEntry(), LinesEntry(), ColorTermEntry()];

        [Fact]
        public async Task EnvironSend_AllRequested_ReturnsExactIsFrame()
        {
            var (output, stream) = await ReadHandlerOnceAsync(ConfigureFull, 255, 250, 36, 1, 0, 3, 255, 240);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().ContainSingle().Which.Should()
              .Equal(ExpectedIsFrame(0, Concat([UserEntry(), DisplayEntry(), .. SystemEntries(), RoleEntry()])));
        }

        [Fact]
        public async Task EnvironSend_VarRequest_ExcludesUserVars()
        {
            var (output, stream) = await ReadHandlerOnceAsync(ConfigureFull, 255, 250, 36, 1, 0, 255, 240);
            output.Should().BeEmpty();
            var frame = stream.ByteWrites.Should().ContainSingle().Subject;
            frame.Should().Equal(ExpectedIsFrame(0, Concat([UserEntry(), DisplayEntry(), .. SystemEntries()])));
        }

        [Fact]
        public async Task EnvironSend_UserVarOnly_OmitsWellKnown()
        {
            var (output, stream) = await ReadHandlerOnceAsync(ConfigureFull, 255, 250, 36, 1, 3, 255, 240);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(ExpectedIsFrame(0, RoleEntry()));
        }

        [Fact]
        public async Task EnvironSend_OrderMirrored_UserVarFirst()
        {
            var (output, stream) = await ReadHandlerOnceAsync(ConfigureFull, 255, 250, 36, 1, 3, 0, 255, 240);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().ContainSingle().Which.Should()
              .Equal(ExpectedIsFrame(0, Concat([RoleEntry(), UserEntry(), DisplayEntry(), .. SystemEntries()])));
        }

        [Fact]
        public async Task EnvironSend_DuplicateType_AnsweredOnce()
        {
            // A repeated type byte must not duplicate the block in the reply.
            var (output, stream) = await ReadHandlerOnceAsync(ConfigureFull, 255, 250, 36, 1, 0, 0, 255, 240);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().ContainSingle().Which.Should()
              .Equal(ExpectedIsFrame(0, Concat([UserEntry(), DisplayEntry(), .. SystemEntries()])));
        }

        [Fact]
        public async Task EnvironSend_EmptyRequest_ReturnsDefaults()
        {
            var (output, stream) = await ReadHandlerOnceAsync(ConfigureFull, 255, 250, 36, 1, 255, 240);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().ContainSingle().Which.Should()
              .Equal(ExpectedIsFrame(0, Concat([UserEntry(), DisplayEntry(), .. SystemEntries(), RoleEntry()])));
        }

        [Fact]
        public async Task EnvironSend_UserVarOnlyNothingConfigured_ReturnsBareIs()
        {
            // With no USERVARs configured a USERVAR-only request has nothing
            // to answer: the session parameters ride on VAR, never USERVAR.
            var (output, stream) = await ReadHandlerOnceAsync(static _ => { }, 255, 250, 36, 1, 3, 255, 240);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(new byte[] { 255, 250, 36, 0, 255, 240 });
        }

        [Fact]
        public async Task EnvironSend_VarRequest_VolunteersSessionParameters()
        {
            // The VAR block volunteers TERM/LANG/COLUMNS/LINES from the live
            // session (telnetlib3's auto-sent send_env set). LANG is "C"
            // without an explicit TextEncoding.
            static void Configure(ByteStreamHandler sut)
            {
                sut.TerminalType = "vt220";
                sut.WindowWidth = 19;
                sut.WindowHeight = 84;
            }

            var (output, stream) = await ReadHandlerOnceAsync(Configure, 255, 250, 36, 1, 0, 255, 240);
            output.Should().BeEmpty();
            var expected = ExpectedIsFrame(0,
              Concat([0], L("TERM"), [1], L("vt220")),
              Concat([0], L("LANG"), [1], L("C")),
              Concat([0], L("COLUMNS"), [1], L("19")),
              Concat([0], L("LINES"), [1], L("84")),
              Concat([0], L("COLORTERM"), [1], L(System.Environment.GetEnvironmentVariable("COLORTERM") ?? string.Empty)));
            stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(expected);
        }

        [Fact]
        public async Task EnvironSend_ExplicitEncoding_SendsLocaleLang()
        {
            // telnetlib3 volunteers LANG as en_US.<encoding> when the session
            // encodes text; ours derives it from the explicit TextEncoding.
            static void Configure(ByteStreamHandler sut) => sut.TextEncoding = Encoding.UTF8;
            var (output, stream) = await ReadHandlerOnceAsync(Configure, 255, 250, 39, 1, 0, 255, 240);
            output.Should().BeEmpty();
            var expected = ExpectedIsFrame(0, 39,
              Concat([0], L("TERM"), [1], L("vt100")),
              Concat([0], L("LANG"), [1], L("en_US.utf8")),
              Concat([0], L("COLUMNS"), [1], L("80")),
              Concat([0], L("LINES"), [1], L("24")),
              Concat([0], L("COLORTERM"), [1], L(System.Environment.GetEnvironmentVariable("COLORTERM") ?? string.Empty)));
            stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(expected);
        }

        [Fact]
        public void ParseEntries_EmptyValue_DistinctFromUndefined()
        {
            // RFC 1408 §2: VALUE followed by a type byte (or the end) is
            // defined-but-empty (""); a type with no VALUE is undefined (null).
            // Bytes: IS, VAR "A", VALUE, VAR "B", VAR "C", VALUE-at-end.
            var entries = EnvironmentProtocol.ParseEntries(new byte[] { 0, 0, (byte)'A', 1, 0, (byte)'B', 0, (byte)'C', 1 });
            entries.Should().HaveCount(3);
            entries[0].Should().Be((false, "A", ""));
            entries[1].Should().Be((false, "B", null));
            entries[2].Should().Be((false, "C", ""));
        }

        [Fact]
        public void ParseEntries_TrailingEsc_IsDropped()
        {
            // A trailing ESC is a truncated escape: it contributes no byte,
            // instead of leaking a literal \x02 into the name.
            var entries = EnvironmentProtocol.ParseEntries(new byte[] { 0, 0, (byte)'A', 2 });
            entries.Should().ContainSingle().Which.Should().Be((false, "A", null));
        }

        [Fact]
        public void ParseEntries_BareDelimiters_SkipsEmptyName()
        {
            // Port of test_decode_env_buf_bare_delimiters (DIVERGENCE): bare
            // VAR + USERVAR decodes to {"":""} in the reference, but entries
            // with empty names are skipped here, so nothing is stored.
            var entries = EnvironmentProtocol.ParseEntries(new byte[] { 0, 0, 3 });
            entries.Should().BeEmpty();
        }

        [Fact]
        public async Task EnvironSend_StrayIs_GetsWont()
        {
            var (output, stream) = await ReadHandlerOnceAsync(ConfigureFull, 255, 250, 36, 0, 255, 240);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task EnvironSend_ValueByte_Escaped()
        {
            static void Configure(ByteStreamHandler sut) =>
              sut.EnvironmentUserVars = new Dictionary<string, string>(StringComparer.Ordinal) { ["K"] = "a\u0001b" };
            var (output, stream) = await ReadHandlerOnceAsync(Configure, 255, 250, 36, 1, 3, 255, 240);
            output.Should().BeEmpty();
            var entry = Concat([3], L("K"), [1, (byte)'a', 2, 1, (byte)'b']);
            stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(ExpectedIsFrame(0, entry));
        }

        [Fact]
        public async Task EnvironSend_Latin1Ff_IacDoubled()
        {
            static void Configure(ByteStreamHandler sut) =>
              sut.EnvironmentUserVars = new Dictionary<string, string>(StringComparer.Ordinal) { ["K"] = "ÿ" };
            var (output, stream) = await ReadHandlerOnceAsync(Configure, 255, 250, 36, 1, 3, 255, 240);
            output.Should().BeEmpty();
            var entry = Concat([3], L("K"), [1, 255]);
            stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(ExpectedIsFrame(0, entry));
        }

        [Fact]
        public async Task DoOldEnvironment_GetsWill()
        {
            var (output, stream) = await ReadHandlerOnceAsync(static _ => { }, 255, 253, 36);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(new byte[] { 255, 251, 36 });
        }

        [Fact]
        public async Task WillNewEnvironment_GetsDo()
        {
            var (output, stream) = await ReadHandlerOnceAsync(static _ => { }, 255, 251, 39);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(new byte[] { 255, 253, 39 });
        }

        [Fact]
        public async Task DoNewEnvironment_GetsWill()
        {
            var (output, stream) = await ReadHandlerOnceAsync(static _ => { }, 255, 253, 39);
            output.Should().BeEmpty();
            stream.ByteWrites.Should().ContainSingle().Which.Should().Equal(new byte[] { 255, 251, 39 });
        }

        private static int CountSubnegotiations(ScriptedStream stream) =>
          stream.ByteWrites.Count(static w => w.Length > 3 && w[0] == 255 && w[1] == 250 && w[2] == 36);

        private static async Task<string> ReadClientOnceAsync(Client client)
        {
            return await client.ReadAsync(TimeSpan.FromMilliseconds(100));
        }

        [Fact]
        public async Task EnvironInfo_SentWhenValuesChangeAfterAgreement()
        {
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = new Client(stream, new CancellationToken());
                stream.Enqueue(255, 253, 36);
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                client.Settings.EnvironmentUser = "carol";
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                var expected = ExpectedIsFrame(2,
                  Concat([0], L("USER"), [1], L("carol")),
                  Concat([0], L("TERM"), [1], L("vt100")),
                  Concat([0], L("LANG"), [1], L("C")),
                  Concat([0], L("COLUMNS"), [1], L("80")),
                  Concat([0], L("LINES"), [1], L("24")));
                stream.ByteWrites.Should().ContainSingle(w => w.Length > 3 && w[1] == 250).Which.Should().Equal(expected);
            }
        }

        [Fact]
        public async Task EnvironInfo_NotSentWhenPeerDisagreed()
        {
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = new Client(stream, new CancellationToken());
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                client.Settings.EnvironmentUser = "carol";
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                CountSubnegotiations(stream).Should().Be(0);
            }
        }

        [Fact]
        public async Task EnvironInfo_NotResentWhenUnchanged()
        {
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = new Client(stream, new CancellationToken());
                stream.Enqueue(255, 253, 36);
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                client.Settings.EnvironmentUser = "carol";
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                CountSubnegotiations(stream).Should().Be(1);
            }
        }

        [Theory]
        [InlineData("en_US.UTF-8", true)]
        [InlineData("ja_JP.EUC-JP", true)]
        [InlineData("en_US", false)]
        [InlineData("C", false)]
        public void ShouldForceBinary_LangRule_MatchesTelnetlib3(string lang, bool expected)
        {
            var environ = new Dictionary<string, string>(StringComparer.Ordinal) { ["LANG"] = lang };
            EnvironmentProtocol.ShouldForceBinary(environ).Should().Be(expected);
        }

        [Fact]
        public void ShouldForceBinary_CharsetEntry_Forces()
        {
            var environ = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["CHARSET"] = "UTF-8",
                ["USER"] = "test",
            };
            EnvironmentProtocol.ShouldForceBinary(environ).Should().BeTrue();
        }

        [Fact]
        public void ShouldForceBinary_PlainEntries_DoesNotForce()
        {
            var environ = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["USER"] = "test",
                ["TERM"] = "xterm",
            };
            EnvironmentProtocol.ShouldForceBinary(environ).Should().BeFalse();
            EnvironmentProtocol.ShouldForceBinary(new Dictionary<string, string>()).Should().BeFalse();
        }

        [Fact]
        public void BuildDefaultSendRequest_NonMicrosoft_IncludesUser()
        {
            var request = EnvironmentProtocol.BuildDefaultSendRequest("xterm", "xterm-256color");
            var text = Encoding.Latin1.GetString(request);
            text.Should().StartWith("\0USER\0LOGNAME");
            request.Should().EndWith([0, 3]);
        }

        [Theory]
        [InlineData("ANSI", "VT100", false)]
        [InlineData("ANSI", "ANSI", true)]
        [InlineData("ansi", "vt100", true)]
        [InlineData("xterm", "xterm", true)]
        [InlineData(null, null, true)]
        public void BuildDefaultSendRequest_MicrosoftTelnet_ExcludesUser(string? ttype1, string? ttype2, bool expectUser)
        {
            // Microsoft telnet (exactly ANSI + VT100, case-sensitive) crashes
            // when USER is requested; every other identity keeps it.
            var text = Encoding.Latin1.GetString(EnvironmentProtocol.BuildDefaultSendRequest(ttype1, ttype2));
            text.Contains("\0USER\0").Should().Be(expectUser);
            text.Should().Contain("\0LOGNAME\0DISPLAY\0LANG\0TERM\0");
        }

        [Fact]
        public void BuildDefaultSendRequest_EncodesVarPrefixedNames()
        {
            var request = EnvironmentProtocol.BuildDefaultSendRequest(null, null);
            var expected = new List<byte> { 0 };
            expected.AddRange(L("USER"));
            expected.AddRange(Concat([0], L("LOGNAME")));
            request.Take(expected.Count).Should().Equal(expected);
            request[^2..].Should().Equal([0, 3]);
        }
    }
}
