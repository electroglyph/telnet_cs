namespace telnet_cs.Server;

using telnet_cs.IO;
using telnet_cs.Protocol;
using telnet_cs.Transport;

/// <summary>
/// Server-role subnegotiation requesters for TSPEED, XDISPLOC, ENVIRON, NEW_ENVIRON, CHARSET, SEND-LOCATION and LINEFLOW-MODE, plus the per-option completion predicates. Split from <see cref="ServerSession"/> collectors; wire behavior is unchanged.
/// </summary>
public partial class ServerSession
{
    /// <summary>
    /// Asks the peer for its terminal speed (RFC 1079: <c>SEND</c>, one
    /// <c>IS</c>). Returns the verbatim validated <c>"&lt;tx&gt;,&lt;rx&gt;"</c>
    /// shape, or null on timeout, on a malformed answer, or when a previous
    /// request is still outstanding (single-active rule, like the reference
    /// <c>request_tspeed</c> returning false while pending).
    /// </summary>
    /// <param name="timeout">The maximum time to wait for the answer.</param>
    /// <param name="cancellationToken">A token to cancel the wait.</param>
    public async Task<string?> RequestTerminalSpeedAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (!Negotiation.IsEnabledByPeer((int)Options.TerminalSpeed))
        {
            WriteLog("Cannot send SB TSPEED SEND without receipt of WILL TSPEED.");
            return null;
        }

        bool preStored;
        using (await AcquireWireForRequestAsync(cancellationToken).ConfigureAwait(false))
        {
            lock (collectorLock)
            {
                if (expectingTerminalSpeed)
                {
                    return null;
                }

                // An answer the pump filed before this request started
                // satisfies it: still SEND (the peer answers again, filed
                // unsolicited), but return the known value without waiting.
                preStored = clientTerminalSpeed is not null;
                if (!preStored)
                {
                    clientTerminalSpeed = null;
                    expectingTerminalSpeed = true;
                }
            }
        }

        await SendSbAsync(Options.TerminalSpeed, [], cancellationToken).ConfigureAwait(false);
        if (preStored)
        {
            lock (collectorLock)
            {
                return clientTerminalSpeed;
            }
        }

