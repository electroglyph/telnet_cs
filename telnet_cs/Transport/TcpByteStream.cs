using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("telnet_cs.Tests")]
[assembly: InternalsVisibleTo("telnet_cs.Fuzz")]

namespace telnet_cs.Transport
{
    using System;
    using System.Diagnostics;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using telnet_cs.Client;
    using telnet_cs.IO;
    using telnet_cs.Server;

    /// <summary>
    /// A ByteStream acting over a TCP channel.
    /// </summary>
    public class TcpByteStream : IByteStream
    {
        private readonly ISocket socket;
        private readonly bool isSocketOwned;
        private INetworkStream? cachedStream;

        /// <summary>
        /// Initialises a new instance of the <see cref="TcpByteStream" /> class.
        /// </summary>
        /// <param name="hostName">The host name.</param>
        /// <param name="port">The port.</param>
        public TcpByteStream(string hostName, int port)
          : this(new TcpClient(hostName, port))
        {
            isSocketOwned = true;
        }

        /// <summary>
        /// Initialises a new instance of the <see cref="TcpByteStream" /> class.
        /// </summary>
        /// <param name="tcpSocket">The TCP socket.</param>
        public TcpByteStream(ISocket tcpSocket)
          : this(tcpSocket, takeOwnership: false)
        {
        }

        /// <summary>
        /// Initialises a new instance of the <see cref="TcpByteStream" /> class,
        /// optionally taking ownership of <paramref name="tcpSocket"/> so
        /// disposing this stream also disposes the socket. A server wrapping an
        /// accepted socket (see <see cref="TelnetServer"/>) passes
        /// <c>true</c>; a caller that keeps using the socket passes <c>false</c>,
        /// in which case disposing this stream leaves the socket untouched and
        /// usable. An explicit <see cref="Close"/> still closes the connection
        /// in both cases.
        /// </summary>
        /// <param name="tcpSocket">The TCP socket.</param>
        /// <param name="takeOwnership"><c>true</c> to dispose the socket with this stream.</param>
        public TcpByteStream(ISocket tcpSocket, bool takeOwnership)
        {
            ArgumentNullException.ThrowIfNull(tcpSocket);
            socket = tcpSocket;
            isSocketOwned = takeOwnership;
        }

        /// <summary>
        /// Gets the network stream, creating it once. A fresh wrapper per call
        /// would allocate pointlessly and let callers observe different objects.
        /// </summary>
        private INetworkStream Stream => cachedStream ??= socket.GetStream();

        /// <summary>
        /// Gets or sets the encoding used to convert strings to bytes. When null
        /// (the default), the legacy Latin-1 mapping is used.
        /// </summary>
        public Encoding? TextEncoding { get; set; }

        /// <summary>
        /// Gets the amount of data that has been received from the network and is available to be read.
        /// </summary>
        /// <value>
        /// The number of bytes of data received from the network and available to be read.
        /// </value>
        public int Available => socket.Available;

        /// <summary>
        /// Gets a value indicating whether this <see cref="IByteStream" /> is connected.
        /// </summary>
        /// <value>
        ///   <c>True</c> if connected; otherwise, <c>false</c>.
        /// </value>
        public bool Connected => socket.Connected;

        /// <summary>
        /// Gets or sets the amount of time this <see cref="IByteStream" /> will wait to receive data once a read operation is initiated.
        /// </summary>
        /// <value>
        /// The time-out value of the connection in milliseconds. The default value is 0.
        /// </value>
        public int ReceiveTimeout
        {
            get => socket.ReceiveTimeout;
            set => socket.ReceiveTimeout = value;
        }

        /// <summary>
        /// Gets or sets the send timeout in milliseconds.
        /// </summary>
        /// <value>
        /// The send timeout.
        /// </value>
        public int SendTimeout
        {
            get => socket.SendTimeout;
            set => socket.SendTimeout = value;
        }

        /// <summary>
        /// Gets or sets a value indicating whether the Nagle algorithm is disabled.
        /// </summary>
        /// <value>
        /// <c>true</c> to disable the Nagle algorithm; otherwise, <c>false</c>.
        /// </value>
        public bool NoDelay
        {
            get => socket.NoDelay;
            set => socket.NoDelay = value;
        }

        /// <summary>
        /// Gets or sets a value indicating whether TCP keep-alive packets are sent.
        /// </summary>
        /// <value>
        /// <c>true</c> to enable keep-alive; otherwise, <c>false</c>.
        /// </value>
        public bool KeepAlive
        {
            get => socket.KeepAlive;
            set => socket.KeepAlive = value;
        }

