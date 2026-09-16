namespace telnet_cs.Fuzz;

using System.Text;

using telnet_cs.Encodings;
using telnet_cs.IO;
using telnet_cs.Protocol;

/// <summary>
/// Stateless-protocol target: drives the pure option helpers (charset,
/// environment, NAWS, status, terminal speed/type, MTTS, SyncTERM fonts,
/// accessories, line flow, negotiation state machine, SLC state, and the
/// byte/string converter) with fuzz-derived shapes. None of these allocate
/// sessions or touch the wire, so this is the cheapest harness per
/// iteration. Oracle: no throw, except the documented caller-error paths
/// consumed inline (blank codec names, unrepresentable characters under a
/// strict fallback, out-of-range SLC rows).
/// </summary>
internal static class ProtoHarness
{
    public static Task<(long Outbound, long Hash)> RunAsync(FuzzInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var bytes = input.Bytes;
        cancellationToken.ThrowIfCancellationRequested();
        DriveNegotiation(bytes, cancellationToken);
        DriveCharset(bytes, cancellationToken);
        DriveEnvironment(bytes, cancellationToken);
        DriveMisc(bytes, cancellationToken);
        DriveConverter(bytes, cancellationToken);
        return Task.FromResult((0L, 0L));
    }

    private static void DriveNegotiation(byte[] bytes, CancellationToken cancellationToken)
    {
        var state = new NegotiationState();
        byte o1 = bytes.Length > 0 ? bytes[0] : (byte)0;
        byte o2 = bytes.Length > 1 ? bytes[1] : (byte)0;
        var agree = bytes.Length > 2 && bytes[2] % 2 == 0;
        _ = state.ReceivedWill(o1, agree);
        _ = state.ReceivedWont(o2);
        _ = state.ReceivedDo(o1, !agree);
        _ = state.ReceivedDont(o2);
        cancellationToken.ThrowIfCancellationRequested();
        _ = state.RequestEnable(o1);
        _ = state.RequestDisable(o2);
        _ = state.OfferEnable(o1);
        _ = state.OfferDisable(o2);
        _ = state.RequestTimingMark();
        _ = state.IsEnabledByUs(o1);
        _ = state.IsEnabledByPeer(o2);
        _ = state.WasRefusedByUs(o1);
        _ = state.WasRefusedByPeer(o2);
        _ = state.GetStates(o1);
        _ = StatusProtocol.BuildIsPayload(state);
        _ = StatusProtocol.FrameStatusIs(bytes.Length == 0 ? [] : bytes[..Math.Min(bytes.Length, 32)]);
        _ = StatusProtocol.FrameStatusSend();
        _ = LineflowProtocol.IsDefined(o1);
    }

    private static void DriveCharset(byte[] bytes, CancellationToken cancellationToken)
    {
        var offers = new List<string>();
        var count = bytes.Length == 0 ? 0 : bytes[0] % 4;
        for (var i = 0; i < count; i++)
        {
            offers.Add(FuzzText.Latin1(bytes, i * 7, bytes.Length, 24));
        }

        _ = CharsetProtocol.BuildRequest(offers);
        _ = CharsetProtocol.ParseRequest(bytes);
        var name = FuzzText.Latin1(bytes, 24);
        _ = CharsetProtocol.BuildAccepted(name);
        _ = CharsetProtocol.ParseAccepted(bytes);
        cancellationToken.ThrowIfCancellationRequested();
        _ = CharsetProtocol.SelectSupported(offers);
        _ = CharsetProtocol.SelectSupported(offers, name.Length == 0 ? null : name);
        _ = CharsetProtocol.SelectSupported([]);
        _ = CharsetProtocol.BuildTTableRejected();
    }

    private static void DriveEnvironment(byte[] bytes, CancellationToken cancellationToken)
    {
        string? field = bytes.Length > 0 && bytes[0] % 2 == 0 ? FuzzText.Latin1(bytes, 24) : null;
        var userVars = new Dictionary<string, string>(StringComparer.Ordinal);
        if (bytes.Length > 1 && bytes[1] % 2 == 0)
        {
            userVars["U"] = FuzzText.Latin1(bytes, 16);
        }

        foreach (var verb in new[] { EnvironmentProtocol.Is, EnvironmentProtocol.Info })
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = EnvironmentProtocol.BuildResponse(verb, bytes, field, field, userVars, field, field, field, field, field);
        }

