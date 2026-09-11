# telnet_cs

Telnet client **and** server library for .NET 10 (C# 14).

## Hard rule

NEVER run git commands (no `git status`, `git diff`, `git log`, `git add`,
`git commit`, etc.). If you need repo state, ask the user.

## How opencode uses AGENTS.md

Repo-root `AGENTS.md` wins per category over global config fallbacks.

## Commands

- Build: `dotnet build telnet_cs.sln -c Release` (or Debug). Requires the .NET 10 SDK **and** .NET 10 runtime.
- Run tests: `dotnet test telnet_cs.sln -c Release` — use `--filter` for focused tests.
- Format check: `dotnet format --verify-no-changes` (build treats warnings as errors; unformatted pushes fail).

## Conventions

- Layout: `telnet_cs/` (library: `Client/`, `Server/`, `Transport/`, `Protocol/`, `IO/`), `telnet_cs.Tests/` (xUnit suites, `Helpers/` shared fixtures + `Fakes/` socket doubles).
- Language reference: [csharp.md](./csharp.md) — local copy (C# 9 → C# 14). The [Best practices (C# 14)](#best-practices-c-14) section below states which practices to apply; per-feature details live in `csharp.md`.
- All new and rewritten code must follow [Best practices (C# 14)](#best-practices-c-14). Modernize surrounding lines you touch; do not reformat untouched code.
- Wire discipline: keep established wire bytes / negotiation behavior byte-for-byte unless a technical reason and a test pin justify the change. Do not paraphrase protocol behavior or "improve" message framing without a wire-exact regression test.
- Tests live next to the code they cover conceptually (`telnet_cs.Tests/`); give tests standalone names that describe the feature under test (`MethodUnderTest_Scenario_ExpectedResult`).

## Hard rules

- ALWAYS use a timeout for every shell command to avoid hangs. Use the `bash` tool's `timeout` param (ms) for every call plus shell-level `timeout 15 dotnet build`, `dotnet test` with `timeout:180000` for the full suite. Never run unbounded `bash`/`dotnet` without a timeout.
- NEVER reference audits in code or test comments (no finding IDs, no "audit-ordered" language). Audits are private documents; this repo is public code. Justify changes with plain technical reasons instead.
- NEVER reference the "owner" (or yourself) in code or test comments — no diary entries or decision logs. Comments explain what the code does and why it must be that way (technical reason). Provenance belongs in the commit message, not the source.
- If the user's command doesn't make sense, challenge them on it. Do not blindly execute instructions that contradict the repo's own rules or plain technical reality — push back with evidence (file paths, test results) and ask for clarification.

## Best practices (C# 14)

Target the latest stable language: `<LangVersion>latest</LangVersion>` (C# 14 on .NET 10 SDK).
For what each feature is and how to use it, see **[csharp.md](./csharp.md)** — the version-by-version
reference (C# 9 → C# 14) with examples. This file states which practices to apply when writing code.

## Nullability and validation

- Keep `<Nullable>enable</Nullable>` on. Never silence nullability with `!` to work around a design
  problem — fix the types or add a real guard instead. `!` is acceptable only for cases the compiler
  cannot see (e.g. initialized via reflection/serialization), with a comment.
- Validate public API arguments with `ArgumentNullException.ThrowIfNull`,
  `ArgumentOutOfRangeException.ThrowIf...`, `ArgumentException.ThrowIfNullOrEmpty/WhiteSpace`.
- Prefer `required` members or constructor parameters over settable-then-validated state.
- Use `is null` / `is not null` instead of `== null` (avoids overloaded-operator surprises).

## Types and object design

- Model immutable data as `record` / `readonly record struct` (value equality, `with` support).
  Use a `class` when identity matters or the type has significant behavior/lifetime.
- Prefer `init` accessors for construction-time-only state; `required` for mandatory members.
- Use primary constructors for simple dependency capture (services, DTOs). If the constructor needs
  validation or logic, write an explicit constructor body instead of clever parameter tricks.
- One type per file; file name matches type name. File-scoped namespaces; `global using` for
  ubiquitous imports only.

## Properties

- Use the `field` keyword for validated/transformed/lazy properties instead of hand-written backing
  fields (see csharp.md — C# 14 `field`-backed properties).
- Expression-bodied members for single-expression getters/methods.
- No redundant backing fields, no `GetX()`/`SetX()` method pairs where a property fits.

## Control flow and expressions

- Prefer `switch` expressions and pattern matching (`and`/`or`/`not`, relational, list patterns)
  over `if`/`else` chains and manual type tests + casts.
- Use target-typed `new()`, collection expressions (`[..]` with spread), and null-coalescing /
  null-conditional assignment (`?.`, `customer?.Order = ...`) to cut ceremony.
- Use raw string literals (`"""`) for embedded JSON/SQL/regex/XML; UTF-8 literals (`"..."u8`) for
  byte-oriented protocols.
- Prefer `foreach` + LINQ over hand-rolled index loops for in-memory collections; prefer
  `for` over `foreach` only in measured hot paths.

## Strings, collections, spans

- String building: interpolation with handlers over `string.Format`/`+` in loops; `StringBuilder`
  for loops; `const` interpolation for compile-time constants.
- Accept the most general usable collection parameter type; offer `params ReadOnlySpan<T>` /
  `params IEnumerable<T>` overloads for allocation-free variadic APIs.
- Use `Span<T>` / `ReadOnlySpan<T>` for parsing and buffer manipulation (first-class conversions in
  C# 14 make them cheap to pass around). Use `stackalloc` for small short-lived buffers.
- `ref struct` only when pinning stack-only semantics; remember it can't box to interfaces —
  expose functionality via `allows ref struct` generics where needed.

## Concurrency

- `async`/`await` end-to-end; never block on async code (`.Result`, `.Wait()`, `.GetAwaiter().GetResult()`
  outside `Main`). Always flow a `CancellationToken`.
- Prefer `ValueTask` over `Task` only for genuinely hot, often-synchronous paths.
- Synchronize with `System.Threading.Lock` + `lock` statement — never `lock (this)`, strings, or `Type`s.
- Prefer immutable messages / channels (`System.Threading.Channels`) over shared mutable state.
- Dispose with `using` declarations; implement `IAsyncDisposable` when teardown is async.

## Extension and generated code

- New `extension(...)` blocks (C# 14) for extension properties and static extensions; classic
  `this`-parameter syntax remains fine for plain extension methods. Don't use extensions to fake
  missing encapsulation — if it needs privates, it belongs in the type.
- Source-generated code (regex, JSON, logging, interceptors): prefer `[GeneratedRegex]`,
  `JsonSerializerContext`, `LoggerMessage.Define` over runtime reflection/emitted code.

## Errors and diagnostics

- Throw specific exception types with actionable messages; include parameter names via `nameof`.
  Don't catch `Exception` except at a true boundary (log + translate there).
- Use `[Experimental("ID")]` for in-progress APIs instead of ad-hoc "DO NOT USE" comments.
- Annotate with `[MemberNotNull]`, `[NotNullWhen]`, `[DoesNotReturn]` etc. where contracts help analysis.

## Quality gates

- Treat warnings as errors in CI (`<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`) with a modern
  `<AnalysisLevel>`; enable relevant warning waves. No `#pragma` suppressions without a justification comment.
- Public APIs get XML doc comments. Tests live next to the code they cover; name tests
  `MethodUnderTest_Scenario_ExpectedResult`.
- Follow .NET naming conventions (PascalCase types/members, camelCase locals/params, `_camelCase`
  private fields only when a hand-written backing field is genuinely needed).
