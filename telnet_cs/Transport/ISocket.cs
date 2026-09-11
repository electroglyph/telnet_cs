namespace telnet_cs.Transport
{
  using System;
  using System.Threading;
  using System.Threading.Tasks;

  /// <summary>
  /// A socket to connect to.
  /// </summary>
  public interface ISocket : IDisposable
  {
    /// <summary>
    /// Gets a value indicating whether this <see cref="ISocket" /> is connected.
    /// </summary>
    /// <value>
    /// <c>true</c> if connected; otherwise, <c>false</c>.
    /// </value>
    bool Connected { get; }

    /// <summary>
    /// Gets the available bytes to be read.
    /// </summary>
    /// <value>
    /// The available bytes to be read.
    /// </value>
    int Available { get; }

    /// <summary>
    /// Gets or sets the receive timeout.
    /// </summary>
    /// <value>
    /// The receive timeout.
    /// </value>
    int ReceiveTimeout { get; set; }

    /// <summary>
    /// Gets or sets the send timeout in milliseconds.
    /// </summary>
    /// <value>
    /// The send timeout.
    /// </value>
    int SendTimeout { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the Nagle algorithm is disabled.
    /// </summary>
    /// <value>
    /// <c>true</c> to disable the Nagle algorithm; otherwise, <c>false</c>.
    /// </value>
    bool NoDelay { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether TCP keep-alive packets are sent.
    /// </summary>
    /// <value>
    /// <c>true</c> to enable keep-alive; otherwise, <c>false</c>.
    /// </value>
    bool KeepAlive { get; set; }

    /// <summary>
    /// Gets the stream.
    /// </summary>
    /// <returns>Network stream socket connected to.</returns>
    INetworkStream GetStream();

    /// <summary>
    /// Sends a single byte with TCP urgent (out-of-band) semantics,
    /// bypassing the normal data stream (RFC 854 Synch support).
    /// </summary>
    /// <param name="value">The urgent byte to send.</param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous send operation.</returns>
    Task SendUrgentAsync(byte value, CancellationToken cancellationToken);

    /// <summary>
    /// Receives a single byte with TCP urgent (out-of-band) semantics
    /// (RFC 854 Synch support): the counterpart of
    /// <see cref="SendUrgentAsync"/>.
    /// </summary>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>The urgent byte received.</returns>
    Task<byte> ReceiveUrgentAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Closes this instance.
    /// </summary>
    void Close();
  }
}
