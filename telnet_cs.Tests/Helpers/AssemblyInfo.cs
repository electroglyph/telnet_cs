// Test parallelization is disabled assembly-wide because the suite shares
// mutable static Client state (SkipProactiveOptionNegotiation, TerminalType,
// TerminalSpeed) and timing-sensitive fakes.
[assembly: Xunit.CollectionBehavior(Xunit.CollectionBehavior.CollectionPerAssembly, DisableTestParallelization = true)]
