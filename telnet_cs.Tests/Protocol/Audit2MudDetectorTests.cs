namespace telnet_cs.Tests
{
    /// <summary>
    /// Round-2 audit (§3/§8) detector notes: both earlier pins were
    /// removed as incorrect. <c>MudClientDetector.IsMudClient</c>
    /// faithfully mirrors the reference <c>_is_maybe_mud</c>
    /// (fingerprinting.py:1132-1143: MUD_TERMINALS + GMCP/MSDP/MXP/MSP/
    /// ATCP/AARDWOLF only — no MTTS check, MSP not MSSP). The MTTS-prefix
    /// loop and MSSP-inclusive set live only in
    /// <c>fingerprinting_server_shell</c> (fingerprinting.py:1213-1221),
    /// a probing path this library does not implement — and the audit
    /// itself downgraded both to latent/wire-neutral (F2-MTTS-MUD /
    /// F2-MSSP-SKEW). Asserting MTTS/MSSP here would bless divergence
    /// from <c>_is_maybe_mud</c>, not parity with it.
    /// </summary>
    public class Audit2MudDetectorTests
    {
    }
}
