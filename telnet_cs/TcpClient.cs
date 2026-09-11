namespace telnet_cs
{
  using System;
  using System.Threading;
  using System.Threading.Tasks;

  /// <summary>
  /// A TcpClient to connect to the specified socket.
  /// </summary>
  public class TcpClient : ISocket
  {
    private readonly System.Net.Sockets.TcpClient client;
    private INetworkStream? cachedStream;

    /// <summary>
    /// Initialises a new instance of the <see cref="TcpClient"/> class.
    /// </summary>
    /// <param name="hostName">The host name.</param>
    /// <param name="port">The port.</param>
    public TcpClient(string hostName, int port)
      : this(GetConnectedClient(hostName, port))
    {
    }

    /// <summary>
    /// Initialises a new instance of the <see cref="TcpClient"/> class.
    /// </summary>
    /// <param name="client">The <see cref="System.Net.Sockets.TcpClient"/> instance to wrap.</param>
    public TcpClient(System.Net.Sockets.TcpClient client)
    {
      ArgumentNullException.ThrowIfNull(client);
      this.client = client;
    }

    /// <summary>
    /// Gets or sets the receive timeout.
    /// </summary>
    /// <value>
    /// The receive timeout.
    /// </value>
    public int ReceiveTimeout
    {
      get => client.ReceiveTimeout;
      set => client.ReceiveTimeout = value;
    }

    /// <summary>
    /// Gets or sets the send timeout.
    /// </summary>
    /// <value>
    /// The send timeout.
    /// </value>
    public int SendTimeout
    {
      get => client.SendTimeout;
      set => client.SendTimeout = value;
    }

    /// <summary>
    /// Gets or sets a value indicating whether the Nagle algorithm is disabled.
    /// </summary>
    /// <value>
    /// <c>true</c> to disable the Nagle algorithm; otherwise, <c>false</c>.
    /// </value>
    public bool NoDelay
    {
      get => client.NoDelay;
      set => client.NoDelay = value;
    }

    /// <summary>
    /// Gets or sets a value indicating whether TCP keep-alive packets are sent.
    /// </summary>
    /// <value>
    /// <c>true</c> to enable keep-alive; otherwise, <c>false</c>.
    /// </value>
    public bool KeepAlive
    {
      get => client.Client.GetSocketOption(
        System.Net.Sockets.SocketOptionLevel.Socket,
        System.Net.Sockets.SocketOptionName.KeepAlive) is int keepAlive && keepAlive != 0;

      set => client.Client.SetSocketOption(
        System.Net.Sockets.SocketOptionLevel.Socket,
        System.Net.Sockets.SocketOptionName.KeepAlive, value);
    }

    /// <summary>
    /// Gets a value indicating whether this <see cref="ISocket" /> is connected.
    /// </summary>
    /// <value>
    ///   <c>true</c> if connected; otherwise, <c>false</c>.
    /// </value>
    public bool Connected => client.Connected;

    /// <summary>
    /// Gets the available bytes to be read.
    /// </summary>
    /// <value>
    /// The available bytes to be read.
    /// </value>
    public int Available => client.Available;

    /// <summary>
    /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
    /// </summary>
    public void Dispose()
    {
      Dispose(true);
      GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Closes this instance.
    /// </summary>
    public void Close()
    {
      client.Dispose();
    }

    /// <summary>
    /// Gets the stream. The wrapper owns the single <see cref="INetworkStream"/>
    /// instance: it is created once, cached, and disposed with this socket.
    /// (Previously every call allocated a fresh wrapper, so callers could
    /// observe different objects and dispose only one of them.)
    /// </summary>
    /// <returns>
    /// Network stream socket connected to.
    /// </returns>
    public INetworkStream GetStream()
    {
      cachedStream ??= new NetworkStream(client.GetStream());
      return cachedStream;
    }

    /// <summary>
    /// Sends a single byte with TCP urgent (out-of-band) semantics via
    /// <see cref="System.Net.Sockets.SocketFlags.OutOfBand"/> (RFC 854 Synch support).
    /// </summary>
    /// <param name="value">The urgent byte to send.</param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous send operation.</returns>
    public Task SendUrgentAsync(byte value, CancellationToken cancellationToken)
    {
      return client.Client.SendAsync(new[] { value }, System.Net.Sockets.SocketFlags.OutOfBand, cancellationToken).AsTask();
    }

    /// <summary>
    /// Receives a single byte with TCP urgent (out-of-band) semantics via
    /// <see cref="System.Net.Sockets.SocketFlags.OutOfBand"/> (RFC 854 Synch support).
    /// </summary>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>The urgent byte received.</returns>
    public async Task<byte> ReceiveUrgentAsync(CancellationToken cancellationToken)
    {
      var buffer = new byte[1];
      _ = await client.Client.ReceiveAsync(buffer, System.Net.Sockets.SocketFlags.OutOfBand, cancellationToken).ConfigureAwait(false);
      return buffer[0];
    }

    /// <inheritdoc/>
    protected virtual void Dispose(bool isDisposing)
    {
      if (isDisposing)
      {
        if (cachedStream != null)
        {
          cachedStream.Dispose();
          cachedStream = null;
        }

        client.Dispose();
      }
    }

    private static System.Net.Sockets.TcpClient GetConnectedClient(string hostName, int port)
    {
      var client = new System.Net.Sockets.TcpClient();
      client.Connect(hostName, port);
      return client;
    }
  }
}
