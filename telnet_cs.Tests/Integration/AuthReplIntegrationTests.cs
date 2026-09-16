// Phase 4 authentication + REPL pins over a live pair: the session's
// AuthenticateAsync driven by a real Client answering prompts (the
// loopback LoginInterop conversation, hermetically), exhaustion/timeout/
// cancel shapes, and the REPL banner/prompt/quit plus the repl-line cap.
// The pump stands down for the whole credential exchange, so no settling
// games are needed around prompts.
namespace telnet_cs.Tests
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.Client;
    using telnet_cs.Server;
    using telnet_cs.Transport;

    public class AuthReplIntegrationTests
    {
        private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

        private static (Client Client, ServerSession Session, IDisposable Guard) CreateAuthPair(
            TelnetServerOptions? serverOptions = null)
        {
            var guard = GlobalStateGuard.SkipProactive(true);
            var (clientStream, serverStream) = InMemoryPipe.Create();
            var session = new ServerSession(
                serverStream, serverOptions ?? new TelnetServerOptions(), CancellationToken.None);
            var client = new Client(clientStream, CancellationToken.None);
            return (client, session, guard);
        }

        private static async Task AnswerLoginAsync(Client client, string user, string password)
        {
            await client.TerminatedReadAsync("login: ", Budget);
            await client.WriteLineAsync(user);
            await client.TerminatedReadAsync("Password: ", Budget);
            await client.WriteLineAsync(password);
        }

        [Fact]
        public async Task Authenticate_ValidCredentials_ReturnsTrue()
        {
            var (client, session, guard) = CreateAuthPair();
            using (client)
            using (session)
            using (guard)
            {
                var authTask = session.AuthenticateAsync(
                    (u, p) => Task.FromResult(u == "bob" && p == "s3cret"),
                    TimeSpan.FromSeconds(10));
                await AnswerLoginAsync(client, "bob", "s3cret");
                (await authTask).Should().BeTrue();

                await session.WriteLineAsync("welcome>");
                (await client.TerminatedReadAsync(">", Budget)).Should().Contain(">");
            }
        }

        [Fact]
        public async Task Authenticate_WrongPassword_ReturnsFalseWithoutPrompt()
        {
            var options = new TelnetServerOptions { MaxLoginAttempts = 1, LoginAttemptDelay = TimeSpan.Zero };
            var (client, session, guard) = CreateAuthPair(options);
            using (client)
            using (session)
            using (guard)
            {
                var authTask = session.AuthenticateAsync(
                    (u, p) => Task.FromResult(p == "s3cret"),
                    TimeSpan.FromSeconds(10));
                await AnswerLoginAsync(client, "bob", "wrong");
                (await authTask).Should().BeFalse();

                // No shell prompt ever arrives: the trailing read throws
                // TimeoutException instead of returning empty.
                Func<Task> act = () => client.TerminatedReadAsync(">", TimeSpan.FromMilliseconds(500));
                await act.Should().ThrowAsync<TimeoutException>();
            }
        }

        [Fact]
        public async Task Authenticate_Exhaustion_DisconnectsWithNotice()
        {
            var options = new TelnetServerOptions
            {
                MaxLoginAttempts = 1,
                LoginAttemptDelay = TimeSpan.Zero,
                DisconnectOnExhaustion = true,
            };
            var (client, session, guard) = CreateAuthPair(options);
            using (client)
            using (session)
            using (guard)
            {
                var authTask = session.AuthenticateAsync(
                    (u, p) => Task.FromResult(false),
                    TimeSpan.FromSeconds(10));
                await AnswerLoginAsync(client, "bob", "wrong");
                (await authTask).Should().BeFalse();

                (await client.TerminatedReadAsync("Login failed.", Budget)).Should().Contain("Login failed.");
                session.IsConnected.Should().BeFalse();
            }
        }

        [Fact]
        public async Task Authenticate_SilentPeer_FailsClosedOnTimeout()
        {
            var options = new TelnetServerOptions { LoginAttemptDelay = TimeSpan.Zero };
            var (client, session, guard) = CreateAuthPair(options);
            using (client)
            using (session)
            using (guard)
            {
                // Nobody answers the login prompt: a missed deadline fails
                // closed (false), never throws.
                (await session.AuthenticateAsync(
                    (u, p) => Task.FromResult(true),
                    TimeSpan.FromSeconds(2))).Should().BeFalse();
            }
        }

        [Fact]
        public async Task Authenticate_Cancelled_ThrowsOperationCanceled()
        {
            var (client, session, guard) = CreateAuthPair();
            using (client)
            using (session)
            using (guard)
            {
                using var cts = new CancellationTokenSource();
                var authTask = session.AuthenticateAsync(
                    (u, p) => Task.FromResult(true),
                    TimeSpan.FromSeconds(10),
                    cts.Token);
                await client.TerminatedReadAsync("login: ", Budget);
                cts.Cancel();
                Func<Task> act = () => authTask;
                await act.Should().ThrowAsync<OperationCanceledException>();
            }
        }

        [Fact]
        public async Task Repl_Quit_BannerPromptAndGoodbye()
        {
            var (client, session, guard) = CreateAuthPair();
            using (client)
            using (session)
            using (guard)
            {
                var replTask = ServerShells.RunReplAsync(session, CancellationToken.None);
                (await client.TerminatedReadAsync("Ready.", Budget)).Should().Contain("Ready.");
                await client.TerminatedReadAsync("tel:sh> ", Budget);
                await client.WriteLineAsync("quit");
                (await client.TerminatedReadAsync("Goodbye.", Budget)).Should().Contain("Goodbye.");
                await replTask;
            }
        }

        [Fact]
        public async Task Repl_LineTooLong_ClosesWithNotice()
        {
            var options = new TelnetServerOptions { MaxReplLineLength = 8 };
            var (client, session, guard) = CreateAuthPair(options);
            using (client)
            using (session)
            using (guard)
            {
                var replTask = ServerShells.RunReplAsync(session, CancellationToken.None);
                await client.TerminatedReadAsync("tel:sh> ", Budget);
                await client.WriteLineAsync(new string('x', 16));
                (await client.TerminatedReadAsync("Line too long.", Budget)).Should().Contain("Line too long.");
                await replTask;
                session.IsConnected.Should().BeFalse();
            }
        }
    }
}
