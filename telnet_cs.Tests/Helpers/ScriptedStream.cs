namespace telnet_cs.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Threading;
    using System.Threading.Tasks;
    using telnet_cs.Transport;

    /// <summary>
    /// In-memory scripted stream: Available tracks queued reads, ReadByte
    /// returns -1 when drained, and all writes are recorded for assertion.
    /// Thread-safe: server sessions now pump inbound bytes on a background
    /// task, so reads/writes may race test-thread enqueues and assertions.
    /// </summary>
    public sealed class ScriptedStream : IByteStream
    {
        private readonly Lock mutex = new();
        private readonly Queue<int> reads;

        private readonly List<byte[]> byteWrites = new List<byte[]>();
        private readonly List<string> stringWrites = new List<string>();
        private readonly List<byte> singleByteWrites = new List<byte>();

        public ReadOnlyCollection<byte[]> ByteWrites
        {
            get
            {
                lock (mutex)
                {
                    return new List<byte[]>(byteWrites).AsReadOnly();
                }
            }
        }

        public ReadOnlyCollection<string> StringWrites
        {
            get
            {
                lock (mutex)
                {
                    return new List<string>(stringWrites).AsReadOnly();
                }
            }
        }

        public ReadOnlyCollection<byte> SingleByteWrites
        {
            get
            {
                lock (mutex)
                {
                    return new List<byte>(singleByteWrites).AsReadOnly();
                }
            }
        }

        public int LastReceiveTimeout { get; private set; } = -1;

        public ScriptedStream(params int[] reads)
        {
            this.reads = new Queue<int>(reads);
        }

        public ScriptedStream(string text)
          : this(ToInts(text))
        {
        }

        /// <summary>
        /// Queues more inbound bytes, so a test can feed separate
        /// <c>ReadAsync</c> calls from one stream.
        /// </summary>
        public void Enqueue(params int[] more)
        {
            lock (mutex)
            {
                foreach (var b in more)
                {
                    reads.Enqueue(b);
                }
            }
        }

        private static int[] ToInts(string text)
        {
            var result = new int[text.Length];
            for (var i = 0; i < text.Length; i++)
            {
                result[i] = text[i];
            }

            return result;
        }

        public int Available
        {
            get
            {
                lock (mutex)
                {
                    return reads.Count;
                }
            }
        }

        public bool Connected { get; set; } = true;

        public int ReceiveTimeout
        {
            get => 0;
            set
            {
                lock (mutex)
                {
                    LastReceiveTimeout = value;
                }
            }
        }

        public void Close()
        {
            lock (mutex)
            {
                reads.Clear();
            }

            Connected = false;
        }

        public void Dispose()
        {
            Close();
        }

        public int ReadByte()
        {
            lock (mutex)
            {
                return reads.Count > 0 ? reads.Dequeue() : -1;
            }
        }

        public Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var slice = new byte[count];
            Array.Copy(buffer, offset, slice, 0, count);
            lock (mutex)
            {
                byteWrites.Add(slice);
            }

            return Task.CompletedTask;
        }

        public Task WriteAsync(string value, CancellationToken cancellationToken)
        {
            lock (mutex)
            {
                stringWrites.Add(value);
            }

            return Task.CompletedTask;
        }

        public Task WriteByteAsync(byte value, CancellationToken cancellationToken)
        {
            lock (mutex)
            {
                singleByteWrites.Add(value);
            }

            return Task.CompletedTask;
        }
    }
}
