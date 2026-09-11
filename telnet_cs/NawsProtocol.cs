namespace telnet_cs
{
  /// <summary>
  /// RFC 1073 NAWS (option 31) terminal-size computation and subnegotiation
  /// framing. Sizes are 16-bit, network byte order; the client sends them
  /// and never parses inbound NAWS (client-side library).
  /// </summary>
  internal static class NawsProtocol
  {
    /// <summary>
    /// Computes the effective terminal size. Explicit settings win;
    /// otherwise the console size is used, falling back to 80x24 when
    /// unavailable. Each dimension is clamped to 80/24 when outside 1-65535.
    /// </summary>
    /// <param name="widthSetting">The configured width, or 0 for auto.</param>
    /// <param name="heightSetting">The configured height, or 0 for auto.</param>
    internal static (ushort Width, ushort Height) GetEffectiveSize(int widthSetting, int heightSetting)
    {
      var width = widthSetting > 0 ? widthSetting : GetConsoleDimension(true);
      var height = heightSetting > 0 ? heightSetting : GetConsoleDimension(false);
      if (width <= 0 || width > 65535)
      {
        width = 80;
      }

      if (height <= 0 || height > 65535)
      {
        height = 24;
      }

      return ((ushort)width, (ushort)height);
    }

    /// <summary>
    /// Builds a complete <c>IAC SB NAWS IS wHi wLo hHi hLo IAC SE</c> frame.
    /// </summary>
    /// <param name="width">The width (0-65535).</param>
    /// <param name="height">The height (0-65535).</param>
    internal static byte[] BuildSubnegotiation(ushort width, ushort height)
    {
      var payload = new byte[]
      {
        EnvironmentProtocol.Is,
        (byte)(width >> 8), (byte)width,
        (byte)(height >> 8), (byte)height,
      };
      return EnvironmentProtocol.FrameSubnegotiation((int)Options.WindowSize, payload);
    }

    private static int GetConsoleDimension(bool width)
    {
      try
      {
        return width ? Console.WindowWidth : Console.WindowHeight;
      }
      catch (System.IO.IOException)
      {
        return width ? 80 : 24;
      }
    }
  }
}
