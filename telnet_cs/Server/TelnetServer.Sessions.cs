namespace telnet_cs.Server;

using System;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using telnet_cs.Transport;

/// <summary>
/// Session bookkeeping: the tracked-session record, admission reservations and the status logger. Split from <see cref="TelnetServer"/>; wire behavior is unchanged.
/// </summary>
public partial class TelnetServer
{
    /// <summary>
    /// One tracked session for the status logger: a weak reference (the
    /// server must not keep caller-owned sessions alive) plus the accept
    /// snapshot and the last-logged counters.
    /// </summary>
    private sealed class SessionRecord
    {
        public SessionRecord(ServerSession session, string endpoint, bool isTls, string ipKey)
        {
            Session = new WeakReference<ServerSession>(session);
            Endpoint = endpoint;
            IsTls = isTls;
            IpKey = ipKey;
        }

        public WeakReference<ServerSession> Session { get; }

        public string Endpoint { get; }

        public bool IsTls { get; }

        public string IpKey { get; }

        public long LastReceived { get; set; }

        public long LastSent { get; set; }
    }

    private void PruneLocked()
    {
        for (int i = sessions.Count - 1; i >= 0; i--)
        {
            if (!sessions[i].Session.TryGetTarget(out var session) || !session.IsConnected)
            {
                sessions.RemoveAt(i);
            }
        }
    }

    private void ReleaseReservation(string ipKey)
    {
        lock (statusLock)
        {
            if (reservations > 0)
            {
                reservations--;
            }

            if (reservationsPerIp.TryGetValue(ipKey, out int current))
            {
                if (current <= 1)
                {
                    reservationsPerIp.Remove(ipKey);
                }
                else
                {
                    reservationsPerIp[ipKey] = current - 1;
                }
            }
        }
    }

    private void ConvertReservation(string ipKey, ServerSession session, string endpoint, bool isTls)
    {
        lock (statusLock)
        {
            if (reservations > 0)
            {
                reservations--;
            }

            if (reservationsPerIp.TryGetValue(ipKey, out int current))
            {
                if (current <= 1)
                {
                    reservationsPerIp.Remove(ipKey);
                }
                else
                {
                    reservationsPerIp[ipKey] = current - 1;
                }
            }

            PruneLocked();
            sessions.Add(new SessionRecord(session, endpoint, isTls, ipKey));
        }
    }

    private void LogOutsideLock(string message)
    {
        try
        {
            options.Log?.Invoke(message);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex.Message);
        }

        System.Diagnostics.Debug.WriteLine(message);
    }

    private void ReportStatus()
    {
#pragma warning disable CA1031 // Do not catch general exception types
        try
        {
            List<(string Endpoint, long Received, long Sent, double IdleSeconds, bool IsTls)> snapshot;
            string aggregate;
            lock (statusLock)
            {
                PruneLocked();
                snapshot = new List<(string, long, long, double, bool)>(sessions.Count);
                foreach (var record in sessions)
                {
                    if (!record.Session.TryGetTarget(out var session))
                    {
                        continue;
                    }

                    long received = session.Context.CharsReceived;
                    long sent = session.Context.CharsSent;
                    if (received == record.LastReceived && sent == record.LastSent)
                    {
                        continue;
                    }

                    record.LastReceived = received;
                    record.LastSent = sent;
                    snapshot.Add((record.Endpoint, received, sent, session.Context.Idle.TotalSeconds, record.IsTls));
                }

                long cap = Interlocked.Read(ref rejectedCapacity);
                long perIp = Interlocked.Read(ref rejectedPerIp);
                long filter = Interlocked.Read(ref rejectedFilter);
                long queuedrop = Interlocked.Read(ref queueDropped);
                aggregate = $"sessions={sessions.Count} rejected(capacity={cap},per-ip={perIp},filter={filter},queue-drop={queuedrop})";
            }

            var log = options.Log;
            if (log is null)
            {
                return;
            }

            foreach (var (endpoint, received, sent, idleSeconds, isTls) in snapshot)
            {
                log($"{endpoint} (rx={received},tx={sent},idle={idleSeconds:F0}s,tls={isTls})");
            }

            log(aggregate);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex.Message);
        }
#pragma warning restore CA1031 // Do not catch general exception types
    }
}
