namespace telnet_cs.Encodings;

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;

/// <summary>
/// Registration and policy helpers for the retro-computer codecs.
/// </summary>
public static class TelnetEncodings
{
    private static readonly object Sync = new();
    private static bool registered;
    private static readonly HashSet<string> ForceBinary =
    new(StringComparer.OrdinalIgnoreCase)
    {
        "atascii",
        "atari8bit",
        "atari_8bit",
        "petscii",
        "cbm",
        "commodore",
        "c64",
        "c128",
        "atarist",
        "atari",
    };

    /// <summary>
    /// Gets the codec names that require 8-bit-transparent (BINARY /
    /// raw-mode) transport. These byte values overlap Big5 lead ranges
    /// and control space, so 7-bit NVT would mangle them.
    /// </summary>
    public static IReadOnlySet<string> ForceBinaryEncodings => ForceBinary;

    /// <summary>
    /// Registers <see cref="TelnetEncodingProvider.Instance"/> so
    /// <see cref="Encoding.GetEncoding(string)"/> resolves the
    /// retro-computer codecs, plus the framework code-pages provider
    /// (cp437, Big5, koi8-*, iso-8859-*) the hybrid big5bbs codec and the
    /// SyncTERM font map resolve through. Safe to call multiple times.
    /// Runs automatically on assembly load; explicit calls are harmless.
    /// </summary>
    public static void Register()
    {
        lock (Sync)
        {
            if (registered)
            {
                return;
            }

            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Encoding.RegisterProvider(TelnetEncodingProvider.Instance);
            registered = true;
        }
    }

    // Module initializers in libraries are flagged because they run
    // implicitly; here that is the point (codecs.register parity: the
    // codecs resolve from Encoding.GetEncoding without an explicit call),
    // and Register is idempotent and lock-guarded.
#pragma warning disable CA2255 // The 'ModuleInitializer' attribute should not be used in libraries
    [ModuleInitializer]
    internal static void AutoRegister()
    {
        Register();
    }
#pragma warning restore CA2255 // The 'ModuleInitializer' attribute should not be used in libraries

    /// <summary>
    /// Determines whether the named encoding requires binary transport.
    /// </summary>
    /// <param name="name">The encoding name (e.g. "petscii"). Null returns false.</param>
    /// <returns>True when the encoding is in <see cref="ForceBinaryEncodings"/>.</returns>
    public static bool RequiresBinaryMode(string? name)
    {
        if (name is null)
        {
            return false;
        }

        var normalized = name.ToLowerInvariant().Replace('-', '_');
        return ForceBinary.Contains(normalized);
    }
}
