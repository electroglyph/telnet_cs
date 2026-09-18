namespace telnet_cs.Server;

public partial class ServerSession
{
    // Once-per-accumulation gate for OnBufferCap: set when a trip fires,
    // cleared when the buffer is back under cap, all under pumpLock so
    // concurrent pump/collector checks decide atomically.
    private bool bufferCapHookFired;

    private bool CheckBufferedTextCap()
    {
        int cap = CappedSetting(static s => s.MaxBufferedTextChars, 65536);
        if (cap <= 0)
        {
            return false;
        }

        int combined;
        bool alreadyFired;
        lock (pumpLock)
        {
            combined = pumpBufferedText.Length + PendingText.Length;
            alreadyFired = bufferCapHookFired;
            bufferCapHookFired = combined > cap;
        }

        if (combined <= cap)
        {
            return false;
        }

        string endpoint = CapEndpoint();
        WriteLog($"buffer-cap: endpoint={endpoint} buffered={combined} cap={cap}");
        try
        {
            Close();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex.Message);
        }

        if (!alreadyFired)
        {
            FireBufferCapHook(endpoint, combined, cap);
        }

        return true;
    }

    private void FireBufferCapHook(string endpoint, int buffered, int cap)
    {
        Action<BufferCapEvent>? hook = CappedOption(static s => s.OnBufferCap);
        if (hook is null)
        {
            return;
        }

#pragma warning disable CA1031 // Do not catch general exception types
        try
        {
            hook(new BufferCapEvent(endpoint, buffered, cap));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex.Message);
        }
#pragma warning restore CA1031
    }
}
