namespace telnet_cs.Fuzz;

using System.Text;
using telnet_cs.Protocol;

/// <summary>
/// Unit-level target: drives every MUD codec directly on fuzz bytes (full
/// span plus prefixes), bypassing the wire. Synchronous and allocation-light,
/// so it sustains orders of magnitude more iterations than the wire
/// harnesses. Oracle: only the documented <see cref="ArgumentException"/>
/// from <c>GmcpDecode</c> on malformed JSON is allowed; anything else —
/// including from the normally total decoders — is a finding.
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

        return Task.FromResult((0L, 0L));
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
