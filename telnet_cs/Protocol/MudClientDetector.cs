namespace telnet_cs.Protocol
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Detects MUD clients, mirroring telnetlib3's
    /// <c>fingerprinting._is_maybe_mud</c>: a terminal name in the known MUD
    /// client set (as the effective type or any of the first three TTYPE
    /// answers), or any agreed MUD option (GMCP, MSDP, MXP, MSP, ATCP,
    /// Aardwolf). The server uses this to withhold <c>WILL ECHO</c>: MUD
    /// clients render it as password mode and mask the input bar.
    /// </summary>
    internal static class MudClientDetector
    {
        private static readonly HashSet<string> MudTerminals = new(StringComparer.OrdinalIgnoreCase)
        {
            "mudlet",
            "cmud",
            "zmud",
            "mushclient",
            "atlantis",
            "tintin++",
            "tt++",
            "blowtorch",
            "mudrammer",
            "kildclient",
            "portal",
            "beip",
            "savitar",
        };

        private static readonly int[] MudOptions =
        [
            (int)Options.Gmcp,
            (int)Options.Msdp,
            (int)Options.Mxp,
            (int)Options.Msp,
            (int)Options.Atcp,
            (int)Options.Aardwolf,
        ];

        /// <summary>
        /// Reports whether the peer looks like a MUD client.
        /// </summary>
        /// <param name="effectiveTerminalType">The effective terminal type (MTTS-aware), if any.</param>
        /// <param name="terminalTypeChain">The collected TTYPE answers, in order.</param>
        /// <param name="remoteOptionEnabled">Tests whether the peer enabled an option.</param>
        internal static bool IsMudClient(string? effectiveTerminalType, IReadOnlyList<string> terminalTypeChain, Func<int, bool> remoteOptionEnabled)
        {
            ArgumentNullException.ThrowIfNull(terminalTypeChain);
            ArgumentNullException.ThrowIfNull(remoteOptionEnabled);
            if (effectiveTerminalType is not null && MudTerminals.Contains(effectiveTerminalType))
            {
                return true;
            }

            for (int i = 0; i < terminalTypeChain.Count && i < 3; i++)
            {
                if (terminalTypeChain[i] is not null && MudTerminals.Contains(terminalTypeChain[i]))
                {
                    return true;
                }
            }

            foreach (int option in MudOptions)
            {
                if (remoteOptionEnabled(option))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
