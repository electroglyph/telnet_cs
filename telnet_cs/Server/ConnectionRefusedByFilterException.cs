namespace telnet_cs.Server
{
    using System;

    /// <summary>
    /// The accept refused by <see cref="TelnetServerOptions.AcceptFilter"/>.
    /// Thrown from
    /// <see cref="TelnetServer.AcceptSessionAsync"/> instead of a bare
    /// <see cref="InvalidOperationException"/> so adapters map refuses by
    /// type; deriving from it keeps existing catches working. The message
    /// keeps the long-standing <c>over-capacity:</c> space form because
    /// downstream log parsing keys on it.
    /// </summary>
    public sealed class ConnectionRefusedByFilterException : InvalidOperationException
    {
        /// <summary>
        /// Initialises a new instance of the <see cref="ConnectionRefusedByFilterException"/> class.
        /// </summary>
        public ConnectionRefusedByFilterException()
            : base("over-capacity: filter rejected endpoint unknown.")
        {
            Reason = "filter-reject";
        }

        /// <summary>
        /// Initialises a new instance of the <see cref="ConnectionRefusedByFilterException"/> class
        /// with a custom message.
        /// </summary>
        /// <param name="message">The message describing the refusal.</param>
        public ConnectionRefusedByFilterException(string message)
            : base(message)
        {
            Reason = "filter-reject";
        }

        /// <summary>
        /// Initialises a new instance of the <see cref="ConnectionRefusedByFilterException"/> class
        /// with a custom message and inner exception.
        /// </summary>
        /// <param name="message">The message describing the refusal.</param>
        /// <param name="innerException">The exception that caused the refusal.</param>
        public ConnectionRefusedByFilterException(string message, Exception innerException)
            : base(message, innerException)
        {
            Reason = "filter-reject";
        }

        /// <summary>
        /// Initialises a new instance of the <see cref="ConnectionRefusedByFilterException"/> class
        /// for a <c>false</c> filter verdict.
        /// </summary>
        /// <param name="remoteEndPoint">The refused peer's endpoint, if known.</param>
        public ConnectionRefusedByFilterException(System.Net.EndPoint? remoteEndPoint)
            : this(remoteEndPoint, "filter-reject", null)
        {
        }

        /// <summary>
        /// Initialises a new instance of the <see cref="ConnectionRefusedByFilterException"/> class
        /// for a filter that threw, preserving the throw as
        /// <see cref="Exception.InnerException"/>.
        /// </summary>
        /// <param name="remoteEndPoint">The refused peer's endpoint, if known.</param>
        /// <param name="innerException">The exception the filter threw.</param>
        public ConnectionRefusedByFilterException(System.Net.EndPoint? remoteEndPoint, Exception innerException)
            : this(remoteEndPoint, "filter-threw", innerException)
        {
        }

        /// <summary>
        /// Initialises a new instance of the <see cref="ConnectionRefusedByFilterException"/> class
        /// with an explicit reason (for example an
        /// <see cref="AcceptDecision"/> reason).
        /// </summary>
        /// <param name="remoteEndPoint">The refused peer's endpoint, if known.</param>
        /// <param name="reason">The machine-readable refuse reason.</param>
        /// <param name="innerException">The exception the filter threw, if any.</param>
        public ConnectionRefusedByFilterException(System.Net.EndPoint? remoteEndPoint, string reason, Exception? innerException)
            : base($"over-capacity: filter rejected endpoint {remoteEndPoint?.ToString() ?? "unknown"}.", innerException)
        {
            ArgumentException.ThrowIfNullOrEmpty(reason);
            RemoteEndPoint = remoteEndPoint;
            Reason = reason;
        }

        /// <summary>
        /// Gets the refused peer's endpoint, if known.
        /// </summary>
        public System.Net.EndPoint? RemoteEndPoint { get; }

        /// <summary>
        /// Gets the machine-readable refuse reason (<c>filter-reject</c>,
        /// <c>filter-threw</c>, or an <see cref="AcceptDecision"/> reason).
        /// </summary>
        public string Reason { get; }
    }
}
