namespace telnet_cs.Server
{
    public partial class ServerSession
    {
        private bool CheckBufferedTextCap()
        {
            int cap;
            try
            {
                cap = Settings.MaxBufferedTextChars;
            }
            catch
            {
                cap = 65536;
            }

            if (cap <= 0)
            {
                return false;
            }

            int combined;
            lock (pumpLock)
            {
                combined = pumpBufferedText.Length + PendingText.Length;
            }

            if (combined <= cap)
            {
                return false;
            }

            string endpoint;
            try
            {
                endpoint = RemoteEndPoint ?? "unknown";
            }
            catch
            {
                endpoint = "unknown";
            }

            if (string.IsNullOrEmpty(endpoint))
            {
                endpoint = "unknown";
            }

            WriteLog($"buffer-cap: endpoint={endpoint} buffered={combined} cap={cap}");
            try
            {
                Close();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex.Message);
            }

            return true;
        }
    }
}
