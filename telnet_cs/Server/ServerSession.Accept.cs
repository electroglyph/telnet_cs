namespace telnet_cs.Server
{
    public partial class ServerSession
    {
        // Accept-split state (see TelnetServer.AcceptTcpAsync): admission
        // stamps the owning server plus its reservation here, and
        // TelnetServer.NegotiateAsync claims it exactly once. Disposing an
        // unnegotiated session releases the reservation back to the server,
        // so an abandoned split accept never leaks capacity.
        private TelnetServer? pendingServer;
        private PendingNegotiation? pendingNegotiation;
        private System.Action? releasePendingReservation;

        internal void SetPendingNegotiation(TelnetServer server, PendingNegotiation pending, System.Action release)
        {
            System.ArgumentNullException.ThrowIfNull(server);
            System.ArgumentNullException.ThrowIfNull(release);
            pendingServer = server;
            pendingNegotiation = pending;
            System.Threading.Interlocked.Exchange(ref releasePendingReservation, release);
        }

        internal bool TryClaimPendingNegotiation(TelnetServer server, out PendingNegotiation pending)
        {
            pending = default;
            if (!ReferenceEquals(pendingServer, server) || pendingNegotiation is null)
            {
                return false;
            }

            // The release hook doubles as the once-only claim token: a
            // concurrent Dispose consumes it first, and the loser observes
            // null and refuses instead of double-releasing the reservation.
            if (System.Threading.Interlocked.Exchange(ref releasePendingReservation, null) is null)
            {
                return false;
            }

            pending = pendingNegotiation.Value;
            pendingServer = null;
            pendingNegotiation = null;
            return true;
        }

        private void ReleasePendingNegotiation()
        {
#pragma warning disable CA1031 // Do not catch general exception types
            try
            {
                System.Threading.Interlocked.Exchange(ref releasePendingReservation, null)?.Invoke();
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex.Message);
            }
            finally
            {
                pendingServer = null;
                pendingNegotiation = null;
            }
#pragma warning restore CA1031
        }
    }
}
