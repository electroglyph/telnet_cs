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
    using telnet_cs.Server;

    /// <summary>
    /// Charset, MUD-protocol encoding, and MCCP default pins against the
    /// telnetlib3 behavior.
    /// </summary>
    public class CharsetAndMudEncodingTests
    {
        private static int[] Ascii(string text)
        {
            return text.Select(c => (int)c).ToArray();
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
        public async Task ExplicitPreferenceExactOffer_Accepted()
        {
            // An exact charset offer matching the explicit preference is ACCEPTED before any narrowing.
            // Reference: client.py:378-407 (scan all offered; :391-393
            // exact canon==desired_name -> matched_offer; :402-407 ACCEPT
            // before any narrowing; no CharsetOffers intersection exists).
            // Repro (real TelnetClient.send_charset, default_encoding=
            // utf-8, offered=[utf-8, latin1]): returns 'utf-8',
            // extra['charset']='utf-8' (ACCEPT = FF FA 2A 02, not 03).
            using var stream = new ScriptedStream([255, 253, 42, 255, 251, 42, 255, 250, 42, 1, .. Ascii("utf-8 latin1"), 255, 240]);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            sut.TextEncoding = Encoding.UTF8;
            sut.CharsetOffers = ["LATIN1"];
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(200))).Should().BeEmpty();
            var outbound = OutboundBytes(stream);
            ContainsSubsequence(outbound, new byte[] { 255, 250, 42, 2 }).Should().BeTrue("exact utf-8 offer must be ACCEPTED");
            ContainsSubsequence(outbound, new byte[] { 255, 250, 42, 3 }).Should().BeFalse("must not be REJECTED");
        }

        [Fact]
        public async Task Latin1OnlyOffer_Accepted()
        {
            // A LATIN1-only offer is accepted via canonical-name normalization.
            // Reference: client.py:311-346 (_normalize: 4 candidates at :340
            // — base, no_leading_zeros, no_hyphens, partial); :424-430 (no
            // preference -> first viable ACCEPT);
            // codecs.lookup('LATIN1').name='iso8859-1'.
            // Repro: default_encoding=None, offered=['LATIN1'] -> returns
            // 'LATIN1', charset='iso8859-1'; _normalize('LATIN1')='LATIN1'.
            using var stream = new ScriptedStream([255, 253, 42, 255, 251, 42, 255, 250, 42, 1, .. Ascii("LATIN1"), 255, 240]);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(200))).Should().BeEmpty();
            ContainsSubsequence(OutboundBytes(stream), new byte[] { 255, 250, 42, 2 }).Should().BeTrue();
        }

        [Fact]
        public void MsdpNullValue_EncodedAsNone()
        {
            // A null MSDP value encodes as the string "None".
            // Reference: mud.py:101-128, esp. :123 (return
            // str(value).encode('utf-8') fallthrough).
            // Repro: mud.msdp_encode({'k':None}) -> 01 6B 02 4E6F6E65 =
            // VAR 'k' VAL 'None'.
            var bytes = MudProtocol.MsdpEncode(new Dictionary<string, object?> { ["k"] = null });
            Encoding.ASCII.GetString(bytes).Should().Contain("None");
        }

        [Fact]
        public void MalformedGmcpJson_ThrowsArgumentException()
        {
            // Malformed GMCP JSON throws ArgumentException (Python ValueError analog).
            // Reference: mud.py:77-98, esp. :94-97 (except JSONDecodeError
            // -> raise ValueError; Python ValueError == .NET ArgumentException
            // family).
            // Repro: mud.gmcp_decode(b'pkg {bad') -> ValueError "Invalid
            // JSON in GMCP payload: Expecting property name...".
            Action act = () => MudProtocol.GmcpDecode("pkg {bad"u8, null);
            act.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void ClientMccpDefault_PassivelyAccepts()
        {
            // MCCP is passively accepted by default (client half).
            // Reference: client.py:71 + :625-627 (compression None default
            // passively accepts; False rejects), :131-132,
            // stream_writer.py:297-299 (None=accept) + :2223-2228 (refuse
            // only if False/TLS) for WILL, :2088-2093 for DO.
            // Repro: client compression=None; client writer
            // handle_will(MCCP2) with None -> FF FD 56 (DO/accept) vs False
            // -> FF FE 56 (DONT/refuse). A default-refuse would be an
            // intentional opt-in deviation.
            new TelnetClientOptions().EnableMccp.Should().BeTrue();
        }

        [Fact]
        public void ServerMccpDefault_PassivelyAccepts()
        {
            // MCCP is passively accepted by default (server half).
            // Reference: server.py:108,135-136,158-159 + :1170-1173 (None
            // default passively accepts); same writer gates as the client
            // half above.
            // Repro: server compression=None; server writer
            // handle_do(MCCP2) with None -> FF FB 56 (WILL/accept) vs False
            // -> FF FC 56 (WONT/refuse).
            new TelnetServerOptions().EnableMccp.Should().BeTrue();
        }
    }
}
