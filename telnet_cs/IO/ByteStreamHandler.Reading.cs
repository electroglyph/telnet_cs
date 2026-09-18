namespace telnet_cs.IO
{
    using System;
    using System.Collections.Generic;
    using System.Runtime.InteropServices;
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
        /// <returns>Any text read from the stream.</returns>
        /// <exception cref="OperationCanceledException">The read was already cancelled before it started.</exception>
        public async Task<string> ReadAsync(TimeSpan timeout)
        {
            // A closed peer ends the stream — but only once held bytes drain:
            // the parser may stash a byte (CR LF continuation pushback, MCCP
            // output) past the peer's final close, and the slice loop below
            // already ends itself on !Connected, so entering it here can only
            // emit the stranded bytes, never block (a second pass finds
            // nothing held and returns empty the same way).
            if (!byteStream.Connected && !IsResponsePending)
            {
                return string.Empty;
            }

            // A pre-cancelled read throws (reference _wait_for_data parity)
            // instead of returning empty: callers that treat cancel as
            // "no data" (Client/ServerSession.ReadAsync) catch this
            // themselves. A cancel landing mid-read still returns the
            // partial response via the catch below.
            internalCancellation.Token.ThrowIfCancellationRequested();

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
                    bool got = await RetrieveAndParseResponse(sb, rawBytes, opByteCounts).ConfigureAwait(false);
                    if (got)
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

            if (LocalEchoEnabled)
            {
                Console.Write(read);
            }

            return read;
        }

        private string DecodeResult(StringBuilder sb, List<byte> rawBytes)
        {
            ApplySyncTermFont(CollectionsMarshal.AsSpan(rawBytes));
            if (TextEncoding is not null)
            {
                // Persistent incremental decoder (reference stream_reader
                // parity): a multibyte sequence split across reads buffers
                // its lead instead of decoding art immediately. The decoder
                // is replaced whenever the encoding changes.
                if (textDecoder is null || !ReferenceEquals(textDecoderEncoding, TextEncoding))
                {
                    textDecoder = TextEncoding.GetDecoder();
                    textDecoderEncoding = TextEncoding;
                }

                byte[] bytes = rawBytes.ToArray();
                char[] chars = new char[TextEncoding.GetMaxCharCount(bytes.Length)];
                int count = textDecoder.GetChars(bytes, 0, bytes.Length, chars, 0, flush: false);
                return new string(chars, 0, count);
            }

            return sb.ToString();
        }

        /// <summary>
        /// Scans inbound bytes for a SyncTERM font-selection sequence and
        /// adopts its encoding for subsequent reads — client role only. A
        /// server must never let peer bytes reconfigure its decoding: server
        /// decoding is set by negotiation and explicit configuration alone,
        /// so the sequence stays inert data there (reference parity:
        /// <c>server_base.py</c> has no font scan). An explicitly configured
        /// <see cref="ByteStreamHandler.TextEncoding"/> always wins; the
        /// sequence itself stays in the data stream untouched. A detected
        /// switch always latches binary decoding (reference parity:
        /// <c>client_base.py</c> sets <c>force_binary</c> even when an explicit
        /// encoding ignores the switch), so the new 8-bit font is not gated
        /// through 7-bit stripping.
        /// </summary>
        private void ApplySyncTermFont(ReadOnlySpan<byte> raw)
        {
            if (IsServerRole || raw.IsEmpty)
            {
                return;
            }

            var name = SyncTermFont.DetectEncoding(raw);
            if (name is null)
            {
                return;
            }

            var encoding = SyncTermFont.ResolveEncoding(name);
            if (encoding is null)
            {
                return;
            }

            ForceBinaryDecoding = true;
            if (TextEncoding is not null)
            {
                return;
            }

            TextEncoding = encoding;
            SyncTermFontDetected?.Invoke(name);
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
