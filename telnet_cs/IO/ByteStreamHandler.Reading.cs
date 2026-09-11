namespace telnet_cs.IO
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using telnet_cs.Client;
    using telnet_cs.Protocol;
    using telnet_cs.Transport;

    /// <summary>
    /// Provides core functionality for interacting with the ByteStream.
    /// </summary>
    public partial class ByteStreamHandler
    {
        private readonly bool isCancellationTokenOwned;
        private readonly CancellationTokenSource internalCancellation;

        /// <summary>
        /// Initialises a new instance of the <see cref="ByteStreamHandler"/> class.
        /// </summary>
        /// <param name="byteStream">The byteStream to handle.</param>
        public ByteStreamHandler(IByteStream byteStream)
          : this(byteStream, new CancellationTokenSource(), Client.DefaultMillisecondReadDelay)
        {
            isCancellationTokenOwned = true;
        }

        /// <summary>
        /// Initialises a new instance of the <see cref="ByteStreamHandler"/> class.
        /// </summary>
        /// <param name="byteStream">The byteStream to handle.</param>
        /// <param name="internalCancellation">A cancellation token.</param>
        public ByteStreamHandler(IByteStream byteStream, CancellationTokenSource internalCancellation)
          : this(byteStream, internalCancellation, Client.DefaultMillisecondReadDelay)
        { }

        /// <summary>
        /// Initialises a new instance of the <see cref="ByteStreamHandler"/> class.
        /// </summary>
        /// <param name="byteStream">The byteStream to handle.</param>
        /// <param name="internalCancellation">A cancellation token.</param>
        /// <param name="millisecondReadDelay">Time to delay between reads from the stream.</param>
        public ByteStreamHandler(IByteStream byteStream, CancellationTokenSource internalCancellation, int millisecondReadDelay)
        {
            ArgumentNullException.ThrowIfNull(byteStream);
            ArgumentNullException.ThrowIfNull(internalCancellation);
            this.byteStream = byteStream;
            this.internalCancellation = internalCancellation;
            MillisecondReadDelay = millisecondReadDelay;
        }

        private bool IsCancellationRequested
        {
            get
            {
                return internalCancellation.Token.IsCancellationRequested;
            }
        }

        /// <summary>
        /// Reads asynchronously from the stream.
        /// </summary>
        /// <param name="timeout">The rolling timeout to wait for no further response from stream.</param>
        /// <returns>Any text read from the stream. Cancellation returns whatever was received so far.</returns>
        public async Task<string> ReadAsync(TimeSpan timeout)
        {
            // Snapshot before the read: bytes arriving before a mid-read DO ECHO
            // agreement must not be echoed back.
            var echoBack = Negotiation.IsEnabledByUs((int)Options.Echo);
            if (!byteStream.Connected || internalCancellation.Token.IsCancellationRequested)
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            var rawBytes = new List<byte>();
            var opByteCounts = new List<int>();
            byteStream.ReceiveTimeout = ClampReceiveTimeout(timeout);
            var endInitialTimeout = DateTime.UtcNow.Add(timeout);
            var rollingTimeout = ExtendRollingTimeout(timeout);
            try
            {
                do
                {
                    if (await RetrieveAndParseResponse(sb, rawBytes, opByteCounts).ConfigureAwait(false))
                    {
                        rollingTimeout = ExtendRollingTimeout(timeout);
                    }
                }
                while (!IsCancellationRequested &&
                  await IsResponseAnticipated(IsInitialResponseReceived(sb), endInitialTimeout, rollingTimeout).ConfigureAwait(false));
                LogIfTimeoutExpired(endInitialTimeout);
            }
            catch (OperationCanceledException)
            {
                // Cancelled mid-read (user token, IP command, or dispose): return the
                // partial response instead of throwing.
            }

            var read = DecodeResult(sb, rawBytes);
            if (echoBack && rawBytes.Count > 0)
            {
                await EchoBackAsync(rawBytes).ConfigureAwait(false);
            }

            if (LocalEchoEnabled)
            {
                Console.Write(read);
            }

            return read;
        }

        /// <summary>
        /// Returns received data bytes to the sender (RFC 857 remote echo: we
        /// agreed via <c>WILL ECHO</c>, so the peer relies on us). Data 0xFF is
        /// escaped per the usual IAC-doubling rule.
        /// </summary>
        private Task EchoBackAsync(List<byte> rawBytes)
        {
            var escaped = new List<byte>(rawBytes.Count);
            foreach (var b in rawBytes)
            {
                escaped.Add(b);
                if (b == IacByte)
                {
                    escaped.Add(b);
                }
            }

            return byteStream.WriteAsync(escaped.ToArray(), 0, escaped.Count, internalCancellation.Token);
        }

        private string DecodeResult(StringBuilder sb, List<byte> rawBytes)
        {
            if (TextEncoding != null)
            {
                return TextEncoding.GetString(rawBytes.ToArray());
            }

            return sb.ToString();
        }

        private static int ClampReceiveTimeout(TimeSpan timeout)
        {
            // Socket timeouts are int milliseconds; saturate instead of overflowing
            // (a raw (int) cast of a huge TotalMilliseconds yields int.MinValue,
            // which the socket setter rejects).
            var ms = timeout.TotalMilliseconds;
            if (ms >= int.MaxValue)
            {
                return int.MaxValue;
            }

            return (int)ms;
        }

        /// <summary>
        /// Add null check to cancel commands. Fail gracefully.
        /// </summary>
        protected void CancelPendingReads()
        {
#pragma warning disable CA1031 // Do not catch general exception types
            try
            {
                internalCancellation?.Cancel();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex.Message);
            }
#pragma warning restore CA1031 // Do not catch general exception types
        }

        private void LogIfTimeoutExpired(DateTime timeout)
        {
            if (IsTimeoutExpired(timeout))
            {
                WriteLog("Timeout exceeded " + DateTime.UtcNow.ToString("ss:fff"));
            }
        }
    }
}
