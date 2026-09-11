namespace telnet_cs.Server
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using telnet_cs.Protocol;

    public partial class ServerSession
    {
        /// <summary>
        /// Gets the per-session runtime state: activity timestamps and
        /// character counters, the optional typescript recorder, and the open
        /// property bag.
        /// </summary>
        public TelnetSessionContext Context { get; } = new();

        /// <summary>
        /// Gets whether the session has been idle past
        /// <see cref="TelnetServerOptions.IdleTimeout"/> (always <c>false</c>
        /// when the timeout is disabled). Latched on fire: the
        /// <c>"Timeout."</c> notice itself counts as activity, so without the
        /// latch the flag would read <c>false</c> right after the kill it
        /// reports.
        /// </summary>
        public bool IsIdleTimedOut =>
          idleTimedOut ||
          (Settings.IdleTimeout != Timeout.InfiniteTimeSpan &&
           Settings.IdleTimeout > TimeSpan.Zero &&
           Context.Idle >= Settings.IdleTimeout);

        private Timer? idleTimer;
        private bool idleTimedOut;

        private void StartIdleTimer()
        {
            var timeout = Settings.IdleTimeout;
            if (timeout == Timeout.InfiniteTimeSpan || timeout <= TimeSpan.Zero)
            {
                return;
            }

            // Short timeouts (tests, tight gates) need a tight period to fire
            // promptly; long ones get a lazy period to avoid wakeups.
            var period = timeout <= TimeSpan.FromSeconds(5) ? TimeSpan.FromMilliseconds(50) : TimeSpan.FromSeconds(5);
            idleTimer = new Timer(static state => _ = ((ServerSession)state!).OnIdleTimeoutAsync(), this, period, period);
        }

        private void StopIdleTimer()
        {
            var timer = Interlocked.Exchange(ref idleTimer, null);
            if (timer is not null)
            {
                timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                timer.Dispose();
            }
        }

        private async Task OnIdleTimeoutAsync()
        {
            if (!IsIdleTimedOut || !IsConnected)
            {
                return;
            }

            StopIdleTimer();
            idleTimedOut = true;
#pragma warning disable CA1031 // Do not catch general exception types
            try
            {
                // Best effort: the reference writes "Timeout." before closing.
                await WriteAsync($"Timeout.{LineFeed.Rfc854}", CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex.Message);
            }
#pragma warning restore CA1031 // Do not catch general exception types
            ByteStream.Close();
            CancelPendingReads();
        }

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                StopIdleTimer();
            }

            base.Dispose(disposing);
        }
    }
}
