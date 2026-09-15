namespace telnet_cs.Server
{
    public partial class ServerSession
    {
        private DateTime environCapLastLogUtc = DateTime.MinValue;
        private DateTime singleValueCapLastLogUtc = DateTime.MinValue;
        private static readonly TimeSpan CapLogWindow = TimeSpan.FromSeconds(5);

        private int EnvironMaxVars()
        {
            try
            {
                return Settings.MaxEnvironVars;
            }
            catch
            {
                return 128;
            }
        }

        private int EnvironMaxValueChars()
        {
            try
            {
                return Settings.MaxEnvironValueChars;
            }
            catch
            {
                return 4096;
            }
        }

        private int EnvironMaxKeyChars()
        {
            try
            {
                return Settings.MaxEnvironKeyChars;
            }
            catch
            {
                return 256;
            }
        }

        private int TtypeMaxChars()
        {
            try
            {
                return Settings.MaxTtypeChars;
            }
            catch
            {
                return 256;
            }
        }

        private static string SanitizeKeyForLog(string key)
        {
            string truncated = key.Length > 64 ? key.Substring(0, 64) : key;
            return truncated.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
        }

        private string CapEndpoint()
        {
            try
            {
                var ep = RemoteEndPoint;
                return string.IsNullOrEmpty(ep) ? "unknown" : ep;
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
}
