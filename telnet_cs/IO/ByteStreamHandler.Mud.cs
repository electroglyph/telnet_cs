namespace telnet_cs.IO;

using System.Text;
using System.Text.Json.Nodes;
using telnet_cs.Protocol;
using telnet_cs.Transport;
/// <summary>
/// MUD extension dispatch: option classification, encoding selection, ZMP answers and MCCP stream arming. Split from <see cref="ByteStreamHandler"/>; wire behavior is unchanged.
/// </summary>
public partial class ByteStreamHandler
{

    /// <summary>
    /// Gets whether <paramref name="inputOption"/> allows an empty
    /// <c>IAC SB &lt;opt&gt; IAC SE</c>: MCCP2/MCCP3 start compression
    /// with one, and several MUD options permit one (reference
    /// <c>_EMPTY_SB_OK</c> set).
    /// </summary>
    /// <param name="inputOption">The option under negotiation.</param>
    private static bool IsEmptySbAllowed(int inputOption)
    {
        return inputOption is (int)Options.Mccp2 or (int)Options.Mccp3 or
          (int)Options.Msp or (int)Options.Mxp or (int)Options.Zmp or
          (int)Options.Aardwolf or (int)Options.Atcp;
    }

    /// <summary>
    /// Gets whether <paramref name="inputOption"/> is a MUD option with an
    /// opaque subnegotiation body (MSDP, MSSP, MSP, MXP, ZMP, Aardwolf,
    /// ATCP, GMCP).
    /// </summary>
    /// <param name="inputOption">The option under negotiation.</param>
    private static bool IsMudOption(int inputOption)
    {
        return inputOption is (int)Options.Msdp or (int)Options.Mssp or
          (int)Options.Msp or (int)Options.Mxp or (int)Options.Zmp or
          (int)Options.Aardwolf or (int)Options.Atcp or (int)Options.Gmcp;
    }