        _ = EnvironmentProtocol.ParseEntries(bytes, bytes.Length == 0 ? 0 : bytes[0]);
        _ = EnvironmentProtocol.FrameSubnegotiation(bytes.Length == 0 ? 0 : bytes[0], bytes.Length == 0 ? [] : bytes[..Math.Min(bytes.Length, 64)]);
        var (width, height) = NawsProtocol.GetEffectiveSize(
            bytes.Length > 0 ? bytes[0] * 257 - 100 : 0,
            bytes.Length > 1 ? bytes[1] * 257 - 100 : 0);
        _ = NawsProtocol.BuildSubnegotiation(width, height);
        cancellationToken.ThrowIfCancellationRequested();
        _ = TerminalSpeedProtocol.Validate(field);
        _ = TerminalSpeedProtocol.Validate(FuzzText.Latin1(bytes, 24));
        _ = TerminalSpeedProtocol.RoundForPadding(bytes.Length > 0 ? bytes[0] * 1000 : 0);
        _ = TerminalSpeedProtocol.RoundForPadding(int.MaxValue);
        _ = MttsProtocol.TryParseBitvector(field, out _);
    }

    private static void DriveMisc(byte[] bytes, CancellationToken cancellationToken)
    {
        _ = SyncTermFont.DetectEncoding(bytes);
        var codecName = FuzzText.Latin1(bytes, 16);
        try
        {
            // Empty names are caller error (documented validation).
            _ = SyncTermFont.ResolveEncoding(codecName.Length == 0 ? "x" : codecName);
        }
        catch (ArgumentException)
        {
        }

        try
        {
            _ = SyncTermFont.ResolveEncoding(string.Empty);
        }
        catch (ArgumentException)
        {
        }

        cancellationToken.ThrowIfCancellationRequested();
        _ = TelnetAccessories.EncodingFromLang(FuzzText.Latin1(bytes, 32));
        _ = TelnetAccessories.EncodingFromLang(null);
        _ = TelnetAccessories.Hexdump(bytes, FuzzText.Latin1(bytes, 8));
        var types = new List<string> { FuzzText.Latin1(bytes, 12) };
        if (bytes.Length > 0 && bytes[0] % 2 == 0)
        {
            types.Add(FuzzText.Latin1(bytes, 4, bytes.Length, 12));
        }

        var cycler = new TerminalTypeCycler(types);
        _ = cycler.Next();
        _ = cycler.Next();
        _ = cycler.Matches(types);
        _ = cycler.Matches([]);
        _ = MudClientDetector.IsMudClient(
            bytes.Length > 0 && bytes[0] % 2 == 0 ? FuzzText.Latin1(bytes, 12) : null,
            types,
            static option => (option & 1) == 1);
        var linemode = new LinemodeState();
        try
        {
            // Function codes outside 1-30 and levels above 3 are caller
            // error (documented validation).
            linemode.SetEntry(
                bytes.Length > 0 ? bytes[0] : (byte)0,
                bytes.Length > 1 ? bytes[1] : (byte)0,
                bytes.Length > 2 ? bytes[2] : (byte)0);
        }
        catch (ArgumentOutOfRangeException)
        {
        }

        if (bytes.Length > 0)
        {
            var function = (byte)(1 + (bytes[0] % 30));
            linemode.SetEntry(function, 2, 7);
            _ = linemode.GetEntry(function);
        }

        _ = linemode.Mode;
        _ = linemode.ExportTriplets();
        _ = linemode.ExportTriplets(forImport: true);
        var b0 = bytes.Length > 0 ? bytes[0] : (byte)0;
        var b1 = bytes.Length > 1 ? bytes[1] : (byte)0;
        var b2 = bytes.Length > 2 ? bytes[2] : (byte)0;
        _ = linemode.ApplyMode(b0);
        _ = linemode.ApplyModeAsServer(b0);
        _ = linemode.ApplySlc(b0, b1, b2);
        _ = linemode.ApplySlcAsServer(b0, b1, b2);
        _ = linemode.ApplyForwardMaskOffer(bytes.Length == 0 ? [] : bytes[..Math.Min(bytes.Length, 40)]);
        linemode.ApplyForwardMaskRefusal();
        linemode.ApplyForwardMaskAnswer(accepted: (b0 & 1) == 1);
        _ = linemode.Snoop(b0);
        _ = linemode.MarkSlcPublished();
        _ = linemode.ForwardMask;
        _ = linemode.ForwardMaskOffered;
        _ = linemode.ForwardMaskAccepted;
        linemode.ResetToDefaults();
        _ = LinemodeProtocol.BuildForwardMask(binaryMode: (b0 & 1) == 1);
        _ = LinemodeProtocol.SlcFunctionForCommand((Commands)b0);
        var requestTtype = FuzzText.Latin1(bytes, 8);
        _ = EnvironmentProtocol.BuildDefaultSendRequest(bytes.Length > 0 && (b0 & 1) == 1 ? requestTtype : null, requestTtype);
        _ = EnvironmentProtocol.ShouldForceBinary(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["LANG"] = FuzzText.Latin1(bytes, 16),
        });
    }

    private static void DriveConverter(byte[] bytes, CancellationToken cancellationToken)
    {
        var text = FuzzText.Latin1(bytes, 128);
        _ = ByteStringConverter.ConvertStringToByteArray(text);
        _ = ByteStringConverter.ConvertStringToByteArray(text, null);
        _ = ByteStringConverter.ConvertStringToByteArray(text, System.Text.Encoding.UTF8);
        var retro = TelnetEncodingProvider.Instance.GetEncoding(80001 + (bytes.Length > 0 ? bytes[0] % 4 : 0));
        if (retro is not null)
        {
            try
            {
                // Unrepresentable characters fail loudly instead of emitting
                // "?": the documented strict contract, not a finding.
                _ = ByteStringConverter.ConvertStringToByteArray(text, retro);
            }
            catch (EncoderFallbackException)
            {
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        _ = ByteStringConverter.ToString(bytes);
        _ = ByteStringConverter.ToString(bytes, System.Text.Encoding.UTF8);
        if (bytes.Length > 0)
        {
            var offset = bytes[0] % bytes.Length;
            _ = ByteStringConverter.ToString(bytes, offset, bytes.Length - offset);
            _ = ByteStringConverter.ToString(bytes, offset, bytes.Length - offset, System.Text.Encoding.UTF8);
        }

        _ = ByteStringConverter.EscapeIacBytes(bytes);
    }
}
