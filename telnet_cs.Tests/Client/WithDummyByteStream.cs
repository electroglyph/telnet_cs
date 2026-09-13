namespace telnet_cs.Tests
{
    using FluentAssertions;
    using Xunit;
    using System;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using telnet_cs.Client;

    public class WithDummyByteStream
    {
        private const int timeoutMs = 500;

        [Fact]
        public void ShouldConnect()
        {
            using (var stream = new DummyByteStream())
            {
                using (var client = new Client(stream, new CancellationToken()))
                {
                    client.IsConnected.Should().Be(true);
                }
            }
        }

        [Fact(Timeout = 2000)]
        public async Task ShouldTerminateWithAColon()
        {
            using (GlobalStateGuard.SkipProactive(false))
            {
                using (var stream = new DummyByteStream())
                {
                    using (var client = new Client(stream, TimeSpan.FromSeconds(30), new CancellationToken(), [], skipProactiveNegotiation: false))
                    {
                        client.IsConnected.Should().Be(true);
                        (await client.TerminatedReadAsync(":", TimeSpan.FromMilliseconds(timeoutMs)))
                          .Should().EndWith(":");
                    }
                }
            }
        }

        [Fact(Timeout = 2000)]
        public async Task ShouldBePromptingForAccount()
        {
            using (GlobalStateGuard.SkipProactive(false))
            {
                using (var stream = new DummyByteStream())
                {
                    using (var client = new Client(stream, TimeSpan.FromSeconds(30), new CancellationToken(), [], skipProactiveNegotiation: false))
                    {
                        client.IsConnected.Should().Be(true);
                        var s = await client.TerminatedReadAsync("Account:", TimeSpan.FromMilliseconds(timeoutMs));

                        s.Should().Contain("Account:");
                    }
                }
            }
        }

        [Fact(Timeout = 2000)]
        public async Task ShouldBePromptingForPassword()
        {
            using (GlobalStateGuard.SkipProactive(false))
            {
                using (var stream = new DummyByteStream())
                {
                    using (var client = new Client(stream, TimeSpan.FromSeconds(30), new CancellationToken(), [], skipProactiveNegotiation: false))
                    {
                        client.IsConnected.Should().Be(true);
                        var s = await client.TerminatedReadAsync("Account:", TimeSpan.FromMilliseconds(timeoutMs));
                        s.Should().Contain("Account:");
                        await client.WriteLineAsync("username");
                        s = await client.TerminatedReadAsync("Password:", TimeSpan.FromMilliseconds(timeoutMs));
                    }
                }
            }
        }

        [Fact(Timeout = 3000)]
        public async Task ShouldPromptForInput()
        {
            using (GlobalStateGuard.SkipProactive(false))
            {
                using (var stream = new DummyByteStream())
                {
                    using (var client = new Client(stream, TimeSpan.FromSeconds(30), new CancellationToken(), [], skipProactiveNegotiation: false))
                    {
                        client.IsConnected.Should().Be(true);
                        await client.TerminatedReadAsync("Account:", TimeSpan.FromMilliseconds(timeoutMs));
                        await client.WriteLineAsync("username");
                        await client.TerminatedReadAsync("Password:", TimeSpan.FromMilliseconds(timeoutMs));
                        await client.WriteLineAsync("password");
                        await client.TerminatedReadAsync(">", TimeSpan.FromMilliseconds(timeoutMs));
                    }
                }
            }
        }

        [Fact(Timeout = 5000)]
        public async Task ShouldRespondWithWan2Info()
        {
            using (GlobalStateGuard.SkipProactive(false))
            {
                using (var stream = new DummyByteStream())
                {
                    using (var client = new Client(stream, TimeSpan.FromSeconds(30), new CancellationToken(), [], skipProactiveNegotiation: false))
                    {
                        client.IsConnected.Should().Be(true);
                        await client.TerminatedReadAsync("Account:", TimeSpan.FromMilliseconds(timeoutMs));
                        await client.WriteLineAsync("username");
                        await client.TerminatedReadAsync("Password:", TimeSpan.FromMilliseconds(timeoutMs));
                        await client.WriteLineAsync("password");
                        // Consume the post-login "Command >" prompt first: the
                        // statistics read below must see the command output, not
                        // the tail of the login exchange.
                        await client.TerminatedReadAsync(">", TimeSpan.FromMilliseconds(timeoutMs));
                        await client.WriteLineAsync("show statistic wan2");
                        var s = await client.TerminatedReadAsync(">", TimeSpan.FromMilliseconds(timeoutMs));
                        s.Should().Contain(">");
                        s.Should().Contain("WAN2");
                    }
                }
            }
        }

        [Fact(Timeout = 5000)]
        public async Task ShouldLogin()
        {
            using (GlobalStateGuard.SkipProactive(false))
            {
                using (var stream = new DummyByteStream())
                {
                    using (var client = new Client(stream, TimeSpan.FromSeconds(30), new CancellationToken(), [], skipProactiveNegotiation: false))
                    {
                        client.IsConnected.Should().Be(true);
                        await client.TerminatedReadAsync("Account:", TimeSpan.FromMilliseconds(timeoutMs));
                        await client.WriteLineAsync("username");
                        var s = await client.TerminatedReadAsync("Password:", TimeSpan.FromMilliseconds(timeoutMs));
                        s.Should().Contain("Password:");
                        await client.WriteLineAsync("password");
                    }
                }
            }
        }

        [Fact]
        public async Task ShouldRespondWithWan2InfoRegexTerminated()
        {
            using (GlobalStateGuard.SkipProactive(false))
            {
                using (var stream = new DummyByteStream())
                {
                    using (var client = new Client(stream, TimeSpan.FromSeconds(30), new CancellationToken(), [], skipProactiveNegotiation: false))
                    {
                        client.IsConnected.Should().Be(true);
                        await client.TerminatedReadAsync("Account:", TimeSpan.FromMilliseconds(timeoutMs));
                        await client.WriteLineAsync("username");
                        await client.TerminatedReadAsync("Password:", TimeSpan.FromMilliseconds(timeoutMs));
                        await client.WriteLineAsync("password");
                        await client.TerminatedReadAsync(">", TimeSpan.FromMilliseconds(timeoutMs));
                        await client.WriteLineAsync("show statistic wan2");
                        var s = await client.TerminatedReadAsync(new Regex(".*>$"), TimeSpan.FromMilliseconds(timeoutMs));
                        s.Should().Contain(">");
                        s.Should().Contain("WAN2");
                    }
                }
            }
        }
    }
}
