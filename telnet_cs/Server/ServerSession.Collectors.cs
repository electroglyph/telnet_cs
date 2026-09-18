namespace telnet_cs.Server;

using telnet_cs.IO;
using telnet_cs.Protocol;
using telnet_cs.Transport;

/// <summary>
/// Server-role subnegotiation requesters (S3): the session asks, the peer
/// answers. The <see cref="ByteStreamHandler"/> routes inbound IS/INFO (and
/// inbound NAWS) payloads to <see cref="OnSubnegotiationResponse"/>; each
/// requester sends its SEND, then polls reads until its collector is
/// satisfied or the timeout elapses. TTYPE IS is stored even when
/// unsolicited (the reference records it with no pending check).
/// </summary>
public partial class ServerSession
{
    // Guards the expecting-flags, chains, and collected values below. The
    // collectors assume single-threaded session use: concurrent
    // Request*Async calls would overwrite each other's expecting-flags and
    // mix the chains (no reentrancy protection by design).
    private readonly Lock collectorLock = new();
    // Session-owned negotiation storm guard (fixed 100 inbound neg
    // frames/s window): one instance shared by every per-read handler
    // so the count survives across reads.
    private readonly NegotiationStormGuard stormGuard = new();
    private bool expectingTerminalType;
    private bool expectingTerminalSpeed;
    private bool expectingEnvironment;
    private bool expectingNewEnvironment;
    private bool expectingXDisplay;
    private bool expectingCharset;
    private bool expectingLocation;
    private readonly List<string> terminalTypeChain = [];
    // Answers before this index were consumed by an earlier collection;
    // a new request only replays what arrived after it.
    private int terminalTypesConsumedUpTo;
    // A completed TTYPE cycle freezes unsolicited appends (answers past
    // the end of a cycle are ignored, like a live request that already
    // stopped polling). Request end releases it, so answers arriving in
    // the gap between requests are still kept for the next collection.
    private bool terminalTypesCycleComplete;
    // Deferred opening negotiation (mirroring the reference): WILL ECHO
    // and DO NEW_ENVIRON leave with the preset only as intent. The TTYPE
    // answers arm them via the pending flags and release them on the
    // next flush even before anything is agreed (the reference on_ttype
    // path, which negotiates echo/environ per answer); the refusal and
    // collection-timeout arming below only releases once negotiation
    // advances (the reference check_negotiation gate). echoNegotiated /
    // environRequested latch so each fires once. advancedNegotiationSent
    // latches the WILL SGA / WILL BINARY / DO NAWS / DO CHARSET advanced
    // preset; ttypeProbeSent latches the SB TTYPE SEND probe on WILL
    // TTYPE (an explicit collection covers its own probe and latches
    // this too).
    private bool advancedNegotiationSent;
    private bool ttypeProbeSent;
    // Like the TTYPE probe above, but for the options the session asks
    // about without a public requester: SB TSPEED SEND on WILL TSPEED and
    // SB XDISPLOC SEND on WILL XDISPLOC, each latched to fire once.
    private bool tspeedProbeSent;
    private bool xdisplayProbeSent;
    // A non-terminal TTYPE answer owed a follow-up SEND (the reference
    // request_ttype per answer): counted when solicited answers arrive
    // and drained by the flush after the read, so background and
    // explicit collection cycle identically with one SEND per answer no
    // matter who consumed first or how answers batch across passes.
    private int ttypeResendsOwed;
    private bool echoNegotiated;
    private bool environRequested;
    private bool negotiateEchoPending;
    private bool negotiateEnvironPending;
    // Game-driven ECHO latch (see SetEchoAsync): null while the session
    // stays in auto mode, otherwise the last manual direction. Once set,
    // the deferred auto-offer stands down (wantEcho gains
    // `manualEcho is null`) so a later TTYPE answer or collection
    // timeout cannot auto-WILL past a game-driven WONT and unmask.
    private bool? manualEcho;
    // Answer-driven release for the flags above: set alongside them when
    // a TTYPE answer arrives, so the flush sends the offer without
    // waiting for the advanced preset. Cleared once consumed.
    private bool echoArmedByAnswer;
    private bool environArmedByAnswer;
    // Set once the deferred DO NEW_ENVIRON is agreed and its default SB
    // SEND went out; the charset auto-REQUEST likewise fires once.
    private bool environSent;
    private bool charsetAutoRequested;
    private string? clientTerminalSpeed;
    private string? clientXDisplay;
    private string? clientCharset;
    private string? clientLocation;
    private readonly Dictionary<string, string> clientEnvironment = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> clientNewEnvironment = new(StringComparer.Ordinal);
    private bool forceBinaryDecoding;
    private System.Text.Encoding? charsetEncoding;
    // The handler of the currently running wire pass, if any (set and
    // cleared by ReadWireOnceAsync). At most one pass runs at a time,
    // so a subnegotiation callback always belongs to this handler and
    // a mid-pass latch can refresh its decoding immediately.
    private ByteStreamHandler? activeReadHandler;
    // MCCP agreement survives across per-read handlers: the arming SB,
    // stream end, and corrupt shutdown report through MccpStateChanged.
    private bool mccp2Agreed;
    private bool mccp3Agreed;
    private MccpDecompressor? mccpStream;
    // Subnegotiation continuation stashed by the last read, fed into the
    // next per-read handler (telnetlib3 _sb_buffer parity).
    private (int Option, byte[] Payload, bool OverCap, bool SePending, bool IacPending, bool HeaderIacPending)? sbResumeState;
    private (bool PendingIac, int? PendingVerb, bool SawCr, int? Pushback) framingState;
    // MUD stores survive across per-read handlers: append collections are
    // injected into each handler, and the replaced MSSP mapping is
    // captured through the MSSP hook.
    private IReadOnlyDictionary<string, object>? mudMsspData;
    private readonly List<byte[]> mudMspData = [];
    private readonly List<byte[]> mudMxpData = [];
    private readonly Dictionary<string, IReadOnlyList<string>> mudZmpData = new(StringComparer.Ordinal);
    private readonly List<AardwolfMessage> mudAardwolfData = [];
    private readonly List<(string Package, string Value)> mudAtcpData = [];
    private (ushort Width, ushort Height)? clientWindowSize;
    // Last parsed STATUS IS report (RFC 859): verb/option pairs and SB
    // blocks, recorded for display only — never fed back into the
    // Q-machine.
    private IReadOnlyList<StatusReportItem>? peerStatusReport;
    // Arrival order shared by option-35 IS and ENVIRON DISPLAY writes, so
    // the effective display resolves last-arrived-wins (RFC 1408 §5). Zero
    // means "never arrived" on both sides.
    private long displayArrivalSeq;
    private long xdisplaySeq;
    private long environDisplaySeq;

