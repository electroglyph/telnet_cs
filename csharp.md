# C# 9 through C# 14 — New Features Reference

Researched from the official Microsoft Learn "What's new" docs (Nov 2020 – Nov 2025).
Each section lists every language feature in that release with a description and a usage example.

Sources:
- https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-14
- https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-13
- https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-12
- https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-version-history (covers 9, 10, 11)

Version / SDK map: C# 9 = .NET 5 · C# 10 = .NET 6 · C# 11 = .NET 7 · C# 12 = .NET 8 · C# 13 = .NET 9 · C# 14 = .NET 10.

---

## C# 9 (.NET 5, November 2020)

### Records
Reference types with value semantics for equality. The compiler synthesizes `Equals`, `GetHashCode`, `ToString`, `==`/`!=`, a copy constructor, and `Clone()`. Declared with `record class` (or just `record`):

```csharp
public record Person(string FirstName, string LastName);

// Positional properties, value equality, non-destructive mutation:
var p1 = new Person("Ada", "Lovelace");
var p2 = p1 with { LastName = "Byron" };   // with-expression, p1 unchanged
Console.WriteLine(p1 == p2);               // False (different values)
```

Records can have methods, custom constructors, and inherit from other records — but a record can
inherit only another record (never a class). A plain class may inherit from a record.

### Init-only setters
Properties (or indexers) that can only be set during construction or in an object/`with` initializer. Backs the `with` expression on records and immutable DTOs:

```csharp
public class Options
{
    public string Name { get; init; } = string.Empty;
}

var o = new Options { Name = "x" };  // OK
// o.Name = "y";                     // CS8852: init-only, compile error
```

### Top-level statements
A console program needs no `namespace` / `class Program` / `static Main` ceremony. The file may contain statements directly; `args`, `await`, and `return` (int exit code) work:

```csharp
// Program.cs — the whole program:
Console.WriteLine("Hello");
await Task.Delay(100);
return 0;
```
Only one file per project may use top-level statements, and they must precede type declarations.

### Pattern matching: relational, logical, parenthesized patterns
`and`, `or`, `not` combinators plus `<`, `>`, `<=`, `>=` relational patterns, usable in `is`, `switch` expressions/statements, and nested patterns:

```csharp
static string Classify(int n) => n switch
{
    < 0 => "negative",
    0 => "zero",
    > 0 and <= 10 => "small positive",
    not 42 => "large, not the answer",
    _ => "the answer",
};

if (obj is not null) { /* ... */ }   // idiomatic null check
```

### Target-typed `new`
Omit the type name when the target type is known:

```csharp
List<string> names = new();
Dictionary<int, Person> cache = new() { [1] = new("Ada", "Lovelace") };
```

### Static anonymous functions / lambdas
`static` on a lambda or anonymous method forbids capturing locals/instance state — avoids accidental closure allocations:

```csharp
Func<int, int> square = static x => x * x;
```

### Target-typed conditional expressions
The `?:` branches no longer need a "best common type" if there's a target type:

```csharp
Person? p = flag ? new Person("A", "B") : null;  // C# 8 rejected this
```

### Covariant return types
An override (or interface implementation) may return a more derived type than the base method:

```csharp
abstract class Animal { public abstract Animal Clone(); }
class Dog : Animal { public override Dog Clone() => new(); }  // was an error before C# 9
```

### Extension `GetEnumerator` in `foreach`
`foreach` accepts an extension method `GetEnumerator()`, so you can enumerate types you don't own without wrapping them:

```csharp
public static class Ext
{
    public static IEnumerator<int> GetEnumerator(this Range r) { /* ... */ }
}
foreach (var i in 1..5) { /* ... */ }
```

