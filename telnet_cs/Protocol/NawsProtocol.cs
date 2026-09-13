namespace telnet_cs.Protocol
{
    /// <summary>
    /// RFC 1073 NAWS (option 31) terminal-size computation and subnegotiation
    /// framing. Sizes are 16-bit, network byte order; the client sends them
    /// and never parses inbound NAWS (client-side library).
    /// </summary>
    internal static class NawsProtocol
    {
        /// <summary>
        /// Computes the effective terminal size. Each setting is clamped to
        /// the 0-65535 unsigned-short wire range like telnetlib3's NAWS send
        /// path (<c>max(min(65535, v), 0)</c>). A 0 dimension is "unspecified"
        /// per RFC 1073 and is sent as-is, never mapped to a console probe.
        /// </summary>
        /// <param name="widthSetting">The configured width, or 0 for unspecified.</param>
        /// <param name="heightSetting">The configured height, or 0 for unspecified.</param>
        internal static (ushort Width, ushort Height) GetEffectiveSize(int widthSetting, int heightSetting)
        {
            return ((ushort)Math.Clamp(widthSetting, 0, 65535), (ushort)Math.Clamp(heightSetting, 0, 65535));
        }

        /// <summary>
        /// Builds a complete bare <c>IAC SB NAWS wHi wLo hHi hLo IAC SE</c>
        /// frame (RFC 1073 carries no IS verb inside NAWS).
        /// </summary>
        /// <param name="width">The width (0-65535).</param>
        /// <param name="height">The height (0-65535).</param>
        internal static byte[] BuildSubnegotiation(ushort width, ushort height)
        {
            var payload = new byte[]
            {
        (byte)(width >> 8), (byte)width,
        (byte)(height >> 8), (byte)height,
            };
            return EnvironmentProtocol.FrameSubnegotiation((int)Options.WindowSize, payload);
        }

    }
}
