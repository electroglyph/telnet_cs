namespace telnet_cs.Client
{
  using System;
  using System.Threading;
  using telnet_cs.Transport;

  /// <summary>
  /// The base class for Clients.
  /// </summary>
  public abstract partial class BaseClient
  {
    /// <summary>
    /// The send rate limit.
    /// </summary>
    private readonly SemaphoreSlim sendRateLimit;

    /// <summary>
    /// The read rate limit. Serialises concurrent reads so interleaved
    /// Read/TerminatedRead calls cannot split a server reply.
    /// </summary>
    private readonly SemaphoreSlim readRateLimit;

    /// <summary>
    /// The registration of the external cancellation token. Stored so it can
    /// be disposed with the client instead of leaking.
    /// </summary>
    private readonly CancellationTokenRegistration cancellationRegistration;

    /// <summary>
    /// The internal cancellation token.
    /// </summary>
    private readonly CancellationTokenSource internalCancellation;

    /// <summary>
    /// Initialises a new instance of the <see cref="BaseClient"/> class.
    /// </summary>
    /// <param name="byteStream">The byte stream.</param>
    /// <param name="token">The token.</param>
    protected BaseClient(IByteStream byteStream, CancellationToken token)
    {
      // Guarded first: the stream field is readonly and assigned here, so a
      // null stream must fail before any resource (semaphores, CTS,
      // registration) is allocated — otherwise the partially-built client
      // would leak them (the caller never gets an instance to Dispose).
      ArgumentNullException.ThrowIfNull(byteStream);
      this.byteStream = byteStream;
      sendRateLimit = new SemaphoreSlim(1);
      readRateLimit = new SemaphoreSlim(1);
      internalCancellation = new CancellationTokenSource();
      cancellationRegistration = token.Register(() =>
      {
        CancelPendingReads();
      });
    }

    /// <summary>
    /// Gets the read rate limit.
    /// </summary>
    protected SemaphoreSlim ReadRateLimit
    {
      get
      {
        return readRateLimit;
      }
    }

    /// <summary>
    /// Gets the send rate limit.
    /// </summary>
    protected SemaphoreSlim SendRateLimit
    {
      get
      {
        return sendRateLimit;
      }
    }

    /// <summary>
    /// Gets the internal cancellation token.
    /// </summary>
    protected CancellationTokenSource InternalCancellation
    {
      get
      {
        return internalCancellation;
      }
    }

    /// <summary>
    /// Add null check to cancel commands. Fail gracefully.
    /// </summary>
    protected void CancelPendingReads()
    {
#pragma warning disable CA1031 // Do not catch general exception types
      try
      {
        internalCancellation?.Cancel();
      }
      catch (Exception ex)
      {
        System.Diagnostics.Debug.WriteLine(ex.Message);
      }
#pragma warning restore CA1031 // Do not catch general exception types
    }

    /// <summary>
    /// Releases unmanaged and - optionally - managed resources.
    /// </summary>
    /// <param name="disposing"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
    protected virtual void Dispose(bool disposing)
    {
      if (disposing)
      {
        ByteStream.Close();
        sendRateLimit.Dispose();
        readRateLimit.Dispose();
        cancellationRegistration.Dispose();
        if (!internalCancellation.IsCancellationRequested)
        {
          CancelPendingReads();
        }

        internalCancellation.Dispose();
      }
    }
  }
}
