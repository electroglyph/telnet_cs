// Phase 4 opening/advanced-preset pins over a live pair: the session
// end wears a WireTap so the exact preset bytes are asserted while a
// real Client answers on the other end. Opening = DO TTYPE only;
// WILL SGA / WILL BINARY / DO NAWS / DO CHARSET follow once the
// client's WILL TTYPE advances negotiation; DO LINEMODE only when
// requested; TSPEED / OLD-ENVIRON / MCCP are never offered unsolicited;
// one SB TTYPE SEND follows the peer WILL TTYPE.
namespace telnet_cs.Tests
{
    using System;
    using System.Diagnostics;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Client;
    using telnet_cs.Protocol;
    using telnet_cs.Server;
    using telnet_cs.Transport;

    public class OpeningPresetIntegrationTests
    {
        private static async Task<(Client Client, ServerSession Session, WireTap Tap, IDisposable Guard)> CreateTappedPairAsync(
            TelnetServerOptions? serverOptions = null)
        {
            var guard = GlobalStateGuard.SkipProactive(true);
            var (clientStream, serverStream) = InMemoryPipe.Create();
            var tap = new WireTap(serverStream);
            var session = new ServerSession(
                tap, serverOptions ?? new TelnetServerOptions(), CancellationToken.None);
            var client = await Client.CreateAsync(clientStream, TimeSpan.FromSeconds(30), CancellationToken.None);
            return (client, session, tap, guard);
        }

        [Fact]
        public async Task SendOpeningPresetAsync_DefaultOptions_SendsDoTtypeOnly()
        {
            var (client, session, tap, guard) = await CreateTappedPairAsync();
            using (client)
            using (session)
            using (guard)
            {
                await session.SendOpeningPresetAsync(CancellationToken.None);

                // No pumping: the opening preset alone is on the wire.
                tap.WrittenBytes.Should().Equal(Wire.Iac, Wire.Do, (byte)(int)Options.TerminalType);
            }
        }

        [Fact]
        public async Task SendOpeningPresetAsync_RequestTerminalTypeOff_SendsNothing()
        {
            var options = new TelnetServerOptions { RequestTerminalType = false };
            var (client, session, tap, guard) = await CreateTappedPairAsync(options);
            using (client)
            using (session)
            using (guard)
            {
                await session.SendOpeningPresetAsync(CancellationToken.None);
                await LiveExchange.PumpForAsync(client, session, TimeSpan.FromMilliseconds(300));

                tap.WrittenBytes.Should().BeEmpty();
                client.Negotiation.IsEnabledByPeer((int)Options.TerminalType).Should().BeFalse();
            }
        }

        [Fact]
        public async Task NegotiateLive_DefaultOptions_AdvancedPresetFollowsTtypeWill()
        {
            var (client, session, tap, guard) = await CreateTappedPairAsync();
            using (client)
            using (session)
            using (guard)
            {
                await session.SendOpeningPresetAsync(CancellationToken.None);
                await LiveExchange.PumpUntilAsync(
                    client,
                    session,
                    () => session.Negotiation.IsEnabledByPeer((int)Options.TerminalType),
                    TimeSpan.FromSeconds(10));
                await LiveExchange.PumpForAsync(client, session, TimeSpan.FromSeconds(1));

                byte[] wire = tap.WrittenBytes;
                Wire.ContainsFrame(wire, Wire.Iac, Wire.Will, (byte)(int)Options.SuppressGoAhead).Should().BeTrue();
                Wire.ContainsFrame(wire, Wire.Iac, Wire.Will, (byte)(int)Options.TransmitBinary).Should().BeTrue();
                Wire.ContainsFrame(wire, Wire.Iac, Wire.Do, (byte)(int)Options.WindowSize).Should().BeTrue();
                Wire.ContainsFrame(wire, Wire.Iac, Wire.Do, (byte)(int)Options.CharacterSet).Should().BeTrue();
                session.Negotiation.IsEnabledByPeer((int)Options.TerminalType).Should().BeTrue();
                session.ClientWindowSize.Should().NotBeNull();
            }
        }