        await PollForResponseAsync(IsTerminalSpeedDone, timeout, cancellationToken).ConfigureAwait(false);
        lock (collectorLock)
        {
            expectingTerminalSpeed = false;
            return clientTerminalSpeed;
        }
    }

    /// <summary>
    /// Asks the peer for its X display location (RFC 1096: <c>SEND</c>, one
    /// <c>IS</c>). Returns the display string, or null on timeout, on a
    /// malformed answer, or when a previous request is still outstanding
    /// (single-active rule, like <see cref="RequestTerminalSpeedAsync"/>:
    /// overlapping calls share the in-flight SEND). Also feeds the
    /// effective-display recency rule (see
    /// <see cref="ClientEffectiveDisplay"/>).
    /// </summary>
    /// <param name="timeout">The maximum time to wait for the answer.</param>
    /// <param name="cancellationToken">A token to cancel the wait.</param>
    public async Task<string?> RequestXDisplayAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (!Negotiation.IsEnabledByPeer((int)Options.XDisplay))
        {
            WriteLog("Cannot send SB XDISPLOC SEND without receipt of WILL XDISPLOC.");
            return null;
        }

        bool preStored;
        using (await AcquireWireForRequestAsync(cancellationToken).ConfigureAwait(false))
        {
            lock (collectorLock)
            {
                if (expectingXDisplay)
                {
                    return null;
                }

                // See RequestTerminalSpeedAsync: a pre-stored answer is
                // returned after the SEND without waiting.
                preStored = clientXDisplay is not null;
                if (!preStored)
                {
                    clientXDisplay = null;
                    expectingXDisplay = true;
                }
            }
        }

        await SendSbAsync(Options.XDisplay, [], cancellationToken).ConfigureAwait(false);
        if (preStored)
        {
            lock (collectorLock)
            {
                return clientXDisplay;
            }
        }

        await PollForResponseAsync(IsXDisplayDone, timeout, cancellationToken).ConfigureAwait(false);
        lock (collectorLock)
        {
            expectingXDisplay = false;
            return clientXDisplay;
        }
    }

    /// <summary>
    /// Asks the peer for environment variables (RFC 1408: <c>SEND</c>, one
    /// <c>IS</c>). Later spontaneous INFO updates also land in
    /// <see cref="ClientEnvironment"/>. Returns the variables known when
    /// the answer arrives or the timeout elapses.
    /// Long type lists are split into size-limited SB frames (at most
    /// <see cref="MaxEnvironBatchBytes"/> type bytes each) so peers with
    /// small subnegotiation buffers are not overflowed.
    /// </summary>
    /// <param name="timeout">The maximum time to wait for the answer.</param>
    /// <param name="types">The requested type bytes (VAR/USERVAR); null or
    /// empty requests the defaults (well-known variables, then user
    /// variables).</param>
    /// <param name="cancellationToken">A token to cancel the wait.</param>
    public async Task<IReadOnlyDictionary<string, string>> RequestEnvironmentAsync(TimeSpan timeout, byte[]? types = null, CancellationToken cancellationToken = default)
    {
        if (!Negotiation.IsEnabledByPeer((int)Options.OldEnvironment))
        {
            WriteLog("Cannot send SB OLD_ENVIRON SEND without receipt of WILL OLD_ENVIRON.");
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        bool preSatisfied;
        using (await AcquireWireForRequestAsync(cancellationToken).ConfigureAwait(false))
        {
            lock (collectorLock)
            {
                // Answers the pump filed before this request started satisfy
                // it (like every other requester): SEND anyway, skip the wait.
                preSatisfied = clientEnvironment.Count > 0;
                expectingEnvironment = true;
            }
        }

        await SendEnvironmentBatchesAsync(Options.OldEnvironment, types ?? [], cancellationToken).ConfigureAwait(false);
        if (!preSatisfied)
        {
            await PollForResponseAsync(IsEnvironmentDone, timeout, cancellationToken).ConfigureAwait(false);
        }

        lock (collectorLock)
        {
            expectingEnvironment = false;
            return new Dictionary<string, string>(clientEnvironment, StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Asks the peer for environment variables in new form (RFC 1572:
    /// <c>SEND</c>, one <c>IS</c>). Later spontaneous INFO updates also
    /// land in <see cref="ClientNewEnvironment"/>. Returns the variables
    /// known when the answer arrives or the timeout elapses.
    /// Long type lists are split like <see cref="RequestEnvironmentAsync"/>.
    /// </summary>
    /// <param name="timeout">The maximum time to wait for the answer.</param>
    /// <param name="types">The requested type bytes (VAR/USERVAR); null or
    /// empty requests the defaults (well-known variables, then user
    /// variables).</param>
    /// <param name="cancellationToken">A token to cancel the wait.</param>
    public async Task<IReadOnlyDictionary<string, string>> RequestNewEnvironmentAsync(TimeSpan timeout, byte[]? types = null, CancellationToken cancellationToken = default)
    {
        if (!Negotiation.IsEnabledByPeer((int)Options.NewEnvironment))
        {
            WriteLog("Cannot send SB NEW_ENVIRON SEND without receipt of WILL NEW_ENVIRON.");
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        bool preSatisfied;
        using (await AcquireWireForRequestAsync(cancellationToken).ConfigureAwait(false))
        {
            lock (collectorLock)
            {
                preSatisfied = clientNewEnvironment.Count > 0;
                expectingNewEnvironment = true;
            }
        }

        await SendEnvironmentBatchesAsync(Options.NewEnvironment, types ?? [], cancellationToken).ConfigureAwait(false);
        if (!preSatisfied)
        {
            await PollForResponseAsync(IsNewEnvironmentDone, timeout, cancellationToken).ConfigureAwait(false);
        }

        lock (collectorLock)
        {
            expectingNewEnvironment = false;
            return new Dictionary<string, string>(clientNewEnvironment, StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Maximum type bytes per ENVIRON SEND frame: with the option, SEND,
    /// and IAC SB/SE framing the wire frame stays within the 240-byte SB
    /// payload budget small-buffer peers (e.g. GNU inetutils telnet)
    /// tolerate.
    /// </summary>
    private const int MaxEnvironBatchBytes = 238;

    private async Task SendEnvironmentBatchesAsync(Options option, byte[] types, CancellationToken cancellationToken)
    {
        if (types.Length == 0)
        {
            await SendSbAsync(option, [], cancellationToken).ConfigureAwait(false);
            return;
        }

        for (var offset = 0; offset < types.Length; offset += MaxEnvironBatchBytes)
        {
            var length = Math.Min(MaxEnvironBatchBytes, types.Length - offset);
            await SendSbAsync(option, types[offset..(offset + length)], cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Asks the peer for a character set (RFC 2066: <c>REQUEST</c>, one
    /// <c>ACCEPTED</c>/<c>REJECTED</c>). Returns the accepted name, or null
    /// on timeout or rejection.
    /// </summary>
    /// <param name="timeout">The maximum time to wait for the answer.</param>
    /// <param name="cancellationToken">A token to cancel the wait.</param>
    public async Task<string?> RequestCharsetAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (!Negotiation.IsEnabledByPeer((int)Options.CharacterSet) &&
            !Negotiation.IsEnabledByUs((int)Options.CharacterSet))
        {
            WriteLog("Cannot send SB CHARSET REQUEST without CHARSET being active.");
            return null;
        }

        bool preStored;
        using (await AcquireWireForRequestAsync(cancellationToken).ConfigureAwait(false))
        {
            lock (collectorLock)
            {
                // An ACCEPTED the pump latched before this request started
                // satisfies it (still REQUEST, the peer answers again).
                preStored = clientCharset is not null;
                if (!preStored)
                {
                    clientCharset = null;
                    expectingCharset = true;
                }
            }
        }

        await SendFrameAsync((int)Options.CharacterSet, CharsetProtocol.BuildRequest(Settings.CharsetOffers), cancellationToken).ConfigureAwait(false);
        if (preStored)
        {
            lock (collectorLock)
            {
                return clientCharset;
            }
        }

        await PollForResponseAsync(IsCharsetDone, timeout, cancellationToken).ConfigureAwait(false);
        lock (collectorLock)
        {
            expectingCharset = false;
            return clientCharset;
        }
    }

    /// <summary>
    /// Asks the peer for its location (RFC 779: <c>DO SNDLOC</c>, one
    /// spontaneous SB). Returns the location string, or null on timeout.
    /// The peer volunteers the SB after WILL, so no SEND goes out here.
    /// </summary>
    /// <param name="timeout">The maximum time to wait for the answer.</param>
    /// <param name="cancellationToken">A token to cancel the wait.</param>
    public async Task<string?> RequestSendLocationAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        bool preStored;
        using (await AcquireWireForRequestAsync(cancellationToken).ConfigureAwait(false))
        {
            lock (collectorLock)
            {
                // A volunteered location the pump filed before this request
                // started satisfies it (still DO, the peer volunteers again).
                preStored = clientLocation is not null;
                if (!preStored)
                {
                    clientLocation = null;
                    expectingLocation = true;
                }
            }
        }

        await RequestEnableAsync(Options.SendLocation, cancellationToken).ConfigureAwait(false);
        if (preStored)
        {
            lock (collectorLock)
            {
                return clientLocation;
            }
        }

        await PollForResponseAsync(IsLocationDone, timeout, cancellationToken).ConfigureAwait(false);
        lock (collectorLock)
        {
            expectingLocation = false;
            return clientLocation;
        }
    }

    /// <summary>
    /// Sends the LFLOW restart mode (RFC 1372) as a server: RESTART_ANY
    /// when <paramref name="restartOnAny"/> is true, else RESTART_XON.
    /// Returns <c>false</c> (sending nothing) unless the peer enabled
    /// LFLOW (WILL) first, mirroring the reference server-only guard.
    /// </summary>
    /// <param name="restartOnAny">Whether any character restarts output.</param>
    /// <param name="cancellationToken">A token to cancel the send.</param>
    public async Task<bool> SendLineflowModeAsync(bool restartOnAny, CancellationToken cancellationToken = default)
    {
        if (!Negotiation.IsEnabledByPeer((int)Options.RemoteFlowControl))
        {
            WriteLog("Cannot send LFLOW without receipt of WILL LFLOW.");
            return false;
        }

        var mode = restartOnAny ? LineflowProtocol.RestartAny : LineflowProtocol.RestartXon;
        await SendFrameAsync((int)Options.RemoteFlowControl, [mode], cancellationToken).ConfigureAwait(false);
        return true;
    }

    private bool IsTerminalTypeDone()
    {
        lock (collectorLock)
        {
            return !expectingTerminalType;
        }
    }

    private bool IsTerminalSpeedDone()
    {
        lock (collectorLock)
        {
            return !expectingTerminalSpeed;
        }
    }

    private bool IsEnvironmentDone()
    {
        lock (collectorLock)
        {
            return !expectingEnvironment;
        }
    }

    private bool IsNewEnvironmentDone()
    {
        lock (collectorLock)
        {
            return !expectingNewEnvironment;
        }
    }

    private bool IsCharsetDone()
    {
        lock (collectorLock)
        {
            return !expectingCharset;
        }
    }

    private bool IsLocationDone()
    {
        lock (collectorLock)
        {
            return !expectingLocation;
        }
    }

    private bool IsXDisplayDone()
    {
        lock (collectorLock)
        {
            return !expectingXDisplay;
        }
    }
}