    /// <summary>
    /// Resolves the text encoding for MUD subnegotiation payloads (MSDP,
    /// MSSP, ZMP, ATCP, GMCP, ...): the agreed CHARSET once one resolved
    /// (strict), else null, which selects UTF-8 with Latin-1 fallback in
    /// <c>MudProtocol</c>.
    /// </summary>
    /// <remarks>
    /// UTF-8-first is deliberate. GMCP carries JSON, which is UTF-8 by
    /// definition, and the MUD option family centers UTF-8 as the
    /// negotiated capability (CHARSET negotiation, the MSDP <c>UTF_8</c>
    /// variable, the MTTS flag) — while no specification mandates a
    /// pre-CHARSET default, so any choice corrupts some peer camp. The
    /// Latin-1 fallback already absorbs the common legacy case (lone high
    /// bytes fail strict UTF-8 decoding and retry as Latin-1); the
    /// accepted residual is Latin-1 payloads that also parse as valid
    /// UTF-8, which decode as UTF-8. Peers are expected to negotiate a
    /// charset; until they do, UTF-8 is the assumed wire encoding.
    /// </remarks>
    private Encoding? MudEncoding()
    {
        if (NegotiatedCharset is null)
        {
            return null;
        }

        try
        {
            return Encoding.GetEncoding(
              NegotiatedCharset,
              EncoderFallback.ExceptionFallback,
              DecoderFallback.ExceptionFallback);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Dispatches a MUD subnegotiation body (empty or not) to its
    /// per-protocol decoder: typed hooks fire, the reference's stores
    /// update (MSSP replaced, MXP/Aardwolf/ATCP appended, ZMP per-command
    /// replaced), and the raw <see cref="MudSubnegotiationReceived"/>
    /// hook fires last so it still surfaces every body.
    /// </summary>
    /// <param name="inputOption">The MUD option number.</param>
    /// <param name="body">The subnegotiation body without the option byte.</param>
    private void DispatchMud(int inputOption, byte[] body)
    {
        if (MaxMudListBytes > 0 && body.Length > MaxMudListBytes
            && (inputOption == (int)Options.Gmcp || inputOption == (int)Options.Msdp))
        {
            WriteLog($"mud-cap: option={inputOption} len={body.Length} cap={MaxMudListBytes}");
            return;
        }

        var encoding = MudEncoding();
        switch (inputOption)
        {
            case (int)Options.Gmcp:
                // Malformed JSON is a peer value, not a framing bug: log
                // and ignore it like every other malformed subnegotiation
                // (the reference debug-logs the ValueError in its feed
                // loop), so a bad frame can never tear down the read.
                string package;
                JsonNode? data;
                try
                {
                    (package, data) = MudProtocol.GmcpDecode(body, encoding);
                }
                catch (ArgumentException ex)
                {
                    WriteLog("Ignoring malformed GMCP payload: " + ex.Message);
                    break;
                }

                GmcpReceived?.Invoke(package, data);
                break;
            case (int)Options.Msdp:
                MsdpReceived?.Invoke(MudProtocol.MsdpDecode(body, encoding));
                break;
            case (int)Options.Mssp:
                var status = MudProtocol.MsspDecode(body, encoding);
                if (MaxMudKeys > 0 && status.Count > MaxMudKeys)
                {
                    WriteLog($"mud-cap: option=mssp vars={status.Count} cap={MaxMudKeys}");
                    status = status.Take(MaxMudKeys).ToDictionary(kv => kv.Key, kv => kv.Value);
                }

                if (MaxMudValueChars > 0)
                {
                    bool dropped = false;
                    var filtered = new Dictionary<string, object>(status.Count);
                    foreach (var kv in status)
                    {
                        if (kv.Value is string s && s.Length > MaxMudValueChars)
                        {
                            dropped = true;
                            continue;
                        }

                        filtered[kv.Key] = kv.Value;
                    }

                    if (dropped)
                    {
                        WriteLog($"mud-cap: option=mssp value-cap={MaxMudValueChars}");
                    }

                    status = filtered;
                }

                MsspData = new Dictionary<string, object>(status);
                MsspReceived?.Invoke(status);
                break;
            case (int)Options.Msp:
                MspData.Add(body);
                TrimMudByteList(MspData, static b => b.Length);
                MspReceived?.Invoke(body);
                break;
            case (int)Options.Mxp:
                MxpData.Add(body);
                TrimMudByteList(MxpData, static b => b.Length);
                MxpReceived?.Invoke(body);
                break;
            case (int)Options.Zmp:
                var parts = MudProtocol.ZmpDecode(body, encoding);
                if (parts.Count > 0)
                {
                    string command = parts[0];
                    List<string> args = [.. parts.Skip(1)];
                    if (MaxMudValueChars > 0 && (command.Length > MaxMudValueChars || args.Any(a => a.Length > MaxMudValueChars)))
                    {
                        WriteLog($"mud-cap: option=zmp cmd-len={command.Length} args={args.Count} cap={MaxMudValueChars}");
                        break;
                    }

                    if (MaxMudKeys > 0 && args.Count > MaxMudKeys)
                    {
                        WriteLog($"mud-cap: option=zmp cmd-len={command.Length} args={args.Count} cap={MaxMudKeys}");
                        break;
                    }

                    if (MaxMudKeys > 0 && !ZmpData.ContainsKey(command) && ZmpData.Count >= MaxMudKeys)
                    {
                        WriteLog($"mud-cap: option=zmp keys={ZmpData.Count} cap={MaxMudKeys}");
                        break;
                    }

                    IReadOnlyList<string> argView = args;
                    ZmpData[command] = argView;
                    ZmpReceived?.Invoke(command, argView);
                }

                break;
            case (int)Options.Aardwolf:
                var message = MudProtocol.AardwolfDecode(body);
                if (MaxMudValueChars > 0 && message.DataBytes.Length + message.Channel.Length > MaxMudValueChars)
                {
                    WriteLog($"mud-cap: option=aardwolf len={message.DataBytes.Length} cap={MaxMudValueChars}");
                    break;
                }

                AardwolfData.Add(message);
                TrimMudByteList(AardwolfData, static m => m.DataBytes.Length + m.Channel.Length);
                AardwolfReceived?.Invoke(message);
                break;
            case (int)Options.Atcp:
                var (atcpPackage, atcpValue) = MudProtocol.AtcpDecode(body, encoding);
                if (MaxMudValueChars > 0 && (atcpPackage.Length > MaxMudValueChars || atcpValue.Length > MaxMudValueChars))
                {
                    WriteLog($"mud-cap: option=atcp len={atcpPackage.Length + atcpValue.Length} cap={MaxMudValueChars}");
                    break;
                }

                AtcpData.Add((atcpPackage, atcpValue));
                TrimMudByteList(AtcpData, static t => t.Package.Length + t.Value.Length);
                AtcpReceived?.Invoke(atcpPackage, atcpValue);
                break;
            default:
                break;
        }

        MudSubnegotiationReceived?.Invoke(inputOption, body);
    }

    private void TrimMudByteList<T>(List<T> list, Func<T, int> sizeOf)
    {
        if (MaxMudListItems > 0)
        {
            while (list.Count > MaxMudListItems)
            {
                list.RemoveAt(0);
            }
        }

        if (MaxMudListBytes > 0)
        {
            long total = 0;
            foreach (var item in list)
            {
                total += sizeOf(item);
            }

            while (list.Count > 0 && total > MaxMudListBytes)
            {
                total -= sizeOf(list[0]);
                list.RemoveAt(0);
            }
        }
    }

    /// <summary>
    /// Auto-answers ZMP capability queries on the client role (reference
    /// on_zmp): <c>zmp.check &lt;cmd&gt;</c> and <c>zmp.send-support</c>
    /// with args each earn <c>zmp.support</c> when either the
    /// <see cref="ZmpCheckHandler"/> approves or the command is in the
    /// advertised <see cref="ZmpSupportedCommands"/> (refused by default
    /// when neither source approves); a bare <c>zmp.send-support</c> is
    /// only answered before our ident went out. The store/hook dispatch
    /// in <see cref="DispatchMud"/> runs first, so answers never precede
    /// recording.
    /// </summary>
    /// <param name="body">The subnegotiation body without the option byte.</param>
    private async Task AnswerZmpAsync(byte[] body)
    {
        if (IsServerRole || !EnableZmp)
        {
            return;
        }

        if (ShouldSuppressStormSbReply((int)Options.Zmp))
        {
            WriteLog("storm-guard: suppressed SB reply for ZMP (negotiation storm).");
            return;
        }

        var parts = MudProtocol.ZmpDecode(body, MudEncoding());
        if (parts.Count == 0)
        {
            return;
        }

        if (parts[0] == "zmp.check" && parts.Count > 1)
        {
            var approved = ZmpCheckHandler?.Invoke(parts[1]) == true
                || ZmpSupportedCommands.Contains(parts[1]);
            await SendNegotiation((int)Options.Zmp,
              MudProtocol.ZmpEncode(approved ? "zmp.support" : "zmp.no-support", parts[1])).ConfigureAwait(false);
            return;
        }

        if (parts[0] == "zmp.send-support")
        {
            if (parts.Count > 1)
            {
                for (var i = 1; i < parts.Count; i++)
                {
                    var command = parts[i];
                    var supported = ZmpCheckHandler?.Invoke(command) == true
                        || ZmpSupportedCommands.Contains(command);
                    await SendNegotiation((int)Options.Zmp,
                      MudProtocol.ZmpEncode(supported ? "zmp.support" : "zmp.no-support", command)).ConfigureAwait(false);
                }

                return;
            }

            if (!ZmpIdentSent)
            {
                foreach (var command in ZmpSupportedCommands.OrderBy(static c => c, StringComparer.Ordinal))
                {
                    await SendNegotiation((int)Options.Zmp,
                      MudProtocol.ZmpEncode("zmp.support", command)).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    /// Handles an empty subnegotiation for an option that allows one:
    /// MCCP2/MCCP3 arm inflation through the session-owned
    /// <see cref="MccpStream"/> (created here, replaced when spent) and
    /// fire their start hooks; MUD options dispatch an empty body through
    /// <see cref="DispatchMud"/> like any other body.
    /// </summary>
    /// <param name="inputOption">The option under negotiation.</param>
    private void ReplyEmptySb(int inputOption)
    {
        if (inputOption is (int)Options.Mccp2 or (int)Options.Mccp3)
        {
            if (!MccpStartAllowed(inputOption))
            {
                return;
            }

            // Direction gate (see the padding-carrying path above):
            // MCCP2 inflates client-side only, MCCP3 server-side only.
            if (inputOption == (int)Options.Mccp2)
            {
                if (IsServerRole)
                {
                    return;
                }

                WriteLog("MCCP2 compression started; inflating inbound bytes.");
                Mccp2Active = true;
                ArmMccpStream(inputOption);
                Mccp2StartReceived?.Invoke();
                return;
            }

            if (!IsServerRole)
            {
                // Wrong direction to inflate, and outbound already
                // started when we agreed to the peer's WILL: nothing
                // more to do.
                return;
            }

            WriteLog("MCCP3 compression started; inflating inbound bytes.");
            Mccp3Active = true;
            ArmMccpStream(inputOption);
            Mccp3StartReceived?.Invoke();
            return;
        }

        DispatchMud(inputOption, []);
    }

    /// <summary>
    /// Whether an MCCP SB may start inflation: compression opted in,
    /// never over TLS (CRIME/BREACH), and only after WILL/DO agreement.
    /// Both the empty and the padding-carrying SB forms share this gate.
    /// </summary>
    /// <param name="inputOption">The MCCP option (MCCP2 or MCCP3).</param>
    private bool MccpStartAllowed(int inputOption)
    {
        return EnableMccp && !IsTlsActive &&
            (Negotiation.IsEnabledByPeer(inputOption) || Negotiation.IsEnabledByUs(inputOption));
    }

    /// <summary>
    /// Arms the session-owned MCCP decompressor, replacing a spent one
    /// (ended or failed) so a fresh SB always starts a fresh stream.
    /// </summary>
    /// <param name="inputOption">The MCCP option that armed (also captures
    /// the corrupt-path refusal: WONT when our own offer is withdrawn,
    /// DONT when the peer's offer is refused; and forces strict zlib on
    /// server-role MCCP3, whose peer sends zlib only).</param>
    private void ArmMccpStream(int inputOption)
    {
        if (MccpStream is null || !MccpStream.IsActive)
        {
            MccpStream = new MccpDecompressor();
        }

        MccpStream.StrictZlib = IsServerRole && inputOption == (int)Options.Mccp3;
        if (MaxDecompressedBytes > 0)
        {
            MccpStream.MaxDecompressedBytes = MaxDecompressedBytes;
        }

        if (MaxDecompressionRatio > 0)
        {
            MccpStream.MaxDecompressionRatio = MaxDecompressionRatio;
        }

        if (MaxCompressedBytes > 0)
        {
            MccpStream.MaxCompressedBytes = MaxCompressedBytes;
        }

        if (MccpCapLog is not null)
        {
            MccpStream.CapLog = MccpCapLog;
        }

        mccpShutdownPending = false;
        mccpShutdownWont = false;
        mccpShutdownOption = inputOption;
        mccpArmedWont = !Negotiation.IsEnabledByPeer(inputOption);
        MccpStateChanged?.Invoke(Mccp2Active, Mccp3Active, MccpStream);
    }
}
