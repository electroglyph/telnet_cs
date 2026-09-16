namespace telnet_cs.Fuzz;

using System.Text;
using telnet_cs.Protocol;

/// <summary>
/// Unit-level target: drives every MUD codec directly on fuzz bytes (full
/// span plus prefixes), bypassing the wire, plus the encode direction built
/// from fuzz-derived values. Synchronous and allocation-light, so it
/// sustains orders of magnitude more iterations than the wire harnesses.
/// Oracle: only documented throws are allowed — <see cref="ArgumentException"/>
/// from <c>GmcpDecode</c> on malformed JSON, argument validation on blank
/// package names or non-string MSSP values; anything else — including from
/// the normally total decoders and encoders — is a finding.
/// </summary>
internal static class CodecHarness
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static Task<(long Outbound, long Hash)> RunAsync(FuzzInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();
        DecodeAll(input.Bytes, null, cancellationToken);
        DecodeAll(input.Bytes, StrictUtf8, cancellationToken);
        if (input.Bytes.Length > 1)
        {
            DecodeAll(input.Bytes.AsSpan(0, input.Bytes.Length / 2), null, cancellationToken);
        }

        EncodeAll(input.Bytes, cancellationToken);
        return Task.FromResult((0L, 0L));
    }

    private static void EncodeAll(byte[] bytes, CancellationToken cancellationToken)
    {
        // Blank package names are caller error (documented validation), so
        // fall back to a fixed package instead of swallowing the throw.
        var package = "P." + FuzzText.Latin1(bytes, 0, Math.Min(bytes.Length, 24), 24).Trim();
        if (string.IsNullOrWhiteSpace(package.Replace("P.", string.Empty, StringComparison.Ordinal)))
        {
            package = "P.X";
        }

        var json = FuzzText.Latin1(bytes, 64);
        _ = MudProtocol.GmcpEncode(package);
        _ = MudProtocol.GmcpEncode(package, json);
        _ = MudProtocol.GmcpEncode(package, null);
        var table = BuildTable(bytes, 0);
        _ = MudProtocol.GmcpEncodeData(package, null);
        _ = MudProtocol.GmcpEncodeData(package, table);
        _ = MudProtocol.GmcpJson(table);
        cancellationToken.ThrowIfCancellationRequested();
        _ = MudProtocol.MsdpEncode(table);
        try
        {
            // Non-string MSSP values are caller error (documented
            // validation): the fuzz table mixes value shapes on purpose.
            _ = MudProtocol.MsspEncode(BuildMsspTable(bytes));
        }
        catch (ArgumentException)
        {
        }

        _ = MudProtocol.MsspEncode(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["K0"] = FuzzText.Latin1(bytes, 32),
            ["K1"] = new[] { FuzzText.Latin1(bytes, 0, bytes.Length / 2, 32), FuzzText.Latin1(bytes, 16) },
        });
        cancellationToken.ThrowIfCancellationRequested();
        _ = MudProtocol.ZmpEncode(FuzzText.Latin1(bytes, 24), FuzzText.Latin1(bytes, Math.Min(bytes.Length, 8), bytes.Length, 24));
        _ = MudProtocol.ZmpEncode();
        try
        {
            // Concatenating an arbitrary body is only valid when the body
            // parses; malformed JSON stays a documented ArgumentException.
            _ = MudProtocol.GmcpDecode(MudProtocol.GmcpEncode(package, json), null);
        }
        catch (ArgumentException)
        {
        }

        _ = MudProtocol.MsdpDecode(MudProtocol.MsdpEncode(table), null);
        _ = MudProtocol.ZmpDecode(MudProtocol.ZmpEncode(FuzzText.Latin1(bytes, 24)), null);
    }

    private static Dictionary<string, object?> BuildTable(byte[] bytes, int depth)
    {
        var table = new Dictionary<string, object?>(StringComparer.Ordinal);
        var entries = bytes.Length == 0 ? 0 : 1 + (bytes[0] % 3);
        for (var i = 0; i < entries; i++)
        {
            var key = "K" + i;
            object? value = ((bytes.Length > i + 1 ? bytes[i + 1] : 0) % 6) switch
            {
                0 => FuzzText.Latin1(bytes, 32),
                1 => depth < 2 ? BuildTable(bytes, depth + 1) : FuzzText.Latin1(bytes, 16),
                2 => new List<object?> { FuzzText.Latin1(bytes, 8), 42, null, true },
                3 => 42,
                4 => null,
                _ => true,
            };
            table[key] = value;
        }

        return table;
    }

    private static Dictionary<string, object> BuildMsspTable(byte[] bytes)
    {
        // Deliberately mixes a non-string value in when the input says so,
        // to pin the documented validation path.
        object odd = bytes.Length % 2 == 0 ? (object)42 : FuzzText.Latin1(bytes, 32);
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["K0"] = FuzzText.Latin1(bytes, 16),
            ["K1"] = odd,
        };
    }

    private static void DecodeAll(ReadOnlySpan<byte> body, Encoding? encoding, CancellationToken cancellationToken)
    {
        try
        {
            _ = MudProtocol.GmcpDecode(body, encoding);
        }
        catch (ArgumentException)
        {
            // Documented contract: malformed JSON is a ValueError equivalent.
        }

        cancellationToken.ThrowIfCancellationRequested();
        _ = MudProtocol.MsdpDecode(body, encoding);
        cancellationToken.ThrowIfCancellationRequested();
        _ = MudProtocol.MsspDecode(body, encoding);
        cancellationToken.ThrowIfCancellationRequested();
        _ = MudProtocol.ZmpDecode(body, encoding);
        cancellationToken.ThrowIfCancellationRequested();
        _ = MudProtocol.AtcpDecode(body, encoding);
        cancellationToken.ThrowIfCancellationRequested();
        _ = MudProtocol.AardwolfDecode(body.ToArray());
    }
}
