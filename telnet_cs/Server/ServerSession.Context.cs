namespace telnet_cs.Server;

using System;
using System.Threading;
using System.Threading.Tasks;

public partial class ServerSession
{
    /// <summary>
    /// Gets the per-session runtime state: activity timestamps and
    /// character counters, the optional typescript recorder, and the open
    /// property bag.
    /// </summary>
    public TelnetSessionContext Context { get; } = new();

    /// <summary>
    /// Gets or sets the effective idle timeout for this session. Initialised from
    /// <see cref="TelnetServerOptions.IdleTimeout"/>. Setting it overrides the
    /// timeout per session and restarts the countdown from now (the reference
    /// schedules <c>on_timeout</c> via <c>call_later</c> on every
    /// <c>set_timeout</c>, including the per-<c>data_received</c> restart;
    /// the analogue of the reference <c>set_timeout() /
    /// get_extra_info("timeout")</c>). <see
    /// cref="System.Threading.Timeout.InfiniteTimeSpan"/> (or any non-positive span)
    /// disables the timeout; the reference uses <c>0</c> for the same.
    /// </summary>
    public TimeSpan Timeout
    {
        get;
        set
        {
            // Arming a timeout never marks peer activity (the reference
            // set_timeout only reschedules on_timeout): otherwise extending
            // the deadline would itself look like the peer talking.
            field = value;
            RestartIdleTimer();
        }
    }

    /// <summary>
    /// Gets whether the session has been idle past <see cref="Timeout"/>
    /// (always <c>false</c> when the timeout is disabled). Latched on fire: the
    /// <c>"Timeout."</c> notice itself counts as activity, so without the
    /// latch the flag would read <c>false</c> right after the kill it
    /// reports.
    /// </summary>
    public bool IsIdleTimedOut =>
      idleTimedOut ||
      (Timeout != System.Threading.Timeout.InfiniteTimeSpan &&
        Timeout > TimeSpan.Zero &&
        Context.Idle >= Timeout);

    private Timer? idleTimer;
    private bool idleTimedOut;

    private void StartIdleTimer()
    {
        var timeout = Timeout;
        if (timeout == System.Threading.Timeout.InfiniteTimeSpan || timeout <= TimeSpan.Zero)
        {
            return;
        }

        // Short timeouts (tests, tight gates) need a tight period to fire
        // promptly; long ones get a lazy period to avoid wakeups.
        var period = timeout <= TimeSpan.FromSeconds(5) ? TimeSpan.FromMilliseconds(50) : TimeSpan.FromSeconds(5);
        idleTimer = new Timer(static state =>
        {
            if (state is ServerSession session)
            {
                _ = session.OnIdleTimeoutAsync();
            }
        }, this, period, period);
    }

    private void RestartIdleTimer()
    {
        StopIdleTimer();
        StartIdleTimer();
    }

    private void StopIdleTimer()
    {
        var timer = Interlocked.Exchange(ref idleTimer, null);
        if (timer is not null)
        {
            timer.Change(System.Threading.Timeout.InfiniteTimeSpan, System.Threading.Timeout.InfiniteTimeSpan);
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
            // Best effort: the reference writes "\r\nTimeout.\r\n"
            // (leading CRLF) before closing.
            await WriteAsync("\r\nTimeout.\r\n", CancellationToken.None).ConfigureAwait(false);
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
            // An unnegotiated split accept abandoned here hands its
            // admission reservation back (claimed sessions no-op).
            ReleasePendingNegotiation();
            ShutdownPump();
            StopHandshakeTimer();
            StopIdleTimer();
            mccp2Filter?.Dispose();
            mccp2Filter = null;
        }

        base.Dispose(disposing);
    }
}
