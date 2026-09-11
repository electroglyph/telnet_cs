namespace telnet_cs.IO
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using System.Threading.Tasks;
    using telnet_cs.Client;
    using telnet_cs.Protocol;
    using telnet_cs.Transport;

    /// <summary>
    /// Provides core functionality for interacting with the ByteStream.
    /// </summary>
    public partial class ByteStreamHandler : IByteStreamHandler
    {
        private const int IacByte = (int)Commands.InterpretAsCommand;
        private const int SeByte = (int)Commands.SubnegotiationEnd;
        private const int MaxSubnegotiationBytes = 512;

        private readonly IByteStream byteStream;

        /// <summary>
        /// Single-byte lookahead stash. Used by CR handling to peek at the byte
        /// following a CR without blocking: CR NUL collapses to CR only when
        /// the NUL is already available at the peek — a split CR…NUL leaks a
        /// literal NUL. CR LF stays CR LF (the LF is pushed back and processed
        /// on the next pass).
        /// </summary>
        private int? pushbackByte;

        /// <summary>
        /// Gets whether the handler is discarding data in an RFC 854 Synch
        /// scan: set by the urgent-data trigger (or <see cref="EnterSynchDiscard"/>
        /// in tests) and cleared by in-band <c>IAC DM</c>.
        /// </summary>
        internal bool InSynchDiscard { get; private set; }

        /// <summary>
        /// Enters RFC 854 Synch discard mode: data is dropped until in-band
        /// <c>IAC DM</c>. The urgent-data trigger calls this automatically on
        /// real TCP streams; tests call it directly for hermetic coverage.
        /// </summary>
        internal void EnterSynchDiscard()
        {
            InSynchDiscard = true;
        }

        /// <summary>
        /// Non-blocking RFC 854 Synch trigger plus scan entry: consumes one
        /// pending TCP urgent byte when the stream is a real
        /// <see cref="TcpByteStream"/> with urgent data waiting (and we are not
        /// already discarding), then runs the discard scan while the mode holds.
        /// Split out of <c>RetrieveAndParseResponse</c> to keep that method
        /// under the complexity gate. Returns null when not discarding.
        /// </summary>
        private async Task<bool?> RetrieveSynchDiscardAsync(StringBuilder sb, List<byte> rawBytes, List<int> opByteCounts, List<byte?> echoBytes)
        {
            if (PollSynchTrigger())
            {
                EnterSynchDiscard();
            }

            return InSynchDiscard
              ? await RetrieveAndParseSynchDiscard(sb, rawBytes, opByteCounts, echoBytes).ConfigureAwait(false)
              : null;
        }

        /// <summary>
        /// Non-blocking RFC 854 Synch trigger: consumes one pending TCP urgent
        /// byte when the stream is a real <see cref="TcpByteStream"/> with
        /// urgent data waiting (and we are not already discarding). Split out
        /// of <c>RetrieveAndParseResponse</c> to keep that method under the
        /// complexity gate.
        /// </summary>
        /// <returns>True when a Synch scan should start.</returns>
        private bool PollSynchTrigger()
        {
            return !InSynchDiscard && byteStream is TcpByteStream tcp && tcp.TryConsumeUrgentSignal() is not null;
        }

        /// <summary>
        /// RFC 854 Synch scan: discard data until <c>IAC DM</c>. Interesting
        /// signals and all other commands dispatch through the normal
        /// <see cref="InterpretNextAsCommand"/> path; EC/EL are swallowed (the
        /// RFC excludes them); escaped IAC is data (dropped). Nothing here
        /// surfaces, so this always returns false; the mode ends only at DM,
        /// so end-of-urgent never cuts the scan short and a later urgent byte
        /// re-triggers a fresh scan.
        /// </summary>
        private async Task<bool> RetrieveAndParseSynchDiscard(StringBuilder sb, List<byte> rawBytes, List<int> opByteCounts, List<byte?> echoBytes)
        {
            var input = ReadNextByte();
            if (input != IacByte)
            {
                // Data (or end-of-stream): dropped, never surfaced.
                return false;
            }

            var verb = TryReadByte();
            if (verb == -1)
            {
                return false;
            }

            if (verb == (int)Commands.DataMark)
            {
                InSynchDiscard = false;
                return false;
            }

            if (verb is (int)Commands.EraseCharacter or (int)Commands.EraseLine)
            {
                return false;
            }

            await InterpretNextAsCommand(sb, rawBytes, opByteCounts, echoBytes, verb).ConfigureAwait(false);
            return false;
        }

        /// <summary>
        /// Gets or sets the persistent RFC 1143 negotiation state. The
        /// <see cref="Client"/> feeds its long-lived instance before each read so
        /// replies survive across reads; a directly-constructed handler uses a
        /// fresh instance (single-read behaviour).
        /// </summary>
        internal NegotiationState Negotiation { get; set; } = new();

        /// <summary>
        /// Gets or sets the terminal type reported during negotiation.
        /// Defaults to <see cref="Client.TerminalType"/>; the client feeds its
        /// effective per-instance setting before each read.
        /// </summary>
        internal string TerminalType { get; set; } = Client.TerminalType;

        /// <summary>
        /// Optional per-SEND terminal-type source for RFC 1091 cycling. The
        /// client feeds a shared cycler before each read; a directly-constructed
        /// handler leaves this null and repeats <see cref="TerminalType"/>.
        /// </summary>
        internal Func<string>? TerminalTypeProvider { get; set; }

        /// <summary>
        /// Gets or sets the terminal speed reported during negotiation.
        /// Defaults to <see cref="Client.TerminalSpeed"/>; the client feeds its
        /// effective per-instance setting before each read.
        /// </summary>
        internal string TerminalSpeed { get; set; } = Client.TerminalSpeed;

        /// <summary>
        /// Gets or sets a value indicating whether text read is echoed to the console.
        /// Defaults to <see cref="Client.IsWriteConsole"/>.
        /// </summary>
        internal bool IsWriteConsole { get; set; } = Client.IsWriteConsole;

        /// <summary>
        /// Gets or sets whether a server <c>DO ECHO</c> may be accepted (RFC 857).
        /// The client feeds its effective per-instance setting before each read.
        /// </summary>
        internal bool AllowRemoteEcho { get; set; }

        /// <summary>
        /// Whether the peer is currently echoing our input (we sent <c>DO ECHO</c>,
        /// RFC 857): local console echo is suppressed while true.
        /// </summary>
        internal bool PeerEchoing => Negotiation.IsEnabledByPeer((int)Options.Echo);

        /// <summary>
        /// Whether a read is written to the console: requested via
        /// <see cref="IsWriteConsole"/> and not suppressed by peer echo (RFC 857).
        /// </summary>
        internal bool LocalEchoEnabled => IsWriteConsole && !PeerEchoing;

        /// <summary>
        /// Gets or sets a value indicating whether a received BEL rings the console bell.
        /// Defaults to <c>true</c>; the client feeds its effective per-instance setting before each read.
        /// </summary>
        internal bool EnableBell { get; set; } = true;

        /// <summary>
        /// Gets or sets the encoding used to decode received bytes. When null
        /// (the default), the legacy <see cref="StringBuilder"/> accumulation is
        /// returned verbatim for bit-identical behavior.
        /// </summary>
        internal Encoding? TextEncoding { get; set; }

        /// <summary>
        /// Gets or sets the terminal width reported via NAWS. Zero (the default)
        /// means auto-detect from the console, falling back to 80.
        /// </summary>
        internal int WindowWidth { get; set; }

        /// <summary>
        /// Gets or sets the terminal height reported via NAWS. Zero (the default)
        /// means auto-detect from the console, falling back to 24.
        /// </summary>
        internal int WindowHeight { get; set; }

        /// <summary>
        /// Gets or sets the hook invoked with the effective size each time a
        /// NAWS report is sent. The client feeds a recorder before each read so
        /// <c>RefreshWindowSizeAsync</c> can skip unchanged sizes.
        /// </summary>
        internal Action<ushort, ushort>? NawsSizeSent { get; set; }

        /// <summary>
        /// Gets or sets the server-role subnegotiation consumer. Invoked with
        /// every non-LINEMODE payload before the SEND gate; returning
        /// <c>true</c> consumes it (server sent SEND and this is the IS/INFO
        /// answer, or unsolicited NAWS/ENV-INFO). Returning <c>false</c> (or unset)
        /// falls through to the normal SEND-responder path, so direct-handler
        /// behavior (including stray-IS <c>WONT</c>) is unchanged.
        /// </summary>
        internal Func<int, List<byte>, bool>? SubnegotiationResponse { get; set; }

        /// <summary>
        /// Gets or sets the hook invoked when an unsuppressed Go-Ahead arrives
        /// (RFC 858 §5: GA is a NOP only while Suppress-GA is in effect on the
        /// peer's transmit path). The owning client or session feeds a raiser
        /// before each read; a directly-constructed handler leaves this null and
        /// the signal is dropped.
        /// </summary>
        internal Action? GoAheadReceived { get; set; }

        /// <summary>
        /// Gets or sets the LINEMODE MODE mask and SLC table (RFC 1184),
        /// shared with the owning client or session so negotiation memory
        /// survives across reads. A directly-constructed handler uses a fresh
        /// instance.
        /// </summary>
        internal LinemodeState Linemode { get; set; } = new();

        /// <summary>
        /// Gets or sets whether inbound LINEMODE MODE masks use the server rules
        /// (<see cref="LinemodeState.ApplyModeAsServer"/>) instead of the client
        /// rules (<see cref="LinemodeState.ApplyMode"/>). Set by the server
        /// session; a client-side handler keeps the default client behavior.
        /// SLC handling is role-symmetric and needs no flag.
        /// </summary>
        internal bool ApplyLinemodeAsServer { get; set; }

        /// <summary>
        /// Gets or sets the per-instance log hook. The client feeds its effective
        /// setting before each read.
        /// </summary>
        internal Action<string>? Log { get; set; }

        /// <summary>
        /// Gets or sets the value reported for the well-known <c>USER</c> variable
        /// in RFC 1408 ENVIRON responses. The client feeds its effective setting
        /// before each read.
        /// </summary>
        internal string? EnvironmentUser { get; set; }

        /// <summary>
        /// Gets or sets the value reported for the well-known <c>DISPLAY</c> variable
        /// in RFC 1408 ENVIRON responses. The client feeds its effective setting
        /// before each read.
        /// </summary>
        internal string? EnvironmentDisplay { get; set; }

        /// <summary>
        /// Gets or sets the user-defined variables reported as <c>USERVAR</c> entries
        /// in RFC 1408 ENVIRON responses. The client feeds its effective setting
        /// before each read.
        /// </summary>
        internal IReadOnlyDictionary<string, string> EnvironmentUserVars { get; set; } =
          new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// Gets or sets the X display location reported in RFC 1096
        /// X-DISPLAY-LOCATION IS answers. The client feeds its effective
        /// setting before each read. Null answers no SEND (logged, silent).
        /// </summary>
        internal string? XDisplayLocation { get; set; }

        /// <summary>
        /// Gets or sets the hook invoked when <c>DO LOGOUT</c> (RFC 727)
        /// arrives. LOGOUT is always refused (WONT) per the RFC 1143 machine;
        /// the hook signals the logout intent so the caller can close. The
        /// client feeds a raiser before each read; a directly-constructed
        /// handler leaves this null and the signal is dropped.
        /// </summary>
        internal Action? LogoutRequested { get; set; }

        /// <summary>
        /// Gets or sets the hook invoked when <c>IAC EOR</c> (239, RFC 885)
        /// arrives. Like GA, the boundary is surfaced and carries no data.
        /// </summary>
        internal Action? EorReceived { get; set; }

        /// <summary>
        /// Gets or sets the location string sent spontaneously as
        /// <c>IAC SB SNDLOC &lt;location&gt; IAC SE</c> (RFC 779) after
        /// answering <c>DO SNDLOC</c> with <c>WILL</c>. Null (the default)
        /// sends nothing. The client feeds its effective setting before each read.
        /// </summary>
        internal string? SendLocation { get; set; }

        /// <summary>
        /// Gets the last location string received via SNDLOC subnegotiation.
        /// </summary>
        internal string? LastLocation { get; private set; }

        /// <summary>
        /// Gets or sets the hook invoked with each received SNDLOC location.
        /// </summary>
        internal Action<string>? LocationReceived { get; set; }

        /// <summary>
        /// Gets whether remote flow control is currently on (RFC 1372 mode
        /// ON/OFF). Defaults to <c>true</c> per the RFC 1372 initial state.
        /// </summary>
        internal bool LineflowEnabled { get; private set; } = true;

        /// <summary>
        /// Gets whether output restarts only on XON (RFC 1372 RESTART_XON)
        /// rather than on any character (RESTART_ANY). Defaults to
        /// <c>false</c> (RESTART_XON), matching the reference default send.
        /// </summary>
        internal bool LineflowXonAny { get; private set; }

        /// <summary>
        /// Gets or sets the hook invoked with each received LFLOW mode byte.
        /// </summary>
        internal Action<byte>? LineflowReceived { get; set; }

        /// <summary>
        /// Gets or sets the LFLOW restart mode this side sends as a server
        /// (RFC 1372): <c>true</c> sends RESTART_ANY, <c>false</c> (the
        /// default) sends RESTART_XON.
        /// </summary>
        internal bool SendLineflowRestartAny { get; set; }

        /// <summary>
        /// Gets or sets whether this handler sends the LFLOW mode SB after
        /// agreeing to a peer WILL (server role, RFC 1372). Defaults to
        /// <c>false</c>: only a server may send SB LFLOW, so a client-side
        /// handler never volunteers one. Fed by the server session.
        /// </summary>
        internal bool SendLineflowAsServer { get; set; }

        /// <summary>
        /// Gets or sets the character sets offered in CHARSET REQUEST answers
        /// (RFC 2066), in preference order. Defaults to UTF-8.
        /// </summary>
        internal IReadOnlyList<string> CharsetOffers { get; set; } = ["UTF-8"];

        /// <summary>
        /// Gets the character set agreed via CHARSET ACCEPTED, or null when
        /// none was negotiated yet. Selecting <see cref="TextEncoding"/> from
        /// it stays the caller's choice; negotiation never reassigns it silently.
        /// </summary>
        internal string? NegotiatedCharset { get; private set; }

        /// <summary>
        /// Gets or sets the hook invoked with the accepted character-set name.
        /// </summary>
        internal Action<string>? CharsetAccepted { get; set; }

        /// <summary>
        /// Gets or sets the hook invoked when a CHARSET offer is rejected.
        /// </summary>
        internal Action? CharsetRejected { get; set; }

        /// <summary>
        /// Gets or sets the hook invoked with the codec name when a
        /// SyncTERM font-selection sequence switches the read encoding.
        /// Fires only for automatic switches (explicit
        /// <see cref="TextEncoding"/> is never overridden).
        /// </summary>
        internal Action<string>? SyncTermFontDetected { get; set; }

        /// <summary>
        /// Gets or sets whether MCCP2/MCCP3 compression (options 86/87) may be
        /// agreed. Defaults to <c>false</c>: like the reference, compression
        /// is refused unless opted in, and must stay off over TLS (CRIME/BREACH).
        /// </summary>
        internal bool EnableMccp { get; set; }

        /// <summary>
        /// Gets whether an MCCP2 (server-to-client) compressed stream was
        /// started via an empty <c>IAC SB MCCP2 IAC SE</c>. Framing-level only:
        /// zlib decoding stays the caller's, fed from this flag and
        /// <see cref="Mccp2StartReceived"/>.
        /// </summary>
        internal bool Mccp2Active { get; private set; }

        /// <summary>
        /// Gets whether an MCCP3 (client-to-server) compressed stream was
        /// started via an empty <c>IAC SB MCCP3 IAC SE</c>. See
        /// <see cref="Mccp2Active"/> for the framing-only scope.
        /// </summary>
        internal bool Mccp3Active { get; private set; }

        /// <summary>
        /// Gets or sets the hook invoked when the peer starts MCCP2.
        /// </summary>
        internal Action? Mccp2StartReceived { get; set; }

        /// <summary>
        /// Gets or sets the hook invoked when the peer starts MCCP3.
        /// </summary>
        internal Action? Mccp3StartReceived { get; set; }

        /// <summary>
        /// Gets or sets whether the MUD options (MSDP 69, MSSP 70, MSP 90,
        /// MXP 91, ZMP 93, Aardwolf 102, ATCP 200, GMCP 201) may be agreed.
        /// Defaults to <c>true</c>. Subnegotiations surface through
        /// <see cref="MudSubnegotiationReceived"/>; encoding stays the caller's
        /// via <see cref="MudProtocol"/>.
        /// </summary>
        internal bool EnableMudOptions { get; set; } = true;

        /// <summary>
        /// Gets or sets whether COM port control (option 44, RFC 2217 framing
        /// level) may be agreed. Defaults to <c>true</c>. Payloads surface
        /// through <see cref="ComPortReceived"/>; modem-line semantics stay
        /// the caller's.
        /// </summary>
        internal bool EnableComPort { get; set; } = true;

        /// <summary>
        /// Gets or sets the hook invoked with MUD subnegotiation payloads as
        /// <c>(option, payload)</c>, the payload without the option byte.
        /// </summary>
        internal Action<int, byte[]>? MudSubnegotiationReceived { get; set; }

        /// <summary>
        /// Gets or sets the hook invoked with COM port control payloads
        /// (without the option byte).
        /// </summary>
        internal Action<byte[]>? ComPortReceived { get; set; }

        /// <summary>
        /// Gets or sets the process-wide log hook. Falls back to
        /// <see cref="System.Diagnostics.Debug"/> when unset.
        /// </summary>
        internal static Action<string>? Trace { get; set; }

        /// <summary>
        /// Idle delay between read polls when no data is available.
        /// </summary>
        internal int MillisecondReadDelay { get; set; } = 16;

        private bool IsResponsePending
        {
            get
            {
                return pushbackByte.HasValue || byteStream.Available > 0;
            }
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        /// <param name="disposing"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                // The handler never owns the byte stream (the client creates one
                // handler per read over its own long-lived stream), so it must not
                // dispose it — doing so forced Client to leak the handler (CA2000).
                // It must also not cancel the token source unless it created it:
                // Client passes its own InternalCancellation, which must survive
                // the read.
                if (isCancellationTokenOwned)
                {
                    internalCancellation.Dispose();
                }
            }
        }

        private static DateTime ExtendRollingTimeout(TimeSpan timeout)
        {
            // Re-arm the incremental window to 1% of the full timeout.
            return DateTime.UtcNow.AddTicks(timeout.Ticks / 100);
        }

        private static bool IsWaitForInitialResponse(DateTime endInitialTimeout, bool isInitialResponseReceived)
        {
            return !isInitialResponseReceived && DateTime.UtcNow < endInitialTimeout;
        }

        private static bool IsTimeoutExpired(DateTime timeout)
        {
            return DateTime.UtcNow >= timeout;
        }

        private static bool IsInitialResponseReceived(StringBuilder sb)
        {
            return sb.Length > 0;
        }

        private async Task<bool> IsWaitForIncrementalResponse(DateTime rollingTimeout)
        {
            var result = DateTime.UtcNow < rollingTimeout;
            await Task.Delay(MillisecondReadDelay, internalCancellation.Token).ConfigureAwait(false);
            return result;
        }

        private void WriteLog(string message)
        {
            if (Log != null)
            {
                Log(message);
            }

            if (Trace != null)
            {
                Trace(message);
            }

            System.Diagnostics.Debug.WriteLine(message);
        }

        /// <summary>
        /// Reads the next byte, honouring the single-byte pushback stash.
        /// I/O failures (<see cref="System.IO.IOException"/>, including read
        /// timeouts) and over-reads surface as -1; anything else the stream
        /// throws (notably <see cref="System.Net.Sockets.SocketException"/>)
        /// propagates to the caller.
        /// </summary>
        private int ReadNextByte()
        {
            if (pushbackByte.HasValue)
            {
                var pending = pushbackByte.Value;
                pushbackByte = null;
                return pending;
            }

            return TryReadByte();
        }

        /// <summary>
        /// Blind continuation read: never polls <see cref="IByteStream.Available"/>
        /// (fakes and real sockets alike may report 0 mid-sequence), mapping I/O
        /// and over-read failures to -1.
        /// </summary>
        private int TryReadByte()
        {
            try
            {
                return byteStream.ReadByte();
            }
            catch (System.IO.IOException)
            {
                return -1;
            }
            catch (InvalidOperationException)
            {
                return -1;
            }
        }

        /// <summary>
        /// Separate TELNET commands from text. Handle non-printable characters.
        /// </summary>
        /// <param name="sb">The incoming message.</param>
        /// <param name="rawBytes">The raw data bytes backing <paramref name="sb"/> (used when <see cref="TextEncoding"/> is set).</param>
        /// <param name="opByteCounts">Parallel to <paramref name="sb"/>: bytes of <paramref name="rawBytes"/> per appended char.</param>
        /// <param name="echoBytes">Parallel to <paramref name="sb"/>: the original data byte to echo per char, or null for command markers (never echoed) and rendering continuations.</param>
        /// <returns>True if response is pending.</returns>
        private async Task<bool> RetrieveAndParseResponse(StringBuilder sb, List<byte> rawBytes, List<int> opByteCounts, List<byte?> echoBytes)
        {
            // RFC 854 Synch trigger: a pending urgent byte enters discard mode.
            // Poll-gated and TCP-only, so fakes and pipes never see it; the urgent
            // byte itself is consumed by the probe, and in-band IAC DM (below)
            // ends the mode.
            var synch = await RetrieveSynchDiscardAsync(sb, rawBytes, opByteCounts, echoBytes).ConfigureAwait(false);
            if (synch.HasValue)
            {
                return synch.Value;
            }

            if (IsResponsePending)
            {
                var input = ReadNextByte();
                switch (input)
                {
                    case -1:
                        break;
                    case IacByte:
                        var inputVerb = TryReadByte();
                        if (inputVerb == -1)
                        {
                            // do nothing
                        }
                        else if (inputVerb == IacByte)
                        {
                            // Escaped literal data byte 255: one char + one raw byte,
                            // not the decimal string "255".
                            AppendRecorded(sb, rawBytes, opByteCounts, echoBytes, (char)IacByte);
                        }
                        else
                        {
                            await InterpretNextAsCommand(sb, rawBytes, opByteCounts, echoBytes, inputVerb).ConfigureAwait(false);
                        }

                        break;
                    case 1: // Start of Heading
                        AppendRecorded(sb, rawBytes, opByteCounts, echoBytes, "\n \n", 1);
                        break;
                    case 2: // Start of Text
                        AppendRecorded(sb, rawBytes, opByteCounts, echoBytes, "\t", 2);
                        break;
                    case 3: // End of Text or "break" CTRL+C
                        AppendRecorded(sb, rawBytes, opByteCounts, echoBytes, "^C", 3);
                        WriteLog("^C");
                        break;
                    case 4: // End of Transmission
                        AppendRecorded(sb, rawBytes, opByteCounts, echoBytes, "^D", 4);
                        break;
                    case 5: // Enquiry
                        await byteStream.WriteByteAsync(6, internalCancellation.Token).ConfigureAwait(false); // Send ACK
                        break;
                    case 6: // Acknowledge
                            // We got an ACK
                        break;
                    case 7: // Bell character
                        if (EnableBell)
                        {
#pragma warning disable CA1031 // Do not catch general exception types
                            try
                            {
                                Console.Beep();
                            }
                            catch (Exception ex)
                            {
                                WriteLog(ex.Message);
                            }
#pragma warning restore CA1031 // Do not catch general exception types
                        }

                        break;
                    case 8: // Backspace
                            // Erase the previously decoded character, if any.
                        EraseLastChar(sb, rawBytes, opByteCounts, echoBytes);
                        break;
                    case 11: // Vertical TAB
                    case 12: // Form Feed
                        AppendRecorded(sb, rawBytes, opByteCounts, echoBytes, Environment.NewLine, (byte)input);
                        break;
                    case 13: // Carriage Return: NUL after CR is ignored (CR NUL -> CR);
                             // LF after CR is data (CR LF stays CR LF).
                        AppendRecorded(sb, rawBytes, opByteCounts, echoBytes, "\r", 13);
                        if (byteStream.Available > 0)
                        {
                            var following = TryReadByte();
                            if (following != 0 && following != -1)
                            {
                                pushbackByte = following;
                            }
                        }

                        break;
                    case 21:
                        AppendRecorded(sb, rawBytes, opByteCounts, echoBytes, "NAK: Retransmit last message.", 21);
                        WriteLog("ERROR NAK: Retransmit last message.");
                        break;
                    case 31: // Unit Separator
                        AppendRecorded(sb, rawBytes, opByteCounts, echoBytes, ",", 31);
                        break;
                    default:
                        AppendRecorded(sb, rawBytes, opByteCounts, echoBytes, (char)input);
                        break;
                }

                return true;
            }

            return false;
        }

        private static void AppendRecorded(StringBuilder sb, List<byte> rawBytes, List<int> opByteCounts, List<byte?> echoBytes, string text, byte? echoByte = null)
        {
            sb.Append(text);
            rawBytes.AddRange(Encoding.ASCII.GetBytes(text));
            // Char-aligned: ASCII yields exactly one byte per char, so each sb
            // char maps to exactly one count entry (the documented invariant).
            // Only the first char carries the original data byte for echo;
            // command markers pass none and are never echoed back.
            for (var i = 0; i < text.Length; i++)
            {
                opByteCounts.Add(1);
                echoBytes.Add(i == 0 ? echoByte : null);
            }
        }

        private static void AppendRecorded(StringBuilder sb, List<byte> rawBytes, List<int> opByteCounts, List<byte?> echoBytes, char c)
        {
            sb.Append(c);
            // Data bytes are Latin-1 by definition here: the default (null
            // encoding) path returns sb.ToString() verbatim, so the recorded byte
            // only matters for explicit TextEncoding decoding.
            rawBytes.Add((byte)c);
            opByteCounts.Add(1);
            // The char overload is only used for genuine data (escaped IAC and
            // the default data case), so the byte always echoes.
            echoBytes.Add((byte)c);
        }

        /// <summary>
        /// Printable proof-alive sent in reply to AYT (RFC 854).
        /// </summary>
        private static readonly byte[] AytProofAlive = "[AYT received]\r\n"u8.ToArray();

        /// <summary>
        /// Erases the last decoded character, if any (BS handling and RFC 854 EC).
        /// </summary>
        private static void EraseLastChar(StringBuilder sb, List<byte> rawBytes, List<int> opByteCounts, List<byte?> echoBytes)
        {
            if (sb.Length > 0)
            {
                sb.Length--;
                var taken = opByteCounts[^1];
                opByteCounts.RemoveAt(opByteCounts.Count - 1);
                rawBytes.RemoveRange(rawBytes.Count - taken, taken);
                echoBytes.RemoveAt(echoBytes.Count - 1);
            }
        }

        /// <summary>
        /// Erases back to (but not including) the last CR LF, or everything if
        /// there is none (RFC 854 EL).
        /// </summary>
        private static void EraseLine(StringBuilder sb, List<byte> rawBytes, List<int> opByteCounts, List<byte?> echoBytes)
        {
            var marker = sb.ToString().LastIndexOf("\r\n", StringComparison.Ordinal);
            var keep = marker < 0 ? 0 : marker + 2;
            var removeBytes = 0;
            for (var i = keep; i < sb.Length; i++)
            {
                removeBytes += opByteCounts[i];
            }

            opByteCounts.RemoveRange(keep, sb.Length - keep);
            echoBytes.RemoveRange(keep, sb.Length - keep);
            rawBytes.RemoveRange(rawBytes.Count - removeBytes, removeBytes);
            sb.Length = keep;
        }

        /// <summary>
        /// We received a TELNET command. Handle it. Control signals (P2) edit
        /// the accumulation buffer in place, so the decoded text and the raw
        /// bytes backing it stay in sync for explicit-<see cref="TextEncoding"/>
        /// decoding.
        /// </summary>
        /// <param name="sb">The incoming message.</param>
        /// <param name="rawBytes">The raw data bytes backing <paramref name="sb"/> (used when <see cref="TextEncoding"/> is set).</param>
        /// <param name="opByteCounts">Parallel to <paramref name="sb"/>: bytes of <paramref name="rawBytes"/> per appended char.</param>
        /// <param name="echoBytes">Parallel to <paramref name="sb"/>: the original data byte to echo per char, or null for command markers (never echoed) and rendering continuations.</param>
        /// <param name="inputVerb">The command we received.</param>
        private async Task InterpretNextAsCommand(StringBuilder sb, List<byte> rawBytes, List<int> opByteCounts, List<byte?> echoBytes, int inputVerb)
        {
            WriteLog(Enum.GetName(typeof(Commands), inputVerb) ?? inputVerb.ToString());
            switch (inputVerb)
            {
                case (int)Commands.InterruptProcess:
                    WriteLog("Interrupt Process (IP) received.");
                    CancelPendingReads();
                    return;
                case (int)Commands.AreYouThere:
                    // RFC 854: answer AYT with printable proof that we are alive.
                    WriteLog("Are You There (AYT) received; sending proof-alive.");
                    await byteStream.WriteAsync(AytProofAlive, 0, AytProofAlive.Length, internalCancellation.Token).ConfigureAwait(false);
                    return;
                case (int)Commands.AbortOutput:
                    // This design has no output queue (writes go straight to the
                    // stream), so there is nothing to discard: consume and log.
                    WriteLog("Abort Output (AO) received; no queued output to discard.");
                    return;
                case (int)Commands.EraseCharacter:
                    // RFC 854 EC: erase the last undeleted character, same as BS.
                    EraseLastChar(sb, rawBytes, opByteCounts, echoBytes);
                    return;
                case (int)Commands.EraseLine:
                    // RFC 854 EL: erase back to (but not including) the last CR LF.
                    EraseLine(sb, rawBytes, opByteCounts, echoBytes);
                    return;
                case (int)Commands.Break:
                    // Surface BRK distinctly instead of swallowing it silently.
                    WriteLog("Break (BRK) received.");
                    AppendRecorded(sb, rawBytes, opByteCounts, echoBytes, "[BRK]");
                    return;
                case (int)Commands.EndOfFile:
                    // RFC 1184 §2.5: notify the process of end of file.
                    WriteLog("End of file (EOF) received.");
                    AppendRecorded(sb, rawBytes, opByteCounts, echoBytes, "[EOF]");
                    return;
                case (int)Commands.Suspend:
                    // RFC 1184 §2.5: suspend is a no-op when unsupported, but still surfaced.
                    WriteLog("Suspend (SUSP) received.");
                    AppendRecorded(sb, rawBytes, opByteCounts, echoBytes, "[SUSP]");
                    return;
                case (int)Commands.Abort:
                    // RFC 1184 §2.5: terminate-only abort, surfaced like BRK.
                    WriteLog("Abort (ABORT) received.");
                    AppendRecorded(sb, rawBytes, opByteCounts, echoBytes, "[ABORT]");
                    return;
                case (int)Commands.EndOfRecord:
                    // RFC 885: IAC EOR marks a prompt boundary with no
                    // subnegotiation. Surfaced through the hook like GA; the
                    // boundary itself never enters the data stream.
                    WriteLog("End of record (EOR) received.");
                    EorReceived?.Invoke();
                    return;
                case (int)Commands.SubnegotiationEnd:
                case (int)Commands.NoOperation:
                case (int)Commands.DataMark:
                    // Stray SE, NOP, and DM in normal mode carry no data: consume silently.
                    return;
                case (int)Commands.GoAhead:
                    // RFC 858 §5: GA is a NOP only while Suppress-GA is in effect on
                    // the peer's transmit path; otherwise it is the NVT turn-taking
                    // signal, surfaced through the GoAheadReceived hook.
                    if (Negotiation.IsEnabledByPeer((int)Options.SuppressGoAhead))
                    {
                        return;
                    }

                    WriteLog("Go Ahead (GA) received; Suppress-GA not in effect.");
                    GoAheadReceived?.Invoke();
                    return;
                case (int)Commands.Dont:
                case (int)Commands.Wont:
                case (int)Commands.Do:
                case (int)Commands.Will:
                    // All four negotiation verbs flow through ReplyToCommand, which
                    // consults the persistent RFC 1143 state: the option byte always
                    // belongs to the command (never leaks into data), and refusals or
                    // repeats are answered only when the state machine says so.
                    await ReplyToCommand(inputVerb).ConfigureAwait(false);
                    return;
                case (int)Commands.Subnegotiation:
                    await PerformNegotiation().ConfigureAwait(false);
                    return;
                default:
                    // RFC 856 §5: IAC followed by a byte that is not a defined TELNET
                    // command has the same meaning as IAC NOP — consume it silently
                    // (never data, never a reply).
                    return;
            }
        }

        /// <summary>
        /// We received a request to perform sub negotiation on a TELNET option.
        /// The terminal type, speed, and window size are taken from the settable
        /// properties on this handler (fed per read from the client's settings).
        /// </summary>
        private async Task PerformNegotiation()
        {
            var inputOption = TryReadByte();
            if (inputOption == -1 || inputOption == IacByte)
            {
                // Truncated subnegotiation, or an IAC where the option byte belongs:
                // either way we cannot frame the payload, so ignore it.
                return;
            }

            // Scan to IAC SE. The payload is capped: over-long input keeps being
            // consumed (so the stream resynchronises) but is then ignored.
            // STATUS (RFC 859) uses inner framing — a bare SE byte terminates
            // and SE SE escapes a literal SE — so the scan is option-aware.
            var payload = new List<byte>();
            var overCap = false;
            var statusFraming = inputOption == (int)Options.Status;
            while (true)
            {
                var b = TryReadByte();
                if (b == -1)
                {
                    return;
                }

                if (statusFraming && b == SeByte)
                {
                    var following = TryReadByte();
                    if (following == -1)
                    {
                        return;
                    }

                    if (following == SeByte)
                    {
                        AddPayloadByte((byte)SeByte);
                        continue;
                    }

                    // Bare SE terminates STATUS; the following byte belongs to
                    // the subsequent stream, so stash it for the next read.
                    // (The scan path always consumes a pending pushback before
                    // reaching SB, so the stash is free here.)
                    pushbackByte = following;
                    break;
                }

                if (b == IacByte)
                {
                    var following = TryReadByte();
                    if (following == -1)
                    {
                        return;
                    }

                    if (following == SeByte)
                    {
                        break;
                    }

                    if (following == IacByte)
                    {
                        // Escaped literal IAC inside the payload.
                        AddPayloadByte(IacByte);
                        continue;
                    }

                    // IAC followed by anything else: framing is lost, give up.
                    return;
                }

                AddPayloadByte((byte)b);
            }

            void AddPayloadByte(byte value)
            {
                if (overCap)
                {
                    return;
                }

                if (payload.Count < MaxSubnegotiationBytes)
                {
                    payload.Add(value);
                }
                else
                {
                    overCap = true;
                }
            }

            if (overCap)
            {
                return;
            }

            if (payload.Count == 0)
            {
                // MCCP2/MCCP3 start on an empty SB, and several MUD options
                // allow one; anything else empty is dropped (unchanged).
                if (IsEmptySbAllowed(inputOption))
                {
                    ReplyEmptySb(inputOption);
                }

                return;
            }

            if (SubnegotiationResponse?.Invoke(inputOption, payload) is true)
            {
                // Server-role consumer (TTYPE/TSPEED/ENVIRON IS or INFO, inbound
                // NAWS, LINEMODE import requests): collected or answered by the
                // session, nothing further to do. Null for clients and
                // directly-constructed handlers, so their path is unchanged.
                return;
            }

            if (inputOption == (int)Options.LineMode)
            {
                // LINEMODE payloads start with their own subcommand (MODE /
                // FORWARDMASK / SLC), not SEND, so they bypass the SEND gate below.
                // In particular a server DO FORWARDMASK ([253, 2, …]) must be
                // refused in-band, never mistaken for a stray SEND.
                await ReplyLinemodeAsync(payload).ConfigureAwait(false);
                return;
            }

            if (inputOption == (int)Options.SendLocation)
            {
                // RFC 779: the SB carries the raw ASCII location with no
                // SEND/IS discrimination.
                ReplySendLocation(payload);
                return;
            }

            if (inputOption == (int)Options.RemoteFlowControl)
            {
                // RFC 1372: the SB carries a single mode byte (0-3), no verbs.
                await ReplyLineflowAsync(payload).ConfigureAwait(false);
                return;
            }

            if (inputOption == (int)Options.COMPortControl)
            {
                // RFC 2217 framing level: surface the raw payload; modem-line
                // semantics stay the caller's.
                ComPortReceived?.Invoke([.. payload]);
                return;
            }

            if (IsMudOption(inputOption))
            {
                MudSubnegotiationReceived?.Invoke(inputOption, [.. payload]);
                return;
            }

            if (inputOption == (int)Options.Mccp2 || inputOption == (int)Options.Mccp3)
            {
                // A non-empty MCCP SB is a protocol error: framing-level
                // support only starts compression on the empty SB.
                WriteLog("Ignoring non-empty MCCP subnegotiation.");
                return;
            }

            if (inputOption == (int)Options.CharacterSet && payload[0] != CharsetProtocol.Request)
            {
                // ACCEPTED/REJECTED answers (and unimplemented table verbs)
                // never look like SEND, so they bypass the gate below.
                await ReplyCharsetAnswerAsync(payload).ConfigureAwait(false);
                return;
            }

            if (payload[0] != 1) // Sub-negotiation SEND command.
            {
                // If we get lost just send WONT to end the negotiation
                await SendWont(inputOption).ConfigureAwait(false);
                return;
            }

            await ReplySendAsync(inputOption, payload).ConfigureAwait(false);
        }

        /// <summary>
        /// Answer a SEND subnegotiation for an option with the SEND/IS shape:
        /// terminal type, terminal speed, environment, X display, status, or
        /// character set.
        /// </summary>
        /// <param name="inputOption">The option under negotiation.</param>
        /// <param name="payload">The full received subnegotiation payload, SEND first.</param>
        private async Task ReplySendAsync(int inputOption, List<byte> payload)
        {
            switch (inputOption)
            {
                case (int)Options.TerminalType:
                    await SendNegotiation(inputOption, TerminalTypeProvider?.Invoke() ?? TerminalType).ConfigureAwait(false);
                    break;
                case (int)Options.TerminalSpeed:
                    string? speed = TerminalSpeedProtocol.Normalize(TerminalSpeed);
                    if (speed is null)
                    {
                        WriteLog("Skipping TERMINAL-SPEED reply: malformed speed (want \"<tx>,<rx>\" decimal).");
                        break;
                    }

                    await SendNegotiation(inputOption, speed).ConfigureAwait(false);
                    break;
                case (int)Options.OldEnvironment:
                case (int)Options.NewEnvironment:
                    await ReplyEnvironmentAsync(inputOption, payload).ConfigureAwait(false);
                    break;
                case (int)Options.XDisplay:
                    // RFC 1096 §4: IS only answers SEND, never spontaneous. Null
                    // means unconfigured: skip silently like a malformed speed.
                    if (XDisplayLocation is null)
                    {
                        WriteLog("Skipping X-DISPLAY-LOCATION reply: no display configured.");
                        break;
                    }

                    await SendNegotiation(inputOption, XDisplayLocation).ConfigureAwait(false);
                    break;
                case (int)Options.Status:
                    // RFC 859 §5: only the WILL-sender answers SEND with IS. Anything
                    // else (never agreed, or still negotiating) gets WONT — symmetric
                    // with the stray-IS path above.
                    if (!Negotiation.IsEnabledByUs((int)Options.Status))
                    {
                        await SendWont(inputOption).ConfigureAwait(false);
                        break;
                    }

                    await ReplyStatusAsync().ConfigureAwait(false);
                    break;
                case (int)Options.CharacterSet:
                    // RFC 2066: REQUEST shares the SEND byte value (1), so it
                    // arrives here; ACCEPTED/REJECTED bypass the gate above.
                    await ReplyCharsetRequestAsync(payload).ConfigureAwait(false);
                    break;
                default:
                    // We don't handle other sub negotiation options yet.
                    WriteLog("Request to negotiate: " + Enum.GetName(typeof(Options), inputOption));
                    break;
            }
        }

        private Task SendWont(int inputOption)
        {
            var outBuffer = new byte[3];
            outBuffer[0] = (byte)Commands.InterpretAsCommand;
            outBuffer[1] = (byte)Commands.Wont;
            outBuffer[2] = (byte)inputOption;
            return byteStream.WriteAsync(outBuffer, 0, outBuffer.Length, internalCancellation.Token);
        }

        /// <summary>
        /// Send the sub negotiation response to the server.
        /// </summary>
        /// <param name="inputOption">The option we are negotiating.</param>
        /// <param name="optionMessage">The setting for <paramref name="inputOption"/>.</param>
        private Task SendNegotiation(int inputOption, string optionMessage)
        {
            WriteLog("Sending: " + Enum.GetName(typeof(Options), inputOption) + " Setting: " + optionMessage);
            return SendNegotiation(inputOption, [EnvironmentProtocol.Is, .. ToNegotiationBytes(optionMessage)]);
        }

        private static byte[] ToNegotiationBytes(string optionMessage)
        {
            // Latin-1 one-to-one mapping (manual truncating loop, kept to pin the
            // historical byte mapping). IAC escaping happens in the byte[]
            // overload below, not here.
            var bytes = new byte[optionMessage.Length];
            for (var i = 0; i < optionMessage.Length; i++)
            {
                bytes[i] = (byte)optionMessage[i];
            }

            return bytes;
        }

        /// <summary>
        /// Send the sub negotiation response to the server, escaping literal IAC
        /// bytes in the payload by doubling them (RFC 854).
        /// </summary>
        /// <param name="inputOption">The option we are negotiating.</param>
        /// <param name="verbFirstPayload">The payload bytes starting with the subnegotiation
        /// verb (<c>IS</c>, <c>INFO</c>, ...), without IAC SB/SE framing.</param>
        private Task SendNegotiation(int inputOption, byte[] verbFirstPayload)
        {
            var frame = EnvironmentProtocol.FrameSubnegotiation(inputOption, verbFirstPayload);
            return byteStream.WriteAsync(frame, 0, frame.Length, internalCancellation.Token);
        }

        /// <summary>
        /// Answer an RFC 1408/1572 ENVIRON SEND with an IS built from the
        /// configured environment values. The SEND type list (after the verb)
        /// is mirrored. Old (36) and new (39) forms share framing and verbs;
        /// the answer goes out on whichever option asked.
        /// </summary>
        /// <param name="inputOption">The option under negotiation (old or new).</param>
        /// <param name="payload">The full received subnegotiation payload, verb first.</param>
        private Task ReplyEnvironmentAsync(int inputOption, List<byte> payload)
        {
            var response = EnvironmentProtocol.BuildResponse(
              EnvironmentProtocol.Is,
              payload.Skip(1),
              EnvironmentUser,
              EnvironmentDisplay,
              EnvironmentUserVars);
            WriteLog("Sending: " + Enum.GetName(typeof(Options), inputOption));
            return SendNegotiation(inputOption, response);
        }

        /// <summary>
        /// Gets whether <paramref name="inputOption"/> allows an empty
        /// <c>IAC SB &lt;opt&gt; IAC SE</c>: MCCP2/MCCP3 start compression
        /// with one, and several MUD options permit one (reference
        /// <c>_EMPTY_SB_OK</c> set).
        /// </summary>
        /// <param name="inputOption">The option under negotiation.</param>
        private static bool IsEmptySbAllowed(int inputOption)
        {
            return inputOption is (int)Options.Mccp2 or (int)Options.Mccp3 or
              (int)Options.Msp or (int)Options.Mxp or (int)Options.Zmp or
              (int)Options.Aardwolf or (int)Options.Atcp;
        }

        /// <summary>
        /// Gets whether <paramref name="inputOption"/> is a MUD option with an
        /// opaque subnegotiation body (MSDP, MSSP, MSP, MXP, ZMP, Aardwolf,
        /// ATCP, GMCP).
        /// </summary>
        /// <param name="inputOption">The option under negotiation.</param>
        private static bool IsMudOption(int inputOption)
        {
            return inputOption is (int)Options.Msdp or (int)Options.Mssp or
              (int)Options.Msp or (int)Options.Mxp or (int)Options.Zmp or
              (int)Options.Aardwolf or (int)Options.Atcp or (int)Options.Gmcp;
        }

        /// <summary>
        /// Handles an empty subnegotiation for an option that allows one:
        /// MCCP2/MCCP3 arm their framing-only active flags and fire their
        /// start hooks; MUD options surface an empty payload through the MUD
        /// hook like any other body.
        /// </summary>
        /// <param name="inputOption">The option under negotiation.</param>
        private void ReplyEmptySb(int inputOption)
        {
            if (inputOption == (int)Options.Mccp2)
            {
                WriteLog("MCCP2 compression started (framing level; zlib decoding stays the caller's).");
                Mccp2Active = true;
                Mccp2StartReceived?.Invoke();
                return;
            }

            if (inputOption == (int)Options.Mccp3)
            {
                WriteLog("MCCP3 compression started (framing level; zlib decoding stays the caller's).");
                Mccp3Active = true;
                Mccp3StartReceived?.Invoke();
                return;
            }

            MudSubnegotiationReceived?.Invoke(inputOption, []);
        }

        /// <summary>
        /// Answer an RFC 859 STATUS SEND with an IS snapshot rendered from the
        /// persistent negotiation state (all options, defaults omitted). Called
        /// only when we are the agreed WILL-sender (see <see cref="WeAgree"/> and
        /// the SEND gate in <see cref="ReplySendAsync"/>): unsolicited SENDs earn
        /// WONT instead.
        /// </summary>
        private Task ReplyStatusAsync()
        {
            var items = StatusProtocol.BuildIsPayload(Negotiation);
            WriteLog("Sending: " + nameof(Options.Status));
            var frame = StatusProtocol.FrameStatusIs(items);
            return byteStream.WriteAsync(frame, 0, frame.Length, internalCancellation.Token);
        }

        /// <summary>
        /// Consumes an SNDLOC subnegotiation (RFC 779): the payload is the raw
        /// ASCII location with no SEND/IS verbs. Records it as
        /// <see cref="LastLocation"/> and fires <see cref="LocationReceived"/>.
        /// </summary>
        /// <param name="payload">The received payload (location bytes).</param>
        private void ReplySendLocation(List<byte> payload)
        {
            var location = Encoding.Latin1.GetString([.. payload]);
            WriteLog("Received SNDLOC location.");
            LastLocation = location;
            LocationReceived?.Invoke(location);
        }

        /// <summary>
        /// Consumes an LFLOW subnegotiation (RFC 1372): a single mode byte
        /// (0-3). ON/OFF flips <see cref="LineflowEnabled"/>, RESTART_ANY/XON
        /// flips <see cref="LineflowXonAny"/>; the other flag is untouched.
        /// Malformed or unsolicited bodies earn WONT like a stray IS.
        /// </summary>
        /// <param name="payload">The received payload (mode byte).</param>
        private Task ReplyLineflowAsync(List<byte> payload)
        {
            if (payload.Count != 1 || !LineflowProtocol.IsDefined(payload[0]))
            {
                WriteLog("Ignoring malformed LFLOW subnegotiation (want one mode byte 0-3).");
                return Task.CompletedTask;
            }

            if (!Negotiation.IsEnabledByPeer((int)Options.RemoteFlowControl))
            {
                return SendWont((int)Options.RemoteFlowControl);
            }

            var mode = payload[0];
            if (mode is LineflowProtocol.Off or LineflowProtocol.On)
            {
                LineflowEnabled = mode == LineflowProtocol.On;
            }
            else
            {
                LineflowXonAny = mode == LineflowProtocol.RestartXon;
            }

            WriteLog("Received LFLOW mode: " + mode);
            LineflowReceived?.Invoke(mode);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Answers a CHARSET REQUEST (RFC 2066) with ACCEPTED for the first
        /// offer this runtime supports (preferring <see cref="CharsetOffers"/>
        /// order when configured) or REJECTED when nothing matches.
        /// </summary>
        /// <param name="payload">The received payload, REQUEST first.</param>
        private Task ReplyCharsetRequestAsync(List<byte> payload)
        {
            var offers = CharsetProtocol.ParseRequest(payload);
            var candidates = CharsetOffers.Count > 0
              ? CharsetOffers.Where(offered => offers.Contains(offered, StringComparer.OrdinalIgnoreCase)).ToList()
              : offers;
            var selected = CharsetProtocol.SelectSupported(candidates);
            if (selected is null)
            {
                WriteLog("Rejecting CHARSET request: no supported offer.");
                CharsetRejected?.Invoke();
                var frame = EnvironmentProtocol.FrameSubnegotiation((int)Options.CharacterSet, [CharsetProtocol.Rejected]);
                return byteStream.WriteAsync(frame, 0, frame.Length, internalCancellation.Token);
            }

            WriteLog("Sending: " + nameof(Options.CharacterSet) + " ACCEPTED " + selected);
            NegotiatedCharset = selected;
            CharsetAccepted?.Invoke(selected);
            var accepted = EnvironmentProtocol.FrameSubnegotiation(
              (int)Options.CharacterSet, CharsetProtocol.BuildAccepted(selected));
            return byteStream.WriteAsync(accepted, 0, accepted.Length, internalCancellation.Token);
        }

        /// <summary>
        /// Consumes a CHARSET ACCEPTED/REJECTED answer (RFC 2066) to our own
        /// REQUEST. ACCEPTED records <see cref="NegotiatedCharset"/> and fires
        /// <see cref="CharsetAccepted"/>; REJECTED fires
        /// <see cref="CharsetRejected"/>. Table-transfer verbs (4-7) are
        /// logged and ignored (unimplemented, like the reference).
        /// </summary>
        /// <param name="payload">The received payload, verb first.</param>
        private Task ReplyCharsetAnswerAsync(List<byte> payload)
        {
            if (payload[0] == CharsetProtocol.Accepted)
            {
                var charset = CharsetProtocol.ParseAccepted(payload);
                WriteLog("CHARSET accepted: " + charset);
                NegotiatedCharset = charset;
                CharsetAccepted?.Invoke(charset);
                return Task.CompletedTask;
            }

            if (payload[0] == CharsetProtocol.Rejected)
            {
                WriteLog("CHARSET rejected by peer.");
                CharsetRejected?.Invoke();
                return Task.CompletedTask;
            }

            WriteLog("Ignoring unimplemented CHARSET table-transfer verb: " + payload[0]);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Sends <c>IAC EOR</c> (RFC 885). Fails (returns <c>false</c>) unless
        /// EOR is locally enabled (the peer sent <c>DO EOR</c>), mirroring the
        /// reference <c>send_eor</c> guard.
        /// </summary>
        internal async Task<bool> SendEorAsync()
        {
            if (!Negotiation.IsEnabledByUs((int)Options.EndOfRecord))
            {
                WriteLog("Cannot send EOR without receipt of DO EOR.");
                return false;
            }

            var frame = new byte[] { (byte)Commands.InterpretAsCommand, (byte)Commands.EndOfRecord };
            await byteStream.WriteAsync(frame, 0, frame.Length, internalCancellation.Token).ConfigureAwait(false);
            return true;
        }

        /// <summary>
        /// Sends our location as <c>IAC SB SNDLOC &lt;location&gt; IAC SE</c>
        /// (RFC 779, ASCII, no verbs).
        /// </summary>
        /// <param name="location">The location string.</param>
        internal Task SendLocationPayloadAsync(string location)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(location);
            WriteLog("Sending: " + nameof(Options.SendLocation));
            var frame = EnvironmentProtocol.FrameSubnegotiation(
              (int)Options.SendLocation, Encoding.ASCII.GetBytes(location));
            return byteStream.WriteAsync(frame, 0, frame.Length, internalCancellation.Token);
        }

        /// <summary>
        /// Sends the LFLOW mode (RFC 1372) as a server: RESTART_ANY when
        /// <paramref name="restartOnAny"/> is true, else RESTART_XON. Fails
        /// (returns <c>false</c>) unless the peer enabled LFLOW (sent
        /// <c>WILL LFLOW</c>), mirroring the reference server-only guard.
        /// </summary>
        /// <param name="restartOnAny">Whether any character restarts output.</param>
        internal async Task<bool> SendLineflowModeAsync(bool restartOnAny)
        {
            if (!Negotiation.IsEnabledByPeer((int)Options.RemoteFlowControl))
            {
                WriteLog("Cannot send LFLOW without receipt of WILL LFLOW.");
                return false;
            }

            var mode = restartOnAny ? LineflowProtocol.RestartAny : LineflowProtocol.RestartXon;
            WriteLog("Sending: " + nameof(Options.RemoteFlowControl) + " mode " + mode);
            var frame = EnvironmentProtocol.FrameSubnegotiation((int)Options.RemoteFlowControl, [mode]);
            await byteStream.WriteAsync(frame, 0, frame.Length, internalCancellation.Token).ConfigureAwait(false);
            return true;
        }

        /// <summary>
        /// Sends a CHARSET REQUEST (RFC 2066) offering
        /// <see cref="CharsetOffers"/>. Fails (returns <c>false</c>) unless
        /// CHARSET is enabled on either side, or an identical request is
        /// already outstanding.
        /// </summary>
        internal async Task<bool> RequestCharsetAsync()
        {
            if (!Negotiation.IsEnabledByPeer((int)Options.CharacterSet) &&
                !Negotiation.IsEnabledByUs((int)Options.CharacterSet))
            {
                WriteLog("Cannot request CHARSET without negotiating CHARSET first.");
                return false;
            }

            if (CharsetOffers.Count == 0)
            {
                WriteLog("Cannot request CHARSET with no offers configured.");
                return false;
            }

            WriteLog("Sending: " + nameof(Options.CharacterSet) + " REQUEST.");
            var frame = EnvironmentProtocol.FrameSubnegotiation(
              (int)Options.CharacterSet, CharsetProtocol.BuildRequest(CharsetOffers));
            await byteStream.WriteAsync(frame, 0, frame.Length, internalCancellation.Token).ConfigureAwait(false);
            return true;
        }

        /// <summary>
        /// Sends a MUD subnegotiation body (MSDP, MSSP, MSP, MXP, ZMP,
        /// Aardwolf, ATCP, GMCP) or a COM port control body (RFC 2217
        /// framing level). Fails (returns <c>false</c>) unless the option is
        /// enabled on either side.
        /// </summary>
        /// <param name="option">The MUD or COM-port option.</param>
        /// <param name="payload">The body bytes (without the option byte).</param>
        internal async Task<bool> SendExtensionPayloadAsync(Options option, byte[] payload)
        {
            ArgumentNullException.ThrowIfNull(payload);
            var number = (int)option;
            if (!IsMudOption(number) && number != (int)Options.COMPortControl)
            {
                throw new ArgumentOutOfRangeException(nameof(option), option, "Only MUD options and COM port control can be sent with SendExtensionPayloadAsync.");
            }

            if (!Negotiation.IsEnabledByPeer(number) && !Negotiation.IsEnabledByUs(number))
            {
                WriteLog("Cannot send " + option + " without negotiating it first.");
                return false;
            }

            var frame = EnvironmentProtocol.FrameSubnegotiation(number, payload);
            await byteStream.WriteAsync(frame, 0, frame.Length, internalCancellation.Token).ConfigureAwait(false);
            return true;
        }

        /// <summary>
        /// Answer an RFC 1184 LINEMODE subnegotiation. Dispatches on the
        /// LINEMODE subcommand byte: MODE mask confirmation, FORWARDMASK
        /// refusal, or SLC table update.
        /// </summary>
        /// <param name="payload">The full received subnegotiation payload, subcommand first.</param>
        private Task ReplyLinemodeAsync(List<byte> payload)
        {
            switch (payload[0])
            {
                case LinemodeProtocol.Mode:
                    return ReplyModeAsync(payload);
                case LinemodeProtocol.SetLocalCharacters:
                    return ReplySlcAsync(payload);
                case (byte)Commands.Do:
                case (byte)Commands.Dont:
                case (byte)Commands.Will:
                case (byte)Commands.Wont:
                    return ReplyForwardMaskAsync(payload);
                default:
                    WriteLog("Ignoring unknown LINEMODE subcommand: " + payload[0]);
                    return Task.CompletedTask;
            }
        }

        /// <summary>
        /// Confirm a MODE mask (RFC 1184 §2.2): client rules by default, server
        /// rules when <see cref="ApplyLinemodeAsServer"/> is set.
        /// </summary>
        /// <param name="payload">The MODE payload ([MODE, mask]).</param>
        private Task ReplyModeAsync(List<byte> payload)
        {
            if (payload.Count != 2)
            {
                WriteLog("Ignoring malformed LINEMODE MODE (want [MODE, mask]).");
                return Task.CompletedTask;
            }

            byte? reply = ApplyLinemodeAsServer ? Linemode.ApplyModeAsServer(payload[1]) : Linemode.ApplyMode(payload[1]);
            if (reply is null)
            {
                return Task.CompletedTask;
            }

            WriteLog("Sending: " + nameof(Options.LineMode) + " MODE " + reply.Value);
            return SendNegotiation((int)Options.LineMode, [LinemodeProtocol.Mode, reply.Value]);
        }

        /// <summary>
        /// Handle a FORWARDMASK exchange (RFC 1184 §2.3). Only the DO side
        /// (the server) may propose a mask, and this client never forwards
        /// buffered input, so a proposal is refused with WONT; DONT and the
        /// unsolicited WILL/WONT are accepted silently.
        /// </summary>
        /// <param name="payload">The FORWARDMASK payload ([verb, FORWARDMASK, mask…]).</param>
        private Task ReplyForwardMaskAsync(List<byte> payload)
        {
            if (payload.Count < 2 || payload[1] != LinemodeProtocol.ForwardMask)
            {
                WriteLog("Ignoring malformed LINEMODE FORWARDMASK.");
                return Task.CompletedTask;
            }

            if (payload[0] != (byte)Commands.Do)
            {
                return Task.CompletedTask;
            }

            WriteLog("Refusing LINEMODE FORWARDMASK (no input forwarding).");
            return SendNegotiation((int)Options.LineMode, [(byte)Commands.Wont, LinemodeProtocol.ForwardMask]);
        }

        /// <summary>
        /// Apply an inbound SLC triplet list (RFC 1184 §2.4/§5.5) and reply
        /// with the resulting ACKs/disagreements, if any.
        /// </summary>
        /// <param name="payload">The SLC payload ([SLC, func, mod, value, …]).</param>
        private Task ReplySlcAsync(List<byte> payload)
        {
            if ((payload.Count - 1) % 3 != 0)
            {
                WriteLog("Ignoring malformed LINEMODE SLC (triplets must be complete).");
                return Task.CompletedTask;
            }

            List<byte>? replies = null;
            for (int i = 1; i < payload.Count; i += 3)
            {
                (byte Modifier, byte Value)? reply = Linemode.ApplySlc(payload[i], payload[i + 1], payload[i + 2]);
                if (reply is not null)
                {
                    replies ??= [];
                    replies.Add(payload[i]);
                    replies.Add(reply.Value.Modifier);
                    replies.Add(reply.Value.Value);
                }
            }

            if (replies is null)
            {
                return Task.CompletedTask;
            }

            WriteLog("Sending: " + nameof(Options.LineMode) + " SLC reply.");
            return SendNegotiation((int)Options.LineMode, [LinemodeProtocol.SetLocalCharacters, .. replies]);
        }

        /// <summary>
        /// Send TELNET command response to the server.
        /// The reply (if any) comes from the persistent <see cref="Negotiation"/>
        /// state machine (RFC 1143): repeats of an answered command and
        /// refusals without new stimulus get no reply, which also keeps
        /// option bytes out of the data stream.
        /// </summary>
        /// <param name="inputVerb">The TELNET command we received.</param>
        private async Task ReplyToCommand(int inputVerb)
        {
            var inputOption = TryReadByte();
            if (inputOption == -1 || inputOption == IacByte)
            {
                // Truncated command, or an IAC where the option byte belongs:
                // not a real option, so there is nothing to reply to.
                return;
            }

            WriteLog(Enum.GetName(typeof(Options), inputOption) ?? inputOption.ToString());
            var reply = inputVerb switch
            {
                (int)Commands.Do => Negotiation.ReceivedDo(inputOption, AgreeEcho(inputOption, peerPerforms: false)),
                (int)Commands.Dont => Negotiation.ReceivedDont(inputOption),
                (int)Commands.Will => Negotiation.ReceivedWill(inputOption, AgreeEcho(inputOption, peerPerforms: true)),
                (int)Commands.Wont => Negotiation.ReceivedWont(inputOption),
                _ => null,
            };
            if (reply is null)
            {
                WriteLog($"No reply to {inputVerb} {inputOption}: already in that state (RFC 1143).");
                return;
            }

            var outBuffer = new byte[]
            {
        (byte)Commands.InterpretAsCommand,
        (byte)reply,
        (byte)inputOption,
            };
            await byteStream.WriteAsync(outBuffer, 0, outBuffer.Length, internalCancellation.Token).ConfigureAwait(false);

            if (inputOption == (int)Options.WindowSize && reply is Commands.Will or Commands.Do)
            {  // NAWS needs to be sent immediately because the server doesn't request subnegotiation.
                await SendWindowSize().ConfigureAwait(false);
            }

            if (inputOption == (int)Options.Logout && inputVerb == (int)Commands.Do)
            {
                // RFC 727: a DO LOGOUT asks us to end the session. The RFC 1143
                // refusal above still goes out (LOGOUT is never agreed); the
                // hook tells the caller to close. Fired on every DO, including
                // repeats the machine answers silently.
                LogoutRequested?.Invoke();
            }

            if (inputOption == (int)Options.SendLocation && reply is Commands.Will && SendLocation is not null)
            {
                // RFC 779: the client volunteers its location right after
                // agreeing (no SEND request exists for this option).
                await SendLocationPayloadAsync(SendLocation).ConfigureAwait(false);
            }

            if (inputOption == (int)Options.RemoteFlowControl && reply is Commands.Do && SendLineflowAsServer)
            {
                await SendLineflowModeAsync(SendLineflowRestartAny).ConfigureAwait(false);
            }
        }

        private bool WeAgree(int inputOption)
        {
            if (inputOption == (int)Options.Mccp2 || inputOption == (int)Options.Mccp3)
            {
                return EnableMccp;
            }

            if (inputOption == (int)Options.COMPortControl)
            {
                return EnableComPort;
            }

            if (IsMudOption(inputOption))
            {
                return EnableMudOptions;
            }

            return inputOption == (int)Options.SuppressGoAhead ||
              inputOption == (int)Options.TerminalType ||
              inputOption == (int)Options.TerminalSpeed ||
              inputOption == (int)Options.WindowSize ||
              inputOption == (int)Options.TransmitBinary ||
              inputOption == (int)Options.OldEnvironment ||
              inputOption == (int)Options.NewEnvironment ||
              inputOption == (int)Options.XDisplay ||
              inputOption == (int)Options.Status ||
              inputOption == (int)Options.TimingMark ||
              inputOption == (int)Options.LineMode ||
              inputOption == (int)Options.SendLocation ||
              inputOption == (int)Options.EndOfRecord ||
              inputOption == (int)Options.RemoteFlowControl ||
              inputOption == (int)Options.CharacterSet;
        }

        /// <summary>
        /// Agreement for RFC 857 ECHO, which needs per-direction guards on top of
        /// <see cref="WeAgree"/>: the option only controls remote echo, and both
        /// sides echoing at once bounces characters forever.
        /// </summary>
        /// <param name="inputOption">The negotiated option.</param>
        /// <param name="peerPerforms">True for a received WILL (the peer would echo).</param>
        /// <returns>Whether to agree: non-ECHO defers to <see cref="WeAgree"/>.</returns>
        private bool AgreeEcho(int inputOption, bool peerPerforms)
        {
            if (inputOption != (int)Options.Echo)
            {
                return WeAgree(inputOption);
            }

            // Accept a server WILL (it echoes; we suppress local echo instead)
            // unless we are already echoing; accept a server DO only with explicit
            // opt-in and a silent peer.
            return peerPerforms
              ? !Negotiation.IsEnabledByUs(inputOption)
              : AllowRemoteEcho && !Negotiation.IsEnabledByPeer(inputOption);
        }

        /// <summary>
        /// Reports the terminal size per RFC 1073 as Width(16-bit) Height(16-bit),
        /// network byte order. Explicit <see cref="WindowWidth"/>/
        /// <see cref="WindowHeight"/> win; otherwise the console size is used,
        /// falling back to 80x24 when unavailable.
        /// </summary>
        private Task SendWindowSize()
        {
            var (width, height) = NawsProtocol.GetEffectiveSize(WindowWidth, WindowHeight);
            NawsSizeSent?.Invoke(width, height);
            var payload = new byte[]
            {
        (byte)(width >> 8), (byte)width,
        (byte)(height >> 8), (byte)height,
            };
            return SendNegotiation((int)Options.WindowSize, payload);
        }

        private async Task<bool> IsResponseAnticipated(bool isInitialResponseReceived, DateTime endInitialTimeout, DateTime rollingTimeout)
        {
            return IsResponsePending || IsWaitForInitialResponse(endInitialTimeout, isInitialResponseReceived) ||
              await IsWaitForIncrementalResponse(rollingTimeout).ConfigureAwait(false);
        }
    }
}
