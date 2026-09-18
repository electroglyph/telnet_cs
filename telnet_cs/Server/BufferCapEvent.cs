namespace telnet_cs.Server;

/// <summary>
/// Informational snapshot fired to
/// <see cref="TelnetServerOptions.OnBufferCap"/> when buffered inbound
/// text passes <c>MaxBufferedTextChars</c>: the same endpoint/buffered/cap
/// values the <c>buffer-cap:</c> log line carries. The session is already
/// closing fail-closed when this fires; there is no continue mode.
/// </summary>
/// <param name="EndPoint">The session endpoint string (<c>"unknown"</c> when unset).</param>
/// <param name="Buffered">The buffered-text chars measured over the cap.</param>
/// <param name="Cap">The live <c>MaxBufferedTextChars</c> value that tripped.</param>
public readonly record struct BufferCapEvent(string EndPoint, int Buffered, int Cap);
