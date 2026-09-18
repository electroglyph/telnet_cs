namespace telnet_cs.Server;

using System;

public partial class ServerSession
{
    /// <summary>
    /// Reads an integer cap from <see cref="Settings"/>, falling back to
    /// <paramref name="fallback"/> when the settings reference is unusable.
    /// The settings object is caller-owned and may be torn down while a
    /// background pump still runs, so every cap read carries its default
    /// instead of throwing out of a guard.
    /// </summary>
    private int CappedSetting(Func<TelnetServerOptions, int> read, int fallback)
    {
        try
        {
            return read(Settings);
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>
    /// Reads a reference option from <see cref="Settings"/>, yielding null
    /// when the settings reference is unusable. See <see
    /// cref="CappedSetting(Func{TelnetServerOptions, int}, int)"/>.
    /// </summary>
    private T? CappedOption<T>(Func<TelnetServerOptions, T?> read)
        where T : class
    {
        try
        {
            return read(Settings);
        }
        catch
        {
            return null;
        }
    }
}
