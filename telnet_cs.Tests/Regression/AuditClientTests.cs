namespace telnet_cs.Tests
{
    using System;
    using System.Linq;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Client;
    using telnet_cs.Protocol;

    public class AuditClientTests
    {
        [Fact]
        public async Task SendGa_Disconnected_ReturnsFalse()
        {
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var sut = new Client(stream, TimeSpan.FromMilliseconds(50), default);
                stream.Close();
                (await sut.SendGaAsync()).Should().BeFalse();
            }
        }

        [Fact]
        public async Task WaitForNegotiation_PreservesApplicationData()
        {
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream("HI");
                using var sut = new Client(stream, TimeSpan.FromMilliseconds(50), default);
                (await sut.WaitForNegotiationAsync(_ => false, TimeSpan.FromMilliseconds(200))).Should().BeFalse();
                (await sut.ReadAsync(TimeSpan.FromMilliseconds(100))).Should().Be("HI");
            }
        }

        [Fact]
        public async Task WriteLine_NullCommand_ThrowsArgumentNull()
        {
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var sut = new Client(stream, new CancellationToken());
                Func<Task> write = () => sut.WriteLineAsync(null!);
                await write.Should().ThrowAsync<ArgumentNullException>();
            }
        }

        [Fact]
        public async Task TerminatedRead_EmptyTerminator_ThrowsArgumentException()
        {
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream("data");
                using var sut = new Client(stream, new CancellationToken());
                Func<Task<string>> read = () => sut.TerminatedReadAsync(string.Empty, TimeSpan.FromMilliseconds(100));
                await read.Should().ThrowAsync<ArgumentException>();
            }
        }

        [Fact]
        public async Task TerminatedRead_EmptyElement_ThrowsArgumentException()
        {
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream("OK");
                using var sut = new Client(stream, new CancellationToken());
                Func<Task<string>> read = () => sut.TerminatedReadAsync(new[] { "OK", string.Empty }, TimeSpan.FromMilliseconds(100));
                await read.Should().ThrowAsync<ArgumentException>();
            }
        }

        [Fact]
        public async Task TerminatedRead_NullRegexElement_ThrowsArgumentException()
        {
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream("OK");
                using var sut = new Client(stream, new CancellationToken());
                Func<Task<string>> read = () => sut.TerminatedReadAsync(new Regex[] { null! }, TimeSpan.FromMilliseconds(100));
                await read.Should().ThrowAsync<ArgumentException>();
            }
        }

        [Fact]
        public async Task TryLogin_NullUser_ThrowsArgumentNull()
        {
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var sut = new Client(stream, new CancellationToken());
                Func<Task<bool>> login = () => sut.TryLoginAsync(null!, "secret", 100);
                await login.Should().ThrowAsync<ArgumentNullException>();
            }
        }

        [Fact]
        public void ApplyOptions_Certificates_AreClonedNotShared()
        {
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var sut = new Client(stream, new CancellationToken());
                var options = new TelnetClientOptions
                {
                    TlsClientCertificates = new System.Security.Cryptography.X509Certificates.X509CertificateCollection(),
                };
                sut.ApplyOptions(options);
                sut.Settings.TlsClientCertificates.Should().NotBeNull();
                sut.Settings.TlsClientCertificates.Should().NotBeSameAs(options.TlsClientCertificates);
            }
        }

        [Fact]
        public async Task Write_AfterExternalCancel_StillSends()
        {
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var cts = new CancellationTokenSource();
                using var sut = new Client(stream, cts.Token);
                cts.Cancel();
                await sut.WriteAsync("hello");
                stream.StringWrites.Should().ContainSingle().Which.Should().Be("hello");
            }
        }
    }
}