        /// <summary>
        /// Reads a byte from the stream and advances the position within the stream by one byte, or returns -1 if at the end of the stream.
        /// </summary>
        /// <returns>
        /// The unsigned byte cast to an integer, or -1 if at the end of the stream.
        /// </returns>
        public int ReadByte()
        {
            // A disconnected or disposed socket has no bytes to offer; report
            // end-of-stream instead of throwing. Genuine I/O failures (timeouts,
            // network errors) still propagate so the read loop can abort loudly.
            if (!socket.Connected)
            {
                return -1;
            }

            try
            {
                return Stream.ReadByte();
            }
            catch (ObjectDisposedException)
            {
                return -1;
            }
            catch (InvalidOperationException)
            {
                return -1;
            }
        }

        /// <summary>
        /// Writes a byte to the current position in the stream and advances the position within the stream by one byte.
        /// </summary>
        /// <param name="value">The byte to write to the stream.</param>
        /// <param name="cancellationToken">The token to monitor for cancellation requests. The default value is System.Threading.CancellationToken.None.</param>
        public async Task WriteByteAsync(byte value, CancellationToken cancellationToken)
        {
            await Stream.WriteByteAsync(value, cancellationToken).ConfigureAwait(false);
            Debug.WriteLine($"SENT: {(char)value}");
        }

        /// <summary>
        /// Asynchronously writes a sequence of bytes to the current stream, advances the current position within this stream by the number of bytes written, and monitors cancellation requests.
        /// </summary>
        /// <param name="buffer">The buffer to write data from.</param>
        /// <param name="offset">The zero-based byte offset in buffer from which to begin copying bytes to the stream.</param>
        /// <param name="count">The maximum number of bytes to write.</param>
        /// <param name="cancellationToken">The token to monitor for cancellation requests. The default value is System.Threading.CancellationToken.None.</param>
        /// <returns>
        /// A task that represents the asynchronous write operation.
        /// </returns>
        public Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var result = Stream.WriteAsync(buffer, offset, count, cancellationToken);
            Debug.WriteLine($"SENT: {Encoding.UTF8.GetString(buffer, offset, count)}");
            return result;
        }

        /// <summary>
        /// Asynchronously writes the specified value to the stream.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task representing the asynchronous action.</returns>
        public Task WriteAsync(string value, CancellationToken cancellationToken)
        {
            var buffer = ByteStringConverter.ConvertStringToByteArray(value, TextEncoding);
            return Stream.WriteAsync(buffer, 0, buffer.Length, cancellationToken);
        }

        /// <summary>
        /// Sends a single byte with TCP urgent (out-of-band) semantics,
        /// bypassing the normal data stream. Used for the RFC 854 Synch signal
        /// (TCP Urgent + DM); see <see cref="Client.SendSynchAsync"/>.
        /// </summary>
        /// <param name="value">The urgent byte to send.</param>
        /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
        /// <returns>A task that represents the asynchronous send operation.</returns>
        public Task SendUrgentAsync(byte value, CancellationToken cancellationToken)
        {
            return socket.SendUrgentAsync(value, cancellationToken);
        }

        /// <summary>
        /// Receives a single byte with TCP urgent (out-of-band) semantics from
        /// the underlying socket. Used to receive the RFC 854 Synch signal
        /// (TCP Urgent + DM); see <see cref="ServerSession.ReceiveUrgentAsync"/>.
        /// </summary>
        /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
        /// <returns>The urgent byte received.</returns>
        public Task<byte> ReceiveUrgentAsync(CancellationToken cancellationToken)
        {
            return socket.ReceiveUrgentAsync(cancellationToken);
        }

        /// <summary>
        /// Non-blocking Synch trigger for the read loop: consumes one pending
        /// TCP urgent byte (RFC 854 Synch signal) when the underlying socket is
        /// the concrete <see cref="TcpClient"/> or the TLS decorator
        /// <see cref="TlsSocket"/> and urgent data waits. Any other socket
        /// (fakes included) reads as "none pending", so this never blocks
        /// and never throws.
        /// </summary>
        /// <returns>The urgent byte, or null when none is pending.</returns>
        public byte? TryConsumeUrgentSignal()
        {
            return (socket as TcpClient)?.TryConsumeUrgent()
                ?? (socket as TlsSocket)?.TryConsumeUrgent();
        }

        /// <summary>
        /// Disposes the instance and requests that the underlying connection be closed.
        /// </summary>
        public void Close()
        {
            socket.Close();
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        /// <param name="isDisposing"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected virtual void Dispose(bool isDisposing)
        {
            if (isDisposing)
            {
                if (isSocketOwned)
                {
                    Close();
                    socket.Dispose();
                }
                else
                {
                    // Not ours: leave the socket (and its stream) alone so the
                    // owner can keep using it. The cached wrapper is dropped
                    // without disposing — disposing it would dispose the
                    // socket's own NetworkStream, closing the connection.
                    cachedStream = null;
                }
            }
        }
    }
}
