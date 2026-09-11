namespace telnet_cs.Protocol
{
    /// <summary>
    /// Wire line-ending sequences shared by the client and the server session.
    /// Single source of truth: <c>Client.LegacyLineFeed</c> and
    /// <c>Client.Rfc854LineFeed</c> forward to these (kept on <c>Client</c> for
    /// public API compatibility, including default parameter values).
    /// </summary>
    internal static class LineFeed
    {
        /// <summary>
        /// Prior to v0.9.0 this was the default. To be Rfc854 compliant prefer <c>Rfc854</c>.
        /// </summary>
        internal const string Legacy = "\n";

        /// <summary>
        /// Retained as the default, but to be Rfc854 compliant you should prefer this.
        /// </summary>
        internal const string Rfc854 = "\r\n";
    }
}