    /// <summary>
    /// Gets the terminal types reported by the peer (RFC 1091), most
    /// specific first. Populated by <see cref="RequestTerminalTypesAsync"/>;
    /// empty until the first answer arrives.
    /// </summary>
    public IReadOnlyList<string> ClientTerminalTypes
    {
        get
        {
            lock (collectorLock)
            {
                return [.. terminalTypeChain];
            }
        }
    }

    /// <summary>
    /// Gets the normalized terminal speed reported by the peer
    /// (<c>"&lt;tx&gt;,&lt;rx&gt;"</c>, RFC 1079), or null when nothing
    /// usable arrived (no answer yet, or a malformed IS).
    /// </summary>
    public string? ClientTerminalSpeed
    {
        get
        {
            lock (collectorLock)
            {
                return clientTerminalSpeed;
            }
        }
    }

    /// <summary>
    /// Gets the environment variables reported by the peer (RFC 1408),
    /// from requested IS answers and spontaneous INFO updates. A variable
    /// sent without VALUE is undefined and omitted; on a VAR/USERVAR name
    /// collision the later entry wins.
    /// </summary>
    public IReadOnlyDictionary<string, string> ClientEnvironment
    {
        get
        {
            lock (collectorLock)
            {
                return new Dictionary<string, string>(clientEnvironment, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>
    /// Gets the environment variables reported by the peer in new form
    /// (RFC 1572), from requested IS answers and spontaneous INFO updates.
    /// Same shape as <see cref="ClientEnvironment"/>, kept separate so a
    /// peer reporting both forms never mixes them.
    /// </summary>
    public IReadOnlyDictionary<string, string> ClientNewEnvironment
    {
        get
        {
            lock (collectorLock)
            {
                return new Dictionary<string, string>(clientNewEnvironment, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>
    /// Gets the character set agreed via CHARSET ACCEPTED (RFC 2066), or
    /// null when none was negotiated yet. Populated by
    /// <see cref="RequestCharsetAsync"/>.
    /// </summary>
    public string? ClientCharset
    {
        get
        {
            lock (collectorLock)
            {
                return clientCharset;
            }
        }
    }

    /// <summary>
    /// Reports whether our own CHARSET REQUEST is outstanding (sent, not
    /// yet answered). Fed into each per-read handler so a simultaneous
    /// inbound REQUEST is answered REJECTED (RFC 2066 §5).
    /// </summary>
    internal bool IsCharsetOutstanding
    {
        get
        {
            lock (collectorLock)
            {
                return expectingCharset;
            }
        }
    }

    /// <summary>
    /// Gets the location reported by the peer via SNDLOC (RFC 779), or
    /// null when none arrived. Populated by
    /// <see cref="RequestSendLocationAsync"/>.
    /// </summary>
    public string? ClientLocation
    {
        get
        {
            lock (collectorLock)
            {
                return clientLocation;
            }
        }
    }

    /// <summary>
    /// Gets the last MSSP variables reported by the peer, or null when
    /// none arrived yet. Each MSSP subnegotiation replaces the mapping.
    /// </summary>
    public IReadOnlyDictionary<string, object>? MsspData
    {
        get
        {
            lock (collectorLock)
            {
                return mudMsspData;
            }
        }
    }

    /// <summary>
    /// Gets the raw MSP payloads received so far, in arrival order.
    /// </summary>
    public IReadOnlyList<byte[]> MspData
    {
        get
        {
            lock (collectorLock)
            {
                return [.. mudMspData];
            }
        }
    }

    /// <summary>
    /// Gets the raw MXP payloads received so far, in arrival order.
    /// </summary>
    public IReadOnlyList<byte[]> MxpData
    {
        get
        {
            lock (collectorLock)
            {
                return [.. mudMxpData];
            }
        }
    }

    /// <summary>
    /// Gets the latest ZMP arguments by command; each command slot holds
    /// its most recent message.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ZmpData
    {
        get
        {
            lock (collectorLock)
            {
                return new Dictionary<string, IReadOnlyList<string>>(mudZmpData, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>
    /// Gets the decoded Aardwolf messages received so far, in arrival
    /// order.
    /// </summary>
    public IReadOnlyList<AardwolfMessage> AardwolfData
    {
        get
        {
            lock (collectorLock)
            {
                return [.. mudAardwolfData];
            }
        }
    }

    /// <summary>
    /// Gets the decoded ATCP <c>(package, value)</c> pairs received so
    /// far, in arrival order.
    /// </summary>
    public IReadOnlyList<(string Package, string Value)> AtcpData
    {
        get
        {
            lock (collectorLock)
            {
                return [.. mudAtcpData];
            }
        }
    }

    /// <summary>
    /// Maximum terminal-type answers stored in distinct slots, mirroring
    /// telnetlib3's <c>TTYPE_LOOPMAX</c>: the answer arriving past slot 8
    /// is recorded once in the overflow slot (<c>ttype9</c>) then the
    /// cycle stops and the deferred environ request is released, so the
    /// last answer wins there without waiting out the timeout.
    /// </summary>
    internal const int TerminalTypeLoopMax = 8;

    /// <summary>
    /// Gets the effective terminal type: when the reported chain reaches
    /// a third entry starting with <c>"MTTS "</c> (MUD Terminal Type
    /// Standard), the second entry names the real terminal and the MTTS
    /// bitmask only lists client capabilities — so the second entry wins.
    /// Otherwise the last entry wins (the most recently reported type is
    /// assumed current), or null when nothing arrived.
    /// </summary>
    public string? ClientEffectiveTerminalType
    {
        get
        {
            lock (collectorLock)
            {
                if (terminalTypeChain.Count >= 3 &&
                    terminalTypeChain[2].StartsWith("MTTS ", StringComparison.OrdinalIgnoreCase))
                {
                    return terminalTypeChain[1];
                }

                return terminalTypeChain.Count > 0 ? terminalTypeChain[^1] : null;
            }
        }
    }

    /// <summary>
    /// Gets the X display reported by the peer via option 35 (RFC 1096), or
    /// null when none arrived yet. Stored even when unsolicited, like
    /// every other server-role answer.
    /// </summary>
    public string? ClientXDisplay
    {
        get
        {
            lock (collectorLock)
            {
                return clientXDisplay;
            }
        }
    }

    /// <summary>
    /// Gets the effective display: the last-arrived of the option-35 X
    /// display and the ENVIRON DISPLAY variable (RFC 1408 §5 recency rule),
    /// or null when neither arrived. Kept out of
    /// <see cref="ClientEnvironment"/>, which holds ENVIRON vars only.
    /// </summary>
    public string? ClientEffectiveDisplay
    {
        get
        {
            lock (collectorLock)
            {
                if (xdisplaySeq > 0 && xdisplaySeq >= environDisplaySeq && clientXDisplay is not null)
                {
                    return clientXDisplay;
                }

                return clientEnvironment.TryGetValue(EnvironmentProtocol.DisplayVariableName, out string? display) ? display : null;
            }
        }
    }

    /// <summary>
    /// Gets the last terminal size reported by the peer (RFC 1073), or null
    /// when the peer never sent NAWS. Updated whenever a NAWS report
    /// arrives; the peer volunteers these, so there is no request method.
    /// </summary>
    public (ushort Width, ushort Height)? ClientWindowSize
    {
        get
        {
            lock (collectorLock)
            {
                return clientWindowSize;
            }
        }
    }

    /// <summary>
    /// Gets the last STATUS IS report from the peer (RFC 859), or null
    /// when none arrived. Recorded for display only: arriving reports
    /// never affect negotiation state.
    /// </summary>
    public IReadOnlyList<StatusReportItem>? PeerStatusReport
    {
        get
        {
            lock (collectorLock)
            {
                return peerStatusReport is null ? null : [.. peerStatusReport];
            }
        }
    }
}
