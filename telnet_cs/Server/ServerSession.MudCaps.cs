namespace telnet_cs.Server
{
    public partial class ServerSession
    {
        private DateTime mudCapLastLogUtc = DateTime.MinValue;

        private int MudMaxItems()
        {
            try
            {
                return Settings.MaxMudListItems;
            }
            catch
            {
                return 128;
            }
        }

        private int MudMaxBytes()
        {
            try
            {
                return Settings.MaxMudListBytes;
            }
            catch
            {
                return 256 * 1024;
            }
        }

        private int MudMaxKeys()
        {
            try
            {
                return Settings.MaxMudKeys;
            }
            catch
            {
                return 128;
            }
        }

        private int MudMaxValueChars()
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

        private bool IsMudValueOverCap(int size)
        {
            int cap = MudMaxValueChars();
            return cap > 0 && size > cap;
        }

        private void LogMudCap(string kind, int size)
        {
            var now = DateTime.UtcNow;
            if (now - mudCapLastLogUtc < CapLogWindow)
            {
                return;
            }

            mudCapLastLogUtc = now;
            WriteLog($"mud-cap: endpoint={CapEndpoint()} kind={kind} len={size}");
        }

        private IReadOnlyDictionary<string, object> CapMsspVars(IReadOnlyDictionary<string, object> vars)
        {
            int maxKeys = MudMaxKeys();
            int maxValue = MudMaxValueChars();
            IEnumerable<KeyValuePair<string, object>> seq = vars;
            if (maxKeys > 0 && vars.Count > maxKeys)
            {
                LogMudCap("mssp-vars", vars.Count);
                seq = seq.Take(maxKeys);
            }

            if (maxValue <= 0)
            {
                return new Dictionary<string, object>(seq);
            }

            var filtered = new Dictionary<string, object>();
            bool dropped = false;
            foreach (var kv in seq)
            {
                if (kv.Value is string s && s.Length > maxValue)
                {
                    dropped = true;
                    continue;
                }

                filtered[kv.Key] = kv.Value;
            }

            if (dropped)
            {
                LogMudCap("mssp-value", maxValue);
            }

            return filtered;
        }

        private bool TryCapZmp(string command, IReadOnlyList<string> args)
        {
            int maxValue = MudMaxValueChars();
            int maxKeys = MudMaxKeys();
            if (maxValue > 0 && (command.Length > maxValue || args.Any(a => a.Length > maxValue)))
            {
                LogMudCap("zmp", command.Length + args.Count);
                return false;
            }

            if (maxKeys > 0 && args.Count > maxKeys)
            {
                LogMudCap("zmp-args", args.Count);
                return false;
            }

            if (maxKeys > 0 && !mudZmpData.ContainsKey(command) && mudZmpData.Count >= maxKeys)
            {
                LogMudCap("zmp-keys", mudZmpData.Count);
                return false;
            }

            return true;
        }

        private void TrimMudSessionBytes<T>(List<T> list, Func<T, int> sizeOf)
        {
            int maxItems = MudMaxItems();
            if (maxItems > 0)
            {
                while (list.Count > maxItems)
                {
                    list.RemoveAt(0);
                }
            }

            int maxBytes = MudMaxBytes();
            if (maxBytes > 0)
            {
                long total = 0;
                foreach (var item in list)
                {
                    total += sizeOf(item);
                }

                int dropped = 0;
                while (list.Count > 0 && total > maxBytes)
                {
                    total -= sizeOf(list[0]);
                    list.RemoveAt(0);
                    dropped++;
                }

                if (dropped > 0)
                {
                    LogMudCap("list-bytes", (int)total);
                }
            }
        }
    }
}
