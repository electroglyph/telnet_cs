namespace telnet_cs.Server
{
    using System;
    using System.Runtime.ExceptionServices;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Background inbound processing for <see cref="ServerSession"/> (the
    /// reference <c>data_received</c> path): a pump started by the constructor
    /// drains the stream through the shared wire pass, so negotiation is
    /// answered and text buffered even when the caller never reads. The pump
    /// holds the same <c>ReadRateLimit</c> as <c>ReadAsync</c>, so at most one
    /// wire pass runs at a time and all session state stays single-flight.
    /// </summary>
    public partial class ServerSession
    {
        /// <summary>
        /// One pump wire pass when no data is expected: short enough to
        /// answer negotiation promptly, long enough to avoid spin noise.
        /// </summary>
        private static readonly TimeSpan PumpReadSlice = TimeSpan.FromMilliseconds(50);

        /// <summary>
        /// How long a session must go without an explicit read before the
        /// background pump starts passing: tests and live request/response
        /// traffic act within milliseconds of construction, so they always
        /// see request-driven behavior (as if there were no pump); a session
        /// nobody reads — the reference data_received case — still gets full
        /// background processing once quiet past this threshold.
        /// </summary>
        private static readonly TimeSpan PumpQuietThreshold = TimeSpan.FromMilliseconds(50);

        /// <summary>
        /// Quietness re-check cadence while standing down.
        /// </summary>
        private static readonly TimeSpan PumpQuietRecheck = TimeSpan.FromMilliseconds(20);

        private readonly Lock pumpLock = new();
        private string pumpBufferedText = string.Empty;
        private volatile bool pumpShutdown;

        // Last explicit ReadAsync entry (ticks UTC): the pump stays dormant
        // while the caller drives the wire itself, so background passes never
        // race an active reader. Initialised at construction, so the pump
        // also stays out of the way of connect-time request bursts.
        private long lastExplicitReadTicks = DateTime.UtcNow.Ticks;

        // A wire error the pump swallowed (malformed SLC triplets: the
        // reference raises, and so does an explicit read). The next
        // ReadAsync with no other data rethrows it instead of returning "",
        // so background consumption never hides a wire error.
        private ExceptionDispatchInfo? pumpWireError;

        /// <summary>
        /// Drains text stashed by terminated reads plus anything the
        /// background pump buffered, leaving both empty.
        /// </summary>
        private string TakePendingText()
        {
            string pending = PendingText;
            PendingText = string.Empty;
            lock (pumpLock)
            {
                if (pumpBufferedText.Length != 0)
                {
                    pending += pumpBufferedText;
                    pumpBufferedText = string.Empty;
                }
            }

            return pending;
        }

        /// <summary>
        /// Closes the session transport (the reference shell's
        /// <c>writer.close()</c> on <c>quit</c>): the stream closes (so
        /// <c>IsConnected</c> reads false) and pending reads cancel. The
        /// caller still owns (and disposes) the session.
        /// </summary>
        public void Close()
        {
            pumpShutdown = true;
            ByteStream.Close();
            CancelPendingReads();
        }

        /// <summary>
        /// Closes the session for server shutdown (the reference
        /// <c>Server.close</c> closing each protocol transport): the stream
        /// closes (so <c>IsConnected</c> reads false) and pending reads
        /// cancel. The caller still owns (and disposes) the session.
        /// </summary>
        internal void CloseForServerStop() => Close();

        private void ShutdownPump()
        {
            pumpShutdown = true;
        }

        private async Task PumpInboundAsync()
        {
#pragma warning disable CA1031 // Do not catch general exception types
            try
            {
                int idleIterations = 0;
                // The first pass is exempt from the quiet threshold: it can
                // only see bytes enqueued before construction returned (the
                // caller cannot act sooner), so answering them immediately is
                // deterministic — and a session nobody ever reads gets its
                // connect-time negotiation answered without waiting out the
                // threshold.
                bool firstPass = true;
                while (!pumpShutdown && !InternalCancellation.IsCancellationRequested && ByteStream.Connected)
                {
                    if (authPumpStanddown)
                    {
                        idleIterations = 0;
                        try
                        {
                            await Task.Delay(PumpQuietRecheck, InternalCancellation.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }

                        continue;
                    }

                    var idleFor = DateTime.UtcNow - new DateTime(Interlocked.Read(ref lastExplicitReadTicks), DateTimeKind.Utc);
                    if (!firstPass && idleFor < PumpQuietThreshold)
                    {
                        // Explicit reads are (or were just) active: stand
                        // down; their own passes flush deferred negotiation too.
                        idleIterations = 0;
                        try
                        {
                            await Task.Delay(PumpQuietRecheck, InternalCancellation.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }

                        continue;
                    }

                    firstPass = false;
                    bool acquired = false;
                    try
                    {
                        // Never block a caller read: skip this pass when one
                        // holds the wire.
                        acquired = await ReadRateLimit.WaitAsync(0).ConfigureAwait(false);
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }

                    if (acquired)
                    {
                        bool releaseFailed = false;
                        try
                        {
                            string text = await ReadWireOnceAsync(PumpReadSlice, InternalCancellation.Token).ConfigureAwait(false);
                            if (text.Length != 0)
                            {
                                lock (pumpLock)
                                {
                                    pumpBufferedText += text;
                                }

                                CheckBufferedTextCap();
                                idleIterations = 0;
                            }
                            else
                            {
                                idleIterations++;
                            }
                        }
                        catch (Exception ex)
                        {
                            // A dead peer or disposed stream ends the pass,
                            // never the pump: the loop condition re-checks. A
                            // wire error (malformed SLC) is stashed for the
                            // next ReadAsync to rethrow.
                            WriteLog("Inbound pump pass failed: " + ex.Message);
                            idleIterations++;
                            if (ex is not OperationCanceledException
                                && ex is not System.Net.Sockets.SocketException
                                && ex is not ObjectDisposedException)
                            {
                                lock (pumpLock)
                                {
                                    pumpWireError ??= ExceptionDispatchInfo.Capture(ex);
                                }
                            }
                        }
                        finally
                        {
                            try
                            {
                                ReadRateLimit.Release();
                            }
                            catch (ObjectDisposedException)
                            {
                                releaseFailed = true;
                            }
                        }

                        if (releaseFailed)
                        {
                            return;
                        }
                    }

                    // Back off when quiet: the first passes run hot so
                    // connect-time negotiation answers promptly, then the
                    // cadence relaxes (explicit reads always bypass this).
                    int delayMs = idleIterations switch
                    {
                        < 4 => 20,
                        < 12 => 100,
                        _ => 500,
                    };
                    try
                    {
                        await Task.Delay(delayMs, InternalCancellation.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex.Message);
            }
#pragma warning restore CA1031 // Do not catch general exception types
        }
    }
}
