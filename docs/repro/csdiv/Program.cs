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
    case "snoop":
    {
        // Dumps Snoop() over all 256 byte values for two tables:
        // A = BSD defaults, B = modified (dup 0x41 on funcs 3+8, zeroed
        // func 10, VARIABLE 0x42 on func 20, VARIABLE 0xFF on func 1).
        var asm2 = typeof(NegotiationState).Assembly;
        var lt2 = asm2.GetType("telnet_cs.Protocol.LinemodeState")!;
        var sm = lt2.GetMethod("Snoop", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        var se = lt2.GetMethod("SetEntry", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        string Dump(object st)
        {
            var toks = new string[256];
            for (int b = 0; b < 256; b++)
            {
                var r = (byte?)sm.Invoke(st, new object[] { (byte)b });
                toks[b] = r is null ? "-" : r.Value.ToString();
            }
            return string.Join(" ", toks);
        }
        var a = Activator.CreateInstance(lt2, true)!;
        Console.WriteLine("A " + Dump(a));
        var c = Activator.CreateInstance(lt2, true)!;
        se.Invoke(c, new object[] { (byte)3, (byte)2, (byte)0x41, (byte)0 });
        se.Invoke(c, new object[] { (byte)8, (byte)2, (byte)0x41, (byte)0 });
        se.Invoke(c, new object[] { (byte)10, (byte)2, (byte)0x00, (byte)0 });
        se.Invoke(c, new object[] { (byte)20, (byte)2, (byte)0x42, (byte)0 });
        se.Invoke(c, new object[] { (byte)1, (byte)2, (byte)0xFF, (byte)0 });
        Console.WriteLine("B " + Dump(c));
        break;
    }
    case "msdptable":
    {
        // D23: nested TABLE with stray bytes must terminate (reference hangs).
        var payload = new byte[] { 1, (byte)'K', 2, 3, (byte)'X', (byte)'Y', 4 };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var res = telnet_cs.Protocol.MudProtocol.MsdpDecode(payload);
        sw.Stop();
        Console.WriteLine($"count={res.Count} elapsedMs={sw.ElapsedMilliseconds} returned=True");
        break;
    }
    case "slcdefault":
    {
        // D25: SLC DEFAULT for an unsupported function (19/MCL) answers
        // NOSUPPORT instead of throwing (reference raises AttributeError).
        var asm3 = typeof(NegotiationState).Assembly;
        var lt3 = asm3.GetType("telnet_cs.Protocol.LinemodeState")!;
        var apply = lt3.GetMethod("ApplySlcAsServer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        var st = Activator.CreateInstance(lt3, true)!;
        var reply = ((byte, byte)?)apply.Invoke(st, new object[] { (byte)19, (byte)3, (byte)0 });
        Console.WriteLine(reply is null ? "reply=null" : $"reply=({reply.Value.Item1},{reply.Value.Item2}) no-throw=True");
        break;
    }
    case "slcisolate":
    {
        // D24: per-instance SLC tables -- mutating one session leaves
        // siblings and fresh instances untouched (reference pollutes all).
        var asm4 = typeof(NegotiationState).Assembly;
        var lt4 = asm4.GetType("telnet_cs.Protocol.LinemodeState")!;
        var se4 = lt4.GetMethod("SetEntry", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        var ge4 = lt4.GetMethod("GetEntry", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        var s1 = Activator.CreateInstance(lt4, true)!;
        var s2 = Activator.CreateInstance(lt4, true)!;
        var before = ge4.Invoke(s2, new object[] { (byte)4 })!.ToString();
        se4.Invoke(s1, new object[] { (byte)4, (byte)0, (byte)15, (byte)0 });
        var s3 = Activator.CreateInstance(lt4, true)!;
        Console.WriteLine($"siblingBefore={before} siblingAfter={ge4.Invoke(s2, new object[] { (byte)4 })} fresh={ge4.Invoke(s3, new object[] { (byte)4 })} isolated=True");
        break;
    }
    case "muddecode":
    {
        // D26: pre-CHARSET MUD decode is UTF-8-first (reference mojibakes).
        var payload = new byte[] { 1, (byte)'K', 2, 0xC3, 0xA9 };
        var res = telnet_cs.Protocol.MudProtocol.MsdpDecode(payload);
        Console.WriteLine($"value={res["K"]} correct={Equals(res["K"], "é")}");
        break;
    }
    case "d6":
    {
        // D6: a stale DO behind an outstanding disable is refused (RFC 1143
        // queue); the reference forgets the withdrawal and resends DO.
        static string R(object? r) => r is null ? "null" : ((int)r).ToString();
        var ns = new NegotiationState();
        Console.WriteLine($"offerEnable => reply={R(ns.OfferEnable(3))}");
        Console.WriteLine($"offerDisable => reply={R(ns.OfferDisable(3))} us={ns.GetStates(3).Us}");
        Console.WriteLine($"receivedDo => reply={R(ns.ReceivedDo(3, true))} us={ns.GetStates(3).Us}");
        break;
    }
    case "d7":
    {
        // D7: ESC VALUE decodes to a literal 0x01 (RFC 1408); a trailing
        // ESC contributes no byte. The reference keeps the ESC and splits.
        var asm = typeof(NegotiationState).Assembly;
        var et = asm.GetType("telnet_cs.Protocol.EnvironmentProtocol")!;
        var parse = et.GetMethod("ParseEntries", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
        static string Esc(object? s) => s is null ? "null" : "'" + string.Concat(((string)s).Select(c => c < 32 ? $"\\x{(int)c:X2}" : c.ToString())) + "'";
        foreach (var (label, payload) in new[] {
            ("esc-value", new byte[] { 0, 3, (byte)'K', 1, (byte)'a', 2, 1, (byte)'b' }),
            ("trailing-esc", new byte[] { 0, 0, (byte)'A', 2 }),
        })
        {
            var entries = (System.Collections.IEnumerable)parse.Invoke(null, new object[] { payload, int.MaxValue })!;
            foreach (var e in entries)
            {
                var t = e.GetType();
                Console.WriteLine($"{label} -> userVar={t.GetField("Item1")!.GetValue(e)} name='{t.GetField("Item2")!.GetValue(e)}' value={Esc(t.GetField("Item3")!.GetValue(e))}");
            }
        }
        break;
    }
    case "d14":
    {
        // D14: signed rates are rejected (RFC 1079 rates are unsigned
        // decimals); the reference coerces them through int().
        var asm = typeof(NegotiationState).Assembly;
        var tt = asm.GetType("telnet_cs.Protocol.TerminalSpeedProtocol")!;
        var val = tt.GetMethod("Validate", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
        foreach (var s in new[] { "+9600,9600", "-1,0", "9600,9600" })
            Console.WriteLine($"validate('{s}')={val.Invoke(null, new object[] { s }) ?? "null"}");
        break;
    }
    case "d16":
    {
        // D16: a tuple has no MSDP structured form, so it encodes as its
        // display string; a list uses ARRAY framing. The reference str()s
        // the tuple into a scalar too, but with Python repr spelling.
        var tup = new Dictionary<string, object?>(StringComparer.Ordinal) { ["K"] = ("a", "b") };
        var lst = new Dictionary<string, object?>(StringComparer.Ordinal) { ["K"] = new List<string> { "a", "b" } };
        Console.WriteLine("tuple -> " + Hex(MudProtocol.MsdpEncode(tup)));
        Console.WriteLine("list -> " + Hex(MudProtocol.MsdpEncode(lst)));
        break;
    }
    case "d18":
    {
        // D18: an oversize font id is skipped so a later valid reply still
        // resolves; the reference first-matches and reports None.
        var poison = new byte[] { 0x1B, (byte)'[', (byte)'0', (byte)';', (byte)'1', (byte)'0', (byte)'0', (byte)'0', (byte)'0', 0x20, (byte)'D' };
        var valid = new byte[] { 0x1B, (byte)'[', (byte)'0', (byte)';', (byte)'0', 0x20, (byte)'D' };
        Console.WriteLine("10000 -> " + (SyncTermFont.DetectEncoding(poison) ?? "null"));
        Console.WriteLine("10000-then-0 -> " + (SyncTermFont.DetectEncoding([.. poison, .. valid]) ?? "null"));
        Console.WriteLine("0 -> " + (SyncTermFont.DetectEncoding(valid) ?? "null"));
        break;
    }
    case "d19":
    {
        // D19: a listed command earns zmp.support with no check hook
        // (handler OR list); the reference consults only the hook.
        var ms = new MemStream(Array.Empty<byte>());
        using var h = new ByteStreamHandler(ms);
        h.GetType().GetProperty("ZmpSupportedCommands", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(h, new List<string> { "look" });
        var body = System.Text.Encoding.Latin1.GetBytes("zmp.check\0look\0");
        ms.Enqueue([0xFF, 0xFB, 93, 0xFF, 0xFA, 93, .. body, 0xFF, 0xF0]);
        try { Console.WriteLine("data=" + HexStr(await h.ReadAsync(TimeSpan.FromMilliseconds(500)))); }
        catch (Exception e) { Console.WriteLine("EX:" + e.GetType().Name); }
        var writes = Hex(ms.AllWrites());
        Console.WriteLine("writes=" + writes);
        Console.WriteLine("support=" + writes.Contains(HexStr("zmp.support\0look\0")));
        break;
    }
    case "d20":
    {
        // D20: an empty answer ends the TTYPE cycle; only prior answers are
        // kept. The reference stores the empty answer and keeps overwriting.
        static byte[] IsFrame(string v) => [0xFF, 0xFA, 24, 0, .. System.Text.Encoding.Latin1.GetBytes(v), 0xFF, 0xF0];
        var ms = new MemStream([.. IsFrame("ALPHA"), .. IsFrame(string.Empty)]);
        using var sess = new telnet_cs.Server.ServerSession(ms, new telnet_cs.Server.TelnetServerOptions(), CancellationToken.None);
        sess.Negotiation.ReceivedWill(24, agree: true);
        var types = await sess.RequestTerminalTypesAsync(TimeSpan.FromSeconds(5));
        Console.WriteLine("types=[" + string.Join(",", types) + "]");
        Console.WriteLine("effective=" + (sess.ClientEffectiveTerminalType ?? "null"));
        break;
    }
    case "msdparray":
    {
        // D22: a delimiter where an array value was expected terminates
        // (the reference spins in ParseArray until OOM).
        var payload = new byte[] { 1, (byte)'K', 2, 5, 4 };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var res = telnet_cs.Protocol.MudProtocol.MsdpDecode(payload);
        sw.Stop();
        Console.WriteLine($"count={res.Count} elapsedMs={sw.ElapsedMilliseconds} returned=True");
        break;
    }
    default: Console.WriteLine("modes: negqueue read readS charsetSim clientSim synch slcdefaults nawsdefault negseq tspeed status snoop d6 d7 d14 d16 d18 d19 d20 msdparray"); break;
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
