# Fuzz harness (`telnet_cs.Fuzz`, dev-only)

Deterministic structure-aware fuzzer for the parser, session, client,
codecs, and transports. It is a development tool, not a shipped surface:
the test suite never references it (except the harness unit tests in
`telnet_cs.Tests/Fuzz/`, which exercise the harnesses directly), and CI
builds/tests only `telnet_cs.sln`.

Run it with `dotnet run --project telnet_cs.Fuzz -- [options]`.

## Modes

`--list-modes` prints the harness names. Each mode drives one area with
mutated inputs under a per-iteration hang guard: `parser`, `session`,
`client`, `auth`, `codec`, `encoding`, `input`, `write`, `term`, `mccp`,
`proto`, `accept`, `repl`, `request`, `tlssniff`, `caps`, `storm`
(`both`/`all` combine them).

## Useful invocations

```sh
# Quick smoke over every harness (single worker, 5000 iterations default)
dotnet run --project telnet_cs.Fuzz

# Focus the MCCP decompressor for 60 seconds on 8 workers
dotnet run --project telnet_cs.Fuzz -- mccp --seconds 60 --jobs 8

# Replay one crashing input without saving artifacts
dotnet run --project telnet_cs.Fuzz -- --input crashes/mccp-s1-i42.bin
```

## Artifacts

A crashing iteration saves `<out>/<mode>-s<seed>-i<iter>.bin` plus a `.txt`
with the input bytes, hex dump, split points, exception, and minimized
repro; a `summary.json` lands next to them. An iteration also fails on
reply amplification (outbound bytes disproportionate to inbound) and on
exceeding the absolute outbound cap, not just on exceptions. Re-run with
the printed replay line to reproduce.

## Ground truth

Expected wire behavior comes from the vendored references: protocol
notes in [`mud-protocols/`](mud-protocols/README.md) and the RFC texts in
[`telnet-rfcs/`](telnet-rfcs/) (start with RFC 854/855/1143). The
[`divergences.md`](divergences.md) log records every intentional
deviation from telnetlib3 with its counterpart.
