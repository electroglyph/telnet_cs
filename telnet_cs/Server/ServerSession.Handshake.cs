namespace telnet_cs.Server;

using System;
using System.Threading;
using System.Threading.Tasks;

public partial class ServerSession
{
    private DateTime handshakeDeadlineUtc = DateTime.MaxValue;
    private Timer? handshakeTimer;
    private int handshakeCompleted;
    private string handshakeEndpoint = "unknown";

    private void StartHandshakeTimer()
    {
        TimeSpan timeout;
        try
        {
            timeout = Settings.HandshakeTimeout;
        }
        catch
        {
            timeout = TimeSpan.FromSeconds(10);
        }

        if (timeout == System.Threading.Timeout.InfiniteTimeSpan || timeout <= TimeSpan.Zero)
        {
            handshakeDeadlineUtc = DateTime.MaxValue;
            return;
        }

        handshakeDeadlineUtc = DateTime.UtcNow.Add(timeout);
        try
        {
            handshakeEndpoint = RemoteEndPoint ?? "unknown";
        }
        catch
        {
            handshakeEndpoint = "unknown";
        }

        ArmHandshakeTimerLocked(handshakeDeadlineUtc - DateTime.UtcNow);
    }

    internal void ResetHandshakeDeadline(DateTime deadlineUtc, string endpoint)
    {
        handshakeDeadlineUtc = deadlineUtc;
        handshakeEndpoint = string.IsNullOrEmpty(endpoint) ? "unknown" : endpoint;
        if (Volatile.Read(ref handshakeCompleted) == 1)
        {
            return;
        }

        if (deadlineUtc == DateTime.MaxValue)
        {
            StopHandshakeTimer();
            return;
        }

        ArmHandshakeTimerLocked(deadlineUtc - DateTime.UtcNow);
    }

    private void ArmHandshakeTimerLocked(TimeSpan remaining)
    {
        StopHandshakeTimer();
        if (Volatile.Read(ref handshakeCompleted) == 1)
        {
            return;
        }

        if (remaining <= TimeSpan.Zero)
        {
            remaining = TimeSpan.FromMilliseconds(1);
        }

        handshakeTimer = new Timer(static state =>
        {
            if (state is ServerSession session)
            {
                _ = session.OnHandshakeTimeoutAsync();
            }
        }, this, remaining, System.Threading.Timeout.InfiniteTimeSpan);
    }

    private void StopHandshakeTimer()
    {
        var timer = Interlocked.Exchange(ref handshakeTimer, null);
        if (timer is not null)
        {
            try
            {
                timer.Change(System.Threading.Timeout.InfiniteTimeSpan, System.Threading.Timeout.InfiniteTimeSpan);
            }
            catch
            {
            }

            timer.Dispose();
        }
    }

    private void MarkHandshakeComplete()
    {
        if (Interlocked.Exchange(ref handshakeCompleted, 1) == 0)
        {
            StopHandshakeTimer();
        }
    }

    private async Task OnHandshakeTimeoutAsync()
    {
        if (Volatile.Read(ref handshakeCompleted) == 1 || !IsConnected)
        {
            return;
        }

        if (DateTime.UtcNow < handshakeDeadlineUtc)
        {
            TimeSpan left = handshakeDeadlineUtc - DateTime.UtcNow;
            if (left > TimeSpan.Zero)
            {
                ArmHandshakeTimerLocked(left);
                return;
            }
        }

        if (Interlocked.Exchange(ref handshakeCompleted, 1) != 0)
        {
            return;
        }

        StopHandshakeTimer();
        string endpoint = handshakeEndpoint;
        try
        {
            endpoint = RemoteEndPoint ?? handshakeEndpoint;
        }
        catch
        {
        }

        if (string.IsNullOrEmpty(endpoint))
        {
            endpoint = "unknown";
        }

        WriteLog($"handshake-timeout: endpoint={endpoint}");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, InternalCancellation.Token);
            if (WriteStream.Connected && !linked.Token.IsCancellationRequested)
            {
                await SendRateLimit.WaitAsync(linked.Token).ConfigureAwait(false);
                try
                {
                    await WriteStream.WriteAsync("\r\nHandshake timeout.\r\n", linked.Token).ConfigureAwait(false);
                    Context.NoteWritten("\r\nHandshake timeout.\r\n".Length);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(ex.Message);
                }
                finally
                {
                    try { SendRateLimit.Release(); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex.Message);
        }

        try
        {
            ByteStream.Close();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex.Message);
        }

        CancelPendingReads();
    }
}
