// Forwards every IByteStream call to an inner stream while recording
// outbound bytes. Lets live client<->server pins assert exact wire frames
// (preset bytes, single-probe rules) without dropping to loopback. The
// library emits negotiation and text as byte writes, so recording the
// byte path captures the full wire image; the string overload only
// forwards.
namespace telnet_cs.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using telnet_cs.Transport;

    internal sealed class WireTap : IByteStream
    {
        private readonly IByteStream inner;
        private readonly Lock gate = new();
        private readonly List<byte> written = new();

        internal WireTap(IByteStream inner)
        {
            ArgumentNullException.ThrowIfNull(inner);
            this.inner = inner;
        }

        internal byte[] WrittenBytes
        {
            get
            {
                lock (gate)
                {
                    return [.. written];
                }
            }
        }

        public int Available => inner.Available;

        public bool Connected => inner.Connected;

        public int ReceiveTimeout
        {
            get => inner.ReceiveTimeout;
            set => inner.ReceiveTimeout = value;
        }

        public void Close() => inner.Close();

        public void Dispose() => inner.Dispose();

        public int ReadByte() => inner.ReadByte();

        public Task WriteByteAsync(byte value, CancellationToken cancellationToken)
        {
            lock (gate)
            {
                written.Add(value);
            }

            return inner.WriteByteAsync(value, cancellationToken);
        }

        public Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            lock (gate)
            {
                for (int i = offset; i < offset + count; i++)
                {
                    written.Add(buffer[i]);
                }
            }

            return inner.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public Task WriteAsync(string value, CancellationToken cancellationToken) =>
            inner.WriteAsync(value, cancellationToken);
    }
}
