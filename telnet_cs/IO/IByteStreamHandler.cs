namespace telnet_cs.IO;

using System;
using System.Threading.Tasks;

/// <summary>
/// Contract of core functionality required to interact with a ByteStream.
/// </summary>
/// <remarks>
/// Deliberately minimal: only the rolling-timeout text read is shared
/// across roles. The negotiation surface on <see cref="ByteStreamHandler"/>
/// (option state, subnegotiation answers, MCCP plumbing) stays
/// <c>internal</c> — it is an engine detail of <see cref="Client.Client"/>
/// and <see cref="Server.ServerSession"/>, not a second public contract —
/// so this interface does not grow a read/write pair per role.
/// </remarks>
public interface IByteStreamHandler : IDisposable
{
    /// <summary>
    /// Reads for up to the specified timeout.
    /// </summary>
    /// <param name="timeout">The timeout.</param>
    /// <returns>A task representing the asynchronous read action.</returns>
    Task<string> ReadAsync(TimeSpan timeout);
}
