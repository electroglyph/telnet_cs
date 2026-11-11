// Unit tests for hardening bounds: ENVIRON parsing caps, the negotiation
// storm guard, MCCP bomb guards, and MUD list caps.
namespace telnet_cs.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.IO.Compression;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;
    using telnet_cs.IO;
    using telnet_cs.Protocol;

    public class HardeningUnitTests
    {
        private static byte[] ZlibCompress(string text)
        {
            byte[] payload = Encoding.ASCII.GetBytes(text);
            using MemoryStream ms = new();
            using (ZLibStream compressor = new(ms, CompressionLevel.Optimal, leaveOpen: true))
            {
                compressor.Write(payload, 0, payload.Length);
            }

            return ms.ToArray();
        }

        [Fact]
        public void ParseEntries_OverMaxEntries_StopsAtMax()
        {
            // Verb IS (0), then five VAR(0)/VALUE(1) pairs.
            var payload = new List<byte> { 0 };
            for (int i = 0; i < 5; i++)
            {
                payload.Add(0);
                payload.Add((byte)('A' + i));
                payload.Add(1);
                payload.Add((byte)'x');
            }

            var entries = EnvironmentProtocol.ParseEntries(CollectionsMarshal.AsSpan(payload), maxEntries: 3);
            entries.Should().HaveCount(3);
            entries.Select(e => e.Name).Should().Equal("A", "B", "C");
        }

        [Fact]
        public void ParseEntries_ZeroMaxEntries_ParsesNothing()
        {
            var entries = EnvironmentProtocol.ParseEntries(new byte[] { 0, 0, (byte)'A' }, maxEntries: 0);
            entries.Should().BeEmpty();
        }

        [Fact]
        public void StormGuard_BelowThreshold_DoesNotSuppress()
        {
            NegotiationStormGuard guard = new();
            for (int i = 0; i < NegotiationStormGuard.Threshold; i++)
            {
                guard.NoteFrame();
            }

            guard.IsOverThreshold.Should().BeFalse();
        }

        [Fact]
        public void StormGuard_OverThreshold_Suppresses()
        {
            NegotiationStormGuard guard = new();
            for (int i = 0; i <= NegotiationStormGuard.Threshold; i++)
            {
                guard.NoteFrame();
            }

            guard.IsOverThreshold.Should().BeTrue();
        }

        [Fact]
        public void StormGuard_WindowExpiry_Resets()
        {
            NegotiationStormGuard guard = new();
            for (int i = 0; i <= NegotiationStormGuard.Threshold; i++)
            {
                guard.NoteFrame();
            }

            guard.IsOverThreshold.Should().BeTrue();
            Thread.Sleep(1100);
            guard.IsOverThreshold.Should().BeFalse();
        }

        [Fact]
        public void Mccp_MaxDecompressedBytesExceeded_FailsWithCapLog()
        {
            var logs = new List<string>();
            var decompressor = new MccpDecompressor
            {
                MaxDecompressedBytes = 64,
                CapLog = m => logs.Add(m),
            };
            byte[] wire = ZlibCompress(new string('a', 1024));
            foreach (byte b in wire)
            {
                decompressor.Feed(b);
            }

            decompressor.Failed.Should().BeTrue();
            logs.Should().Contain(m => m.StartsWith("mccp-output-cap:", StringComparison.Ordinal));
        }

        [Fact]
        public void Mccp_NegativeCaps_ThrowArgumentOutOfRange()
        {
            // Negative caps have no defined meaning (0 = unlimited);
            // they fail at set time instead of misbehaving mid-stream.
            var decompressor = new MccpDecompressor();
            Action decompressed = () => decompressor.MaxDecompressedBytes = -1;
            decompressed.Should().Throw<ArgumentOutOfRangeException>();
            Action ratio = () => decompressor.MaxDecompressionRatio = -1;
            ratio.Should().Throw<ArgumentOutOfRangeException>();
            Action compressed = () => decompressor.MaxCompressedBytes = -1;
            compressed.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Fact]
        public void Mccp_MaxCompressedBytesExceeded_FailsFast()
        {
            var logs = new List<string>();
            var decompressor = new MccpDecompressor
            {
                MaxCompressedBytes = 8,
                CapLog = m => logs.Add(m),
            };
            byte[] wire = ZlibCompress(new string('a', 1024));
            wire.Length.Should().BeGreaterThan(8);
            foreach (byte b in wire)
            {
                decompressor.Feed(b);
            }

            decompressor.Failed.Should().BeTrue();
            logs.Should().Contain(
                m => m.StartsWith("mccp-output-cap: compressed=", StringComparison.Ordinal));
        }

        [Fact]
        public void Mccp_TrailingOverCap_LogsOnce()
        {
            var logs = new List<string>();
            var decompressor = new MccpDecompressor
            {
                MaxCompressedBytes = 64,
                CapLog = m => logs.Add(m),
            };
            byte[] wire = ZlibCompress("hi");
            wire.Length.Should().BeLessThan(64);
            foreach (byte b in wire)
            {
                decompressor.Feed(b);
            }

            while (decompressor.TryTakeReady(out _))
            {
            }

            decompressor.StreamEnded.Should().BeTrue();
            for (int i = 0; i < 200; i++)
            {
                decompressor.Feed((byte)'p');
            }

            logs.Count(m => m.StartsWith("mccp-output-cap: trailing=", StringComparison.Ordinal)).Should().Be(1);
        }

        [Fact]
        public void Mccp_SmallStream_EndStillDetected()
        {
            // Regression: the compressed-input buffer must survive until
            // end-confirmation re-examines it, so small streams still end.
            var decompressor = new MccpDecompressor();
            foreach (byte b in ZlibCompress("hi"))
            {
                decompressor.Feed(b);
            }

            while (decompressor.TryTakeReady(out _))
            {
            }

            decompressor.StreamEnded.Should().BeTrue();
            decompressor.Failed.Should().BeFalse();
        }

        [Fact]
        public async Task Handler_MspOverCap_TrimsOldest()
        {
            using ScriptedStream stream = new(
                255, 250, 90, (int)'a', 255, 240,
                255, 250, 90, (int)'b', 255, 240);
            using CancellationTokenSource cts = new();
            using ByteStreamHandler handler = new(stream, cts, 1);
            handler.MaxMudListItems = 1;
            for (int i = 0; i < 10; i++)
            {
                await handler.ReadAsync(TimeSpan.FromMilliseconds(50));
            }

            handler.MspData.Should().HaveCount(1);
            handler.MspData[0].Should().Equal([(byte)'b']);
        }

        [Fact]
        public async Task Handler_NegotiationStorm_RefusalsSuppressedPastThreshold()
        {
            // Alternating DO/DONT for an unimplemented option (7): the
            // Q-machine stays silent for duplicates, so the test
            // self-calibrates instead of baking in reply counts. WILL ECHO
            // would not work: the server role ignores it reply-less. The
            // guard trips past 100 frames/s, so the head stays under the
            // threshold while the full stream exceeds it by a small tail.
            static List<int> BuildFrames(int count)
            {
                var frames = new List<int>();
                for (int i = 0; i < count; i++)
                {
                    frames.AddRange([255, i % 2 == 0 ? 253 : 254, 7]);
                }

                return frames;
            }

            async Task<(int Iacs, bool Over, bool Suppressed)> CountReplyIacsAsync(List<int> frames, bool guarded)
            {
                using ScriptedStream stream = new([.. frames]);
                using CancellationTokenSource cts = new();
                using ByteStreamHandler handler = new(stream, cts, 1);
                NegotiationStormGuard? guard = guarded ? new NegotiationStormGuard() : null;
                handler.StormGuard = guard;
                var logs = new List<string>();
                handler.Log = m => logs.Add(m);
                // One ReadAsync drains all available bytes, so loop until
                // the scripted bytes are consumed (not a fixed call count).
                int spins = 0;
                while (stream.Available > 0 && spins++ < 500)
                {
                    await handler.ReadAsync(TimeSpan.FromMilliseconds(50));
                }

                stream.Available.Should().Be(0);
                int iacs = stream.ByteWrites.SelectMany(w => w).Count(b => b == 255);
                return (iacs, guard?.IsOverThreshold ?? false, logs.Any(m => m.StartsWith("storm-guard:", StringComparison.Ordinal)));
            }

            // The over-threshold tail (frames 101+) must contribute zero
            // replies under the guard while contributing some without it.
            var (headUnguarded, _, _) = await CountReplyIacsAsync(BuildFrames(100), false);
            var (fullUnguarded, _, _) = await CountReplyIacsAsync(BuildFrames(110), false);
            var (guardedReplies, over, suppressed) = await CountReplyIacsAsync(BuildFrames(110), true);
            headUnguarded.Should().BeGreaterThan(0);
            fullUnguarded.Should().BeGreaterThan(headUnguarded);
            over.Should().BeTrue();
            suppressed.Should().BeTrue();
            guardedReplies.Should().Be(headUnguarded);
        }

        [Fact]
        public async Task Handler_ZmpTinyArgsOverKeyCap_RejectedWithArgsLog()
        {
            // One-char command with three one-char args: far below the value
            // cap, so only the key-count cap can refuse it (the old gate let
            // the count check through under the value cap).
            var reads = new List<int> { 255, 251, 93, 255, 250, 93 };
            reads.AddRange(Encoding.ASCII.GetBytes("c\0a\0b\0c\0").Select(b => (int)b));
            reads.AddRange([255, 240]);
            using ScriptedStream stream = new([.. reads]);
            using CancellationTokenSource cts = new();
            using ByteStreamHandler handler = new(stream, cts, 1);
            handler.MaxMudKeys = 2;
            var logs = new List<string>();
            handler.Log = m => logs.Add(m);
            int fired = 0;
            handler.ZmpReceived += (_, _) => fired++;
            for (int i = 0; i < 10 && stream.Available > 0; i++)
            {
                await handler.ReadAsync(TimeSpan.FromMilliseconds(50));
            }

            fired.Should().Be(0);
            handler.ZmpData.Should().BeEmpty();
            logs.Should().Contain(
                m => m.StartsWith("mud-cap:", StringComparison.Ordinal) && m.Contains("cap=2"));
        }

        [Fact]
        public async Task Handler_MsspListValueOverCap_FiltersOverCapVals()
        {
            // Repeated VALs decode to List<string>: each VAL over the
            // per-value cap must be filtered like an over-cap string.
            List<int> reads = [255, 251, 70, 255, 250, 70, 1, (int)'Y'];
            foreach (int rep in new[] { 0, 1 })
            {
                reads.Add(2);
                foreach (byte b in Encoding.UTF8.GetBytes(new string('B', 5000)))
                {
                    reads.Add(b);
                }
            }

            reads.AddRange([255, 240]);
            using ScriptedStream stream = new([.. reads]);
            using CancellationTokenSource cts = new();
            using ByteStreamHandler handler = new(stream, cts, 1);
            handler.MaxMudValueChars = 4096;
            var logs = new List<string>();
            handler.Log = m => logs.Add(m);
            IReadOnlyDictionary<string, object>? got = null;
            handler.MsspReceived += d => got = d;
            for (int i = 0; i < 20 && stream.Available > 0; i++)
            {
                await handler.ReadAsync(TimeSpan.FromMilliseconds(50));
            }

            got.Should().NotBeNull();
            got.Should().BeEmpty();
            handler.MsspData.Should().NotBeNull();
            handler.MsspData.Should().BeEmpty();
            logs.Should().Contain(m => m.StartsWith("mud-cap:", StringComparison.Ordinal) && m.Contains("value-cap"));
        }

        [Fact]
        public async Task Handler_MsspOverByteCap_DropsFrame()
        {
            // A 12KB MSSP body past a 1KB list-byte cap is dropped
            // pre-decode, like GMCP/MSDP: the hook never fires.
            List<int> reads = [255, 251, 70, 255, 250, 70, 1, (int)'Z'];
            for (int rep = 0; rep < 3; rep++)
            {
                reads.Add(2);
                foreach (byte b in Encoding.UTF8.GetBytes(new string('C', 4000)))
                {
                    reads.Add(b);
                }
            }

            reads.AddRange([255, 240]);
            using ScriptedStream stream = new([.. reads]);
            using CancellationTokenSource cts = new();
            using ByteStreamHandler handler = new(stream, cts, 1);
            handler.MaxMudListBytes = 1024;
            var logs = new List<string>();
            handler.Log = m => logs.Add(m);
            int fired = 0;
            handler.MsspReceived += _ => fired++;
            for (int i = 0; i < 40 && stream.Available > 0; i++)
            {
                await handler.ReadAsync(TimeSpan.FromMilliseconds(50));
            }

            fired.Should().Be(0);
            handler.MsspData.Should().BeNull();
            logs.Should().Contain(m => m.StartsWith("mud-cap:", StringComparison.Ordinal));
        }
    }
}
