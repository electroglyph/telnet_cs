namespace telnet_cs.Tests
{
    using System;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Client;
    using telnet_cs.IO;
    using telnet_cs.Protocol;

    /// <summary>
    /// Round-2 audit (§5) client pins: each test asserts the telnetlib3
    /// behavior, so every test here fails against the current code.
    /// </summary>
    public class Audit2ClientTests
    {
        private static async Task<string> ReadClientOnceAsync(Client client)
        {
            return await client.ReadAsync(TimeSpan.FromMilliseconds(100));
        }

        private static async Task<(string Output, ScriptedStream Stream)> ReadHandlerOnceAsync(Action<ByteStreamHandler> configure, params int[] reads)
        {
            var stream = new ScriptedStream(reads);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            configure(sut);
            var output = await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
            return (output, stream);
        }

        private static byte[] OutboundBytes(ScriptedStream stream)
        {
            return stream.ByteWrites.SelectMany(w => w).ToArray();
        }

        private static bool ContainsSubsequence(byte[] haystack, byte[] needle)
        {
            for (int i = 0; i + needle.Length <= haystack.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    return true;
                }
            }

            return false;
        }

        [Fact]
        public void Client_ExposesSendEor()
        {
            // audit2 §5 F2-EOR-SEND (corrected; verified live).
            // Reference: stream_writer.py:1123-1136 (:1131 gate "if not
            // local_option EOR: return False", :1134-1136 send IAC CMD_EOR,
            // return True); telopt.py:19,194 (IAC=FF, CMD_EOR=EF).
            // Repro: no DO EOR -> send_eor()==False, 0 bytes;
            // local_option[EOR]=True -> True + FF EF.
            // The plumbing guard already mirrors this
            // (ByteStreamHandler.SendEorAsync, internal), but no
            // Client/IClient member reaches it and SendCommand(EOR) throws
            // by design — so the client cannot emit FF EF.
            typeof(Client).GetMethod("SendEorAsync").Should().NotBeNull();
        }

        [Fact]
        public async Task WaitForOptionEnabledAsync_Timeout_ReturnsFalse()
        {
            // audit2 §5 F2-WAITER — pins the C# documented contract, NOT a
            // telnetlib3 behavior (verified live: stream_writer.py:564-620
            // wait_for returns True, raises CancelledError on close (:579),
            // and has no timeout/False path — callers use asyncio.wait_for,
            // which raises TimeoutError; repro awaited wait_for with a 0.2 s
            // asyncio timeout -> TimeoutError, never False).
            // The C# doc promises false on timeout; throwing
            // TimeoutException breaks that contract.
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = new Client(stream, new CancellationToken());
                (await client.WaitForOptionEnabledAsync(Options.EndOfRecord, false, TimeSpan.FromMilliseconds(100))).Should().BeFalse();
            }
        }

        [Fact]
        public async Task ClientWillGmcp_SendsHello()
        {
            // audit2 §5/§8 B13 (client half, corrected; verified live).
            // Reference: client.py:166-172 (setup_gmcp via passive_do),
            // :174-182 (on_will_gmcp), :199-209 (Core.Hello+Supports.Set);
            // stream_writer.py:110 (_MUD_PROTOCOL_OPTIONS), :2209-2224
            // (opt-in -> DO + callbacks, else DONT).
            // Repro (client role, passive_do={GMCP}): IAC WILL GMCP ->
            // FF FD C9 (DO GMCP) + FF FA C9 "Core.Hello"...; bare writer ->
            // FF FE C9 (DONT). Hence Client with EnableMudOptions below.
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = new Client(stream, new CancellationToken());
                client.Settings.EnableMudOptions = true;
                stream.Enqueue(255, 251, 201);
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                ContainsSubsequence(OutboundBytes(stream), new byte[] { 255, 253, 201 }).Should().BeTrue("mud opt-in agrees WILL GMCP with DO GMCP");
                ContainsSubsequence(OutboundBytes(stream), Encoding.ASCII.GetBytes("Core.Hello")).Should().BeTrue();
            }
        }

        [Fact]
        public async Task ClientWillZmp_SendsIdent()
        {
            // audit2 §5/§8 B13 (client half, corrected; verified live).
            // Reference: client.py:184-197 (setup_zmp/on_will_zmp),
            // :211-219 (send_zmp_ident "zmp.ident"); same
            // stream_writer.py:2209-2224 opt-in gate.
            // Repro: IAC WILL ZMP -> FF FD 5D (DO ZMP) + FF FA 5D
            // "zmp.ident"...; bare writer -> DONT.
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = new Client(stream, new CancellationToken());
                client.Settings.EnableMudOptions = true;
                stream.Enqueue(255, 251, 93);
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                ContainsSubsequence(OutboundBytes(stream), new byte[] { 255, 253, 93 }).Should().BeTrue("mud opt-in agrees WILL ZMP with DO ZMP");
                ContainsSubsequence(OutboundBytes(stream), Encoding.ASCII.GetBytes("zmp.ident")).Should().BeTrue();
            }
        }

        [Fact]
        public void DefaultCharsetOffers_MatchReference()
        {
            // audit2 §5 F2-INFO (offers half; verified live).
            // Reference: client.py:436-445 (on_request_charset returns
            // ["UTF-8","LATIN1","US-ASCII"]; bare-server :1958 returns
            // ["UTF-8"] only).
            // Repro: TelnetClient().on_request_charset() ==
            // ['UTF-8','LATIN1','US-ASCII'].
            new TelnetClientOptions().CharsetOffers.Should().Equal("UTF-8", "LATIN1", "US-ASCII");
        }

        [Fact]
        public async Task WriteHighByte_BeforeBinary_Throws()
        {
            // audit2 §5 F2-INFO (BINARY-gate half; verified live;
            // deliberate-deviation pin, type maps Py->.NET).
            // Reference: client.py:466-502 (:502 returns US-ASCII unless
            // force_binary/may_encode; :68 force_binary=False),
            // stream_writer.py:950-956 (inbinary/outbinary default False),
            // :3433-3452 (strict encode via fn_encoding).
            // Repro: outbinary=False, encode('é', US-ASCII strict) ->
            // UnicodeEncodeError (C# analog: EncoderFallbackException);
            // utf-8 -> c3a9, not e9.
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = new Client(stream, new CancellationToken());
                Func<Task> act = () => client.WriteAsync("é");
                await act.Should().ThrowAsync<EncoderFallbackException>();
            }
        }

        [Fact]
        public async Task EnvironmentDisplay_NeverSent()
        {
            // audit2 §5 F2-INFO (DISPLAY half; verified live — title
            // overstates: "never" holds for the volunteered path, not an
            // explicit SEND DISPLAY which yields an empty value).
            // Reference: client.py:280-307 (:304 "DISPLAY intentionally not
            // available", no DISPLAY in all_env; :307 filter).
            // Repro: send_env([]) has no DISPLAY; explicit
            // send_env(['DISPLAY'])=={DISPLAY:''} (empty). This test uses
            // SEND-all (FF FA 27 01 FF F0) -> no DISPLAY bytes.
            // CONFLICT: AuditFixListTests.EnvironInfo_ExplicitDisplay_IsVolunteered
            // pins the Cs superset as deliberate — owner to triage.
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = new Client(stream, new CancellationToken());
                client.Settings.EnvironmentDisplay = "host:0";
                stream.Enqueue(255, 253, 39, 255, 250, 39, 1, 255, 240);
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                ContainsSubsequence(OutboundBytes(stream), Encoding.ASCII.GetBytes("DISPLAY")).Should().BeFalse();
            }
        }

        [Fact]
        public async Task DefaultTerminalType_MatchesReference()
        {
            // audit2 §5 5.2 statics (verified live).
            // Reference: client.py:59 (term="unknown"), :114, :556, :978
            // (--term default unknown); server.py:96,1104 same. (--ttype
            // VT100 at client.py:1231-1233 is fingerprint-tool only.)
            // Repro: TelnetClient()._extra['term']=='unknown'.
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = new Client(stream, new CancellationToken());
                stream.Enqueue(255, 251, 24, 255, 250, 24, 1, 255, 240);
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                ContainsSubsequence(OutboundBytes(stream), Encoding.ASCII.GetBytes("unknown")).Should().BeTrue();
            }
        }

        [Fact]
        public void DefaultTextEncoding_MatchesReference()
        {
            // audit2 §5 5.2 statics (verified live).
            // Reference: client.py:66,553 (encoding="utf8");
            // server.py:64,102,1096 same; client.py:909 CLI default=utf8.
            // Repro: TelnetClient()._extra['charset']=='utf8',
            // lang=='en_US.utf8'.
            new TelnetClientOptions().TextEncoding.Should().Be(Encoding.UTF8);
        }

        [Fact]
        public async Task CancelledRead_Throws()
        {
            // audit2 §5 5.4 cancel (verified live).
            // Reference: stream_reader.py:147 (_wait_for_data), :379/:422
            // (read/readexactly await it); stream_writer.py:564-579
            // (wait_for raises CancelledError).
            // Repro: pending read(10) cancelled -> CancelledError: True
            // (C# analog: OperationCanceledException).
            using var stream = new ScriptedStream("AB");
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            Func<Task> act = () => sut.ReadAsync(TimeSpan.FromSeconds(5));
            await act.Should().ThrowAsync<OperationCanceledException>();
        }

        [Fact]
        public async Task TerminatedRead_WithoutTerminator_Throws()
        {
            // audit2 §5 5.4 partial-on-timeout (verified live).
            // Reference: stream_reader.py:175-260 (readuntil never returns
            // empty; :253 IncompleteReadError on EOF, :242/:259
            // LimitOverrunError, else pends).
            // Repro: readuntil(b'.') with no terminator + 0.2 s asyncio
            // timeout -> TimeoutError (not "").
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = new Client(stream, new CancellationToken());
                Func<Task> act = () => client.TerminatedReadAsync(".", TimeSpan.FromMilliseconds(200));
                await act.Should().ThrowAsync<TimeoutException>();
            }
        }

        [Fact]
        public async Task TerminatedRead_OverlongLine_Throws()
        {
            // audit2 §5 5.4 no-limit (verified live).
            // Reference: stream_reader.py:17 (_DEFAULT_LIMIT=65536), :32,
            // :242/:259 (LimitOverrunError).
            // Repro: 70000 x 'A' at the default limit ->
            // LimitOverrunError ("Separator is not found, and chunk exceed
            // the limit"). (Exact C# exception type is owner's choice; any
            // raise beats a silent partial.)
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream(new string('A', 70000));
                using var client = new Client(stream, new CancellationToken());
                Func<Task> act = () => client.TerminatedReadAsync(".", TimeSpan.FromSeconds(2));
                await act.Should().ThrowAsync<Exception>();
            }
        }

        [Fact]
        public void Client_ExposesReadExactly()
        {
            // audit2 §1 1.28 read-primitives gap (verified live).
            // Reference: stream_reader.py:388-420 (readexactly; :420
            // IncompleteReadError), :653-675 (unicode variant); :338
            // (read), :175 (readuntil), :470/:600 (readline).
            // Repro: hasattr(readexactly)==True; feed b'AB'+EOF then
            // readexactly(5) -> IncompleteReadError (partial=b'AB',
            // expected=5). No exact-length read with end-of-stream
            // semantics exists here.
            typeof(Client).GetMethod("ReadExactlyAsync").Should().NotBeNull();
        }

        [Fact]
        public void NawsZero_SentAsIs()
        {
            // audit2 §5 F2-NAWS-ZERO (verified live).
            // Reference: stream_writer.py:2627-2646 (:2638
            // max(min(65535),0) preserves 0; struct.pack '!HH' cols,rows).
            // Repro: (0,0) -> FF FA 1F 00 00 00 00 FF F0 (payload
            // 00000000 unpacks (0,0)); (25,80) -> FF FA 1F 00 50 00 19
            // FF F0. The documented promise is that a 0 dimension is sent
            // as-is (RFC 1073 "unspecified"), not mapped to
            // console/80x24. (Conflicts with 0-means-auto; pins the doc.)
            var (width, height) = NawsProtocol.GetEffectiveSize(0, 0);
            width.Should().Be(0);
            height.Should().Be(0);
        }

    }
}
