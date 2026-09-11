namespace telnet_cs
{
  using System.Threading;
  using System.Threading.Tasks;

  public partial class Client
  {
    /// <summary>
    /// Requests the server's special-character table (RFC 1184 §2.4: SLC
    /// func 0 with DEFAULT). Sends nothing unless LINEMODE is agreed (we
    /// are the WILL-sender); the server's answer is folded into the shared
    /// LINEMODE state by subsequent reads.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the send.</param>
    /// <returns>An awaitable Task.</returns>
    public Task ImportRemoteSpecialCharactersAsync(CancellationToken cancellationToken = default)
    {
      if (!Negotiation.IsEnabledByUs((int)Options.LineMode))
      {
        return Task.CompletedTask;
      }

      var frame = EnvironmentProtocol.FrameSubnegotiation(
        (int)Options.LineMode,
        [LinemodeProtocol.SetLocalCharacters, 0, LinemodeProtocol.LevelDefault, 0]);
      return SendFrameAsync(frame, cancellationToken);
    }

    /// <summary>
    /// Exports the configured special characters to the server (RFC 1184
    /// §5.5). Sends nothing unless LINEMODE is agreed, and nothing at all
    /// when no SLC row is configured (an all-NOSUPPORT export would wrongly
    /// tell the server to disable everything).
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the send.</param>
    /// <returns>An awaitable Task.</returns>
    public Task ExportSpecialCharactersAsync(CancellationToken cancellationToken = default)
    {
      if (!Negotiation.IsEnabledByUs((int)Options.LineMode))
      {
        return Task.CompletedTask;
      }

      byte[]? triplets = linemodeState.ExportTriplets();
      if (triplets is null)
      {
        WriteLog("ExportSpecialCharacters: no special characters configured; nothing sent.");
        return Task.CompletedTask;
      }

      var frame = EnvironmentProtocol.FrameSubnegotiation(
        (int)Options.LineMode,
        [LinemodeProtocol.SetLocalCharacters, .. triplets]);
      return SendFrameAsync(frame, cancellationToken);
    }

    /// <summary>
    /// Sets one LINEMODE SLC table row (test/setup hook).
    /// </summary>
    /// <param name="function">The SLC function code (1–30).</param>
    /// <param name="level">The agreement level.</param>
    /// <param name="value">The character value.</param>
    internal void SetLinemodeEntry(byte function, byte level, byte value) =>
      linemodeState.SetEntry(function, level, value);

    private async Task SendFrameAsync(byte[] frame, CancellationToken cancellationToken)
    {
      using var linked = CancellationTokenSource.CreateLinkedTokenSource(
        cancellationToken, InternalCancellation.Token);
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

    private readonly LinemodeState linemodeState = new();
  }
}
