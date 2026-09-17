namespace telnet_cs.Transport
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
        /// Peer-close probe for the session liveness check (see <see
        /// cref="TcpByteStream.Connected"/>): readable with nothing
        /// available means FIN/RST arrived, while <see cref="Connected"/>
        /// alone stays true after a clean FIN until a 0-byte read. Never
        /// throws: an unusable socket reports "unknown", never "gone".
        /// </summary>
        /// <returns><c>true</c> when the peer has definitely closed.</returns>
        internal bool PeerGone()
        {
            try
            {
                // An out-of-band (SYNCH) byte also marks the socket readable
                // with nothing in Available, so exclude it: urgent data is a
                // live peer talking, not a close (see IsUrgentDataPending).
                return client.Client.Poll(0, System.Net.Sockets.SelectMode.SelectRead) && client.Available == 0 && !IsUrgentDataPending();
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            catch (System.Net.Sockets.SocketException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

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
        /// Gets the raw <see cref="System.Net.Sockets.NetworkStream"/> without
        /// triggering the caching <see cref="GetStream"/> wrapper, for
        /// handshake-layer use (e.g. wrapping it in an <c>SslStream</c>).
        /// </summary>
        internal System.Net.Sockets.NetworkStream RawStream => client.GetStream();

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
        /// <exception cref="System.IO.EndOfStreamException">The peer went away
        /// before any urgent byte arrived: a 0-byte receive (graceful close) or
        /// a socket failure (e.g. Linux reports <c>EINVAL</c> for OOB reads
        /// after FIN) means there is no Synch to report, so this throws
        /// instead of yielding a phantom <c>0x00</c>.</exception>
        public async Task<byte> ReceiveUrgentAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[1];
            int got;
            try
            {
                got = await client.Client.ReceiveAsync(buffer, System.Net.Sockets.SocketFlags.OutOfBand, cancellationToken).ConfigureAwait(false);
            }
            catch (System.Net.Sockets.SocketException ex)
            {
                throw new System.IO.EndOfStreamException("The connection failed before any urgent byte arrived.", ex);
            }
            catch (ObjectDisposedException ex)
            {
                throw new System.IO.EndOfStreamException("The connection failed before any urgent byte arrived.", ex);
            }
            catch (NullReferenceException ex)
            {
                // Close disposes the inner socket handle, after which
                // Client reads back null. No handle means no urgent byte.
                throw new System.IO.EndOfStreamException("The connection failed before any urgent byte arrived.", ex);
            }
            catch (InvalidOperationException ex)
            {
                throw new System.IO.EndOfStreamException("The connection failed before any urgent byte arrived.", ex);
            }

            if (got == 0)
            {
                throw new System.IO.EndOfStreamException("The peer closed the connection: no urgent byte was received.");
            }

            return buffer[0];
        }

        /// <summary>
        /// Non-blocking probe for pending TCP urgent data (RFC 854 Synch
        /// trigger): true when an out-of-band byte waits. Polls with a zero
        /// timeout, so it never blocks; socket errors read as "none pending".
        /// </summary>
        /// <returns>True when urgent data is waiting to be consumed.</returns>
        public bool IsUrgentDataPending()
        {
            try
            {
                var socket = client.Client;
                if (socket is null)
                {
                    return false;
                }

                return socket.Poll(0, System.Net.Sockets.SelectMode.SelectError);
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            catch (System.Net.Sockets.SocketException)
            {
                return false;
            }
            catch (NullReferenceException)
            {
                // Close releases the inner handle, after which Client reads
                // back null and the member access faults.
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        /// <summary>
        /// Consumes one pending urgent byte after <see cref="IsUrgentDataPending"/>.
        /// The receive itself runs non-blocking (see below), so the poll→receive
        /// race resolves to an instant null instead of a block. Returns null when
        /// nothing is pending or the consume fails.
        /// </summary>
        /// <returns>The urgent byte, or null when none is pending.</returns>
        public byte? TryConsumeUrgent()
        {
            if (!IsUrgentDataPending())
            {
                return null;
            }

            try
            {
                // Non-blocking receive: even if the OOB byte vanished after the
                // poll, this returns WouldBlock instantly instead of blocking up
                // to ReceiveTimeout. Restored in finally; SendAsync stays correct
                // in the microsecond non-blocking window.
                var socket = client.Client;
                if (socket is null)
                {
                    return null;
                }

                socket.Blocking = false;
                try
                {
                    var buffer = new byte[1];
                    int got = socket.Receive(buffer, 0, 1, System.Net.Sockets.SocketFlags.OutOfBand);
                    return got == 1 ? buffer[0] : null;
                }
                finally
                {
                    socket.Blocking = true;
                }
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
            catch (System.Net.Sockets.SocketException)
            {
                return null;
            }
            catch (NullReferenceException)
            {
                return null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
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
            try
            {
                client.Connect(hostName, port);
                return client;
            }
            catch
            {
                // A failed sync connect must not leak the socket handle.
                client.Dispose();
                throw;
            }
        }
    }
}
