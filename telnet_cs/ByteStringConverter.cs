
namespace telnet_cs
{
  using System.Text;

  /// <summary>
  /// Converts between strings and bytes for the TELNET data path.
  /// </summary>
  /// <remarks>
  /// The default (null-encoding) mapping is Latin-1: bytes 0-255 round-trip
  /// one-to-one. This is intentionally 8-bit-clean whether or not BINARY
  /// (RFC 856, option 0) was negotiated — a documented deviation from strict
  /// 7-bit NVT, kept so agreed-BINARY transfers arrive unmangled.
  /// </remarks>
  internal static class ByteStringConverter
  {
    public static byte[] ConvertStringToByteArray(string value)
    {
      return ConvertStringToByteArray(value, null);
    }

    /// <summary>
    /// Converts a string to bytes, escaping literal IAC bytes by doubling.
    /// </summary>
    /// <param name="value">The string to convert.</param>
    /// <param name="encoding">The encoding to use. When null (default), the legacy Latin-1 mapping is used.</param>
    public static byte[] ConvertStringToByteArray(string value, Encoding? encoding)
    {
      // RFC 854: a literal IAC byte (255) in outgoing data must be escaped by
      // doubling it. Done with a manual loop so the comparison is ordinal by
      // construction (and CA1307 flags the 2-argument string.Replace form).
      var doubled = new StringBuilder(value.Length);
      foreach (var c in value)
      {
        doubled.Append(c);
        if (c == (char)Commands.InterpretAsCommand)
        {
          doubled.Append(c);
        }
      }

      var escaped = doubled.ToString();

      if (encoding == null)
      {
        // Latin-1 maps bytes 0-255 one-to-one to the first 256 Unicode code
        // points. (ASCIIEncoding would map everything above 127 to '?'.) A
        // manual loop preserves the legacy truncation semantics exactly.
        var buffer = new byte[escaped.Length];
        for (var i = 0; i < escaped.Length; i++)
        {
          buffer[i] = (byte)escaped[i];
        }

        return buffer;
      }

      return encoding.GetBytes(escaped);
    }

    public static string ToString(byte[] bytes)
    {
      return ToString(bytes, 0, bytes.Length);
    }

    /// <summary>
    /// Decodes bytes to a string.
    /// </summary>
    /// <param name="bytes">The bytes to decode.</param>
    /// <param name="encoding">The encoding to use. When null (default), the legacy Latin-1 mapping is used.</param>
    public static string ToString(byte[] bytes, Encoding? encoding)
    {
      return ToString(bytes, 0, bytes.Length, encoding);
    }

    internal static string ToString(byte[] bytes, int offset, int count)
    {
      return ToString(bytes, offset, count, null);
    }

    internal static string ToString(byte[] bytes, int offset, int count, Encoding? encoding)
    {
      if (encoding != null)
      {
        return encoding.GetString(bytes, offset, count);
      }

      // Latin-1 decode: every byte round-trips, including 0xFF. Do not trim:
      // leading/trailing 0xFF bytes may be legitimate data.
      var chars = new char[count];
      for (var i = 0; i < count; i++)
      {
        chars[i] = (char)bytes[offset + i];
      }

      return new string(chars);
    }
  }
}
