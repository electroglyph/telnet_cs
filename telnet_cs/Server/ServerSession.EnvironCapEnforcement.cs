namespace telnet_cs.Server;

public partial class ServerSession
{
    private DateTime environCapLastLogUtc = DateTime.MinValue;
    private DateTime singleValueCapLastLogUtc = DateTime.MinValue;
    private static readonly TimeSpan CapLogWindow = TimeSpan.FromSeconds(5);

    private int EnvironMaxVars() => CappedSetting(static s => s.MaxEnvironVars, 128);

    private int EnvironMaxValueChars() => CappedSetting(static s => s.MaxEnvironValueChars, 4096);

    private int EnvironMaxKeyChars() => CappedSetting(static s => s.MaxEnvironKeyChars, 256);

    private int TtypeMaxChars() => CappedSetting(static s => s.MaxTtypeChars, 256);

    private static string SanitizeKeyForLog(string key)
    {
        string truncated = key.Length > 64 ? key[..64] : key;
        return truncated.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
    }

    private string CapEndpoint()
    {
        try
        {
            var ep = RemoteEndPoint;
            return ep is null or "" ? "unknown" : ep;
        }
        catch
        {
            return "unknown";
        }
    }

    private bool ShouldRateLimitCapLog(ref DateTime last)
    {
        var now = DateTime.UtcNow;
        if (now - last < CapLogWindow)
        {
            return true;
        }

        last = now;
        return false;
    }

    private void LogEnvironCap(string detail)
    {
        if (ShouldRateLimitCapLog(ref environCapLastLogUtc))
        {
            return;
        }

        WriteLog($"environ-cap: endpoint={CapEndpoint()} {detail}");
    }

    private void LogSingleValueCap(string field, int len, int cap)
    {
        if (ShouldRateLimitCapLog(ref singleValueCapLastLogUtc))
        {
            return;
        }

        WriteLog($"environ-cap: endpoint={CapEndpoint()} field={field} len={len} cap={cap}");
    }
}
