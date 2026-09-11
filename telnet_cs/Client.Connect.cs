namespace telnet_cs
{
  using System;
  using System.Threading;
  using System.Threading.Tasks;

  /// <summary>
  /// Basic Telnet client.
  /// Terminal type and speed can be configured via static properties on the <see cref="Client"/> class.
  /// <see cref="Client"/>.IsWriteConsole can be used to configure whether to write output to the console; often useful for debugging purposes.
  /// </summary>
  public partial class Client
  {
    /// <summary>
    /// Prior to v0.9.0 LegacyLineFeed was the default. To be Rfc854 compliant you should prefer Rfc854LineFeed.
    /// </summary>
    public const string LegacyLineFeed = "\n";

    /// <summary>
    /// Post to v0.9.0 LegacyLineFeed has been retained as the default, but to be Rfc854 compliant you should prefer this.
    /// </summary>
    public const string Rfc854LineFeed = "\r\n";

    /// <summary>
    /// Skips the proactive option negotiation on connect. Prefer the
    /// per-instance constructor flag on new code; this static remains for
    /// backward compatibility.
    /// </summary>
    public static bool SkipProactiveOptionNegotiation { get; set; }

    /// <summary>
    /// Gets or sets the process-wide log hook. Falls back to
    /// <see cref="System.Diagnostics.Debug"/> when unset.
    /// </summary>
    public static Action<string>? Trace { get; set; }

    /// <summary>
    /// Initialises a new instance of the <see cref="Client"/> class.
    /// </summary>
    /// <param name="hostname">The hostname.</param>
    /// <param name="port">The port.</param>
    /// <param name="token">The cancellation token.</param>
    [Obsolete("Prefer overload that accepts new TcpByteStream(hostname, port) and dispose it properly.")]
#pragma warning disable CA2000 // Ownership of the stream transfers to this Client via the chained constructor.
    public Client(string hostname, int port, CancellationToken token)
      : this(new TcpByteStream(hostname, port), token)
    {
    }
#pragma warning restore CA2000

    /// <summary>
    /// Initialises a new instance of the <see cref="Client"/> class.
    /// </summary>
    /// <param name="byteStream">The stream served by the host connected to.</param>
    /// <param name="token">The cancellation token.</param>
    public Client(IByteStream byteStream, CancellationToken token)
      : this(byteStream, TimeSpan.FromSeconds(30), token)
    {
    }

    /// <summary>
    /// Initialises a new instance of the <see cref="Client"/> class.
    /// </summary>
    /// <param name="byteStream">The stream served by the host connected to.</param>
    /// <param name="timeout">The timeout to wait for initial successful connection to <paramref name="byteStream"/>. Other overloads default to 30 seconds.</param>
    /// <param name="token">The cancellation token.</param>
    public Client(IByteStream byteStream, TimeSpan timeout, CancellationToken token)
      : this(byteStream, timeout, token, Array.Empty<(Commands Command, Options Option)>())
    { }

    /// <summary>
    /// Initialises a new instance of the <see cref="Client"/> class.
    /// </summary>
    /// <param name="byteStream">The byte stream served by the host connected to.</param>
    /// <param name="timeout">The timeout to wait for initial successful connection to <paramref name="byteStream"/>.</param>
    /// <param name="token">The cancellation token.</param>
    /// <param name="options">Additional options to send during negotiation.</param>
    /// <param name="skipProactiveNegotiation">When <c>true</c>, suppresses the
    /// opening <c>IAC DO SuppressGoAhead</c> (and the <paramref name="options"/>
    /// sends). Per-instance alternative to the process-global
    /// <see cref="SkipProactiveOptionNegotiation"/>: a client and a server
    /// session can coexist in one process with different choices.</param>
    public Client(IByteStream byteStream, TimeSpan timeout, CancellationToken token, (Commands Command, Options Option)[] options, bool skipProactiveNegotiation = false)
      : base(byteStream, token)
    {
      // NOTE: byteStream is validated by the base constructor; options cannot
      // be validated before the base call, so a null options array still
      // constructs (and connects) the client before throwing below.
      ArgumentNullException.ThrowIfNull(options);
      var timeoutEnd = DateTime.UtcNow.Add(timeout);
      using var are = new AutoResetEvent(false);
      while (!ByteStream.Connected && timeoutEnd > DateTime.UtcNow)
      {
        are.WaitOne(2);
      }

      if (!ByteStream.Connected)
      {
        throw new InvalidOperationException("Unable to connect to the host.");
      }
      else
      {
#pragma warning disable VSTHRD002 // Avoid problematic synchronous waits
        // https://stackoverflow.com/questions/70964917/optimising-an-asynchronous-call-in-a-constructor-using-joinabletaskfactory-run
        // GetAwaiter().GetResult() surfaces the real failure directly (an
        // IOException), unlike Task.Wait() which wraps it in AggregateException.
        if (!SkipProactiveOptionNegotiation && !skipProactiveNegotiation)
        {
          Task.Run(async () => await ProactiveOptionNegotiation().ConfigureAwait(false)).GetAwaiter().GetResult();
        }

        if (!skipProactiveNegotiation)
        {
          foreach (var option in options)
          {
            Task.Run(async () => await NegotiateOption(option.Command, option.Option).ConfigureAwait(false)).GetAwaiter().GetResult();
          }
        }
#pragma warning restore VSTHRD002 // Avoid problematic synchronous waits
      }
    }

    /// <summary>
    /// Connects to a Telnet server, honouring cancellation and a connect timeout.
    /// </summary>
    /// <param name="hostname">The hostname.</param>
    /// <param name="port">The port.</param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <param name="timeout">The maximum time to wait for the TCP connect. Defaults to 30 seconds.</param>
    /// <returns>A connected <see cref="Client"/> owning its stream. Dispose it when done.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="hostname"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">The connection could not be established within <paramref name="timeout"/>.</exception>
    public static async Task<Client> ConnectAsync(string hostname, int port, CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
      ArgumentNullException.ThrowIfNull(hostname);
      var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
      var tcpClient = new System.Net.Sockets.TcpClient();
      try
      {
        var connectTask = tcpClient.ConnectAsync(hostname, port);
        var completed = await Task.WhenAny(connectTask, Task.Delay(effectiveTimeout, cancellationToken)).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (completed != connectTask)
        {
          throw new InvalidOperationException($"Unable to connect to {hostname}:{port} within {effectiveTimeout}.");
        }

        await connectTask.ConfigureAwait(false);
#pragma warning disable CA2000 // Ownership of the stream transfers to the Client on success; the catch below releases it otherwise.
        return new Client(new TcpByteStream(new TcpClient(tcpClient)), CancellationToken.None);
#pragma warning restore CA2000
      }
      catch
      {
        // Ownership transfers to the Client only on success. A failed connect
        // or a failed Client constructor must still release the socket itself
        // (any wrappers created so far hold no unmanaged resources of their own).
        tcpClient.Dispose();
        throw;
      }
    }

    /// <summary>
    /// Gets and sets a value indicating whether the <see cref="Client"/> should write responses received via <see cref="ByteStreamHandler"/>.Read to the Console.
    /// </summary>
    public static bool IsWriteConsole { get; set; }

    /// <summary>
    /// Gets and sets the TerminalType to negotiate.
    /// </summary>
    public static string TerminalType { get; set; } = "vt100";

    /// <summary>
    /// Gets and sets the TerminalSpeed to negotiate.
    /// </summary>
    public static string TerminalSpeed { get; set; } = "19200,19200";

    internal static readonly byte[] SuppressGoAheadBuffer =
    [
      (byte)Commands.InterpretAsCommand,
      (byte)Commands.Do,
      (byte)Options.SuppressGoAhead,
    ];

    /// <summary>
    /// Sending <see cref="Commands.Do"/> <see cref="Options.SuppressGoAhead"/> up front will get us to the logon prompt faster.
    /// </summary>
    private Task ProactiveOptionNegotiation()
    {
      return SendNegotiationBytesAsync(Negotiation.RequestEnable((int)Options.SuppressGoAhead), Options.SuppressGoAhead);
    }

    /// <summary>
    /// Negotiate Option specified.
    /// </summary>
    private Task NegotiateOption(Commands command, Options option)
    {
      Commands? verb = command switch
      {
        Commands.Do => Negotiation.RequestEnable((int)option),
        Commands.Dont => Negotiation.RequestDisable((int)option),
        Commands.Will => Negotiation.OfferEnable((int)option),
        Commands.Wont => Negotiation.OfferDisable((int)option),
        _ => command,
      };
      return SendNegotiationBytesAsync(verb, option);
    }

    private Task SendNegotiationBytesAsync(Commands? verb, Options option)
    {
      if (verb is null)
      {
        return Task.CompletedTask;
      }

      var buffer = new byte[] { (byte)Commands.InterpretAsCommand, (byte)verb, (byte)option };
      return ByteStream.WriteAsync(buffer, 0, buffer.Length, InternalCancellation.Token);
    }
  }
}
