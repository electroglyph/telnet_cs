// Handler- and session-level tests for per-protocol MUD dispatch (GMCP
// 201, MSDP 69, MSSP 70, MSP 90, MXP 91, ZMP 93, Aardwolf 102, ATCP 200):
// typed hooks fire, the reference's stores update, and the send helpers
// gate on either-side agreement. Mirrors test_mud_negotiation.py.
namespace telnet_cs.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Text.Json.Nodes;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.IO;
    using telnet_cs.Protocol;
    using telnet_cs.Server;

    public class MudDispatchTests
    {
        private const int Iac = 255;
        private const int Sb = 250;
        private const int Se = 240;
        private const int Will = 251;
        private const int Do = 253;

        private const int Msdp = 69;
        private const int Mssp = 70;
        private const int Msp = 90;
        private const int Mxp = 91;
        private const int Zmp = 93;
        private const int Aardwolf = 102;
        private const int Atcp = 200;
        private const int Gmcp = 201;

        private static async Task<(string Output, List<byte[]> Writes, ByteStreamHandler Handler)> ReadOnceAsync(
          Action<ByteStreamHandler> configure, params int[] reads)
        {
            using var stream = new ScriptedStream(reads);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            configure(sut);
            var output = await sut.ReadAsync(TimeSpan.FromMilliseconds(50));
            return (output, [.. stream.ByteWrites], sut);
        }

        private static byte[] Concat(List<byte[]> writes)
        {
            return [.. writes.SelectMany(w => w)];
        }

        private static int[] Text(string text)
        {
            return [.. text.Select(c => (int)c)];
        }

        private static int[] SbBody(int option, int[] body)
        {
            int[] frame = [Iac, Sb, option, .. body, Iac, Se];
            return frame;
        }

        [Theory]
        [InlineData(Msdp)]
        [InlineData(Mssp)]
        [InlineData(Msp)]
        [InlineData(Mxp)]
        [InlineData(Zmp)]
        [InlineData(Aardwolf)]
        [InlineData(Atcp)]
        [InlineData(Gmcp)]
        public async Task WillMudOption_AgreedWithDo(int option)
        {
            var (output, writes, _) = await ReadOnceAsync(_ => { }, Iac, Will, option);
            output.Should().BeEmpty();
            Concat(writes).Should().Equal((byte)Iac, (byte)Do, (byte)option);
        }

        [Theory]
        [InlineData(Msdp)]
        [InlineData(Mssp)]
        [InlineData(Msp)]
        [InlineData(Mxp)]
        [InlineData(Zmp)]
        [InlineData(Aardwolf)]
        [InlineData(Atcp)]
        [InlineData(Gmcp)]
        public async Task DoMudOption_AgreedWithWill(int option)
        {
            var (output, writes, _) = await ReadOnceAsync(_ => { }, Iac, Do, option);
            output.Should().BeEmpty();
            Concat(writes).Should().Equal((byte)Iac, (byte)Will, (byte)option);
        }

        [Fact]
        public async Task SbGmcp_DispatchesPackageAndJson()
        {
            (string? Package, JsonNode? Data) received = (null, null);
            int[] body = [.. Text("Char.Vitals {\"hp\": 100}")];
            int[] reads = [Iac, Will, Gmcp, .. SbBody(Gmcp, body)];
            var (output, _, _) = await ReadOnceAsync(
              h => h.GmcpReceived += (package, data) => received = (package, data),
              reads);
            output.Should().BeEmpty();
            received.Package.Should().Be("Char.Vitals");
            received.Data.Should().NotBeNull();
            received.Data!["hp"]!.GetValue<int>().Should().Be(100);
        }

        [Fact]
        public async Task SbMsdp_DispatchesVariables()
        {
            IReadOnlyDictionary<string, object?>? received = null;
            int[] body = [1, .. Text("HEALTH"), 2, .. Text("100")];
            int[] reads = [Iac, Will, Msdp, .. SbBody(Msdp, body)];
            var (output, _, _) = await ReadOnceAsync(
              h => h.MsdpReceived += variables => received = variables,
              reads);
            output.Should().BeEmpty();
            received.Should().NotBeNull();
            received!.Should().ContainKey("HEALTH").WhoseValue.Should().Be("100");
        }

        [Fact]
        public async Task SbMssp_StoresDataAndFiresHook()
        {
            IReadOnlyDictionary<string, object>? received = null;
            int[] body = [1, .. Text("NAME"), 2, .. Text("TestMUD"), 1, .. Text("PLAYERS"), 2, .. Text("5")];
            int[] reads = [Iac, Will, Mssp, .. SbBody(Mssp, body)];
            var (output, _, sut) = await ReadOnceAsync(
              h => h.MsspReceived += variables => received = variables,
              reads);
            output.Should().BeEmpty();
            received.Should().BeEquivalentTo(new Dictionary<string, object>
            {
                ["NAME"] = "TestMUD",
                ["PLAYERS"] = "5",
            });
            sut.MsspData.Should().BeEquivalentTo(received);
        }

        [Fact]
        public async Task SbMssp_Latin1Fallback()
        {
            int[] body = [1, .. Text("NAME"), 2, 0xC9, .. Text("toile")];
            int[] reads = [Iac, Will, Mssp, .. SbBody(Mssp, body)];
            var (_, _, sut) = await ReadOnceAsync(_ => { }, reads);
            sut.MsspData.Should().ContainSingle().Which.Value.Should().Be("Étoile");
        }

        [Fact]
        public async Task SbGmcp_Latin1Fallback()
        {
            (string? Package, JsonNode? Data) received = (null, null);
            int[] body = [(int)'C', (int)'a', (int)'f', 0xE9];
            int[] reads = [Iac, Will, Gmcp, .. SbBody(Gmcp, body)];
            await ReadOnceAsync(
              h => h.GmcpReceived += (package, data) => received = (package, data),
              reads);
            received.Package.Should().Be("Café");
            received.Data.Should().BeNull();
        }

        [Fact]
        public async Task SbMsdp_Latin1Fallback()
        {
            IReadOnlyDictionary<string, object?>? received = null;
            int[] body = [1, .. Text("KEY"), 2, (int)'C', (int)'a', (int)'f', 0xE9];
            int[] reads = [Iac, Will, Msdp, .. SbBody(Msdp, body)];
            await ReadOnceAsync(
              h => h.MsdpReceived += variables => received = variables,
              reads);
            received.Should().ContainSingle().Which.Value.Should().Be("Café");
        }

        [Theory]
        [InlineData(Msp)]
        [InlineData(Mxp)]
        public async Task SbRawMud_EmptyPayload_FiresHookWithEmpty(int option)
        {
            var received = new List<byte[]>();
            var (output, _, _) = await ReadOnceAsync(
              h =>
              {
                  if (option == Msp)
                  {
                      h.MspReceived += body => received.Add(body);
                  }
                  else
                  {
                      h.MxpReceived += body => received.Add(body);
                  }
              },
              Iac, Will, option, Iac, Sb, option, Iac, Se);
            output.Should().BeEmpty();
            received.Should().ContainSingle().Subject.Should().BeEmpty();
        }

        [Theory]
        [InlineData(Msp)]
        [InlineData(Mxp)]
        public async Task SbRawMud_WithPayload_FiresHookWithRawBytes(int option)
        {
            var received = new List<byte[]>();
            int[] reads = [Iac, Will, option, .. SbBody(option, [1, 2, 3])];
            var (output, _, _) = await ReadOnceAsync(
              h =>
              {
                  if (option == Msp)
                  {
                      h.MspReceived += body => received.Add(body);
                  }
                  else
                  {
                      h.MxpReceived += body => received.Add(body);
                  }
              },
              reads);
            output.Should().BeEmpty();
            received.Should().ContainSingle().Subject.Should().Equal(1, 2, 3);
        }

        [Fact]
        public async Task MxpData_AccumulatesAcrossSubnegotiations()
        {
            int[] reads = [Iac, Will, Mxp, Iac, Sb, Mxp, Iac, Se, .. SbBody(Mxp, [1, 2])];
            var (output, _, sut) = await ReadOnceAsync(_ => { }, reads);
            output.Should().BeEmpty();
            sut.MxpData.Should().HaveCount(2);
            sut.MxpData[0].Should().BeEmpty();
            sut.MxpData[1].Should().Equal(1, 2);
        }

        [Fact]
        public async Task MspData_AccumulatesAcrossSubnegotiations()
        {
            int[] reads = [Iac, Will, Msp, Iac, Sb, Msp, Iac, Se, .. SbBody(Msp, [1, 2])];
            var (output, _, sut) = await ReadOnceAsync(_ => { }, reads);
            output.Should().BeEmpty();
            sut.MspData.Should().HaveCount(2);
            sut.MspData[0].Should().BeEmpty();
            sut.MspData[1].Should().Equal(1, 2);
        }

        [Fact]
        public async Task SbZmp_DispatchesAndStoresByCommand()
        {
            (string? Command, IReadOnlyList<string>? Args) received = (null, null);
            int[] body = [.. Text("zmp.ident\0MudName\01.0\0A test MUD\0")];
            int[] reads = [Iac, Will, Zmp, .. SbBody(Zmp, body)];
            var (output, _, sut) = await ReadOnceAsync(
              h => h.ZmpReceived += (command, args) => received = (command, args),
              reads);
            output.Should().BeEmpty();
            received.Command.Should().Be("zmp.ident");
            received.Args.Should().Equal("MudName", "1.0", "A test MUD");
            sut.ZmpData.Should().ContainSingle();
            sut.ZmpData["zmp.ident"].Should().Equal("MudName", "1.0", "A test MUD");
        }

        [Fact]
        public async Task SbZmp_EmptyPayload_StoresNothingFiresNothing()
        {
            var fired = 0;
            var (output, _, sut) = await ReadOnceAsync(
              h => h.ZmpReceived += (_, _) => fired++,
              Iac, Will, Zmp, Iac, Sb, Zmp, Iac, Se);
            output.Should().BeEmpty();
            fired.Should().Be(0);
            sut.ZmpData.Should().BeEmpty();
        }

        [Fact]
        public async Task SbZmp_SecondMessage_ReplacesCommandSlot()
        {
            int[] first = SbBody(Zmp, [.. Text("char.vitals\0hp=100\0")]);
            int[] second = SbBody(Zmp, [.. Text("char.vitals\0hp=50\0")]);
            int[] reads = [Iac, Will, Zmp, .. first, .. second];
            var (_, _, sut) = await ReadOnceAsync(_ => { }, reads);
            sut.ZmpData.Should().ContainSingle();
            sut.ZmpData["char.vitals"].Should().Equal("hp=50");
        }

        [Fact]
        public async Task SbAtcp_DispatchesPackageAndValue()
        {
            (string? Package, string? Value) received = (null, null);
            int[] reads = [Iac, Will, Atcp, .. SbBody(Atcp, [.. Text("Room.Exits ne,sw,nw")])];
            var (output, _, sut) = await ReadOnceAsync(
              h => h.AtcpReceived += (package, value) => received = (package, value),
              reads);
            output.Should().BeEmpty();
            received.Should().Be(("Room.Exits", "ne,sw,nw"));
            sut.AtcpData.Should().ContainSingle().Which.Should().Be(("Room.Exits", "ne,sw,nw"));
        }

        [Fact]
        public async Task SbAtcp_NoValue_DecodesEmptyValue()
        {
            int[] reads = [Iac, Will, Atcp, .. SbBody(Atcp, [.. Text("Conn.MXP")])];
            var (_, _, sut) = await ReadOnceAsync(_ => { }, reads);
            sut.AtcpData.Should().ContainSingle().Which.Should().Be(("Conn.MXP", string.Empty));
        }

        [Fact]
        public async Task SbAtcp_EmptyPayload_DecodesEmptyPair()
        {
            var (_, _, sut) = await ReadOnceAsync(
              _ => { }, Iac, Will, Atcp, Iac, Sb, Atcp, Iac, Se);
            sut.AtcpData.Should().ContainSingle().Which.Should().Be((string.Empty, string.Empty));
        }

        [Fact]
        public async Task SbAardwolf_DispatchesNamedChannel()
        {
            AardwolfMessage? received = null;
            int[] reads = [Iac, Will, Aardwolf, .. SbBody(Aardwolf, [100, 3])];
            var (output, _, sut) = await ReadOnceAsync(
              h => h.AardwolfReceived += message => received = message,
              reads);
            output.Should().BeEmpty();
            received.Should().NotBeNull();
            received!.Channel.Should().Be("status");
            received.DataByte.Should().Be(3);
            sut.AardwolfData.Should().ContainSingle().Which.Channel.Should().Be("status");
        }

        [Fact]
        public async Task SbAardwolf_TickChannel()
        {
            int[] reads = [Iac, Will, Aardwolf, .. SbBody(Aardwolf, [101, 1])];
            var (_, _, sut) = await ReadOnceAsync(_ => { }, reads);
            sut.AardwolfData.Should().ContainSingle().Which.Channel.Should().Be("tick");
            sut.AardwolfData[0].DataByte.Should().Be(1);
        }

        [Fact]
        public async Task SbAardwolf_EmptyPayload_DecodesUnknown()
        {
            var (_, _, sut) = await ReadOnceAsync(
              _ => { }, Iac, Will, Aardwolf, Iac, Sb, Aardwolf, Iac, Se);
            sut.AardwolfData.Should().ContainSingle().Which.Channel.Should().Be("unknown");
        }

        [Fact]
        public async Task SendGmcp_NotNegotiated_ReturnsFalseWithoutWrites()
        {
            using var stream = new ScriptedStream();
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            var sent = await sut.SendGmcpAsync("Char.Vitals", new Dictionary<string, object> { ["hp"] = 100 });
            sent.Should().BeFalse();
            stream.ByteWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task SendMsdp_NotNegotiated_ReturnsFalseWithoutWrites()
        {
            // Port of test_send_mud_protocol_returns_early_without_negotiation
            // (MSDP half): no agreement means no bytes on the wire.
            using var stream = new ScriptedStream();
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            var sent = await sut.SendMsdpAsync(new Dictionary<string, object?> { ["HEALTH"] = 100 });
            sent.Should().BeFalse();
            stream.ByteWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task SendMssp_NotNegotiated_ReturnsFalseWithoutWrites()
        {
            // Port of test_send_mud_protocol_returns_early_without_negotiation
            // (MSSP half): no agreement means no bytes on the wire.
            using var stream = new ScriptedStream();
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            var sent = await sut.SendMsspAsync(new Dictionary<string, object> { ["NAME"] = "test" });
            sent.Should().BeFalse();
            stream.ByteWrites.Should().BeEmpty();
        }

        [Fact]
        public async Task SendGmcp_Negotiated_SendsJsonFrame()
        {
            using var stream = new ScriptedStream(Iac, Will, Gmcp);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            var sent = await sut.SendGmcpAsync("Char.Vitals", new Dictionary<string, object> { ["hp"] = 100 });
            sent.Should().BeTrue();
            var frame = stream.ByteWrites[^1];
            var expected = new List<byte> { Iac, Sb, Gmcp };
            expected.AddRange(Encoding.UTF8.GetBytes("Char.Vitals {\"hp\":100}"));
            expected.AddRange([Iac, Se]);
            frame.Should().Equal([.. expected]);
        }

        [Fact]
        public async Task SendZmp_Negotiated_SendsNulSeparatedFrame()
        {
            using var stream = new ScriptedStream(Iac, Will, Zmp);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            var sent = await sut.SendZmpAsync("zmp.ident", "MudName", "1.0");
            sent.Should().BeTrue();
            var frame = stream.ByteWrites[^1];
            var expected = new List<byte> { Iac, Sb, Zmp };
            expected.AddRange(Encoding.UTF8.GetBytes("zmp.ident\0MudName\01.0\0"));
            expected.AddRange([Iac, Se]);
            frame.Should().Equal([.. expected]);
        }

        [Fact]
        public async Task SendMsdp_Negotiated_SendsFrame()
        {
            using var stream = new ScriptedStream(Iac, Will, Msdp);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            var sent = await sut.SendMsdpAsync(new Dictionary<string, object?> { ["HEALTH"] = "100" });
            sent.Should().BeTrue();
            var frame = stream.ByteWrites[^1];
            frame.Should().Equal(
              (byte)Iac, (byte)Sb, (byte)Msdp, 1,
              (byte)'H', (byte)'E', (byte)'A', (byte)'L', (byte)'T', (byte)'H', 2,
              (byte)'1', (byte)'0', (byte)'0', (byte)Iac, (byte)Se);
        }

        [Fact]
        public async Task SendMssp_Negotiated_SendsFrame()
        {
            using var stream = new ScriptedStream(Iac, Will, Mssp);
            using var cts = new CancellationTokenSource();
            using var sut = new ByteStreamHandler(stream, cts, 1);
            (await sut.ReadAsync(TimeSpan.FromMilliseconds(50))).Should().BeEmpty();
            var sent = await sut.SendMsspAsync(new Dictionary<string, object> { ["NAME"] = "TestMUD" });
            sent.Should().BeTrue();
            var frame = stream.ByteWrites[^1];
            frame.Should().Equal(
              (byte)Iac, (byte)Sb, (byte)Mssp, 1,
              (byte)'N', (byte)'A', (byte)'M', (byte)'E', 2,
              (byte)'T', (byte)'e', (byte)'s', (byte)'t', (byte)'M', (byte)'U', (byte)'D',
              (byte)Iac, (byte)Se);
        }

        [Fact]
        public async Task Session_MxpAccumulatesAcrossReads()
        {
            using var stream = new ScriptedStream(Iac, Will, Mxp, Iac, Sb, Mxp, Iac, Se);
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().BeEmpty();
            stream.Enqueue(Iac, Sb, Mxp, 1, 2, Iac, Se);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().BeEmpty();
            session.MxpData.Should().HaveCount(2);
            session.MxpData[0].Should().BeEmpty();
            session.MxpData[1].Should().Equal(1, 2);
        }

        [Fact]
        public async Task Session_MspAccumulatesAcrossReads()
        {
            using var stream = new ScriptedStream(Iac, Will, Msp, Iac, Sb, Msp, Iac, Se);
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().BeEmpty();
            stream.Enqueue(Iac, Sb, Msp, 1, 2, Iac, Se);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().BeEmpty();
            session.MspData.Should().HaveCount(2);
            session.MspData[0].Should().BeEmpty();
            session.MspData[1].Should().Equal(1, 2);
        }

        [Fact]
        public async Task Session_MsspReplacedAcrossReads()
        {
            int[] first = SbBody(Mssp, [1, .. Text("NAME"), 2, .. Text("One")]);
            using var stream = new ScriptedStream([Iac, Will, Mssp, .. first]);
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().BeEmpty();
            session.MsspData.Should().ContainSingle().Which.Value.Should().Be("One");
            int[] second = SbBody(Mssp, [1, .. Text("NAME"), 2, .. Text("Two")]);
            stream.Enqueue(second);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().BeEmpty();
            session.MsspData.Should().ContainSingle().Which.Value.Should().Be("Two");
        }

        [Fact]
        public async Task Session_ZmpAtcpAardwolf_Stored()
        {
            int[] reads = [
                Iac, Will, Zmp, Iac, Will, Atcp, Iac, Will, Aardwolf,
                .. SbBody(Zmp, [.. Text("zmp.ident\0Mud\0")]),
                .. SbBody(Atcp, [.. Text("Room.Exits ne")]),
                .. SbBody(Aardwolf, [100, 3]),
            ];
            using var stream = new ScriptedStream(reads);
            using var session = new ServerSession(stream, new TelnetServerOptions(), CancellationToken.None);
            (await session.ReadAsync(TimeSpan.FromMilliseconds(500))).Should().BeEmpty();
            session.ZmpData["zmp.ident"].Should().Equal("Mud");
            session.AtcpData.Should().ContainSingle().Which.Should().Be(("Room.Exits", "ne"));
            session.AardwolfData.Should().ContainSingle().Which.Channel.Should().Be("status");
        }
    }
}