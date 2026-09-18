namespace telnet_cs.Server;

using System;

/// <summary>
/// The accept refused because the server already holds
/// <see cref="TelnetServerOptions.MaxConcurrentSessions"/> live sessions.
/// Thrown from <see cref="TelnetServer.AcceptSessionAsync"/> instead of a
/// bare <see cref="InvalidOperationException"/> so adapters map refuses
/// by type; deriving from it keeps existing catches working. The message
/// keeps the long-standing <c>over-capacity:</c> space form because
/// downstream log parsing keys on it.
/// </summary>
public sealed class SessionCapacityException : InvalidOperationException
{
    /// <summary>
    /// Initialises a new instance of the <see cref="SessionCapacityException"/> class.
    /// </summary>
    public SessionCapacityException()
        : base("over-capacity: capacity endpoint unknown.")
    {
    }

    /// <summary>
    /// Initialises a new instance of the <see cref="SessionCapacityException"/> class
    /// with a custom message.
    /// </summary>
    /// <param name="message">The message describing the refusal.</param>
    public SessionCapacityException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initialises a new instance of the <see cref="SessionCapacityException"/> class
    /// with a custom message and inner exception.
    /// </summary>
    /// <param name="message">The message describing the refusal.</param>
    /// <param name="innerException">The exception that caused the refusal.</param>
    public SessionCapacityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initialises a new instance of the <see cref="SessionCapacityException"/> class.
    /// </summary>
    /// <param name="remoteEndPoint">The refused peer's endpoint, if known.</param>
    public SessionCapacityException(System.Net.EndPoint? remoteEndPoint)
        : base($"over-capacity: capacity endpoint {remoteEndPoint?.ToString() ?? "unknown"}.")
    {
        RemoteEndPoint = remoteEndPoint;
    }

    /// <summary>
    /// Gets the refused peer's endpoint, if known.
    /// </summary>
    public System.Net.EndPoint? RemoteEndPoint { get; }

    /// <summary>
    /// Gets the machine-readable refuse reason: <c>capacity</c>.
    /// </summary>
    public string Reason => "capacity";
}
