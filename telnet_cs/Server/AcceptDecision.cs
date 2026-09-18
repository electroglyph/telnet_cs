namespace telnet_cs.Server;

/// <summary>
/// A <see cref="TelnetServerOptions.AcceptFilter"/> verdict: whether
/// the accepted connection may proceed, plus the machine-readable reason
/// a refusal carries into the <c>over-capacity: filter-reject
/// endpoint=…</c> log line and the
/// <see cref="ConnectionRefusedByFilterException.Reason"/>. An empty
/// reason normalizes to <c>filter-reject</c> at the accept path.
/// </summary>
/// <param name="Allowed"><c>true</c> to accept the connection.</param>
/// <param name="Reason">The machine-readable refuse reason.</param>
public readonly record struct AcceptDecision(bool Allowed, string Reason = "filter-reject");
