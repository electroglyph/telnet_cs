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
    }
}
