namespace telnet_cs.Tests
{
  using System;
  using System.Diagnostics.CodeAnalysis;
  using System.Net;
  using System.Net.Sockets;
  using System.Text;
  using System.Threading;

  [ExcludeFromCodeCoverage]
  public abstract class DummyTelnetServerBase : System.Net.Sockets.Socket
  {
    private readonly System.Threading.Thread t;
    private readonly object handlerLock = new object();
    private volatile bool isListening;
    private Socket currentHandler = null!;

    private readonly string expectedLineFeedTerminator;
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(10);

    protected DummyTelnetServerBase(string expectedLineFeedTerminator)
      : base(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
    {
      isListening = true;
      this.expectedLineFeedTerminator = expectedLineFeedTerminator;

      // Loopback + an OS-assigned port: no fixed port, no DNS lookup, and the
      // socket is already listening before the accept thread starts, so a
      // client can never race the Bind/Listen call.
      IPAddress = IPAddress.Loopback;
      Bind(new IPEndPoint(IPAddress, 0));
      Listen(10);
      Port = ((IPEndPoint)LocalEndPoint!).Port;

      t = new System.Threading.Thread(new System.Threading.ThreadStart(SpinListen))
      {
        IsBackground = true,
      };
      t.Start();
    }

    protected override void Dispose(bool disposing)
    {
      if (disposing)
      {
        isListening = false;
        CloseAcceptedHandler();
        // NOTE: do NOT call Close() here: Socket.Close() routes back into
        // Dispose(), which would recurse. base.Dispose(disposing) below
        // closes the listener, unblocking Accept() with ObjectDisposedException.
      }

      base.Dispose(disposing);
      if (t.IsAlive && Thread.CurrentThread != t)
      {
        t.Join(TimeSpan.FromSeconds(5));
      }
      else
      {
        System.Threading.Thread.Sleep(10);
      }
    }

    public bool IsListening
    {
      get { return isListening; }
      private set { isListening = value; }
    }

    public void StopListening()
    {
      IsListening = false;
    }

    //private static string data = null;
    private readonly TimeSpan spinWait = TimeSpan.FromMilliseconds(10);

    public IPAddress IPAddress { get; private set; }

    public int Port { get; private set; }

    private void SpinListen()
    {
#pragma warning disable CA1031 // Do not catch general exception types
      try
      {
        while (IsListening)
        {
          Socket handler = null!;
          try
          {
            Console.WriteLine("Waiting for a connection...");
            handler = Accept();
            TrackAcceptedHandler(handler);
            HandleConnection(handler);
          }
          catch (ObjectDisposedException)
          {
            break;
          }
          catch (SocketException e)
          {
            if (!IsListening)
            {
              break;
            }

            Console.WriteLine(e.ToString());
          }
          finally
          {
            UntrackAndCloseAcceptedHandler(handler);
          }
        }
      }
      catch (Exception e)
      {
        Console.WriteLine(e.ToString());
      }
#pragma warning restore CA1031 // Do not catch general exception types
    }

    private void HandleConnection(Socket handler)
    {
      handler.ReceiveTimeout = (int)ConnectionTimeout.TotalMilliseconds;
      handler.SendTimeout = (int)ConnectionTimeout.TotalMilliseconds;

      Console.WriteLine("Connection made, respond with Account: prompt");
      handler.Send(Encoding.ASCII.GetBytes("Account:"));

      WaitFor(handler, $"username{expectedLineFeedTerminator}");

      Console.WriteLine("Account entered, respond with Password: prompt");
      handler.Send(Encoding.ASCII.GetBytes("Password:"));

      WaitFor(handler, $"password{expectedLineFeedTerminator}");

      Console.WriteLine("Password entered, respond with Command> prompt");
      handler.Send(Encoding.ASCII.GetBytes("Command >"));

      WaitFor(handler, $"show statistic wan2{expectedLineFeedTerminator}");

      Console.WriteLine("Command entered, respond with WAN2 terminated reply");
      handler.Send(Encoding.ASCII.GetBytes("show statistic wan2\n\r WAN1 total TX: 0 Bytes ,RX: 0 Bytes \n\r WAN2 total TX: 6.3 GB ,RX: 6.9 GB \n\r WAN3 total TX: 0 Bytes ,RX: 0 Bytes \n\r WAN4 total TX: 0 Bytes ,RX: 0 Bytes \n\r WAN5 total TX: 0 Bytes ,RX: 0 Bytes \n\r>"));

      while (IsListening)
      {
        System.Threading.Thread.Sleep(50);
      }
    }

    private void TrackAcceptedHandler(Socket handler)
    {
      lock (handlerLock)
      {
        currentHandler = handler;
      }
    }

    private void UntrackAndCloseAcceptedHandler(Socket handler)
    {
      if (handler == null)
      {
        return;
      }

      lock (handlerLock)
      {
        if (currentHandler == handler)
        {
          currentHandler = null!;
        }
      }

      try
      {
        handler.Shutdown(SocketShutdown.Both);
      }
      catch (SocketException)
      {
      }
      catch (ObjectDisposedException)
      {
      }

      try
      {
        handler.Close();
      }
      catch (SocketException)
      {
      }
      catch (ObjectDisposedException)
      {
      }
    }

    private void CloseAcceptedHandler()
    {
      Socket handler;
      lock (handlerLock)
      {
        handler = currentHandler;
        currentHandler = null!;
      }

      UntrackAndCloseAcceptedHandler(handler);
    }

    private void WaitFor(Socket handler, string awaitedResponse)
    {
      var data = string.Empty;
      var deadline = DateTime.UtcNow + ConnectionTimeout;
      while (true)
      {
        if (!IsListening)
        {
          throw new OperationCanceledException();
        }

        if (DateTime.UtcNow >= deadline)
        {
          throw new TimeoutException($"Timed out waiting for '{awaitedResponse}'. Received '{data}'.");
        }

        // Poll in small slices so Dispose/stop can interrupt a stalled client
        // instead of blocking in Receive() until the socket timeout expires.
        if (handler.Poll(100 * 1000, SelectMode.SelectRead))
        {
          data += ReceiveResponse(handler);
          if (IsResponseReceived(data, awaitedResponse))
          {
            break;
          }
        }
      }
    }

    private static string ReceiveResponse(Socket handler)
    {
      var bytes = new byte[1024];
      var bytesRec = handler.Receive(bytes);
      if (bytesRec == 0)
      {
        throw new SocketException((int)SocketError.ConnectionReset);
      }

      return Encoding.ASCII.GetString(bytes, 0, bytesRec).Trim((char)255);
    }

    private bool IsResponseReceived(string currentResponse, string responseAwaited)
    {
      if (currentResponse.Contains(responseAwaited, StringComparison.InvariantCulture))
      {
        System.Diagnostics.Debug.Print("{0} response received", responseAwaited);
        Console.WriteLine("{0} response received", responseAwaited);
        return true;
      }
      else
      {
        System.Diagnostics.Debug.Print("Waiting for {1} response, received {0}", currentResponse, responseAwaited);
        Console.WriteLine("Waiting for {1} response, received {0}", currentResponse, responseAwaited);
        System.Threading.Thread.Sleep(spinWait);
        return false;
      }
    }
  }
}
