// Shared harness for Phase 4 live client<->server pins: a real Client
// against a real ServerSession over the public InMemoryPipe pair — no
// sockets, no ports. The client only answers negotiation while its read
// path is driven, so every exchange pumps both ends with bounded waits.
// Collectors (Request*Async) are not re-entrant with reads on the same
// session: while a session collector is outstanding, pump the client only.
// App text needs settled negotiation first: the session's background
// charset auto-request (default on, fired by BINARY agreement) polls
// through the same ReadAsync path, so a write racing it can land in the
// pending stash and surface on the *next* read. Pump SettleAsync (or turn
// RequestCharacterSet off) before client->session text exchanges.
namespace telnet_cs.Tests
{
    using System;
    using System.Diagnostics;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using telnet_cs.Client;
    using telnet_cs.Server;
    using telnet_cs.Transport;

    internal static class LiveExchange
    {
        private static readonly TimeSpan Slice = TimeSpan.FromMilliseconds(50);

        internal static async Task<(Client Client, ServerSession Session, IDisposable Guard)> CreatePairAsync(
            TelnetServerOptions? serverOptions = null,
            Action<TelnetClientOptions>? configureClient = null)
        {
            var guard = GlobalStateGuard.SkipProactive(true);
            var (clientStream, serverStream) = InMemoryPipe.Create();
            var session = new ServerSession(
                serverStream, serverOptions ?? new TelnetServerOptions(), CancellationToken.None);
            var client = await Client.CreateAsync(clientStream, TimeSpan.FromSeconds(30), CancellationToken.None).ConfigureAwait(false);
            configureClient?.Invoke(client.Settings);
            return (client, session, guard);
        }

        internal static void Dispose((Client Client, ServerSession Session, IDisposable Guard) pair)
        {
            pair.Client.Dispose();
            pair.Session.Dispose();
            pair.Guard.Dispose();
        }

        internal static async Task PumpUntilAsync(
            Client client,
            ServerSession session,
            Func<bool> done,
            TimeSpan budget,
            bool pumpSession = true)
        {
            var sw = Stopwatch.StartNew();
            while (!done() && sw.Elapsed < budget)
            {
                await client.ReadAsync(Slice);
                if (pumpSession)
                {
                    await session.ReadAsync(Slice);
                }
            }
        }

        /// <summary>
        /// Drives a scripted session until <paramref name="done"/> holds by
        /// pumping short reads on the test thread instead of sleeping a
        /// fixed span: the budget only bounds genuine hangs, it never
        /// synchronizes. Use before write-assertions — the background pump
        /// may own the pass that sends, so a fixed read timeout can expire
        /// (vacuous pass) while the send is still queued behind a starved
        /// thread pool, flaking under parallel load.
        /// </summary>
        internal static async Task PumpSessionUntilAsync(
            ServerSession session,
            Func<bool> done,
            TimeSpan budget)
        {
            var sw = Stopwatch.StartNew();
            while (!done() && sw.Elapsed < budget)
            {
                await session.ReadAsync(Slice);
            }
        }

        /// <summary>
        /// Drives a scripted session until the wire goes quiet — no
        /// inbound bytes waiting, no new writes, no new text across
        /// consecutive slices — and returns everything observed. Negative
        /// assertions (a frame never sent, text never inflated) need a
        /// quiet period to be meaningful; quiescence bounds it adaptively
        /// instead of sleeping a fixed span. The budget only bounds
        /// genuine hangs, it never synchronizes.
        /// </summary>
        internal static async Task<(string Text, byte[] Writes)> PumpSessionUntilQuiescentAsync(
            ServerSession session,
            ScriptedStream stream,
            TimeSpan budget,
            int stillSlices = 3)
        {
            var text = new StringBuilder();
            byte[] writes = [];
            int prevText = 0;
            int prevWrites = 0;
            int still = 0;
            var sw = Stopwatch.StartNew();
            while (still < stillSlices && sw.Elapsed < budget)
            {
                text.Append(await session.ReadAsync(Slice));
                byte[] now = stream.ByteWrites.SelectMany(w => w).ToArray();
                if (stream.Available == 0 && now.Length == prevWrites && text.Length == prevText)
                {
                    still++;
                }
                else
                {
                    still = 0;
                }

                prevText = text.Length;
                prevWrites = now.Length;
                writes = now;
            }

            return (text.ToString(), writes);
        }

        internal static async Task PumpForAsync(Client client, ServerSession session, TimeSpan duration)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < duration)
            {
                await client.ReadAsync(Slice);
                await session.ReadAsync(Slice);
            }
        }

        internal static async Task<T> CollectAsync<T>(Task<T> collector, Client client, TimeSpan budget)
        {
            var sw = Stopwatch.StartNew();
            while (!collector.IsCompleted && sw.Elapsed < budget)
            {
                await client.ReadAsync(Slice);
            }

            return await collector;
        }

        /// <summary>
        /// Pumps both ends until the background charset auto-request settles
        /// (the session records the accepted charset). Call this before
        /// client->session text exchanges so no background collector poll is
        /// still racing the read path (see the file header).
        /// </summary>
        internal static async Task SettleAsync(Client client, Server.ServerSession session, TimeSpan budget)
        {
            var sw = Stopwatch.StartNew();
            while (session.ClientCharset is null && sw.Elapsed < budget)
            {
                await client.ReadAsync(Slice);
                await session.ReadAsync(Slice);
            }
        }
    }
}
