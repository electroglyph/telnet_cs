namespace telnet_cs.Fuzz;

using System.Text;

using telnet_cs.Client;
using telnet_cs.Protocol;
using telnet_cs.Server;

/// <summary>
/// Outbound target: drives the client and server write paths with fuzz text,
/// bytes, line feeds, control commands, and negotiation requests — the half
/// the read harnesses never touch. Agreed-option state is reached through
/// the public <see cref="NegotiationState"/> API (as if negotiation had
/// completed) so the option-gated sends actually emit instead of no-op'ing.
/// Oracle: no unexpected throw. <see cref="EncoderFallbackException"/> from
/// strict client writes and <see cref="ArgumentOutOfRangeException"/> from
/// out-of-range commands or overlong forward masks are documented caller
/// errors and are consumed here. Synch/urgent paths are skipped: they need
/// a real TCP stream by design (<see cref="NotSupportedException"/>).
/// </summary>
internal static class WriteHarness
{
    public static async Task<(long Outbound, long Hash)> RunAsync(FuzzInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var bytes = input.Bytes;
        var text = FuzzText.Latin1(bytes, 512);
        var lineFeed = FuzzText.Latin1(bytes, 0, bytes.Length, 8);
        var option = (Options)(bytes.Length > 0 ? bytes[0] : 0);
        var command = (Commands)(bytes.Length > 1 ? bytes[1] : 0);
        var mode = bytes.Length > 2 ? bytes[2] : (byte)0;

        using var clientStream = new FuzzStream();
        using var client = await Client.CreateAsync(clientStream, TimeSpan.FromSeconds(5), CancellationToken.None, [], skipProactiveNegotiation: true);
        client.MillisecondReadDelay = 1;
        await DriveClientAsync(client, text, bytes, lineFeed, option, command, mode, cancellationToken).ConfigureAwait(false);

        var options = new TelnetServerOptions
        {
            IdleTimeout = Timeout.InfiniteTimeSpan,
            StatusInterval = null,
            IsWriteConsole = false,
        };
        using var serverStream = new FuzzStream();
        using var session = new ServerSession(serverStream, options, CancellationToken.None)
        {
            MillisecondReadDelay = 1,
        };
        await DriveServerAsync(session, text, bytes, lineFeed, option, command, mode, cancellationToken).ConfigureAwait(false);

        var (first, firstHash) = clientStream.OutboundSignature();
        var (second, secondHash) = serverStream.OutboundSignature();
        return (first + second, unchecked((firstHash * 31) + secondHash));
    }

    private static async Task DriveClientAsync(
        Client client, string text, byte[] bytes, string lineFeed,
        Options option, Commands command, byte mode, CancellationToken cancellationToken)
    {
        // Strict path first (no BINARY): high bytes fail loudly by design.
        try
        {
            await client.WriteAsync(text, cancellationToken).ConfigureAwait(false);
        }
        catch (EncoderFallbackException)
        {
        }

        await client.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        try
        {
            // WriteLine funnels into the same strict-ASCII WriteAsync path.
            await client.WriteLineAsync(text).ConfigureAwait(false);
            await client.WriteLineAsync(text, "\n").ConfigureAwait(false);
            await client.WriteLineAsync(text, lineFeed).ConfigureAwait(false);
        }
        catch (EncoderFallbackException)
        {
        }
        try
        {
            await client.SendCommand(command, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentOutOfRangeException)
        {
            // Negotiation verbs go through the RFC 1143 API, not SendCommand.
        }

        // Agree options both ways so the gated sends below actually emit.
        client.Negotiation.ReceivedDo((int)Options.LineMode, true);
        client.Negotiation.ReceivedDo((int)Options.SuppressGoAhead, true);
        client.Negotiation.ReceivedDo((int)Options.TransmitBinary, true);
        client.Negotiation.ReceivedDo((int)Options.EndOfRecord, true);
        client.Negotiation.ReceivedDo((int)Options.WindowSize, true);
        client.Negotiation.ReceivedWill((int)Options.TerminalType, true);

        // Binary path now (pre-encoded), plus the previously gated sends.
        await client.WriteAsync(text, cancellationToken).ConfigureAwait(false);
        _ = await client.SendEorAsync(cancellationToken).ConfigureAwait(false);
        await client.RequestEnableAsync(option, cancellationToken).ConfigureAwait(false);
        await client.RequestDisableAsync(option, cancellationToken).ConfigureAwait(false);
        await client.SendTimingMarkAsync(cancellationToken).ConfigureAwait(false);
        client.Settings.WindowWidth = bytes.Length > 0 ? bytes[0] * 257 : 0;
        client.Settings.WindowHeight = bytes.Length > 1 ? bytes[1] * 257 : 0;
        await client.RefreshWindowSizeAsync(cancellationToken).ConfigureAwait(false);
        await client.ImportRemoteSpecialCharactersAsync(cancellationToken).ConfigureAwait(false);
        await client.ExportSpecialCharactersAsync(cancellationToken).ConfigureAwait(false);
        _ = await client.SendGaAsync(cancellationToken).ConfigureAwait(false);
        _ = mode;
    }

    private static async Task DriveServerAsync(
        ServerSession session, string text, byte[] bytes, string lineFeed,
        Options option, Commands command, byte mode, CancellationToken cancellationToken)
    {
        await session.WriteAsync(text, cancellationToken).ConfigureAwait(false);
        await session.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await session.WriteLineAsync(text, cancellationToken).ConfigureAwait(false);
        await session.WriteLineAsync(text, lineFeed, cancellationToken).ConfigureAwait(false);
        session.Settings.TextEncoding = null;
        await session.WriteAsync(text, cancellationToken).ConfigureAwait(false);
        session.Settings.TextEncoding = System.Text.Encoding.UTF8;
        try
        {
            await session.SendCommand(command, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentOutOfRangeException)
        {
            // Negotiation verbs go through the RFC 1143 API, not SendCommand.
        }

        session.Negotiation.ReceivedWill((int)Options.LineMode, true);
        session.Negotiation.ReceivedWill((int)Options.SuppressGoAhead, true);
        session.Negotiation.ReceivedDo((int)Options.TerminalType, true);

        await session.RequestEnableAsync(option, cancellationToken).ConfigureAwait(false);
        await session.RequestDisableAsync(option, cancellationToken).ConfigureAwait(false);
        await session.SendOpeningPresetAsync(cancellationToken).ConfigureAwait(false);
        session.Settings.DisableAllNegotiation = true;
        await session.SendOpeningPresetAsync(cancellationToken).ConfigureAwait(false);
        session.Settings.DisableAllNegotiation = false;
        await session.SendModeAsync(mode, cancellationToken).ConfigureAwait(false);
        try
        {
            // Masks over 32 octets are caller error (documented validation);
            // the fuzz length deliberately straddles the limit.
            await session.SendForwardMaskAsync(bytes[..Math.Min(bytes.Length, 40)], cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentOutOfRangeException)
        {
        }

        await session.PublishSpecialCharactersAsync(cancellationToken).ConfigureAwait(false);
        await session.RequestRemoteSpecialCharactersAsync(cancellationToken).ConfigureAwait(false);
        _ = await session.SendGaAsync(cancellationToken).ConfigureAwait(false);
    }
}
