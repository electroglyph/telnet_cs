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
    using telnet_cs.Protocol;
    using telnet_cs.Server;

    /// <summary>
    /// Extended terminal/environment option pins: each test asserts the
    /// telnetlib3 behavior.
    /// Serial: EnvironmentInfo_IncludesColorterm mutates the process-wide
    /// COLORTERM variable (the only channel the reference reads it from).
    /// </summary>
    [Collection("Serial")]
    public class TerminalEnvironmentTests
    {
        private static async Task<string> ReadClientOnceAsync(Client client)
        {
            return await client.ReadAsync(TimeSpan.FromMilliseconds(100));
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
        public async Task LongTerminalType_SentVerbatim()
        {
            // Long terminal types are sent verbatim.
            // Reference: client.py:265-268 (send_ttype returns _extra["term"]
            // verbatim); stream_writer.py:2537-2557 (_handle_sb_ttype :2553
            // encode("ascii"), no truncation); server.py:602-616 (on_ttype
            // stores verbatim). grep "40/truncate" finds no TTYPE cap (only
            // stream_writer.py:1295 ENVIRON, :233 EBCDIC).
            // Repro: TelnetClient(term='A'*50).send_ttype() len 50;
            // SB TTYPE IS frame FF FA 18 00 + 41*50 len 56, contains 41*50.
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = new Client(stream, new CancellationToken());
                client.ApplyOptions(new TelnetClientOptions { TerminalType = new string('A', 50) });
                stream.Enqueue(255, 251, 24, 255, 250, 24, 1, 255, 240);
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                ContainsSubsequence(OutboundBytes(stream), Encoding.ASCII.GetBytes(new string('A', 50))).Should().BeTrue();
            }
        }

        [Fact]
        public async Task DefaultTerminalSpeed_MatchesReference()
        {
            // Default terminal speed matches the reference.
            // Reference: client.py:62 (tspeed=(38400,38400)), :115
            // ("38400,38400"), :270-273 (send_tspeed); server.py:598-600
            // (on_tspeed(rx,tx)); stream_writer.py:1887-1890 (bare 9600,9600
            // is not the client default).
            // Repro: TelnetClient()._extra["tspeed"]=='38400,38400',
            // send_tspeed()==(38400,38400); (57600,38400) frame =
            // FF FA 20 00 "57600,38400" FF F0 (rx,tx order).
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = new Client(stream, new CancellationToken());
                stream.Enqueue(255, 253, 32, 255, 250, 32, 1, 255, 240);
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                ContainsSubsequence(OutboundBytes(stream), Encoding.ASCII.GetBytes("38400,38400")).Should().BeTrue();
            }
        }

        [Fact]
        public async Task StatusSnapshot_IncludesRefusedOptions()
        {
            // STATUS snapshot covers refused options too.
            // Reference: stream_writer.py:2781-2805 (_send_status: :2793
            // WILL/WONT for all local_option, :2802/:2804 DO/DONT for all
            // remote_option; only STATUS itself skipped).
            // Repro: local ECHO=False,SGA=True; remote ECHO=False ->
            // FF FA 05 00 FC 01 FB 03 FE 01 FF F0 — contains FC 01
            // (WONT ECHO) and FE 01 (DONT ECHO).
            using var stream = new ScriptedStream(255, 253, 1, 255, 253, 5);
            using var cts = new CancellationTokenSource();
            using var sut = new telnet_cs.IO.ByteStreamHandler(stream, cts, 1);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            var outbound = OutboundBytes(stream);
            int start = FindSubsequence(outbound, new byte[] { 255, 250, 5, 0 });
            start.Should().BeGreaterThanOrEqualTo(0);
            int end = FindSubsequence(outbound, new byte[] { 255, 240 }, start);
            end.Should().BeGreaterThan(start);
            ContainsSubsequence(outbound[start..end], new byte[] { 252, 1 }).Should().BeTrue();
        }

        [Fact]
        public async Task EnvironmentInfo_IncludesColorterm()
        {
            // Environment INFO includes COLORTERM.
            // Reference: client.py:55 (DEFAULT_SEND_ENVIRON =
            // TERM,LANG,COLUMNS,LINES,COLORTERM), :99, :280-307 (:300 reads
            // os.environ COLORTERM), :953 (CLI default).
            // Repro: COLORTERM=24bit -> send_env([])=={LANG:en_US.utf8,
            // TERM:unknown, LINES:25, COLUMNS:80, COLORTERM:24bit};
            // send_env(['COLORTERM'])=={COLORTERM:24bit}. No COLORTERM ->
            // {COLORTERM:''} (key still present, empty).
            // Caveat: telnetlib3 has no INFO sender (INFO only received,
            // :2590-2601), so this pins C# internal consistency (IS path
            // sends it, INFO builder drops it), not a direct wire divergence.
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = new Client(stream, new CancellationToken());
                stream.Enqueue(255, 253, 39);
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                client.Settings.EnvironmentDisplay = "display:0";
                Environment.SetEnvironmentVariable("COLORTERM", "24bit");
                try
                {
                    (await ReadClientOnceAsync(client)).Should().BeEmpty();
                }
                finally
                {
                    Environment.SetEnvironmentVariable("COLORTERM", null);
                }

                ContainsSubsequence(OutboundBytes(stream), Encoding.ASCII.GetBytes("COLORTERM")).Should().BeTrue();
            }
        }

        [Fact]
        public async Task LangSpelling_AgreesAcrossSbAndInfo()
        {
            // SB and INFO LANG spellings agree.
            // Reference: client.py:52 (DEFAULT_LOCALE=en_US), :111
            // (lang=en_US.+encoding -> en_US.utf8), :295 (send_env uses the
            // single _extra["lang"] source for SB+INFO), :405/:416/:428
            // (post-CHARSET lang=en_US.+canon).
            // Repro: initial lang=='en_US.utf8';
            // codecs.lookup('utf8').name=='utf-8';
            // send_charset(['latin1','utf-8']) -> sel='utf-8',
            // lang='en_US.utf-8'. Without CHARSET both paths read
            // en_US.utf8, so stripped&&dashed==False.
            // Caveat: same as COLORTERM — telnetlib3 has no INFO sender, so
            // this pins C# SB-vs-INFO agreement, not a wire divergence.
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var client = new Client(stream, new CancellationToken());
                client.ApplyOptions(new TelnetClientOptions { TextEncoding = Encoding.UTF8 });
                stream.Enqueue(255, 253, 39, 255, 250, 39, 1, 255, 240);
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                client.Settings.EnvironmentDisplay = "display:0";
                (await ReadClientOnceAsync(client)).Should().BeEmpty();
                var text = Encoding.ASCII.GetString(OutboundBytes(stream));
                bool stripped = text.Contains("en_US.utf8", StringComparison.Ordinal);
                bool dashed = text.Contains("en_US.utf-8", StringComparison.Ordinal);
                (stripped && dashed).Should().BeFalse("SB and INFO LANG spellings must agree");
            }
        }

        [Fact]
        public async Task ExtendedEnvironmentRequest_BatchedAt240()
        {
            // Extended environment requests batch at 240 bytes.
            // Reference: stream_writer.py:1295 (_ENVIRON_SB_MAX=240),
            // :1297-1323 (request_environ), :1326-1360 (_batch_environ_keys,
            // :1349 >240 split), :1362-1380 (_send_environ_batch),
            // :2603-2604 (continuation on IS).
            // Repro: 300 names -> 7 batches, payloads <=240 (e.g. 237);
            // frames len 241<=245, e.g. FF FA 27 01 00 "K0000"...; queued 6,
            // second frame sent after IS.
            using var stream = new ScriptedStream();
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            _ = session.Negotiation.ReceivedWill((int)Options.OldEnvironment, agree: true);
            var types = Enumerable.Repeat((byte)0, 300).ToArray();
            await session.RequestEnvironmentAsync(TimeSpan.FromMilliseconds(300), types);
            var frames = stream.ByteWrites
                .Where(w => w.Length >= 3 && w[0] == 255 && w[1] == 250 && w[2] == 36)
                .ToList();
            frames.Should().HaveCountGreaterThanOrEqualTo(2);
            frames.Should().OnlyContain(w => w.Length <= 245);
        }

        private static int FindSubsequence(byte[] haystack, byte[] needle, int from = 0)
        {
            for (int i = from; i + needle.Length <= haystack.Length; i++)
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
                    return i;
                }
            }

            return -1;
        }
    }
}
