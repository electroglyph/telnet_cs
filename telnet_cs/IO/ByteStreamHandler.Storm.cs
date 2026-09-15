namespace telnet_cs.IO
{
    using System;
    using System.Threading;
    using telnet_cs.Protocol;

    /// <summary>
    /// Session-owned negotiation storm tracker: a fixed 1s window counting
    /// inbound negotiation frames (WILL/WONT/DO/DONT verbs plus completed
    /// subnegotiation frames). The window starts at the first frame and
    /// resets 1000ms later, so a tight burst always trips regardless of
    /// wall-clock alignment. Handlers are built per read, so the session
    /// owns one instance and hands it to every handler.
    /// </summary>
    internal sealed class NegotiationStormGuard
    {
        /// <summary>
        /// Inbound negotiation frames per second before refused/duplicate
        /// replies are suppressed. Fixed, not an option.
        /// </summary>
        internal const int Threshold = 100;

        private const long WindowMs = 1000;

        private readonly Lock gate = new();

        private long windowStartMs;

        private int count;

        /// <summary>
        /// Records one inbound negotiation frame. Returns the window count.
        /// </summary>
        internal int NoteFrame()
        {
            long now = Environment.TickCount64;
            lock (gate)
            {
                if (count == 0 || now - windowStartMs >= WindowMs)
                {
                    windowStartMs = now;
                    count = 1;
                    return 1;
                }

                count++;
                return count;
            }
        }

        /// <summary>
        /// Gets whether the current window is over threshold (resets an
        /// expired window first so a stale burst never suppresses forever).
        /// </summary>
        internal bool IsOverThreshold
        {
            get
            {
                long now = Environment.TickCount64;
                lock (gate)
                {
                    if (count == 0 || now - windowStartMs >= WindowMs)
                    {
                        windowStartMs = now;
                        count = 0;
                        return false;
                    }

                    return count > Threshold;
                }
            }
        }
    }

    public partial class ByteStreamHandler
    {
        /// <summary>
        /// Gets or sets the session-owned storm guard. Null for
        /// directly-constructed handlers (no suppression, unchanged behavior).
        /// </summary>
        internal NegotiationStormGuard? StormGuard { get; set; }

        private bool ShouldSuppressStormRefusal()
        {
            return StormGuard is not null && StormGuard.IsOverThreshold;
        }

        private bool ShouldSuppressStormSbReply(int inputOption)
        {
            // TTYPE SEND answers are never suppressed: the TTYPE loop must
            // keep working even while a storm is being shed.
            return inputOption != (int)Options.TerminalType &&
                StormGuard is not null && StormGuard.IsOverThreshold;
        }
    }
}
