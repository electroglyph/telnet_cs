namespace telnet_cs.Transport
{
    using System;
    using System.Buffers;
    using System.Collections.Generic;
    using System.Net.Security;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// An <see cref="ISocket"/> decorator that runs the data path through TLS.
    /// Wraps an already-connected <see cref="TcpClient"/> plus the authenticated
    /// <see cref="SslStream"/> built over its raw stream. Telnet bytes are unchanged;
    /// only the transport below them is encrypted.
    /// </summary>
    /// <remarks>
    /// Invariants: <see cref="GetStream"/> is the single data path — never call
    /// <c>inner.GetStream()</c> alongside it, or two wrappers will share one raw
    /// stream and one of them will observe use-after-dispose. Timeouts and urgent
    /// (Synch) operations delegate to <c>inner</c>: the urgent byte travels outside
    /// the TLS records (see the weakened-guarantee note on
    /// <see cref="SendUrgentAsync"/>), and the socket timeouts have a single owner.
    /// </remarks>
    public sealed class TlsSocket : ISocket
    {
        private readonly TcpClient inner;
        private readonly SslStream ssl;
        private BufferedStream? bufferedStream;
        private bool disposed;

        private TlsSocket(TcpClient inner, SslStream ssl)
        {
            this.inner = inner;
            this.ssl = ssl;
        }

        /// <summary>
        /// Handshakes as the TLS client over an already-connected socket and
        /// returns the encrypting decorator.
        /// </summary>
        /// <param name="inner">The connected socket to encrypt.</param>
        /// <param name="options">Client authentication options (target host, certs, validation).</param>
        /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
        /// <returns>The authenticated <see cref="TlsSocket"/>.</returns>
        public static async Task<TlsSocket> AuthenticateAsClientAsync(
            TcpClient inner,
            SslClientAuthenticationOptions options,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(inner);
            ArgumentNullException.ThrowIfNull(options);

#pragma warning disable CA2000 // Ownership of the stream transfers to the TlsSocket on success; the catch releases it otherwise.
            var ssl = new SslStream(inner.RawStream, leaveInnerStreamOpen: false);
            try
            {
                await ssl.AuthenticateAsClientAsync(options, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                ssl.Dispose();
                throw;
            }
#pragma warning restore CA2000

            return new TlsSocket(inner, ssl);
        }

        /// <summary>
        /// Handshakes as the TLS server over an already-accepted socket and
        /// returns the encrypting decorator.
        /// </summary>
        /// <param name="inner">The accepted socket to encrypt.</param>
        /// <param name="options">Server authentication options (certificate, client-cert policy).</param>
        /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
        /// <returns>The authenticated <see cref="TlsSocket"/>.</returns>
        public static async Task<TlsSocket> AuthenticateAsServerAsync(
            TcpClient inner,
            SslServerAuthenticationOptions options,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(inner);
            ArgumentNullException.ThrowIfNull(options);

#pragma warning disable CA2000 // Ownership of the stream transfers to the TlsSocket on success; the catch releases it otherwise.
            var ssl = new SslStream(inner.RawStream, leaveInnerStreamOpen: false);
            try
            {
                await ssl.AuthenticateAsServerAsync(options, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                ssl.Dispose();
                throw;
            }
#pragma warning restore CA2000

            return new TlsSocket(inner, ssl);
        }

        /// <inheritdoc/>
        public bool Connected => inner.Connected;

        /// <summary>
        /// Peer-close probe through to the encrypted socket (see <see
        /// cref="TcpClient.PeerGone"/> and <see
        /// cref="TcpByteStream.Connected"/>). A clean TCP FIN under TLS
        /// carries no decryptable bytes, so the read path's availability
        /// gate would never attempt the read that observes it.
        /// </summary>
        /// <returns><c>true</c> when the peer has definitely closed.</returns>
        internal bool PeerGone()
        {
            return inner.PeerGone();
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Adds decrypted-but-unconsumed bytes staged in the visible queue (see
        /// <see cref="BufferedStream"/>) to the raw-socket count, so the
        /// read path's availability gate keeps reopening over TLS until the
        /// staged bytes drain.
        /// </remarks>
        public int Available => inner.Available + (bufferedStream?.BufferedCount ?? 0);

        /// <inheritdoc/>
        public int ReceiveTimeout
        {
            get => inner.ReceiveTimeout;
            set => inner.ReceiveTimeout = value;
        }

        /// <inheritdoc/>
        public int SendTimeout
        {
            get => inner.SendTimeout;
            set => inner.SendTimeout = value;
        }

        /// <inheritdoc/>
        public bool NoDelay
        {
            get => inner.NoDelay;
            set => inner.NoDelay = value;
        }

        /// <inheritdoc/>
        public bool KeepAlive
        {
            get => inner.KeepAlive;
            set => inner.KeepAlive = value;
        }

        /// <summary>
        /// Gets the encrypted stream. Cached once, mirroring
        /// <see cref="TcpClient.GetStream"/>: a single <c>BufferedStream</c>
        /// stages decrypted bytes in a visible queue (see its remarks).
        /// </summary>
        /// <returns>Stream socket connected to, over TLS.</returns>
        public INetworkStream GetStream()
        {
            bufferedStream ??= new BufferedStream(ssl);
            return bufferedStream;
        }

        /// <summary>
        /// Sends a single byte with TCP urgent (out-of-band) semantics on the
        /// inner socket, outside the TLS records. Weaker guarantee than on
        /// plaintext: middleboxes and TLS stacks may mishandle OOB, and the
        /// urgent byte is not covered by the session authentication.
        /// </summary>
        /// <param name="value">The urgent byte to send.</param>
        /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
        /// <returns>A task that represents the asynchronous send operation.</returns>
        public Task SendUrgentAsync(byte value, CancellationToken cancellationToken)
        {
            return inner.SendUrgentAsync(value, cancellationToken);
        }

        /// <inheritdoc/>
        public Task<byte> ReceiveUrgentAsync(CancellationToken cancellationToken)
        {
            return inner.ReceiveUrgentAsync(cancellationToken);
        }

        /// <summary>
        /// Poll-gated urgent probe mirroring <see cref="TcpClient.TryConsumeUrgent"/>:
        /// consumed by <c>TcpByteStream.TryConsumeUrgentSignal</c> so the Synch
        /// auto-discard trigger stays alive over TLS.
        /// </summary>
        /// <returns>The urgent byte, or null when none is pending.</returns>
        public byte? TryConsumeUrgent()
        {
            return inner.TryConsumeUrgent();
        }

        /// <inheritdoc/>
        public void Close()
        {
            Dispose();
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            if (bufferedStream is null)
            {
                ssl.Dispose();
            }
            else
            {
                bufferedStream.Dispose();
                bufferedStream = null;
            }

            inner.Dispose();
        }

        /// <summary>
        /// An <see cref="INetworkStream"/> over an <see cref="SslStream"/> that
        /// stages decrypted bytes in a visible queue instead of leaving them in
        /// <see cref="SslStream"/>'s internal decrypt buffer.
        /// </summary>
        /// <remarks>
        /// <see cref="SslStream"/> exposes no count of bytes it has decrypted
        /// but not yet returned, while the read path gates its first read of
        /// each pass on socket-level availability (<c>Available &gt; 0</c>).
        /// Serving one byte per <see cref="SslStream.ReadByte()"/> would strand
        /// each record's tail in that invisible buffer once the socket runs dry,
        /// stalling the session: the gate never reopens for bytes it cannot see.
        /// Staging bulk reads in this visible queue restores the gate invariant
        /// (gate-closed implies a read would block), with no changes to the
        /// read path or the stream interfaces. The stop rule is exact, not
        /// heuristic: the runtime's read loop fills the caller buffer while
        /// complete frames remain, holding at most one frame aside and otherwise
        /// decrypting straight into the caller buffer, so only a full return can
        /// leave holdings behind — the loop continues precisely then. A bulk
        /// size above the maximum TLS fragment (2^14 payload plus framing) keeps
        /// single-record tails in one pass. Cost: the extra read on exact
        /// multiples of the drain size can block up to the receive timeout, the
        /// same bound as any dry read.
        /// </remarks>
        private sealed class BufferedStream : INetworkStream
        {
            private const int DrainSize = 32768;
            private readonly SslStream ssl;
            private readonly Queue<byte> queued = new();

            internal BufferedStream(SslStream ssl)
            {
                this.ssl = ssl;
            }

            internal int BufferedCount => queued.Count;

            public int ReadByte()
            {
                if (queued.Count == 0)
                {
                    Drain();
                }

                return queued.Count == 0 ? -1 : queued.Dequeue();
            }

            /// <inheritdoc/>
            public Task WriteByteAsync(byte value, CancellationToken cancellationToken)
            {
                return ssl.WriteAsync(new byte[] { value }, 0, 1, cancellationToken);
            }

            /// <inheritdoc/>
            public Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                return ssl.WriteAsync(buffer, offset, count, cancellationToken);
            }

            /// <inheritdoc/>
            public void Dispose()
            {
                ssl.Dispose();
            }

            private void Drain()
            {
                byte[] buffer = ArrayPool<byte>.Shared.Rent(DrainSize);
                try
                {
                    int read;
                    do
                    {
                        read = ssl.Read(buffer, 0, buffer.Length);
                        for (int i = 0; i < read; i++)
                        {
                            queued.Enqueue(buffer[i]);
                        }
                    }
                    while (read == buffer.Length);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }
    }
}
