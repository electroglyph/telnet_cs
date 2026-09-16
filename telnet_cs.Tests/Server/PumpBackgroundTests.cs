// C2 background-pump pin: a session nobody ever reads still answers
// negotiation (TTYPE probe, NAWS advance) via the constructor-started pump.
// Hermetic duplex pair; the server end is never read, only its state and
// written bytes are polled with bounded waits.
namespace telnet_cs.Tests
{
    using System;
    using System.Diagnostics;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Server;
    using telnet_cs.Transport;

    public class PumpBackgroundTests
    {
        private const int Iac = 255;
        private const int Will = 251;
        private const int Do = 253;
        private const int Sb = 250;
        private const int Se = 240;

        private static bool ContainsSequence(byte[] haystack, byte[] needle)
        {
            if (needle.Length == 0 || haystack.Length < needle.Length)
            {
                return false;
            }

            for (int i = 0; i <= haystack.Length - needle.Length; i++)
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
        public async Task PumpInbound_NeverRead_AnswersTtypeAndNaws()
        {
            var options = new TelnetServerOptions
            {
                MaxConnectionsPerIp = 0,
                LoginAttemptDelay = TimeSpan.Zero,
                HandshakeTimeout = TimeSpan.FromSeconds(30),
            };
            var (clientStream, serverStream) = DuplexPipe.Create();
            using var session = new ServerSession(serverStream, options, CancellationToken.None);

            // Peer WILLs TTYPE; the pump must answer SB TTYPE SEND with no
            // caller read driving the wire.
            await clientStream.WriteAsync([Iac, Will, 24], 0, 3, CancellationToken.None);
            byte[] ttypeSend = [Iac, Sb, 24, 1, Iac, Se];
            var sw = Stopwatch.StartNew();
            bool sawSend = false;
            while (sw.Elapsed < TimeSpan.FromSeconds(5))
            {
                byte[] written = [.. serverStream.WrittenBytes];
                if (ContainsSequence(written, ttypeSend))
                {
                    sawSend = true;
                    break;
                }

                await Task.Delay(50);
            }

            sawSend.Should().BeTrue("the background pump answers TTYPE without any caller read");

            // Answer the probe plus WILL/SB NAWS; state must converge with
            // still no caller read.
            byte[] nameBytes = Encoding.ASCII.GetBytes("XTERM");
            byte[] isXterm = new byte[4 + nameBytes.Length + 2];
            isXterm[0] = Iac;
            isXterm[1] = Sb;
            isXterm[2] = 24;
            isXterm[3] = 0;
            Array.Copy(nameBytes, 0, isXterm, 4, nameBytes.Length);
            isXterm[^2] = Iac;
            isXterm[^1] = Se;
            await clientStream.WriteAsync(isXterm, 0, isXterm.Length, CancellationToken.None);
            await clientStream.WriteAsync([Iac, Will, 31], 0, 3, CancellationToken.None);
            await clientStream.WriteAsync([Iac, Sb, 31, 0, 80, 0, 24, Iac, Se], 0, 9, CancellationToken.None);

            sw.Restart();
            bool converged = false;
            byte[] advanced = [Iac, Do, 31];
            while (sw.Elapsed < TimeSpan.FromSeconds(5))
            {
                if (session.ClientTerminalTypes.Contains("XTERM") && session.ClientWindowSize is { Width: 80, Height: 24 })
                {
                    converged = true;
                    break;
                }

                await Task.Delay(50);
            }

            converged.Should().BeTrue("pump-buffered negotiation state converges without any caller read");
            ContainsSequence([.. serverStream.WrittenBytes], advanced).Should().BeTrue("the TTYPE advance releases the advanced preset (DO NAWS)");
        }
    }
}
