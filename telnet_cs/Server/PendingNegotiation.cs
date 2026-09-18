namespace telnet_cs.Server;

/// <summary>
/// Admission state held between <see cref="TelnetServer.AcceptTcpAsync"/>
/// and <see cref="TelnetServer.NegotiateAsync"/>: the capacity
/// reservation key, the endpoint and TLS role recorded for the session
/// record, and the handshake-deadline budget the opening preset sends
/// under. Stamped on the session by the accept call and claimed once by
/// the negotiate call; never constructed by callers.
/// </summary>
internal readonly record struct PendingNegotiation(
    string ReservationIpKey,
    string Endpoint,
    bool IsTls,
    System.DateTime HandshakeDeadlineUtc,
    bool HandshakeEnabled);
