// Phase 4 TTYPE-chain pins over a live pair: a real Client with a
// configured TerminalTypes list answers every SEND verbatim, and the
// session collector assembles the reference chain (order, repeat-stop,
// case-sensitivity, MTTS third slot, loop-max overflow slot).
namespace telnet_cs.Tests
{
    using System;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Client;
    using telnet_cs.Protocol;
    using telnet_cs.Server;

    public class TtypeChainIntegrationTests
    {
        private static async Task<(Client Client, ServerSession Session, IDisposable Guard)> OpenAgreedPairAsync(
            Action<TelnetClientOptions>? configureClient = null,
            TelnetServerOptions? serverOptions = null)
        {
            var pair = await LiveExchange.CreatePairAsync(serverOptions, configureClient);
            await pair.Session.SendOpeningPresetAsync(CancellationToken.None);
            await LiveExchange.PumpUntilAsync(
                pair.Client,
                pair.Session,
                () => pair.Session.Negotiation.IsEnabledByPeer((int)Options.TerminalType),
                TimeSpan.FromSeconds(10));
            pair.Session.Negotiation.IsEnabledByPeer((int)Options.TerminalType).Should().BeTrue();
            return pair;
        }

        [Fact]
        public async Task RequestTerminalTypesAsync_MultiAnswerChain_CollectsInOrder()
        {
            var pair = await OpenAgreedPairAsync(o => o.TerminalTypes = ["vt100", "xterm"]);
            using (pair.Client)
            using (pair.Session)
            using (pair.Guard)
            {
                var collect = pair.Session.RequestTerminalTypesAsync(TimeSpan.FromSeconds(10));
                var chain = await LiveExchange.CollectAsync(collect, pair.Client, TimeSpan.FromSeconds(15));
                chain.Should().Equal("vt100", "xterm");
            }
        }

        [Fact]
        public async Task RequestTerminalTypesAsync_SingleType_StopsAtFirstRepeat()
        {
            var pair = await OpenAgreedPairAsync(o => o.TerminalTypes = ["xterm"]);
            using (pair.Client)
            using (pair.Session)
            using (pair.Guard)
            {
                var collect = pair.Session.RequestTerminalTypesAsync(TimeSpan.FromSeconds(10));
                var chain = await LiveExchange.CollectAsync(collect, pair.Client, TimeSpan.FromSeconds(15));
                chain.Should().Equal("xterm");
            }
        }

        [Fact]
        public async Task RequestTerminalTypesAsync_CaseVariants_BothKept()
        {
            // Case-sensitive compare: xterm != XTERM is a new answer.
            var pair = await OpenAgreedPairAsync(o => o.TerminalTypes = ["XTERM", "xterm"]);
            using (pair.Client)
            using (pair.Session)
            using (pair.Guard)
            {
                var collect = pair.Session.RequestTerminalTypesAsync(TimeSpan.FromSeconds(10));
                var chain = await LiveExchange.CollectAsync(collect, pair.Client, TimeSpan.FromSeconds(15));
                chain.Should().Equal("XTERM", "xterm");
            }
        }

        [Fact]
        public async Task RequestTerminalTypesAsync_MttsThirdSlot_EffectiveTypeIsSecond()
        {
            var pair = await OpenAgreedPairAsync(o => o.TerminalTypes = ["XTERM", "VT100", "mtts 137"]);
            using (pair.Client)
            using (pair.Session)
            using (pair.Guard)
            {
                var collect = pair.Session.RequestTerminalTypesAsync(TimeSpan.FromSeconds(10));
                var chain = await LiveExchange.CollectAsync(collect, pair.Client, TimeSpan.FromSeconds(15));
                chain.Should().Equal("XTERM", "VT100", "mtts 137");
                pair.Session.ClientEffectiveTerminalType.Should().Be("VT100");
            }
        }

        [Fact]
        public async Task RequestTerminalTypesAsync_LongChain_StopsAtLoopMaxOverflowSlot()
        {
            // Past slot 8 the 10th answer overwrites the single overflow
            // slot a final time, then the cycle stops: T9 is lost, T10
            // wins, later answers are ignored.
            var names = Enumerable.Range(1, 12).Select(i => "T" + i).ToList();
            var pair = await OpenAgreedPairAsync(o => o.TerminalTypes = names);
            using (pair.Client)
            using (pair.Session)
            using (pair.Guard)
            {
                var collect = pair.Session.RequestTerminalTypesAsync(TimeSpan.FromSeconds(10));
                var chain = await LiveExchange.CollectAsync(collect, pair.Client, TimeSpan.FromSeconds(20));
                chain.Should().Equal("T1", "T2", "T3", "T4", "T5", "T6", "T7", "T8", "T10");
            }
        }
    }
}
