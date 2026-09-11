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
    /// following a CR without blocking: CR NUL collapses to CR, CR LF stays
    /// CR LF (the LF is pushed back and processed on the next pass).
    /// </summary>
    private int? pushbackByte;

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
    /// Gets or sets the process-wide log hook. Falls back to
    /// <see cref="System.Diagnostics.Debug"/> when unset.
    /// </summary>
    internal static Action<string>? Trace { get; set; }

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
    /// Stream failures surface as -1 (end of data) so a fragmented or reset
    /// connection aborts the parse instead of throwing out of the read loop.
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
    /// <returns>True if response is pending.</returns>
    private async Task<bool> RetrieveAndParseResponse(StringBuilder sb, List<byte> rawBytes, List<int> opByteCounts)
    {
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
              AppendRecorded(sb, rawBytes, opByteCounts, (char)IacByte);
            }
            else
            {
              await InterpretNextAsCommand(sb, rawBytes, opByteCounts, inputVerb).ConfigureAwait(false);
            }

            break;
          case 1: // Start of Heading
            AppendRecorded(sb, rawBytes, opByteCounts, "\n \n");
            break;
          case 2: // Start of Text
            AppendRecorded(sb, rawBytes, opByteCounts, "\t");
            break;
          case 3: // End of Text or "break" CTRL+C
            AppendRecorded(sb, rawBytes, opByteCounts, "^C");
            WriteLog("^C");
            break;
          case 4: // End of Transmission
            AppendRecorded(sb, rawBytes, opByteCounts, "^D");
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
            EraseLastChar(sb, rawBytes, opByteCounts);
            break;
          case 11: // Vertical TAB
          case 12: // Form Feed
            AppendRecorded(sb, rawBytes, opByteCounts, Environment.NewLine);
            break;
          case 13: // Carriage Return: NUL after CR is ignored (CR NUL -> CR);
            // LF after CR is data (CR LF stays CR LF).
            AppendRecorded(sb, rawBytes, opByteCounts, "\r");
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
            AppendRecorded(sb, rawBytes, opByteCounts, "NAK: Retransmit last message.");
            WriteLog("ERROR NAK: Retransmit last message.");
            break;
          case 31: // Unit Separator
            AppendRecorded(sb, rawBytes, opByteCounts, ",");
            break;
          default:
            AppendRecorded(sb, rawBytes, opByteCounts, (char)input);
            break;
        }

        return true;
      }

      return false;
    }

    private static void AppendRecorded(StringBuilder sb, List<byte> rawBytes, List<int> opByteCounts, string text)
    {
      sb.Append(text);
      AppendRecorded(rawBytes, opByteCounts, Encoding.ASCII.GetBytes(text));
    }

    private static void AppendRecorded(StringBuilder sb, List<byte> rawBytes, List<int> opByteCounts, char c)
    {
      sb.Append(c);
      // Data bytes are Latin-1 by definition here: the default (null
      // encoding) path returns sb.ToString() verbatim, so the recorded byte
      // only matters for explicit TextEncoding decoding.
      rawBytes.Add((byte)c);
      opByteCounts.Add(1);
    }

    private static void AppendRecorded(List<byte> rawBytes, List<int> opByteCounts, byte[] bytes)
    {
      rawBytes.AddRange(bytes);
      opByteCounts.Add(bytes.Length);
    }

    /// <summary>
    /// Printable proof-alive sent in reply to AYT (RFC 854).
    /// </summary>
    private static readonly byte[] AytProofAlive = "[AYT received]\r\n"u8.ToArray();

    /// <summary>
    /// Erases the last decoded character, if any (BS handling and RFC 854 EC).
    /// </summary>
    private static void EraseLastChar(StringBuilder sb, List<byte> rawBytes, List<int> opByteCounts)
    {
      if (sb.Length > 0)
      {
        sb.Length--;
        var taken = opByteCounts[^1];
        opByteCounts.RemoveAt(opByteCounts.Count - 1);
        rawBytes.RemoveRange(rawBytes.Count - taken, taken);
      }
    }

    /// <summary>
    /// Erases back to (but not including) the last CR LF, or everything if
    /// there is none (RFC 854 EL).
    /// </summary>
    private static void EraseLine(StringBuilder sb, List<byte> rawBytes, List<int> opByteCounts)
    {
      var marker = sb.ToString().LastIndexOf("\r\n", StringComparison.Ordinal);
      var keep = marker < 0 ? 0 : marker + 2;
      var removeBytes = 0;
      for (var i = keep; i < sb.Length; i++)
      {
        removeBytes += opByteCounts[i];
      }

      opByteCounts.RemoveRange(keep, sb.Length - keep);
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
    /// <param name="inputVerb">The command we received.</param>
    private async Task InterpretNextAsCommand(StringBuilder sb, List<byte> rawBytes, List<int> opByteCounts, int inputVerb)
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
          EraseLastChar(sb, rawBytes, opByteCounts);
          return;
        case (int)Commands.EraseLine:
          // RFC 854 EL: erase back to (but not including) the last CR LF.
          EraseLine(sb, rawBytes, opByteCounts);
          return;
        case (int)Commands.Break:
          // Surface BRK distinctly instead of swallowing it silently.
          WriteLog("Break (BRK) received.");
          AppendRecorded(sb, rawBytes, opByteCounts, "[BRK]");
          return;
        case (int)Commands.EndOfFile:
          // RFC 1184 §2.5: notify the process of end of file.
          WriteLog("End of file (EOF) received.");
          AppendRecorded(sb, rawBytes, opByteCounts, "[EOF]");
          return;
        case (int)Commands.Suspend:
          // RFC 1184 §2.5: suspend is a no-op when unsupported, but still surfaced.
          WriteLog("Suspend (SUSP) received.");
          AppendRecorded(sb, rawBytes, opByteCounts, "[SUSP]");
          return;
        case (int)Commands.Abort:
          // RFC 1184 §2.5: terminate-only abort, surfaced like BRK.
          WriteLog("Abort (ABORT) received.");
          AppendRecorded(sb, rawBytes, opByteCounts, "[ABORT]");
          return;
        case (int)Commands.SubnegotiationEnd:
        case (int)Commands.NoOperation:
        case (int)Commands.DataMark:
        case (int)Commands.GoAhead:
          // Stray SE, NOP, DM in normal mode, and GA (a NOP while
          // Suppress-GA holds, RFC 858) carry no data: consume silently.
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
      var payload = new List<byte>();
      var overCap = false;
      var complete = false;
      while (true)
      {
        var b = TryReadByte();
        if (b == -1)
        {
          return;
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
            complete = true;
            break;
          }

          if (following == IacByte)
          {
            // Escaped literal IAC inside the payload.
            if (!overCap)
            {
              if (payload.Count < MaxSubnegotiationBytes)
              {
                payload.Add(IacByte);
              }
              else
              {
                overCap = true;
              }
            }

            continue;
          }

          // IAC followed by anything else: framing is lost, give up.
          return;
        }

        if (!overCap)
        {
          if (payload.Count < MaxSubnegotiationBytes)
          {
            payload.Add((byte)b);
          }
          else
          {
            overCap = true;
          }
        }
      }

      if (!complete || overCap || payload.Count == 0)
      {
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
    /// terminal type, terminal speed, environment, or status.
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
          await ReplyEnvironmentAsync(payload).ConfigureAwait(false);
          break;
        case (int)Options.Status:
          await ReplyStatusAsync().ConfigureAwait(false);
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
    /// Answer an RFC 1408 ENVIRON SEND with an IS built from the configured
    /// environment values. The SEND type list (after the verb) is mirrored.
    /// </summary>
    /// <param name="payload">The full received subnegotiation payload, verb first.</param>
    private Task ReplyEnvironmentAsync(List<byte> payload)
    {
      var response = EnvironmentProtocol.BuildResponse(
        EnvironmentProtocol.Is,
        payload.Skip(1),
        EnvironmentUser,
        EnvironmentDisplay,
        EnvironmentUserVars);
      WriteLog("Sending: " + nameof(Options.OldEnvironment));
      return SendNegotiation((int)Options.OldEnvironment, response);
    }

    /// <summary>
    /// Answer an RFC 859 STATUS SEND with an IS snapshot rendered from the
    /// persistent negotiation state (all options, defaults omitted). Like
    /// <see cref="ReplyEnvironmentAsync"/>, any SEND is answered: permission
    /// was already granted by accepting WILL/DO (see <see cref="WeAgree"/>).
    /// </summary>
    private Task ReplyStatusAsync()
    {
      var items = StatusProtocol.BuildIsPayload(Negotiation);
      WriteLog("Sending: " + nameof(Options.Status));
      var frame = StatusProtocol.FrameStatusIs(items);
      return byteStream.WriteAsync(frame, 0, frame.Length, internalCancellation.Token);
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
    }

    private static bool WeAgree(int inputOption)
    {
      return inputOption == (int)Options.SuppressGoAhead ||
        inputOption == (int)Options.TerminalType ||
        inputOption == (int)Options.TerminalSpeed ||
        inputOption == (int)Options.WindowSize ||
        inputOption == (int)Options.TransmitBinary ||
        inputOption == (int)Options.OldEnvironment ||
        inputOption == (int)Options.Status ||
        inputOption == (int)Options.TimingMark ||
        inputOption == (int)Options.LineMode;
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
        EnvironmentProtocol.Is,
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
