namespace telnet_cs.Client;

/// <summary>
/// The result of feeding one character to <see cref="LinemodeBuffer"/>:
/// text to display locally, plus bytes to send to the server (null while
/// buffering).
/// </summary>
/// <param name="Echo">Text to display locally (may be empty).</param>
/// <param name="Data">Bytes to send to the server, or null if buffering.</param>
public readonly record struct LinemodeEdit(string Echo, byte[]? Data);
