using System.Reflection;
using telnet_cs.IO;
using telnet_cs.Protocol;

static string Hex(byte[] b) => string.Concat(b.Select(x => x.ToString("X2")));
static string HexStr(string s) => string.Concat(s.Select(c => ((int)c).ToString("X2")));

var mode = args.Length > 0 ? args[0] : "help";
switch (mode)
{
    case "negqueue":
    {
        var ns = new NegotiationState();
        var r1 = ns.OfferEnable(3);
        var r2 = ns.OfferDisable(3);
        var st = ns.GetStates(3);
        Console.WriteLine($"offerEnable => reply={(r1 is null ? "null" : ((int)r1).ToString())}");
        Console.WriteLine($"offerDisable => reply={(r2 is null ? "null" : ((int)r2).ToString())} us={st.Us}");
        break;
    }
    case "read":
    case "readS":
    {
        bool server = mode == "readS";
        var bytes = args.Skip(1).Select(s => (byte)int.Parse(s, System.Globalization.NumberStyles.HexNumber)).ToArray();
        var ms = new MemStream(bytes);
        using var h = new ByteStreamHandler(ms);
        if (server) h.GetType().GetProperty("IsServerRole", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(h, true);
        try
        {
            var s = await h.ReadAsync(TimeSpan.FromMilliseconds(200));
            Console.WriteLine("data=" + HexStr(s));
        }
        catch (Exception e) { Console.WriteLine("EX:" + e.GetType().Name + ":" + e.Message); }
        Console.WriteLine("writes=" + Hex(ms.AllWrites()));
        foreach (var pn in new[] { "LastLocation", "LineflowEnabled", "NegotiatedCharset", "ForceBinaryDecoding", "ClientWindowSize" })
        {
            var pr = h.GetType().GetProperty(pn, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (pr is null) continue;
            object? v = null;
            try { v = pr.GetValue(h); } catch (Exception e) { v = "EX:" + e.Message; }
            Console.WriteLine(pn + "=" + (v is null ? "null" : v.ToString()));
        }
        break;
    }
    case "charsetSim":
    case "clientSim":
    {
        var ms = new MemStream(Array.Empty<byte>());
        using var h = new ByteStreamHandler(ms);
        bool server = mode == "charsetSim";
        if (server) h.GetType().GetProperty("IsServerRole", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(h, true);
        h.GetType().GetProperty("CharsetRequestPending", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(h, true);
        var bytes = new byte[] { 0xFF, 0xFA, 0x2A, 0x01, 0x20, 0x55, 0x54, 0x46, 0x2D, 0x38, 0xFF, 0xF0 };
        ms.Enqueue(bytes);
        try { Console.WriteLine("data=" + HexStr(await h.ReadAsync(TimeSpan.FromMilliseconds(200)))); }
        catch (Exception e) { Console.WriteLine("EX:" + e.GetType().Name); }
        Console.WriteLine("writes=" + Hex(ms.AllWrites()));
        break;
    }
    case "synch":
    {
        var ms = new MemStream(new byte[] { (byte)'A', (byte)'B', 0xFF, 0xF2, (byte)'C', (byte)'D' });
        using var h = new ByteStreamHandler(ms);
        h.GetType().GetMethod("EnterSynchDiscard", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(h, null);
        Console.WriteLine("data=" + HexStr(await h.ReadAsync(TimeSpan.FromMilliseconds(200))));
        Console.WriteLine("writes=" + Hex(ms.AllWrites()));
        break;
    }
    case "slcdefaults":
    {
        var asm = typeof(NegotiationState).Assembly;
        var lt = asm.GetType("telnet_cs.Protocol.LinemodeState")!;
        var st = Activator.CreateInstance(lt, true)!;
        var m = lt.GetMethod("ExportTriplets", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        var trip = (byte[]?)m.Invoke(st, new object[] { false });
        Console.WriteLine("triplets=" + (trip is null ? "null" : Hex(trip)));
        Console.WriteLine("triplet-bytes=" + (trip is null ? "0" : trip.Length.ToString()));
        break;
    }
    case "nawsdefault":
    {
        var asm = typeof(NegotiationState).Assembly;
        var nt = asm.GetType("telnet_cs.Protocol.NawsProtocol")!;
        var m = nt.GetMethod("GetEffectiveSize", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
        var v = ((ushort, ushort))m.Invoke(null, new object[] { 0, 0 })!;
        Console.WriteLine($"effective0x0={v.Item1}x{v.Item2}");
        var bm = nt.GetMethod("BuildSubnegotiation", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
        var frame = (byte[])bm.Invoke(null, new object[] { v.Item1, v.Item2 })!;
        Console.WriteLine("frame=" + Hex(frame));
        break;
    }
    case "negseq":
    {
        var ns = new NegotiationState();
        foreach (var s in args.Skip(1))
        {
            var p = s.Split(':');
            Commands? r = p[0] switch
            {
                "will" => ns.ReceivedWill(int.Parse(p[1]), bool.Parse(p[2])),
                "do" => ns.ReceivedDo(int.Parse(p[1]), bool.Parse(p[2])),
                _ => throw new Exception("bad verb " + p[0]),
            };
            var st = ns.GetStates(int.Parse(p[1]));
            Console.WriteLine($"{s} => reply={(r is null ? "null" : ((int)r).ToString())} us={st.Us} him={st.Him}");
        }
        break;
    }
    case "tspeed":
    {
        // tspeed <configured>: set opaque "<tx>,<rx>" speed, answer a SEND, print wire IS.
        var ms = new MemStream(new byte[] { 0xFF, 0xFA, 0x20, 0x01, 0xFF, 0xF0 });
        using var h = new ByteStreamHandler(ms);
        h.GetType().GetProperty("TerminalSpeed", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(h, args[1]);
        try { Console.WriteLine("data=" + HexStr(await h.ReadAsync(TimeSpan.FromMilliseconds(200)))); }
        catch (Exception e) { Console.WriteLine("EX:" + e.GetType().Name); }
        Console.WriteLine("writes=" + Hex(ms.AllWrites()));
        break;
    }
    case "status":
    {
        var ns2 = new NegotiationState();
        foreach (var s in args.Skip(1))
        {
            var p = s.Split(':');
            _ = p[0] switch
            {
                "will" => ns2.ReceivedWill(int.Parse(p[1]), bool.Parse(p[2])),
                "do" => ns2.ReceivedDo(int.Parse(p[1]), bool.Parse(p[2])),
                _ => throw new Exception("bad"),
            };
        }
        var t = typeof(NegotiationState).Assembly.GetType("telnet_cs.Protocol.StatusProtocol")!;
        var mm = t.GetMethod("BuildIsPayload", BindingFlags.NonPublic | BindingFlags.Static)!;
        Console.WriteLine("payload=" + Hex((byte[])mm.Invoke(null, new object[] { ns2 })!));
        break;
    }
    default: Console.WriteLine("modes: negqueue read readS charsetSim clientSim synch slcdefaults nawsdefault negseq tspeed status"); break;
}

sealed class MemStream : telnet_cs.Transport.IByteStream
{
    readonly Queue<int> q = new();
    public List<byte[]> Writes = new();
    public MemStream(byte[] init) { foreach (var x in init) q.Enqueue(x); }
    public int Available => q.Count;
    public bool Connected => true;
    public int ReceiveTimeout { get; set; }
    public void Close() { }
    public void Dispose() { }
    public int ReadByte() => q.Count > 0 ? q.Dequeue() : -1;
    public void Enqueue(byte[] b) { foreach (var x in b) q.Enqueue(x); }
    public Task WriteByteAsync(byte v, CancellationToken c) { Writes.Add(new[] { v }); return Task.CompletedTask; }
    public Task WriteAsync(byte[] b, int o, int n, CancellationToken c) { var s = new byte[n]; Array.Copy(b, o, s, 0, n); Writes.Add(s); return Task.CompletedTask; }
    public Task WriteAsync(string v, CancellationToken c) { Writes.Add(System.Text.Encoding.Latin1.GetBytes(v)); return Task.CompletedTask; }
    public byte[] AllWrites() => Writes.SelectMany(x => x).ToArray();
}
