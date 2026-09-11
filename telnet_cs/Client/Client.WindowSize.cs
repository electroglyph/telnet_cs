namespace telnet_cs.Client
{
    using System.Threading;
    using System.Threading.Tasks;
    using telnet_cs.Protocol;

    public partial class Client
    {
        /// <summary>
        /// Re-sends the NAWS terminal size (<c>IAC SB NAWS IS … IAC SE</c>,
        /// RFC 1073) when it changed since the last report. Sends nothing unless
        /// we are the WILL-sender: a server <c>DON'T</c> after accept suppresses
        /// further updates (the RFC's anti-loop rule), as does a never-negotiated
        /// option. Change <see cref="TelnetClientOptions.WindowWidth"/>/
        /// <see cref="TelnetClientOptions.WindowHeight"/> (or resize the console)
        /// before calling; .NET exposes no console-resize event, so polling via
        /// this method is the detection mechanism.
        /// </summary>
        /// <param name="cancellationToken">A token to cancel the send.</param>
        /// <returns>An awaitable Task.</returns>
        public async Task RefreshWindowSizeAsync(CancellationToken cancellationToken = default)
        {
            if (!Negotiation.IsEnabledByUs((int)Options.WindowSize))
            {
                return;
            }

            var size = NawsProtocol.GetEffectiveSize(Settings.WindowWidth, Settings.WindowHeight);
            if (_lastSentNaws == size)
            {
                return;
            }

            var frame = NawsProtocol.BuildSubnegotiation(size.Width, size.Height);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
              cancellationToken, InternalCancellation.Token);
            if (ByteStream.Connected && !linked.Token.IsCancellationRequested)
            {
                await SendRateLimit.WaitAsync(linked.Token).ConfigureAwait(false);
                try
                {
                    await ByteStream.WriteAsync(frame, 0, frame.Length, linked.Token).ConfigureAwait(false);
                    _lastSentNaws = size;
                }
                finally
                {
                    SendRateLimit.Release();
                }
            }
        }

        private void RecordNawsSize(ushort width, ushort height) => _lastSentNaws = (width, height);

        private (ushort Width, ushort Height)? _lastSentNaws;
    }
}
