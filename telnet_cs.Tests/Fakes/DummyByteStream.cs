namespace telnet_cs.Tests
{
    using System;
    using System.Linq;
    using System.Collections.Generic;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using telnet_cs.Client;
    using telnet_cs.IO;
    using telnet_cs.Transport;

    public sealed class DummyByteStream : IByteStream
    {
        private readonly Queue<byte> buffer = new Queue<byte>();
        private bool isErrored = false;
        private readonly Queue<WriteHandlerBase> handlers;

        /// <summary>
        /// Supplies the lineFeed character expected: default = "\n")
        /// </summary>
        public DummyByteStream()
          : this(Client.LegacyLineFeed)
        { }

        /// <summary>
        ///
        /// </summary>
        /// <param name="lineFeed">The lineFeed character expected: default = "\n")</param>
        public DummyByteStream(string lineFeed)
        {
            Connected = true;

            handlers = new Queue<WriteHandlerBase>();
            handlers.Enqueue(BuildNegotiationHandler());
            handlers.Enqueue(BuildUsernameHandler());
            handlers.Enqueue(BuildPasswordHandler());
            handlers.Enqueue(BuildGetStatisticsHandler());
            Console.WriteLine("Waiting for a connection...");
            LineFeed = lineFeed;
        }

        private WriteHandler BuildGetStatisticsHandler()
        {
            return new WriteHandler(
                      o => ByteStringConverter.ToString(o) == $"show statistic wan2{this.LineFeed}",
                      () =>
                      {
                          Console.WriteLine("Command entered, respond with WAN2 terminated reply");
                          Encoding.ASCII.GetBytes("show statistic wan2\n\r WAN1 total TX: 0 Bytes ,RX: 0 Bytes \n\r WAN2 total TX: 6.3 GB ,RX: 6.9 GB \n\r WAN3 total TX: 0 Bytes ,RX: 0 Bytes \n\r WAN4 total TX: 0 Bytes ,RX: 0 Bytes \n\r WAN5 total TX: 0 Bytes ,RX: 0 Bytes \n\r>").ToList().ForEach(o => this.buffer.Enqueue(o));
                          return;
                      });
        }

        private WriteHandler BuildPasswordHandler()
        {
            return new WriteHandler(
                      o => ByteStringConverter.ToString(o) == $"password{this.LineFeed}",
                      () =>
                      {
                          Console.WriteLine("Password entered, respond with Command> prompt");
                          Encoding.ASCII.GetBytes("Command >").ToList().ForEach(o => this.buffer.Enqueue(o));
                          return;
                      });
        }

        private WriteHandler BuildUsernameHandler()
        {
            return new WriteHandler(
                    o => ByteStringConverter.ToString(o) == $"username{this.LineFeed}",
                    () =>
                    {
                        Console.WriteLine("Account entered, respond with Password: prompt");
                        Encoding.ASCII.GetBytes("Password:").ToList().ForEach(o => this.buffer.Enqueue(o));
                        return;
                    });
        }

        private WriteHandler BuildNegotiationHandler()
        {
            return new WriteHandler(
                      bytes => Enumerable.SequenceEqual(bytes, Client.SuppressGoAheadBuffer),
                      () =>
                      {
                          Connected = true;
                          Console.WriteLine("Connection made, respond with Account: prompt");
                          Encoding.ASCII.GetBytes("Account:").ToList().ForEach(o => this.buffer.Enqueue(o));
                          return;
                      });
        }

        public int Available => buffer.Count;

        public bool Connected { get; set; } = false;

        public int ReceiveTimeout { get; set; } = 0;

        public string LineFeed { get; }

        public void Close()
        {
            buffer.Clear();
            Connected = false;
        }

#pragma warning disable CA1816 // Dispose methods should call SuppressFinalize
        public void Dispose()
#pragma warning restore CA1816 // Dispose methods should call SuppressFinalize
        {
            this.Close();
        }

        public int ReadByte()
        {
            // An empty buffer means the server has nothing to say: report
            // end-of-stream instead of throwing InvalidOperationException from
            // Queue.Dequeue on an empty queue.
            if (this.buffer.Count == 0)
            {
                return -1;
            }

            return this.buffer.Dequeue();
        }

        public Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (isErrored)
            {
                return Task.CompletedTask;
            }

            var bufferEnum = buffer.Skip(offset).Take(count);
            var bufferString = ByteStringConverter.ToString(buffer, offset, count);

            if (handlers.Count == 0)
            {
                throw new InvalidOperationException(
                  $"Unexpected write of {count} bytes (\"{bufferString}\"): no write handlers remain. " +
                  "The login/negotiation script has already run to completion.");
            }

            if (handlers.Peek().Check(buffer))
            {
                handlers.Dequeue().Handle();
            }
            else
            {
                isErrored = true;
                Connected = false;
                throw new NotImplementedException();
            }

            return Task.CompletedTask;
        }

        public async Task WriteAsync(string value, CancellationToken cancellationToken)
        {
            var buffer = ByteStringConverter.ConvertStringToByteArray(value);
            await WriteAsync(buffer, 0, buffer.Length, cancellationToken);
        }

        public async Task WriteByteAsync(byte value, CancellationToken cancellationToken)
        {
            await WriteAsync(new byte[] { value }, 0, 1, cancellationToken);
        }
    }
}