        [Fact]
        public async Task NegotiateLive_DefaultOptions_NoDoLinemodeNoTspeedNoOldEnvironNoMccp()
        {
            var (client, session, tap, guard) = await CreateTappedPairAsync();
            using (client)
            using (session)
            using (guard)
            {
                await session.SendOpeningPresetAsync(CancellationToken.None);
                await LiveExchange.PumpUntilAsync(
                    client,
                    session,
                    () => session.Negotiation.IsEnabledByPeer((int)Options.TerminalType),
                    TimeSpan.FromSeconds(10));
                await LiveExchange.PumpForAsync(client, session, TimeSpan.FromSeconds(1));

                byte[] wire = tap.WrittenBytes;
                Wire.ContainsFrame(wire, Wire.Iac, Wire.Do, (byte)(int)Options.LineMode).Should().BeFalse();
                Wire.ContainsFrame(wire, Wire.Iac, Wire.Do, (byte)(int)Options.TerminalSpeed).Should().BeFalse();
                Wire.ContainsFrame(wire, Wire.Iac, Wire.Do, (byte)(int)Options.OldEnvironment).Should().BeFalse();
                Wire.ContainsFrame(wire, Wire.Iac, Wire.Will, (byte)(int)Options.Mccp2).Should().BeFalse();
                Wire.ContainsFrame(wire, Wire.Iac, Wire.Will, (byte)(int)Options.Mccp3).Should().BeFalse();
            }
        }

        [Fact]
        public async Task NegotiateLive_RequestLinemode_SendsSingleDoLinemode()
        {
            var options = new TelnetServerOptions { RequestLinemode = true };
            var (client, session, tap, guard) = await CreateTappedPairAsync(options);
            using (client)
            using (session)
            using (guard)
            {
                await session.SendOpeningPresetAsync(CancellationToken.None);
                await LiveExchange.PumpUntilAsync(
                    client,
                    session,
                    () => session.Negotiation.IsEnabledByPeer((int)Options.TerminalType)
                        || session.Negotiation.WasRefusedByPeer((int)Options.TerminalType),
                    TimeSpan.FromSeconds(10));
                await LiveExchange.PumpForAsync(client, session, TimeSpan.FromSeconds(1));

                // Sent exactly once whether the client agrees or refuses;
                // either outcome proves the DO went out.
                Wire.CountFrames(tap.WrittenBytes, Wire.Iac, Wire.Do, (byte)(int)Options.LineMode).Should().Be(1);
            }
        }

        [Fact]
        public async Task NegotiateLive_PeerWillTtype_DrawsSbTtypeSendThenStops()
        {
            // One SEND per answer is reference behavior (telnetlib3
            // server.py request_ttype parity, pinned scripted by
            // TerminalTypeCollection_ResendsSendPerAnswer), so the live pin
            // is: the WILL draws a probe and the finite client chain ends
            // in a repeat that stops the exchange — the SEND count reaches
            // quiescence instead of growing without bound.
            var (client, session, tap, guard) = await CreateTappedPairAsync();
            using (client)
            using (session)
            using (guard)
            {
                await session.SendOpeningPresetAsync(CancellationToken.None);
                await LiveExchange.PumpUntilAsync(
                    client,
                    session,
                    () => session.Negotiation.IsEnabledByPeer((int)Options.TerminalType),
                    TimeSpan.FromSeconds(10));

                int sends = -1;
                int stableWindows = 0;
                var sw = Stopwatch.StartNew();
                while (sw.Elapsed < TimeSpan.FromSeconds(15))
                {
                    await LiveExchange.PumpForAsync(client, session, TimeSpan.FromMilliseconds(300));
                    int now = Wire.CountFrames(
                        tap.WrittenBytes, Wire.Iac, Wire.Sb, (byte)(int)Options.TerminalType);
                    if (now == sends)
                    {
                        stableWindows++;
                        if (stableWindows >= 2)
                        {
                            break;
                        }
                    }
                    else
                    {
                        sends = now;
                        stableWindows = 0;
                    }
                }

                stableWindows.Should().BeGreaterThanOrEqualTo(2, "the TTYPE repeat must stop the SEND cycle");
                sends.Should().BeGreaterThanOrEqualTo(1, "the peer WILL must draw a probe");
                session.ClientTerminalTypes.Should().NotBeEmpty();
            }
        }
    }
}
