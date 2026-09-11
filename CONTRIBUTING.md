# Contributing

## Build, test, format

Requires the .NET 10 SDK **and** .NET 10 runtime (pinned via
`global.json`):

```sh
dotnet build telnet_cs.sln -c Release
dotnet test telnet_cs.sln -c Release
```

Treat warnings as errors is on: keep builds at `0 Warning(s)`. The
`ci` workflow also enforces formatting — match the `.editorconfig`
(2-space indent, `charset=utf-8-bom`, usings inside namespaces) before
pushing.

## Tests

- xUnit, in `telnet_cs.Tests/`, grouped by feature (`Client/`,
  `Server/`, `Protocol/`, `Transport/`, `ByteStream/`, `Regression/`).
  Shared fixtures live in `Helpers/` (`ScriptedStream`,
  `GlobalStateGuard`), socket doubles in `Fakes/`.
- Name tests `MethodUnderTest_Scenario_ExpectedResult`.
- Parallelization is off assembly-wide (shared mutable `Client`
  statics + timing-sensitive fakes). Tests that touch the statics must
  restore them via `Helpers.GlobalStateGuard`.
- Wire discipline: protocol behavior changes need wire-exact assertions
  (assert the bytes), not just happy-path coverage.

## Protocol changes

Read the RFC in `docs/telnet-rfcs/` first, update `docs/rfcs.md` and
`docs/wire.md` if behavior changes, and record user-visible breaks in
`CHANGELOG.md`.
