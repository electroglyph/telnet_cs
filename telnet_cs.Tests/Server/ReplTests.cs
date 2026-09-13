// Tests for the §13a REPL shell: banner, prompt loop, per-prompt
// Go-Ahead, introspection commands, and quit.
namespace telnet_cs.Tests
{
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Server;

    public class ReplTests
    {
        private static int[] Ascii(string text) => text.Select(c => (int)c).ToArray();

        private static ServerSession NewSession(ScriptedStream stream, TelnetServerOptions? options = null)
        {
            return new ServerSession(stream, options ?? new TelnetServerOptions(), CancellationToken.None);
        }

        [Fact]
        public async Task Repl_HelpAndQuit_PromptsTwiceWithGa()
        {
            using var stream = new ScriptedStream([.. Ascii("help\n"), .. Ascii("quit\n")]);
            using var session = NewSession(stream);
            await ServerShells.RunReplAsync(session, CancellationToken.None);
            string all = string.Concat(stream.StringWrites);
            all.Should().Contain("Ready.");
            all.Should().Contain("tel:sh> ");
            all.Should().Contain("quit/help/version/negotiation/stats/environ");
            all.Should().Contain("Goodbye.");
            stream.ByteWrites.Count(w => w.SequenceEqual(new byte[] { 255, 249 })).Should().Be(2);
        }

        [Fact]
        public async Task Repl_NeverSendGa_SendsNoGa()
        {
            using var stream = new ScriptedStream(Ascii("quit\n"));
            var options = new TelnetServerOptions { NeverSendGa = true };
            using var session = NewSession(stream, options);
            await ServerShells.RunReplAsync(session, CancellationToken.None);
            stream.ByteWrites.Should().BeEmpty();
            string.Concat(stream.StringWrites).Should().Contain("Goodbye.");
        }

        [Fact]
        public async Task Repl_IntrospectionCommands_ReportState()
        {
            using var stream = new ScriptedStream(Ascii("bogus\nnegotiation\nstats\nenviron\nversion\nquit\n"));
            using var session = NewSession(stream);
            await ServerShells.RunReplAsync(session, CancellationToken.None);
            string all = string.Concat(stream.StringWrites);
            all.Should().Contain("no such command.");
            all.Should().Contain("Echo:");
            all.Should().Contain("rx=45");
            all.Should().Contain("(empty)");
            all.Should().Contain("telnet_cs ");
        }

        [Fact]
        public async Task Repl_Cancelled_StopsPromptly()
        {
            using var stream = new ScriptedStream();
            using var session = NewSession(stream);
            using var cts = new CancellationTokenSource(200);
            await ServerShells.RunReplAsync(session, cts.Token);
            string.Concat(stream.StringWrites).Should().Contain("tel:sh> ");
        }

        [Fact]
        public async Task Repl_DisconnectedPeer_Stops()
        {
            using var stream = new ScriptedStream(Ascii("stats\n"));
            using var session = NewSession(stream);
            stream.Close();
            await ServerShells.RunReplAsync(session, CancellationToken.None);
            stream.StringWrites.Should().BeEmpty();
        }
    }
}