### Lambda discard parameters
Unused lambda parameters can be `_`, and multiple discards are allowed (plain `_` was previously the only discard name and couldn't repeat):

```csharp
Func<int, int, int> add = (_, _) => 0; // both unnamed
```

### Attributes on local functions
```csharp
void Outer()
{
    [return: NotNull]
    string Inner() => "hi";
}
```

### Native-sized integers: `nint` / `nuint`
CPU-word-sized signed/unsigned integers (`IntPtr`/`UIntPtr` under the hood), usable in arithmetic:

```csharp
nint count = 5;
nuint size = (nuint)IntPtr.Size;
```

### Function pointers
`delegate*` gives delegate-like calls without the delegate allocation — interop/high-perf scenarios, `unsafe` context required:

```csharp
static int Square(int x) => x * x;

unsafe
{
    delegate*<int, int> square = &Square;
    int y = square(4);
}
```
Supports calling conventions: `delegate* unmanaged[Cdecl]<int, int>`.

### SkipLocalsInit
`[SkipLocalsInit]` (System.Runtime.CompilerServices) omits the `localsinit` IL flag (which zero-initializes locals) — saves instructions in hot paths. Only safe when every local is assigned before use; reading an unassigned local yields garbage (and unverifiable code when combined with pointers):

```csharp
[SkipLocalsInit]
static void HotPath()
{
    Span<int> buf = stackalloc int[64]; // not zeroed — must fully assign before reading
}
```

### Module initializers
A parameterless static void method marked `[ModuleInitializer]` runs when the assembly loads (before any other code in it):

```csharp
[ModuleInitializer]
internal static void Init() { /* runs at assembly load */ }
```

### Partial methods: accessibility + non-void returns
Partial methods may now have access modifiers and return values — but then an implementation is mandatory (the "disappearing call" behavior only remains for `private void` partials):

```csharp
public partial class Gen
{
    public partial string GetName();   // must be implemented in another part
}
```

---

## C# 10 (.NET 6, November 2021)

### Record structs
Records for value types. `record struct` is a mutable-by-default struct with synthesized equality/members; `readonly record struct` is immutable:

```csharp
public readonly record struct Point(double X, double Y);
var p = new Point(1, 2) with { X = 5 };
```

### Struct improvements
- Parameterless constructors and field initializers in structs:
  ```csharp
  struct S
  {
      public S() { Value = 42; }   // legal since C# 10
      public int Value { get; set; } = 42;
  }
  ```
  Note: `new S()` runs it, but `default(S)` does not (fields are 0).
- Structs and anonymous types support `with` expressions.

### Interpolated string handlers
Library authors can define a custom `[InterpolatedStringHandler]` struct that receives the literal/placeholder parts of an `$"..."` at compile time — zero-allocation formatting and conditional evaluation (e.g. skip building the string when logging is disabled). Built-in consumers: `StringBuilder.Append`, `string.Create`, `Debug.Assert`, console and logging APIs:

```csharp
// No string is allocated: the handler gets parts only if the assert message is needed.
Debug.Assert(items.Count > 0, $"Expected items, got {items.Count}");
```

### Global using directives
One `global using` applies project-wide (conventionally in `GlobalUsings.cs` or auto-generated via `<ImplicitUsings>`):

```csharp
global using System.Collections.Generic;
global using static System.Math;
```

### File-scoped namespace declarations
One namespace per file without an indentation level:

```csharp
namespace MyApp.Services;   // no braces, rest of file is in the namespace

public class Worker { }
```

### Extended property patterns
Nested members can be matched with dotted paths instead of extra nesting:

```csharp
if (order is { Customer.Address.City: "Paris", Total: > 100 }) { /* ... */ }
```

### Lambda improvements
- **Natural type**: lambdas/method groups get an inferred delegate type, so `var f = () => 42;` works (infers `Func<int>`).
- **Explicit return type** when inference fails:
  ```csharp
  var f = object () => null;            // return type object
  var g = bool (string s) => s == "";   // (return-type)(params) => body
  ```
- **Attributes on lambdas** (applied to the method, return, or compiler-generated delegate):
  ```csharp
  Func<string, string> f = [return: NotNull] (string s) => s.Trim();
  ```

### Constant interpolated strings
`const` strings may use interpolation if every hole is itself constant:

```csharp
const string Prefix = "v";
const string Version = $"{Prefix}1.0";   // legal since C# 10
```

### Sealed `ToString` in records
```csharp
public record R
{
    public sealed override string ToString() => "R";  // prevents further overrides
}
```

### Deconstruction: assign + declare together
```csharp
int x = 0;
(x, int y) = point;   // x assigned, y newly declared — C# 9 rejected this mix
```

### `AsyncMethodBuilder` on methods
The `[AsyncMethodBuilder(typeof(...))]` attribute can target a single method (previously only types), allowing a custom async state-machine builder per method (e.g. pooling builders).

### CallerArgumentExpression
Capture the source text of an argument — powers `ArgumentNullException.ThrowIfNull`-style diagnostics and assertion libraries:

```csharp
static void Assert(bool condition, [CallerArgumentExpression("condition")] string? expr = null)
{
    if (!condition) throw new InvalidOperationException($"Failed: {expr}");
}
Assert(items.Count > 0);   // expr == "items.Count > 0"
```

### `#line` pragma: line + column + ranges
`#line` accepts a full span mapping for generated code (source generators, Razor), so diagnostics
point at the original source:

```csharp
#line (2, 6) - (2, 27) 15 "page.razor"
```
Components: `(start line, char)` – `(end line, char)` span in the mapped file, optional character
offset (here `15`) where the mapped span starts on the generated line, then the file name.

### Better definite-assignment / null-state analysis
Fewer false warnings around common patterns (e.g. `if (x is null) return;` flows, `??=` chains).

---

## C# 11 (.NET 7, November 2022)

### Raw string literals
`"""..."""` (3+ quotes) — no escaping needed; content can span lines; interpolation uses `$"""...{x}..."""` with more `$`s to disambiguate braces:

```csharp
string json = """
    {
        "name": "Ada",
        "tags": ["a", "b"]
    }
    """;
// Single-line also allowed: """He said "hi"."""
string path = """C:\Temp\file.txt""";

var name = "Ada";
string msg = $$"""Value: {{name}} is "{{name}}".""";  // $$ → {{...}} are holes
```

### Generic math (static abstract/virtual interface members)
Interfaces can declare `static abstract` / `static virtual` members; generic algorithms can constrain on operators:

```csharp
T AddAll<T>(IEnumerable<T> values) where T : INumber<T>
{
    T sum = T.Zero;
    foreach (var v in values) sum += v;
    return sum;   // works for int, double, decimal, ...
}
```
Backed by new BCL interfaces (`INumber<T>`, `IParsable<T>`, etc.). `IParsable<T>.TryParse` also enables generic parsing.

### Generic attributes
Attribute types can be generic — no more `Type` arguments:

```csharp
[Validator<MyValidator>()]
public class Model { }

public class ValidatorAttribute<T> : Attribute where T : new() { }
```

### UTF-8 string literals
`u8` suffix produces `ReadOnlySpan<byte>` of UTF-8 bytes at compile time — no runtime transcoding:

```csharp
ReadOnlySpan<byte> utf8 = "hello"u8;
```

### Newlines in interpolation holes
Line breaks (and any valid C#) are allowed inside `{...}` holes of interpolated strings:

```csharp
string s = $"Result: {items.Select(x => x.Value)
    .Where(v => v > 0)
    .Sum()}";
```

### List patterns
Match sequences by shape + elements; `..` is a slice sub-pattern:

```csharp
static string Describe(int[] a) => a switch
{
    [] => "empty",
    [var only] => $"one: {only}",
    [1, 2, ..] => "starts 1,2",
    [.., -1] => "ends -1",
    [_, .., _, _] => "at least 3",
    _ => "other",
};
```

### File-local types
`file` modifier restricts a type's visibility to its source file — lets source generators emit helpers without name collisions:

```csharp
file class GeneratedHelper { }
```

### Required members
`required` forces callers to initialize the member in an object initializer or via constructor; enforced at compile time (uses `SetsRequiredMembersAttribute` + `[MemberNotNull]` plumbing for constructors):

```csharp
public class User
{
    public required string Name { get; set; }
    public int Age { get; set; }
}
var u = new User { Name = "Ada" };   // OK
// var bad = new User();             // CS9035: required member missing
```

### Auto-default structs
The compiler auto-initializes any field/property the user constructor doesn't assign (previously every field had to be definitely assigned). Pairs with required members in structs.

### Pattern-match `Span<char>` / `ReadOnlySpan<char>` against constant strings
```csharp
static bool IsHello(ReadOnlySpan<char> s) => s is "hello";  // no allocation
```

### Extended `nameof` scope
`nameof` can now see method parameters (e.g. inside attributes on the method or in the parameter's own default/attribute):

```csharp
[return: NotNullIfNotNull(nameof(path))]
public string? GetEndpoint(string? path)
    => string.IsNullOrEmpty(path) ? null : BaseUrl + path;
```

### `nint`/`nuint` = `IntPtr`/`UIntPtr` aliases
The keywords `nint`/`nuint` are now true aliases for `System.IntPtr`/`System.UIntPtr` in all positions (previously only usable in some contexts).

### `ref` fields and `scoped`
- `ref` fields (inside `ref struct` only) enable `Span<T>`-like types to hold references:
  ```csharp
  ref struct Wrapper
  {
      public ref int Value;   // legal since C# 11, ref struct only
  }
  ```
  Ref-safety rule (verified): a method local can't be stored in the field of a returnable
  struct — `w.Value = ref v;` is CS8374. Scope the struct variable (or store a heap reference
  such as an array element) so the reference can't escape:
  ```csharp
  int v = 5;
  scoped var w = new Wrapper();
  w.Value = ref v;            // OK: scoped w can't escape
  ```
- `scoped` (on `ref`/`in` params, locals, or the `ref` itself) limits ref lifetime to the current scope, satisfying the ref-safety analyzer:
  ```csharp
  void M(scoped ref int x) { /* can't escape x to the caller */ }
  ```

### Checked user-defined operators
Overload `checked` variants of operators so `checked(...)` contexts use them (verified: compiles
and `checked(max + one)` throws, `unchecked` wraps):

```csharp
struct Quantity
{
    public int V;
    public Quantity(int v) { V = v; }
    public static Quantity operator +(Quantity a, Quantity b) => new(a.V + b.V);
    public static Quantity operator checked +(Quantity a, Quantity b)
        => new(checked(a.V + b.V));
}

var ok = new Quantity(1) + new Quantity(2);                       // unchecked op
var boom = checked(new Quantity(int.MaxValue) + new Quantity(1)); // OverflowException
```

### Method group conversion to delegate: cached delegates
Performance, no syntax change: when a method group is converted to a delegate (e.g.
`items.Where(Filter)`), the C# 11 compiler caches and reuses the delegate object instead of
allocating a new one on every execution — less GC pressure just by recompiling. (The ECMA
standard was updated to permit this caching; C# 11 is the first compiler to use it.)

### Warning wave 7
New warnings incl. CS8981: "The type name 'X' only contains lower-cased ascii characters. Such names may become reserved for the language." (e.g. a type named `parser`). Enable waves with `<WarningLevel>` / `<AnalysisLevel>`. Note: naming a type `file` itself is a hard error (CS9056: "Types and aliases cannot be named 'file'"), not this warning (verified).

---

## C# 12 (.NET 8, November 2023)

### Primary constructors (any class/struct)
Constructor parameters declared on the type itself, in scope throughout the type body (previously records-only):

```csharp
public class Service(ILogger logger, string name)
{
    public void Run() => logger.Log($"Running {name}");
    // logger/name captured as hidden fields if used in members
}
```
Rules: every other declared constructor must chain `: this(...)`; adding one removes the implicit parameterless ctor; unlike records, no public properties are generated (parameters used only in the body become private captured fields). For structs, `new()` zero-init still bypasses the primary ctor.

### Collection expressions
`[...]` builds any collection type — arrays, spans, lists, or anything with a collection initializer; `..` spreads another collection inline:

```csharp
int[] a = [1, 2, 3];
List<string> b = ["one", "two"];
Span<char> c = ['a', 'b'];
int[][] grid = [[1, 2], [3, 4]];
int[] combined = [..a, 4, 5];          // spread
string[] empty = [];                   // replaces Array.Empty<string>()
int[] fromEnumerable = [..GetItems()]; // any IEnumerable<T>
```
The target type drives the construction (array → `new[]`, span → inline storage, `List<T>` → initializer + `Add`).

### Inline arrays
Fixed-size inline buffers in a struct (safe-code equivalent of `fixed` buffers), indexed like arrays:

```csharp
[System.Runtime.CompilerServices.InlineArray(10)]
public struct Buffer
{
    private int _element0;   // single field; compiler synthesizes indexer/length
}

var b = new Buffer();
b[0] = 42;                   // bounds-checked, no heap allocation
```
Mainly consumed via `Span<T>` from runtime/library APIs; rarely declared by hand.

### Default lambda parameters
```csharp
var greet = (string name = "world") => $"Hello, {name}!";
greet();          // "Hello, world!"
greet("Ada");     // "Hello, Ada!"
```
Same rules as method optional parameters (defaults must be compile-time constants, trailing).

### `ref readonly` parameters
Expresses "passed by reference, definitely not mutated, but must be a variable (not a value)". Unlike
`in` (where the compiler may silently copy to a temp), the argument must be a variable the method
truly references. Callers use `in` or `ref` at the call site (`ref readonly` itself is not valid
there); omitting both compiles with a warning. Lets pre-`in` APIs using `ref` adopt readonly-ness
without breaking callers:

```csharp
bool IsNull(ref readonly int x) => Unsafe.IsNullRef(in x);
// calls: IsNull(in someVariable);  IsNull(ref someVariable);
```

### Alias any type
`using` aliases work for tuples, arrays, nullable, and other non-named types — not just named types:

```csharp
using Point = (int X, int Y);
using IntArray = int[];
using MaybeInt = int?;
```
Limitation: pointer and function-pointer types still can't be aliased — a `using` directive is
never inside an `unsafe` context, so `using P = int*;` fails with CS0214 (verified).

### Experimental attribute
`[Experimental("DIAG-ID")]` (System.Diagnostics.CodeAnalysis) marks a type/member/assembly experimental; consumers get an **error** with the given ID (proceed deliberately with `#pragma warning disable DIAG-ID` or `<NoWarn>`):

```csharp
[Experimental("MYLIB001")]
public class PreviewApi { }
// var x = new PreviewApi();  // error MYLIB001: 'PreviewApi' is for evaluation
//                            // purposes only and is subject to change or removal...
```

### Interceptors (experimental in C# 12)
Source generators can reroute calls to a known method to a generated "interceptor" method at compile time (used by e.g. minimal-API route-handler codegen). Opt-in via `<InterceptorsPreviewNamespaces>`. Generator-author feature, not everyday code.

---

## C# 13 (.NET 9, November 2024)

### `params` collections
`params` is no longer arrays-only — spans and collection interfaces work, avoiding array allocations:

```csharp
void Concat<T>(params ReadOnlySpan<T> items) { /* no array allocated at call site */ }
Concat(1, 2, 3);

void AddAll(params List<string> names) { }
void Print(params IEnumerable<int> values) { }  // compiler synthesizes storage
```
Supported: `Span<T>`, `ReadOnlySpan<T>`, `IEnumerable<T>`-plus-`Add` types, and the interfaces `IEnumerable<T>`, `ICollection<T>`, `IList<T>`, `IReadOnlyCollection<T>`, `IReadOnlyList<T>`.

### New `lock` type
`System.Threading.Lock` is the modern mutual-exclusion type; `lock (myLock)` on it compiles to `EnterScope()` (scope-based, exception-safe) instead of `Monitor`:

```csharp
private readonly Lock _gate = new();

void Update()
{
    lock (_gate)   // uses Lock.EnterScope() under the hood — no code change needed
    {
        /* critical section */
    }
}
```
Just changing the field type from `object` to `Lock` upgrades semantics. Don't lock on `this`/strings/Type objects (unchanged guidance).

### `\e` escape sequence
`'\e'` is the ESC character (U+001B) — replaces error-prone `"\x1b"` (which swallowed following hex digits):

```csharp
string clear = "\e[2J";   // ANSI clear-screen, no ambiguity
```

### Method-group natural type improvements
Overload pruning per scope: inapplicable candidates (wrong arity, unsatisfied constraints) are discarded before natural-type inference, so method-group-to-delegate conversions resolve correctly more often.

### Implicit index (`^`) in object initializers
`[^n]` ("from end") now works when initializing collections inside an object initializer:

```csharp
var t = new TimerRemaining { buffer = { [^1] = 0, [^2] = 1 } };
```

### `ref` locals and `unsafe` in iterators / async methods
`ref` locals and `ref struct` variables are allowed in `async` methods and iterators — as long as they don't cross an `await`/`yield` boundary:

```csharp
async Task<int> SumAsync(int[] data)
{
    ReadOnlySpan<int> span = data;   // OK: not used across await
    int sum = 0;
    foreach (var v in span) sum += v;
    await Task.Yield();
    return sum;
}
```

### `allows ref struct` (generic anti-constraint)
A type parameter can opt into accepting `ref struct` arguments; the compiler enforces ref-safety on it:

```csharp
class Box<T> where T : allows ref struct
{
    public void M(scoped T value) { /* ref-safety rules apply to value */ }
}
var b = new Box<Span<int>>();   // legal
```

### `ref struct`s can implement interfaces
```csharp
ref struct Parser : IDisposable
{
    public void Dispose() { }
}
```
Catch: no boxing conversion — a `ref struct` can't be assigned to the interface type (would box and break ref safety); interface members are reachable through `allows ref struct` type parameters or directly on the struct. All interface members (even default-implemented ones) must be implemented.

### Partial properties and indexers
Split declaration/implementation like partial methods (declaring declaration = no body; implementing declaration has the body — auto-property bodies are NOT allowed as the implementation):

```csharp
public partial class C
{
    public partial string Name { get; set; }          // declaring
}
public partial class C
{
    private string _name = "";
    public partial string Name                        // implementing
    {
        get => _name;
        set => _name = value;
    }
}
```

### OverloadResolutionPriority attribute
Library authors can mark one overload as preferred when adding a better overload that would otherwise be ambiguous — recompiled callers bind to the new overload, existing binaries keep working:

```csharp
public void Log(string message) { }
[OverloadResolutionPriority(1)]
public void Log(string message, bool structured = false) { }  // preferred on recompile
```

> Note: the `field` keyword shipped as a **preview** in C# 13 (VS 17.12+) and became final in C# 14 (see below).

---

## C# 14 (.NET 10, November 2025)

### Extension members (`extension` blocks)
The old `static ... this` syntax still works, but new `extension(...)` blocks additionally support **extension properties** and **static extension members/operators** (called as statics of the extended type):

```csharp
public static class EnumerableExtensions
{
    // Instance-style members for IEnumerable<T>:
    extension<T>(IEnumerable<T> source)
    {
        public bool IsEmpty => !source.Any();

        public IEnumerable<T> WhereNot(Func<T, bool> predicate)
            => source.Where(x => !predicate(x));
    }

    // Static-style members of IEnumerable<T> (no receiver name):
    extension<T>(IEnumerable<T>)
    {
        public static IEnumerable<T> Identity => Enumerable.Empty<T>();

        public static IEnumerable<T> operator +(IEnumerable<T> l, IEnumerable<T> r)
            => l.Concat(r);
    }
}

// Usage:
bool empty = sequence.IsEmpty;          // extension property
var all = IEnumerable<int>.Identity;    // static extension
var both = listA + listB;               // extension operator
```
The receiver parameter (`source`) is in scope for the whole block. Details: [extension members](https://learn.microsoft.com/en-us/dotnet/csharp/programming-guide/classes-and-structs/extension-methods) · [`extension` keyword](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/keywords/extension).

### `field`-backed properties
The contextual keyword `field` refers to the compiler-synthesized backing field, so one-sided validation/transformation no longer needs an explicit field:

```csharp
public string Message
{
    get;
    set => field = value ?? throw new ArgumentNullException(nameof(value));
}

public SqlConnection Connection
{
    get => field ??= new SqlConnection(connString);   // lazy init
}

public string Email
{
    get;
    set => field = value?.Trim().ToLowerInvariant() ?? string.Empty;
}

public string DisplayName
{
    get;
    set
    {
        if (field == value) return;
        field = value;
        OnPropertyChanged();                            // INPC without a field decl
    }
}
```
One or both accessors may have bodies. If the type already has a member named `field`, use `@field` / `this.field` to disambiguate.

### Null-conditional assignment
`?.` / `?[]` are allowed on the left of `=` and compound assignment (`+=`, `-=`, …; `++`/`--` excluded). The right-hand side evaluates **only when the receiver is non-null**:

```csharp
customer?.Order = GetCurrentOrder();   // GetCurrentOrder not called if null
config?["key"] = ComputeValue();
counter?.Count += 1;
```

### `nameof` with unbound generics
```csharp
string s = nameof(List<>);   // "List" — previously required List<int> etc.
```

### First-class span conversions
New implicit conversions from `T[]` to `Span<T>` and `ReadOnlySpan<T>` (alongside the existing
`Span<T>` → `ReadOnlySpan<T>`), composing with other conversions and participating in type
inference — spans behave like natural collection types:

```csharp
void Print(ReadOnlySpan<int> s) { /* ... */ }
int[] a = [1, 2, 3];
Print(a);                 // array → ReadOnlySpan, implicit
Span<int> span = a;       // array → Span, implicit
ReadOnlySpan<int> ro = span;
```

### Lambda parameter modifiers without types
`ref`, `in`, `out`, `ref readonly`, `scoped` on implicitly-typed lambda parameters (`params` still needs explicit types):

```csharp
delegate bool TryParse<T>(string text, out T result);
TryParse<int> parse = (text, out result) => int.TryParse(text, out result);
```

### Partial events and constructors
Partial types can split events and instance constructors (exactly one defining + one implementing declaration each):

```csharp
public partial class C
{
    public partial event EventHandler Changed;   // defining (field-like)
    public partial C();                          // defining
}
public partial class C
{
    public partial event EventHandler Changed    // implementing: needs add/remove
    {
        add { /* ... */ } remove { /* ... */ }
    }
    public partial C() : base() { /* ... */ }    // only impl. may have initializer
}
```
Only one part may use primary-constructor syntax.

### User-defined compound assignment operators
A type can declare the compound form (`+=`, `-=`, …) separately from the binary operator, typically mutating in place to avoid copies of large structs:

```csharp
struct BigBuffer
{
    public void operator +=(BigBuffer other) { /* mutate this in place */ }
}
```
The same proposal also allows parameterless instance `++` / `--` operators
(`public void operator ++() { ... }`). If no instance operator matches, the compiler falls back to
the classic binary/unary operator + assignment behavior.
See the [feature spec](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/proposals/csharp-14.0/user-defined-compound-assignment).

### File-based apps directives (tooling)
`dotnet run file.cs` supports `#:` directives that declare build settings inline in a single
file — no .csproj needed. Supported directives: `#:package`, `#:sdk`, `#:property`,
`#:project`, `#:include`:

```csharp
#:sdk Microsoft.NET.Sdk.Web
#:package System.CommandLine@2.0.0-*
#:property LangVersion=preview
```

---

## Quick lookup: "which version introduced X?"

| Feature | C# |
|---|---|
| Records, init setters, top-level statements, `and`/`or`/`not` + relational patterns, target-typed `new`, static lambdas, covariant returns, `nint`/`nuint`, function pointers, module initializers | 9 |
| Record structs, global usings, file-scoped namespaces, interpolated string handlers, lambda natural type / return type / attributes, const interpolation, `CallerArgumentExpression`, extended property patterns | 10 |
| Raw string literals, required members, generic math (`static abstract` interfaces), generic attributes, UTF-8 literals, list patterns, file-local types, `ref` fields + `scoped`, checked operators, cached method-group delegates, newlines in interpolation holes | 11 |
| Primary constructors (all types), collection expressions + spread, inline arrays, default lambda params, `ref readonly` params, alias-any-type, `[Experimental]` | 12 |
| `params` collections (Span etc.), `System.Threading.Lock`, `\e`, `ref` in async/iterators, `allows ref struct`, ref-struct interfaces, partial properties/indexers, `OverloadResolutionPriority` | 13 |
| `extension` members (properties + statics), `field`-backed properties, null-conditional assignment, `nameof(List<>)`, first-class span conversions, modifier-only lambda params, partial events/ctors, user-defined compound assignment | 14 |
