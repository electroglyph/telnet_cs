namespace telnet_cs.IO
{
    using System;
    using System.Threading.Tasks;

    /// <summary>
    /// Contract of core functionality required to interact with a ByteStream.
    /// </summary>
    public interface IByteStreamHandler : IDisposable
    {
        /// <summary>
        /// Reads for up to the specified timeout.
        /// </summary>
        /// <param name="timeout">The timeout.</param>
        /// <returns>A task representing the asynchronous read action.</returns>
        Task<string> ReadAsync(TimeSpan timeout);
    }
}
