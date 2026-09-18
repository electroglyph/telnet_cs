namespace telnet_cs.Protocol;

/// <summary>
/// A decoded Aardwolf (option 102) message: the channel byte mapped to its
/// well-known name (<c>0x..</c> when unknown, <c>unknown</c> for empty
/// input), the raw channel byte, the single data byte (two-byte payloads
/// only), and any trailing data bytes.
/// </summary>
/// <param name="Channel">The channel name.</param>
/// <param name="ChannelByte">The raw channel byte.</param>
/// <param name="DataByte">The data byte, set for two-byte payloads only.</param>
/// <param name="DataBytes">The trailing data bytes (empty when none).</param>
public sealed record AardwolfMessage(string Channel, byte ChannelByte, byte? DataByte, byte[] DataBytes);
