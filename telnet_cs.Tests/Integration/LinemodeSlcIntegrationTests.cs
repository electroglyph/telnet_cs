// Phase 4 LINEMODE + SLC pins over a live pair: the loopback
// LinemodeInterop_ModeAndSlcTable_EndToEnd exchange, hermetically. Mode
// propose/ack plus both SLC directions (publish server->client, export
// client->server) go live; crafted-frame rules (NOSUPPORT/out-of-range
// rows, ACK-drop, gap omission, malformed tails) stay scripted — they need
// byte-exact SBs no public API can emit.
namespace telnet_cs.Tests
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Client;
    using telnet_cs.Protocol;
    using telnet_cs.Server;

    public class LinemodeSlcIntegrationTests
    {
        private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

        private static (Client Client, ServerSession Session, IDisposable Guard) CreateLinemodePair()
        {
            var options = new TelnetServerOptions { RequestLinemode = true };
            return LiveExchange.CreatePair(options);
        }

        private static async Task AgreeLinemodeAsync(Client client, ServerSession session)
        {
            await session.SendOpeningPresetAsync(CancellationToken.None);
            await LiveExchange.PumpUntilAsync(
                client, session,
                () => client.Negotiation.IsEnabledByUs((int)Options.LineMode)
                    && session.Negotiation.IsEnabledByPeer((int)Options.LineMode),
                Budget);
            client.Negotiation.IsEnabledByUs((int)Options.LineMode).Should().BeTrue();
            session.Negotiation.IsEnabledByPeer((int)Options.LineMode).Should().BeTrue();
        }

        [Fact]
        public async Task Linemode_ModePropose_AckedBothSides()
        {
            // The server's MODE proposal lands in the client table on the
            // client's next read; the client's ACK folds into the server
            // table on the server's next read.
            var (client, session, guard) = CreateLinemodePair();
            using (client)
            using (session)
            using (guard)
            {
                await AgreeLinemodeAsync(client, session);

                const byte mode = (byte)(LinemodeProtocol.Edit | LinemodeProtocol.TrapSignal);
                await session.SendModeAsync(mode);
                await LiveExchange.PumpUntilAsync(
                    client, session, () => client.GetLinemodeMode() == mode, Budget);

                client.GetLinemodeMode().Should().Be(mode);
                await LiveExchange.PumpUntilAsync(
                    client, session, () => session.GetLinemodeMode() == mode, Budget);
                session.GetLinemodeMode().Should().Be(mode);
            }
        }

        [Fact]
        public async Task Linemode_PublishSpecialCharacters_LandsInClientTable()
        {
            var (client, session, guard) = CreateLinemodePair();
            using (client)
            using (session)
            using (guard)
            {
                await AgreeLinemodeAsync(client, session);

                session.SetLinemodeEntry(3, LinemodeProtocol.LevelValue, 9);
                await session.PublishSpecialCharactersAsync();
                await LiveExchange.PumpUntilAsync(
                    client, session, () => client.GetLinemodeEntry(3).Value == 9, Budget);

                client.GetLinemodeEntry(3).Level.Should().Be(LinemodeProtocol.LevelValue);
                client.GetLinemodeEntry(3).Value.Should().Be(9);
            }
        }

        [Fact]
        public async Task Linemode_ExportSpecialCharacters_LandsInServerTable()
        {
            var (client, session, guard) = CreateLinemodePair();
            using (client)
            using (session)
            using (guard)
            {
                await AgreeLinemodeAsync(client, session);

                client.SetLinemodeEntry(10, LinemodeProtocol.LevelValue, 8);
                await client.ExportSpecialCharactersAsync();
                await LiveExchange.PumpUntilAsync(
                    client, session, () => session.GetLinemodeEntry(10).Value == 8, Budget);

                session.GetLinemodeEntry(10).Level.Should().Be(LinemodeProtocol.LevelValue);
                session.GetLinemodeEntry(10).Value.Should().Be(8);
            }
        }
    }
}
