// Phase 4 TLS + MCCP loopback pin: over a real TLS handshake the MCCP
// offers are refused on both sides (CRIME/BREACH) while text flows as
// plaintext-inside-TLS. The refusal halves are pinned scripted
// (TlsActive_RefusesMccp, OfferMccp2/3_Tls_SendsNoWill); only the live
// handshake + plaintext flowing is new.
namespace telnet_cs.Tests
{
    using System;
    using System.Security.Cryptography;
    using System.Security.Cryptography.X509Certificates;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Client;
    using telnet_cs.Protocol;
    using telnet_cs.Server;

    public class TlsMccpIntegrationTests
    {
        private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

        private static X509Certificate2 CreateSelfSignedCert()
        {
            using var key = ECDsa.Create();
            return new CertificateRequest(
                "CN=localhost", key, HashAlgorithmName.SHA256).CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        }

        [Fact]
        public async Task TlsMccp_OfferedOverTls_RefusedAndPlaintextFlows()
        {
            using var cert = CreateSelfSignedCert();
            var serverOptions = new TelnetServerOptions
            {
                ServerCertificate = cert,
                OfferMccp2 = true,
                OfferMccp3 = true,
            };
            using var server = new TelnetServer(0, serverOptions);
            server.Start();
            var acceptTask = server.AcceptSessionAsync(CancellationToken.None);
            using var client = await Client.ConnectAsync(
                "127.0.0.1",
                server.Port,
                new TelnetClientOptions
                {
                    UseTls = true,
                    TlsValidationCallback = (_, _, _, _) => true,
                },
                CancellationToken.None,
                TimeSpan.FromSeconds(10));
            using var session = await acceptTask;

            session.IsTls.Should().BeTrue();

            await session.SendOpeningPresetAsync(CancellationToken.None);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(3))
            {
                await client.ReadAsync(TimeSpan.FromMilliseconds(50));
                await session.ReadAsync(TimeSpan.FromMilliseconds(50));
            }

            // Refused over TLS on both sides: no agreement either
            // direction, despite the offers.
            session.Negotiation.IsEnabledByUs((int)Options.Mccp2).Should().BeFalse();
            session.Negotiation.IsEnabledByUs((int)Options.Mccp3).Should().BeFalse();
            client.Negotiation.IsEnabledByPeer((int)Options.Mccp2).Should().BeFalse();
            client.Negotiation.IsEnabledByPeer((int)Options.Mccp3).Should().BeFalse();

            // ... while text flows fine inside the TLS record layer.
            await session.WriteAsync("secure-down", CancellationToken.None);
            (await client.ReadAsync(TimeSpan.FromSeconds(5))).Should().Be("secure-down");
            await client.WriteAsync("secure-up", CancellationToken.None);
            (await session.ReadAsync(TimeSpan.FromSeconds(5))).Should().Be("secure-up");
        }
    }
}
