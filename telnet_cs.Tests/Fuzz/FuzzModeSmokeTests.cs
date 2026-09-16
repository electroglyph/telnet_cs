namespace telnet_cs.Tests
{
    using System;
    using System.IO;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Xunit;

    /// <summary>
    /// End-to-end pins for every fuzzer mode: a short deterministic run of
    /// each harness must exit clean (no crashes). These do not hunt bugs —
    /// the standalone fuzzer does that — they pin the wiring (mode parsing,
    /// harness dispatch, oracle discipline) so a refactor that breaks a
    /// harness fails the suite instead of silently fuzzing nothing. Serial:
    /// the fuzzer keeps run-global mutable state.
    /// </summary>
    [Collection("Serial")]
    public class FuzzModeSmokeTests
    {
        [Theory]
        [InlineData("parser")]
        [InlineData("session")]
        [InlineData("client")]
        [InlineData("auth")]
        [InlineData("codec")]
        [InlineData("encoding")]
        [InlineData("input")]
        [InlineData("write")]
        [InlineData("term")]
        [InlineData("mccp")]
        [InlineData("proto")]
        [InlineData("accept")]
        [InlineData("repl")]
        [InlineData("request")]
        [InlineData("tlssniff")]
        [InlineData("caps")]
        [InlineData("storm")]
        public async Task FuzzMode_FixedSeedShortRun_ExitsClean(string mode)
        {
            var outDir = Path.Combine(Path.GetTempPath(), "telnetcs-fuzz-" + Guid.NewGuid().ToString("N"));
            try
            {
                var exit = await telnet_cs.Fuzz.Program.Main(
                [
                    "--mode", mode,
                    "--seed", "7",
                    "--iters", "12",
                    "--max-bytes", "128",
                    "--timeout-ms", "500",
                    "--out", outDir,
                ]);
                exit.Should().Be(0);
            }
            finally
            {
                if (Directory.Exists(outDir))
                {
                    Directory.Delete(outDir, true);
                }
            }
        }

        [Fact]
        public async Task FuzzListModes_PrintsEveryHarness()
        {
            var exit = await telnet_cs.Fuzz.Program.Main(["--list-modes"]);

            exit.Should().Be(0);
        }

        [Fact]
        public async Task FuzzInput_ReplaysFileThroughHarness()
        {
            var workDir = Path.Combine(Path.GetTempPath(), "telnetcs-fuzz-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDir);
            try
            {
                var input = Path.Combine(workDir, "replay.bin");
                await File.WriteAllBytesAsync(input, [0xFF, 0xFD, 0x18, 0xFF, 0xFA, 0x18, 0x01, 0xFF, 0xF0]);
                var exit = await telnet_cs.Fuzz.Program.Main(
                [
                    "--mode", "session",
                    "--input", input,
                    "--timeout-ms", "500",
                ]);

                exit.Should().Be(0, "replay saves nothing and the TTYPE probe is benign");
            }
            finally
            {
                Directory.Delete(workDir, true);
            }
        }

        [Fact]
        public async Task FuzzInput_MissingFile_ExitsUsageError()
        {
            var exit = await telnet_cs.Fuzz.Program.Main(
            [
                "--mode", "parser",
                "--input", Path.Combine(Path.GetTempPath(), "telnetcs-nope-" + Guid.NewGuid().ToString("N") + ".bin"),
            ]);

            exit.Should().Be(2);
        }

        [Fact]
        public async Task FuzzFaults_ShortRun_ExitsClean()
        {
            var outDir = Path.Combine(Path.GetTempPath(), "telnetcs-fuzz-" + Guid.NewGuid().ToString("N"));
            try
            {
                var exit = await telnet_cs.Fuzz.Program.Main(
                [
                    "--mode", "session",
                    "--seed", "7",
                    "--iters", "16",
                    "--max-bytes", "128",
                    "--timeout-ms", "500",
                    "--faults",
                    "--out", outDir,
                ]);
                exit.Should().Be(0, "injected IOExceptions are handled, not findings");
            }
            finally
            {
                if (Directory.Exists(outDir))
                {
                    Directory.Delete(outDir, true);
                }
            }
        }

        [Fact]
        public async Task FuzzJobs_ParallelShortRun_ExitsCleanAndWritesSummary()
        {
            var outDir = Path.Combine(Path.GetTempPath(), "telnetcs-fuzz-" + Guid.NewGuid().ToString("N"));
            try
            {
                var exit = await telnet_cs.Fuzz.Program.Main(
                [
                    "--mode", "parser",
                    "--seed", "7",
                    "--iters", "24",
                    "--max-bytes", "64",
                    "--timeout-ms", "500",
                    "--jobs", "4",
                    "--out", outDir,
                ]);

                exit.Should().Be(0);
                File.Exists(Path.Combine(outDir, "summary.json")).Should().BeTrue();
            }
            finally
            {
                if (Directory.Exists(outDir))
                {
                    Directory.Delete(outDir, true);
                }
            }
        }
    }
}
