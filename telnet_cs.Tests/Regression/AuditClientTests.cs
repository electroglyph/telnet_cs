namespace telnet_cs.Tests
{
    using System;
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
            // The method documents true as "the GA byte pair was sent"; a
            // closed stream sends nothing (RFC 854 defines GA as the IAC
            // GA turn-taking signal, with no return-value concept, so the
            // method's own contract is the source of truth here).
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
            // Waiting on option state must not consume stream data: the
            // stack's never-drop-bytes contract keeps pumped text in
            // PendingText, matching telnetlib3 where waiters observe option
            // state while application data stays buffered in the reader.
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
            // string.Format renders null as "", which would emit a bare
            // CRLF for invalid input; WriteAsync(string) already throws on
            // null, and public members must fail fast (CA1062) rather than
            // send protocol bytes for a null argument.
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
            // An empty terminator is meaningless (String.IndexOf("") is 0,
            // yet the located-check never matches ""), so fail fast instead
            // of spinning to timeout and returning "". telnetlib3's
            // readuntil likewise raises ValueError on an empty separator.
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
            // An empty element cuts at 0 (IndexOf("") == 0), returning ""
            // while stashing the whole input — silent data corruption, so
            // every element must be validated, not just the collection.
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
            // A null element currently times out and then throws
            // NullReferenceException from the cut loop; public APIs must
            // name the bad argument with ArgumentException instead.
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
            // Whether a login prompt arrives must not decide whether a null
            // argument is detected: fail fast per .NET validation
            // convention instead of waiting out the prompt timeout.
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
            // ApplyOptions documents "collections are re-seated, not
            // shared" and re-seats the other collections; the mutable
            // X509CertificateCollection must be defensively copied the same
            // way so later caller mutation cannot change client behavior.
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
        public async Task Write_AfterExternalCancel_IsNoop()
        {
            // Reassessment: a cancelled token must prevent work, never
            // perform it (.NET cancellation convention; telnetlib3's writer
            // likewise no-ops when the connection is closed). The silent
            // no-op matches this stack's own cancelled-read and
            // after-close patterns, so it is pinned, not fixed. The
            // remaining design question is whether one external cancel
            // should latch the client forever or only affect current calls.
            using (GlobalStateGuard.SkipProactive(true))
            {
                using var stream = new ScriptedStream();
                using var cts = new CancellationTokenSource();
                using var sut = new Client(stream, cts.Token);
                cts.Cancel();
                await sut.WriteAsync("hello");
                stream.StringWrites.Should().BeEmpty();
            }
        }
    }
}
