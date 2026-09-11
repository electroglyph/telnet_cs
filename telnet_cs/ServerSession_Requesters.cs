namespace telnet_cs
{
  /// <summary>
  /// Server-role subnegotiation requesters (S3): the session asks, the peer
  /// answers. The <see cref="ByteStreamHandler"/> routes inbound IS/INFO (and
  /// inbound NAWS) payloads to <see cref="OnSubnegotiationResponse"/>; each
  /// requester sends its SEND, then polls reads until its collector is
  /// satisfied or the timeout elapses. Stray IS with no outstanding request
  /// keeps the safe default (the handler answers WONT).
  /// </summary>
  public partial class ServerSession
  {
    private readonly Lock collectorLock = new();
    private bool expectingTerminalType;
    private bool expectingTerminalSpeed;
    private bool expectingEnvironment;
    private readonly List<string> terminalTypeChain = [];
    private string? clientTerminalSpeed;
    private readonly Dictionary<string, string> clientEnvironment = new(StringComparer.Ordinal);
    private (ushort Width, ushort Height)? clientWindowSize;

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
    /// Asks the peer for its terminal-type list (RFC 1091: <c>SEND</c>,
    /// then one <c>IS</c> per entry). The returned list ends at the first
    /// repeat (same-string-twice terminates the list); the terminating
    /// duplicate is excluded. Returns whatever arrived when the timeout
    /// elapses (possibly empty).
    /// </summary>
    /// <param name="timeout">The maximum time to wait for the full chain.</param>
    /// <param name="cancellationToken">A token to cancel the wait.</param>
    public async Task<IReadOnlyList<string>> RequestTerminalTypesAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
      lock (collectorLock)
      {
        terminalTypeChain.Clear();
        expectingTerminalType = true;
      }

      await SendSbAsync(Options.TerminalType, [], cancellationToken).ConfigureAwait(false);
      await PollForResponseAsync(IsTerminalTypeDone, timeout, cancellationToken).ConfigureAwait(false);
      lock (collectorLock)
      {
        expectingTerminalType = false;
        var result = new List<string>(terminalTypeChain);
        if (result.Count >= 2 && result[^1] == result[^2])
        {
          result.RemoveAt(result.Count - 1);
        }

        return result;
      }
    }

    /// <summary>
    /// Asks the peer for its terminal speed (RFC 1079: <c>SEND</c>, one
    /// <c>IS</c>). Returns the normalized <c>"&lt;tx&gt;,&lt;rx&gt;"</c>
    /// shape, or null on timeout or a malformed answer.
    /// </summary>
    /// <param name="timeout">The maximum time to wait for the answer.</param>
    /// <param name="cancellationToken">A token to cancel the wait.</param>
    public async Task<string?> RequestTerminalSpeedAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
      lock (collectorLock)
      {
        clientTerminalSpeed = null;
        expectingTerminalSpeed = true;
      }

      await SendSbAsync(Options.TerminalSpeed, [], cancellationToken).ConfigureAwait(false);
      await PollForResponseAsync(IsTerminalSpeedDone, timeout, cancellationToken).ConfigureAwait(false);
      lock (collectorLock)
      {
        expectingTerminalSpeed = false;
        return clientTerminalSpeed;
      }
    }

    /// <summary>
    /// Asks the peer for environment variables (RFC 1408: <c>SEND</c>, one
    /// <c>IS</c>). Later spontaneous INFO updates also land in
    /// <see cref="ClientEnvironment"/>. Returns the variables known when
    /// the answer arrives or the timeout elapses.
    /// </summary>
    /// <param name="timeout">The maximum time to wait for the answer.</param>
    /// <param name="types">The requested type bytes (VAR/USERVAR); null or
    /// empty requests the defaults (well-known variables, then user
    /// variables).</param>
    /// <param name="cancellationToken">A token to cancel the wait.</param>
    public async Task<IReadOnlyDictionary<string, string>> RequestEnvironmentAsync(TimeSpan timeout, byte[]? types = null, CancellationToken cancellationToken = default)
    {
      lock (collectorLock)
      {
        expectingEnvironment = true;
      }

      await SendSbAsync(Options.OldEnvironment, types ?? [], cancellationToken).ConfigureAwait(false);
      await PollForResponseAsync(IsEnvironmentDone, timeout, cancellationToken).ConfigureAwait(false);
      lock (collectorLock)
      {
        expectingEnvironment = false;
        return new Dictionary<string, string>(clientEnvironment, StringComparer.Ordinal);
      }
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

    private bool OnSubnegotiationResponse(int inputOption, List<byte> payload)
    {
      if (payload.Count == 0)
      {
        return false;
      }

      if (inputOption == (int)Options.WindowSize)
      {
        return TryConsumeNaws(payload);
      }

      if (inputOption == (int)Options.LineMode)
      {
        return TryConsumeLinemodeImport(payload);
      }

      if (payload[0] != EnvironmentProtocol.Is && payload[0] != EnvironmentProtocol.Info)
      {
        return false;
      }

      if (inputOption == (int)Options.TerminalType)
      {
        return TryConsumeTerminalType(payload);
      }

      if (inputOption == (int)Options.TerminalSpeed)
      {
        return TryConsumeTerminalSpeed(payload);
      }

      if (inputOption == (int)Options.OldEnvironment)
      {
        return TryConsumeEnvironment(payload);
      }

      return false;
    }

    private bool TryConsumeNaws(List<byte> payload)
    {
      lock (collectorLock)
      {
        if (payload.Count == 5 && payload[0] == EnvironmentProtocol.Is)
        {
          // Our own stack's shape (verb first).
          clientWindowSize = ((ushort)(payload[1] << 8 | payload[2]), (ushort)(payload[3] << 8 | payload[4]));
        }
        else if (payload.Count == 4)
        {
          // Strict RFC 1073 shape (no verb).
          clientWindowSize = ((ushort)(payload[0] << 8 | payload[1]), (ushort)(payload[2] << 8 | payload[3]));
        }
        else
        {
          return false;
        }

        return true;
      }
    }

    private bool TryConsumeTerminalType(List<byte> payload)
    {
      lock (collectorLock)
      {
        if (!expectingTerminalType)
        {
          return false;
        }

        var text = new byte[payload.Count - 1];
        payload.CopyTo(1, text, 0, text.Length);
        terminalTypeChain.Add(System.Text.Encoding.Latin1.GetString(text));
        if (terminalTypeChain.Count >= 2 && terminalTypeChain[^1] == terminalTypeChain[^2])
        {
          // Same-string-twice ends the list (RFC 1091 §6).
          expectingTerminalType = false;
        }
        else if (terminalTypeChain.Count >= 32)
        {
          // No end in sight: stop growing, keep consuming.
          WriteLog("TERMINAL-TYPE chain exceeded 32 entries without repeating; ignoring the rest.");
          expectingTerminalType = false;
        }

        return true;
      }
    }

    private bool TryConsumeTerminalSpeed(List<byte> payload)
    {
      lock (collectorLock)
      {
        if (!expectingTerminalSpeed)
        {
          return false;
        }

        expectingTerminalSpeed = false;
        var text = new byte[payload.Count - 1];
        payload.CopyTo(1, text, 0, text.Length);
        clientTerminalSpeed = TerminalSpeedProtocol.Normalize(System.Text.Encoding.Latin1.GetString(text));
        return true;
      }
    }

    private bool TryConsumeEnvironment(List<byte> payload)
    {
      var isInfo = payload[0] == EnvironmentProtocol.Info;
      lock (collectorLock)
      {
        if (!isInfo && !expectingEnvironment)
        {
          return false;
        }

        if (!isInfo)
        {
          expectingEnvironment = false;
        }

        foreach (var entry in EnvironmentProtocol.ParseEntries(payload))
        {
          if (entry.Value is not null)
          {
            clientEnvironment[entry.Name] = entry.Value;
          }
        }

        return true;
      }
    }

    private async Task PollForResponseAsync(Func<bool> isDone, TimeSpan timeout, CancellationToken cancellationToken)
    {
      var end = DateTime.UtcNow.Add(timeout);
      using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, InternalCancellation.Token);
      while (!isDone() && DateTime.UtcNow < end && !linked.Token.IsCancellationRequested)
      {
        await ReadAsync(TimeSpan.FromMilliseconds(MillisecondReadDelay), linked.Token).ConfigureAwait(false);
      }
    }

    private async Task SendSbAsync(Options option, byte[] types, CancellationToken cancellationToken)
    {
      var frame = EnvironmentProtocol.FrameSubnegotiation((int)option, [EnvironmentProtocol.Send, .. types]);
      using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, InternalCancellation.Token);
      if (ByteStream.Connected && !linked.Token.IsCancellationRequested)
      {
        await SendRateLimit.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
          await ByteStream.WriteAsync(frame, 0, frame.Length, linked.Token).ConfigureAwait(false);
        }
        finally
        {
          SendRateLimit.Release();
        }
      }
    }
  }
}
