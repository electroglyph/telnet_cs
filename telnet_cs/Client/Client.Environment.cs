namespace telnet_cs.Client
{
  using System;
  using System.Collections.Generic;
  using System.Linq;
  using System.Threading.Tasks;
  using telnet_cs.Protocol;

  public partial class Client
  {
    /// <summary>
    /// Sends an ENVIRON INFO update when the configured environment values
    /// changed since the last read and the peer agreed to receive them
    /// (we are the WILL-sender, RFC 1408 §4.4). Always re-baselines, so a
    /// change is reported once.
    /// </summary>
    private async Task MaybeSendEnvironmentInfoAsync()
    {
      var snapshot = SnapshotEnvironment();
      var changed = _environmentSnapshot is not null && _environmentSnapshot != snapshot;
      _environmentSnapshot = snapshot;
      if (!changed || !Negotiation.IsEnabledByUs((int)Options.OldEnvironment))
      {
        return;
      }

      var info = EnvironmentProtocol.BuildResponse(
        EnvironmentProtocol.Info,
        [],
        Settings.EnvironmentUser,
        Settings.EnvironmentDisplay,
        Settings.EnvironmentUserVars);
      var frame = EnvironmentProtocol.FrameSubnegotiation((int)Options.OldEnvironment, info);
      if (ByteStream.Connected && !InternalCancellation.Token.IsCancellationRequested)
      {
        await SendRateLimit.WaitAsync(InternalCancellation.Token).ConfigureAwait(false);
        try
        {
          await ByteStream.WriteAsync(frame, 0, frame.Length, InternalCancellation.Token).ConfigureAwait(false);
        }
        finally
        {
          SendRateLimit.Release();
        }
      }
    }

    private string SnapshotEnvironment()
    {
      var parts = new List<string>
      {
        Settings.EnvironmentUser ?? string.Empty,
        Settings.EnvironmentDisplay ?? string.Empty,
      };
      foreach (var pair in Settings.EnvironmentUserVars.OrderBy(static p => p.Key, StringComparer.Ordinal))
      {
        parts.Add(pair.Key);
        parts.Add(pair.Value);
      }

      return string.Join("\0", parts);
    }

    private string? _environmentSnapshot;
  }
}
