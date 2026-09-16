namespace telnet_cs.Transport
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using telnet_cs.IO;

    /// <summary>
    /// In-memory linked pair of <see cref="IByteStream"/> ends for hermetic
    /// tests: bytes written on one end arrive on the other with real blocking,
    /// no sockets and no ports. Internal until the public
    /// hermetic-transport review; tests reach it via the
    /// <c>InternalsVisibleTo("telnet_cs.Tests")</c> grant.
    /// </summary>
    internal static class DuplexPipe
    {
        /// <summary>
        /// Creates two linked ends. Bytes written on <c>A</c> are readable
        /// on <c>B</c> and vice versa.
        /// </summary>
        /// <returns>The linked <c>(A, B)</c> ends.</returns>
        public static (IByteStream A, IByteStream B) Create()
        {
            var endA = new DuplexEnd();
            var endB = new DuplexEnd();
            endA.Peer = endB;
            endB.Peer = endA;
            return (endA, endB);
        }

        /// <summary>
        /// One end of a <see cref="DuplexPipe"/> pair. Reads block until the
        /// peer writes, the peer closes, or <see cref="IByteStream.ReceiveTimeout"/>
        /// expires (surfacing as <see cref="IOException"/>, matching
        /// <see cref="TcpByteStream"/>). A non-positive timeout waits
        /// indefinitely; <c>Close</c> wakes blocked readers so no test can
        /// hang on a drained peer.
        /// </summary>
        private sealed class DuplexEnd : IByteStream
        {
            private readonly Lock mutex = new();
            private readonly Queue<byte> inbound = new();
            private readonly List<byte> written = new();
            private readonly ManualResetEventSlim dataAvailable = new(false);
            private int receiveTimeout;
            private bool closed;
            private bool disposed;

            public DuplexEnd? Peer { get; set; }

            /// <summary>
            /// Gets the bytes this end has written, in order, for assertion.
            /// </summary>
            public IReadOnlyList<byte> WrittenBytes
            {
                get
                {
                    lock (mutex)
                    {
                        return [.. written];
                    }
                }
            }

            /// <inheritdoc/>
            public int Available
            {
                get
                {
                    lock (mutex)
                    {
                        return inbound.Count;
                    }
                }
            }

            /// <inheritdoc/>
            public bool Connected
            {
                get
                {
                    lock (mutex)
                    {
                        return !closed;
                    }
                }
            }

            /// <inheritdoc/>
            public int ReceiveTimeout
            {
                get
                {
                    lock (mutex)
                    {
                        return receiveTimeout;
                    }
                }

                set
                {
                    lock (mutex)
                    {
                        receiveTimeout = value;
                    }
                }
            }

            /// <inheritdoc/>
            public int ReadByte()
            {
                int timeout;
                lock (mutex)
                {
                    timeout = receiveTimeout;
                }

                long deadline = timeout <= 0 ? long.MaxValue : Environment.TickCount64 + timeout;
                while (true)
                {
                    lock (mutex)
                    {
                        if (inbound.Count > 0)
                        {
                            byte next = inbound.Dequeue();
                            if (inbound.Count == 0)
                            {
                                dataAvailable.Reset();
                            }

                            return next;
                        }

                        // Drained and nobody will ever write again: end of
                        // stream. Anything already queued above still reads
                        // first, so a close never truncates in-flight bytes.
                        if (closed || Peer is null || Peer.IsClosed)
                        {
                            return -1;
                        }

                        // Empty but live: arm the signal while holding the
                        // same mutex the writer sets it under, so no wake-up
                        // between the check and the wait below is lost.
                        dataAvailable.Reset();
                    }

                    if (timeout > 0)
                    {
                        long remaining = deadline - Environment.TickCount64;
                        if (remaining <= 0)
                        {
                            throw new IOException("Receive timed out.");
                        }

                        dataAvailable.Wait(TimeSpan.FromMilliseconds(remaining));
                    }
                    else
                    {
                        dataAvailable.Wait();
                    }
                }
            }

            /// <inheritdoc/>
            public Task WriteByteAsync(byte value, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Deliver([value]);
                return Task.CompletedTask;
            }

            /// <inheritdoc/>
            public Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                ArgumentNullException.ThrowIfNull(buffer);
                ArgumentOutOfRangeException.ThrowIfNegative(offset);
                ArgumentOutOfRangeException.ThrowIfNegative(count);
                if (offset + count > buffer.Length)
                {
                    throw new ArgumentException("Offset and count exceed the buffer length.", nameof(count));
                }

                cancellationToken.ThrowIfCancellationRequested();
                var slice = new byte[count];
                Array.Copy(buffer, offset, slice, 0, count);
                Deliver(slice);
                return Task.CompletedTask;
            }

            /// <inheritdoc/>
            public Task WriteAsync(string value, CancellationToken cancellationToken)
            {
                ArgumentNullException.ThrowIfNull(value);
                cancellationToken.ThrowIfCancellationRequested();
                // Null encoding keeps the legacy Latin-1 mapping, exactly
                // like TcpByteStream.WriteAsync(string) with its default.
                Deliver(ByteStringConverter.ConvertStringToByteArray(value, null));
                return Task.CompletedTask;
            }

            /// <inheritdoc/>
            public void Close()
            {
                lock (mutex)
                {
                    closed = true;
                    dataAvailable.Set();
                }

                // The peer may be parked in ReadByte: wake it so it drains
                // and reports end-of-stream instead of hanging.
                Peer?.Signal();
            }

            /// <inheritdoc/>
            public void Dispose()
            {
                lock (mutex)
                {
                    if (disposed)
                    {
                        return;
                    }

                    disposed = true;
                }

                Close();
                dataAvailable.Dispose();
            }

            private bool IsClosed
            {
                get
                {
                    lock (mutex)
                    {
                        return closed;
                    }
                }
            }

            private void Signal()
            {
                lock (mutex)
                {
                    dataAvailable.Set();
                }
            }

            private void Deliver(byte[] payload)
            {
                lock (mutex)
                {
                    ObjectDisposedException.ThrowIf(closed, this);
                    written.AddRange(payload);
                }

                // A write to a closed peer is discarded: nobody will ever
                // read it, and buffering it would only leak. The write
                // itself still succeeds and stays in this end's log.
                DuplexEnd? peer = Peer;
                if (peer is null || peer.IsClosed)
                {
                    return;
                }

                lock (peer.mutex)
                {
                    foreach (byte b in payload)
                    {
                        peer.inbound.Enqueue(b);
                    }

                    peer.dataAvailable.Set();
                }
            }
        }
    }
}
