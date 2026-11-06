# Hooking blaze-cs into the SDK's Roslyn and MSBuild

Research note, 2026-11-06. Question: can blaze-cs hook into or patch the Roslyn and
MSBuild that ship with the user's SDK, without becoming a compiler of its own, and
where would each hook take it?

Short answer: five hooks exist. Two are worth building, one is worth building later,
one is a modest cleanup, and one (patching Roslyn's internals) is not a hook at all
and should be left alone. Ranked by what the user gets:

| # | Hook | What it gives | API status | Verdict |
|---|------|---------------|------------|---------|
| 1 | Hot reload deltas into the test hosts | Test hosts (and the servers integration tests start) stay up across edits; tests selected per changed type | Shipped in the SDK, `internal` class, needs reflection | Build |
| 2 | Serve the Roslyn compiler-server pipe | `dotnet build`, IDE builds and `dotnet test` compile through the daemon instead of invalidating it | Internal wire protocol, stable within one SDK | Build |
| 3 | Run analyzers incrementally in the daemon | Warnings in `blaze-cs build`, close to `dotnet build`'s | Public API | Later |
| 4 | Evaluate projects in-process with the MSBuild API | A `.csproj` edit re-evaluates one project instead of running a whole `dotnet build` | Public API, heavy dependency | Maybe |
| 5 | Patch Roslyn's emit (Harmony/MonoMod, `IgnoresAccessChecksTo`) | Nothing: no internal cache of compiled method bodies exists to unlock | Fragile | No |

## What the daemon does today, and where its time goes

`ProjectCompiler` parses csc's arguments with `CSharpCommandLineParser`, builds a
`CSharpCompilation`, and on an edit calls `ReplaceSyntaxTree` for the changed file and
`Emit` for the project (`src/BlazeCs/ProjectCompiler.cs`). On jellyfin a method-body
edit in `MediaBrowser.Controller` costs about 340 ms in the daemon: compile 280 to
350 ms, identifier scan about 10 ms, writing `obj/` and `bin/` about 25 ms (measured
2026-11-06 after PR #24).

Roslyn's `ReplaceSyntaxTree` reuses two things: the metadata side of the old
compilation ("reuses metadata from the old compilation object", per the
[API docs](https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.compilation.replacesyntaxtree)),
and the lazily built declaration of every tree that did not change
(`SyntaxAndDeclarationManager.ReplaceSyntaxTree` removes and re-adds only the replaced
tree's `RootSingleNamespaceDeclaration` in the `DeclarationTable`, see
[SyntaxAndDeclarationManager.cs](https://github.com/dotnet/roslyn/blob/main/src/Compilers/CSharp/Portable/Compilation/SyntaxAndDeclarationManager.cs)).
Everything else is redone on `Emit`: every source symbol is re-created, and
`MethodCompiler` binds, lowers and emits every method body of the project again. That
is the 280 to 350 ms. Roslyn has no cache of lowered bodies across compilations, in any
layer. The only incremental emit it has is Edit and Continue, which produces deltas
against a baseline, not a PE
([`Compilation.EmitDifference`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.compilation.emitdifference)).

That fact shapes the ranking: the compile step cannot be made cheaper by hooking
Roslyn, only by not doing it (hot reload) or by doing it for more callers (the
compiler-server pipe).

## 1. Hot reload deltas into the test hosts

### What the SDK ships

SDK 10.0.403 on this machine ships the whole engine under
`sdk/10.0.403/DotnetTools/dotnet-watch/10.0.403/tools/net10.0/any/`:
`Microsoft.CodeAnalysis.Features.dll`, `Microsoft.CodeAnalysis.CSharp.Features.dll`,
`Microsoft.CodeAnalysis.Workspaces.dll`, `Microsoft.CodeAnalysis.CSharp.Workspaces.dll`,
`Microsoft.CodeAnalysis.ExternalAccess.HotReload.dll`, `Microsoft.DotNet.HotReload.Watch.dll`,
and the in-process agent `hotreload/net10.0/Microsoft.Extensions.DotNetDeltaApplier.dll`.
Those are the same Roslyn build as `Roslyn/bincore` (file version 5.900.26.52412 on both
sides; `dotnet-watch.deps.json` lists `Microsoft.CodeAnalysis.*/5.9.0-1.26523.112` and
ships no `Microsoft.CodeAnalysis.dll` of its own). The daemon already loads
`Microsoft.CodeAnalysis.dll` and `Microsoft.CodeAnalysis.CSharp.dll` from the SDK's
`bincore` (`src/BlazeCs/SdkRoslyn.cs`); loading Features and Workspaces from the
dotnet-watch folder of the same SDK is the same mechanism.

### The API

[`HotReloadService`](https://github.com/dotnet/roslyn/blob/main/src/Features/ExternalAccess/HotReload/Api/HotReloadService.cs)
in `Microsoft.CodeAnalysis.ExternalAccess.HotReload` is `internal sealed` (visible to
dotnet-watch). Its surface:

- `HotReloadService(SolutionServices services, Func<ValueTask<ImmutableArray<string>>> capabilitiesProvider)`
- `Task StartSessionAsync(Solution solution, CancellationToken)`: "Solution that represents sources that match the built binaries on disk."
- `Task<Updates> GetUpdatesAsync(Solution solution, ImmutableDictionary<ProjectId, RunningProjectInfo> runningProjects, CancellationToken)`
- `Updates`: `Status` (`NoChangesToApply`, `ReadyToApply`, `Blocked`), `ProjectUpdates`
  (per module: `ModuleId`, `ILDelta`, `MetadataDelta`, `PdbDelta`, `UpdatedTypes`,
  `RequiredCapabilities`), `TransientDiagnostics` (rude edits), `ProjectsToRestart`,
  `ProjectsToRebuild`, `ProjectsToRedeploy`.
- `CommitUpdate()`, `DiscardUpdate()`, `EndSession()`.

It takes a Workspaces `Solution`, not a bare `Compilation`. The daemon can build one
with `AdhocWorkspace` from what it already has per project: parsed `ParseOptions`,
`CompilationOptions`, metadata references, analyzer references (generators) and
document texts. Being `internal`, it needs reflection or an
`IgnoresAccessChecksToAttribute` shim, the same kind of thing `ProjectCompiler` already
does to set `GeneratorDriverOptions.ChecksumAlgorithm`. The underlying compiler API,
`EmitBaseline.CreateInitialBaseline(Compilation, ModuleMetadata, debugInformationProvider, localSignatureProvider, hasPortableDebugInformation)`
and `Compilation.EmitDifference`, is public
([EmitBaseline.cs](https://github.com/dotnet/roslyn/blob/main/src/Compilers/Core/Portable/Emit/EditAndContinue/EmitBaseline.cs)),
but the rude-edit analysis that decides which edits are deltas and which need a
restart lives in Features, so going below `HotReloadService` means rewriting that.

### Applying deltas

The test host is blaze-cs's own code (`src/BlazeCs.TestHost`), so it can call
[`MetadataUpdater.ApplyUpdate(Assembly, ReadOnlySpan<byte> metadataDelta, ReadOnlySpan<byte> ilDelta, ReadOnlySpan<byte> pdbDelta)`](https://learn.microsoft.com/en-us/dotnet/api/system.reflection.metadata.metadataupdater.applyupdate)
itself, as dotnet-watch's agent and Rider's do; `MetadataUpdater.IsSupported` says
whether the process allows it. Two constraints:

- The host must start with `DOTNET_MODIFIABLE_ASSEMBLIES=debug`
  ([dotnet watch docs](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-watch)
  describe the pipe dotnet-watch sets up with `DOTNET_HOTRELOAD_NAMEDPIPE_NAME`; the
  env var is the runtime's switch). The daemon's pre-started hosts can set it.
- Updated assemblies must be compiled without optimizations and with debug
  information. The runtime's own hot reload test project states "calling the
  ApplyUpdate API requires optimizations turned off and debug information emitted"
  ([dotnet/runtime test project](https://github.com/radical/runtime/commit/93f407d231e31687decdc1f5b7f466af2fb94737)).
  Debug configuration satisfies this; a Release test run falls back to today's path.

Microsoft's own test platform does this already:
[`Microsoft.Testing.Extensions.HotReload`](https://learn.microsoft.com/en-us/dotnet/core/testing/microsoft-testing-platform-hot-reload)
applies changes to a running test host in console mode when
`TESTINGPLATFORM_HOTRELOAD_ENABLED=1`. That is the precedent that test hosts can be
hot-reload targets.

### Rude edits

Edits that need a restart, per the
[supported-edits list](https://learn.microsoft.com/en-us/visualstudio/debugger/supported-code-changes-csharp):
renames, deleting types or members, changing method signatures, modifying interfaces,
adding abstract or virtual members or overrides, changing base types, changing a
static member after it was accessed, attribute changes. Supported: method bodies,
adding methods, fields and constructors, lambdas, async, iterators, generics. blaze-cs
already classifies edits (`ApiSurface`): a body edit is the common case and is a delta;
a public signature change is a rude edit and takes today's rebuild-and-restart path.
`HotReloadService` reports the split itself (`ProjectsToRestart`).

### Where it takes blaze-cs

- A body edit in a core library no longer restarts the test host: the daemon emits a
  delta (only the changed methods are compiled), the host applies it, and reruns. The
  host's JIT state, loaded packages and test framework stay warm.
- Integration test projects that start a server in the host
  (`Jellyfin.Server.Integration.Tests`, 42 s of the 44 s full run) keep that server up
  between runs. This is the single largest time in the jellyfin test loop and today
  nothing in blaze-cs touches it.
- `UpdatedTypes` names the types a delta changed. Test selection can go from "any
  assembly bytes changed" to "tests in or calling changed types" without extra
  analysis.
- The daemon must still emit full assemblies to `obj/` and `bin/` so `dotnet run`,
  IDEs and later `dotnet build`s keep working; the delta is extra work on the build
  side (small: EnC compiles only the edited methods) and saved work on the test side.
- Risk: the `internal` API can change between SDKs. The daemon already pins itself to
  "SDK 10.0.100 or later" and loads Roslyn by path; a reflection shim with a version
  check and a fallback to the restart path contains the risk.

## 2. Serve the Roslyn compiler-server pipe

### How the Csc task finds its server

The `Csc` MSBuild task, when `UseSharedCompilation` is true (the SDK default), does not
run `csc.dll`. It builds a `BuildRequest` and sends it to a named pipe
([ManagedCompiler.cs](https://github.com/dotnet/roslyn/blob/main/src/Compilers/Core/MSBuildTask/ManagedCompiler.cs)):

```csharp
var clientDirectory = Path.GetDirectoryName(PathToBuiltInTool);
...
var pipeName = !string.IsNullOrEmpty(SharedCompilationId)
    ? SharedCompilationId
    : BuildServerConnection.GetPipeName(clientDirectory);
var responseTask = BuildServerConnection.RunServerBuildRequestAsync(
    buildRequest, pipeName, clientDirectory, TaskEnvironment.BuildEnvironment, logger, ...);
```

- `clientDirectory` is the SDK's `Roslyn/bincore` (where `csc.dll` lives).
- `GetPipeName` hashes `"{userName}.{isAdmin}.{clientDirectory}"` with SHA256, base64,
  `/` to `_`, `=` removed
  ([BuildServerConnection.cs](https://github.com/dotnet/roslyn/blob/main/src/Compilers/Shared/BuildServerConnection.cs)).
  The pipe is therefore per user and per SDK, shared by every solution built with that
  SDK.
- `SharedCompilationId` is an MSBuild property (`Microsoft.CSharp.Core.targets` passes
  `SharedCompilationId="$(SharedCompilationId)"`) that replaces the pipe name outright.
  A solution can pick its server with `-p:SharedCompilationId=...` or a
  `Directory.Build.props` entry.
- If no server answers, the client starts `VBCSCompiler` from `clientDirectory`; if the
  connection fails or the server rejects the request, the task runs csc in-process
  ("If server compilation fails for any reason, the compiler will fall back to
  stand-alone compilation", [Compiler Server.md](https://github.com/dotnet/roslyn/blob/main/docs/compilers/Compiler%20Server.md)).
  After two `CannotConnect` results in one build the task stops trying
  (`maxCannotConnectCount = 2` in ManagedCompiler.cs).

### The wire protocol

[BuildProtocol.cs](https://github.com/dotnet/roslyn/blob/main/src/Compilers/Shared/BuildProtocol.cs)
is shared source compiled into both `csc.dll` and `Microsoft.Build.Tasks.CodeAnalysis.dll`,
so within one SDK the two ends always agree. A request carries `CompilerHash` (the
client's commit hash; a server with a different hash answers `IncorrectHash`), the
language, `CurrentDirectory`, `LibEnvVariable`, optional `KeepAlive`, and the command
line arguments. Responses: `Completed` (exit code plus output text), `MismatchedVersion`,
`IncorrectHash`, `AnalyzerInconsistency`, `Rejected`, `Shutdown`, `CannotConnect`. The
server writes the output files itself; the client only relays the exit code and text.
"The client and server must run on the same machine, as the client and server only
exchange a command line, not real assets."

The classes are `internal`. A blaze-cs server would implement the byte format (length,
protocol version, hash, argument list), about 200 lines, and read the commit hash from
the `CommitHashAttribute` of the SDK's `Microsoft.CodeAnalysis.dll` it already loads.

### Where it takes blaze-cs

- Every build that goes through the SDK's `Csc` task, which is `dotnet build`, Rider,
  VS Code's C# extension, `dotnet test`, `dotnet watch`, hands each project's command
  line to the daemon. The daemon answers from its warm compilation: `Refresh`,
  `Emit`, write, same bytes as today. It also learns the new arguments from the
  request itself, which is what the MSBuild logger exists to capture today
  (`src/BlazeCs.Logger`).
- Today a build outside the daemon makes its cached arguments stale and forces a
  `dotnet build --no-incremental` (`SolutionBuilder`, "its cached compiler arguments
  are stale"). With the daemon as the server there is no outside build: an IDE build
  keeps the daemon in sync instead of invalidating it.
- It does not make `dotnet build` fast. On jellyfin a no-op `dotnet build` takes 2.7 to
  4.0 s and a pass with `-p:SkipCompilerExecution=true -p:ProvideCommandLineArgs=true`
  (MSBuild without csc) 2.7 to 3.0 s, measured 2026-11-06 on this machine. MSBuild
  evaluation and targets are the floor; csc is the part above it. `blaze-cs build` at
  0.34 s stays the fast path.
- The daemon's API diff does not apply on this path: MSBuild decides what to compile
  from timestamps (a dependent recompiles only when the reference assembly changed,
  which is coarser than the daemon's member-level diff). Each request is one project's
  compile, as csc would do it.
- Pipe ownership: the default pipe is per SDK, not per solution. A daemon serving it
  receives requests for other solutions too; it must either answer them like
  `VBCSCompiler` would (a plain compile with the SDK's Roslyn, which is cheap to
  support since that is `csc`'s public `CSharpCompiler` code path) or respond
  `Rejected` and let them fall back to in-process csc. Using `SharedCompilationId` per
  solution avoids this but needs a line in the user's `Directory.Build.props`.
- Risk: the protocol is internal and can change between Roslyn versions; it has
  changed rarely, and because the daemon loads the SDK's own Roslyn, the version to
  match is always the one on disk. A mismatch degrades to in-process csc, never to a
  wrong build.

## 3. Run analyzers incrementally in the daemon

Today `blaze-cs build` skips analyzers and reports warnings-as-errors as warnings
(README, "Good to know"). On jellyfin analyzers are about 0.6 s of a 3.0 s
`dotnet build` body edit (3.01 s against 2.37 s with `RunAnalyzers=false`).

The public API is per compilation:
[`CompilationWithAnalyzers.GetAnalyzerSemanticDiagnosticsAsync(SemanticModel, TextSpan?, CancellationToken)`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.diagnostics.compilationwithanalyzers.getanalyzersemanticdiagnosticsasync)
and `GetAnalyzerSyntaxDiagnosticsAsync(SyntaxTree, ...)`, so the daemon can run
analyzers on the changed trees only. The docs warn: "Depending on analyzers' behavior,
returned diagnostics can have locations outside the tree or filter span, and some
diagnostics that would be reported for the tree by an analysis of the complete
compilation can be absent." Roslyn's IDE engine handles that by caching the last
`CompilationWithAnalyzers` per project and splitting analyzers into those that support
span-based incremental analysis and those that must run over the whole document
([DiagnosticIncrementalAnalyzer_GetDiagnosticsForSpan.cs](https://source.dot.net/Microsoft.CodeAnalysis.Features/Diagnostics/Service/EngineV2/DiagnosticIncrementalAnalyzer_GetDiagnosticsForSpan.cs.html)).
The daemon already loads analyzer assemblies to find generators (`GeneratorCache`), so
loading `DiagnosticAnalyzer`s is the same loader.

Where it takes blaze-cs: warnings for the edited files on every build, and
compilation-end analyzers (unused members, and the like) on a slower cadence or on
`--all`. Byte-identical output is unaffected, since analyzers never change emitted
bytes (`ProjectCompiler.CompilationOptions` already relies on that). It does not change
build time much either way; it closes a correctness gap, not a speed one.

## 4. Evaluate projects in-process with the MSBuild API

The slow path runs `dotnet build` as a process and reads what it did through the
logger. The SDK's own design-time pass is documented in
`Roslyn/Microsoft.CSharp.Core.targets`: Visual Studio runs `CoreCompile` with
`SkipCompilerExecution=true` and `ProvideCommandLineArgs=true`, "that resulting command
line string is where we get our compiler switches." That pass costs as much as a no-op
build (2.7 to 3.0 s on jellyfin, above), so skipping csc buys nothing by itself.

The gain would come from keeping MSBuild's evaluated `ProjectInstance`s in the daemon
through the `Microsoft.Build` API (`ProjectCollection`, `BuildManager`), loaded from the
SDK with `Microsoft.Build.Locator` (the SDK ships it next to dotnet-watch, along with
`Microsoft.CodeAnalysis.Workspaces.MSBuild` and its `BuildHost`, which is how Roslyn's
own tooling loads projects). Then a `.csproj` or `.props` edit re-evaluates the
projects it affects and runs their `CoreCompile` dependencies in-process, instead of a
whole-solution build. This is a large dependency to host in the daemon (MSBuild wants
to own the process's assembly loading and environment) and the win is only on the
project-file-edit path. Worth doing after 1 and 2 if project-file edits turn out to be
frequent for users.

## 5. Patching Roslyn's internals

Runtime patching is technically available on .NET 10: `MonoMod.Core` 1.3.6 targets
`net10.0` ([NuGet](https://www.nuget.org/packages/MonoMod.Core)) and Harmony builds on
it. `IgnoresAccessChecksToAttribute` reaches internal members without patching. The
question is what there is to reach.

For the compile step, nothing. The expensive work on a body edit is `MethodCompiler`
re-emitting every method of the project, and Roslyn keeps no reusable result of that
across compilations: not in the compiler, not in the compiler server (which "holds a
fixed-size cache of metadata symbols" only, per Compiler Server.md), not in the IDE.
Making emit skip unchanged methods would mean splicing bodies from the previous PE into
the new one while keeping token numbering, MVID and ordering byte-identical to csc's.
That is a new emit mode, not a hook, and it would break the daemon's one guarantee
(same bytes as `dotnet build`). EnC's `EmitDifference` is Roslyn's own answer to the
same problem and it deliberately produces deltas instead; hook 1 uses it where deltas
are enough.

Reflection into internals is justified in two narrow places, both already in the
pattern the code uses for `GeneratorDriverOptions.ChecksumAlgorithm`: constructing
`HotReloadService` (hook 1) and reading `CommitHashAttribute` (hook 2).

## Memory: what the daemon holds, and what moves it

Measured 2026-11-06 on jellyfin (41 projects, 19 MB of C# in 2,251 files), daemon from this
worktree, SDK 10.0.403, 32 cores, server GC as `Program.StartDaemon` sets it.

### Where the 1.4 GB goes

After load and warm-up the daemon's RSS was 1421 MB. A heap dump (`dotnet-dump collect --type Heap`,
`dumpheap -stat`) showed:

| Part | Size | Notes |
|---|---:|---|
| Live managed objects | 461 MB | 4.19 M objects, see breakdown below |
| GC committed beyond live | 151 MB | `eeheap -gc`: 612 MB committed |
| Loader heaps (types, method tables) | 55 MB | `eeheap -loader` |
| JIT code | 15 MB heaps, 69 MB mapped | `/memfd:doublemapper` |
| Mapped managed DLLs | 73 MB | Roslyn, generators, analyzers |
| Native libraries | 32 MB | |
| Rest: anonymous memory outside the GC's committed count | ~600 MB | freed GC regions not yet decommitted, see experiment |

Live managed objects by subsystem (`dumpheap -stat`, grouped by type name):

| Group | MB |
|---|---:|
| Green syntax trees (the parse) | 79 |
| `System.String` + `char[]` (source texts, token values, identifier index) | 75 |
| `Free` (fragmentation left after the warm-up `GC.Collect`) | 60 |
| Dictionaries and other `System.*` | 53 |
| `byte[]` (reference images, bytes of every written output in `_published`) | 50 |
| Red syntax trees (materialized by the identifier scan and by binding) | 35 |
| Source and constructed symbols | 24 |
| Metadata (PE) symbols | 23 |
| Generator driver state | 9 |
| Binders and caches | 8 |
| Other Roslyn | 22 |
| blaze-cs's own types | 1 |

The symbol graph (symbols, binders, caches: about 100 MB with their dictionaries) is rebuilt on
every `Emit` anyway (see "where its time goes" above); holding it between edits buys nothing but
costs nothing in time. The parse (green trees, 79 MB) is what makes the next edit cheap.

### GC settings tried

Each row: daemon restarted with the setting, 15 body edits in `MediaBrowser.Controller`, median
daemon build time of the last 10, RSS right after. Baseline repeated three times across the runs
(284 to 288 ms, 1444 to 1522 MB); differences under ~10 ms and ~100 MB are noise.

| Setting | Median build | RSS after | Verdict |
|---|---:|---:|---|
| baseline (server GC, 31 heaps) | 285 ms | 1444 to 1522 MB | |
| `DOTNET_GCHeapCount=4` | 292 ms | 1785 MB | worse |
| `DOTNET_GCHeapCount=8` | 306 ms | 2028 MB | worse |
| `DOTNET_GCDynamicAdaptationMode=1` (DATAS) | 303 ms | 1481 MB | no change |
| `MALLOC_ARENA_MAX=2` | 280 ms | 1539 MB | no change: not glibc arenas |
| `DOTNET_GCgen0size=32MB` | 277 ms | 1610 MB | no change |
| `DOTNET_GCConserveMemory=9` | 288 ms | 1320 MB | −200 MB, free |
| `DOTNET_GCHeapHardLimit=768MB` | 328 ms | 1038 MB | −480 MB for +15% time |
| `DOTNET_GCHeapHardLimit=768MB` + `GCgen0size=32MB` | 290 ms | 1208 MB | −300 MB, no time cost |

The hard-limit row is the diagnosis: with the GC held to 768 MB the daemon still builds in
328 ms, so about 500 MB of the baseline RSS is GC slack (freed regions kept committed, gen0
budget across 31 heaps), not data. Non-GC memory is the remainder, about 270 MB.

### Techniques, ranked

1. **Aggressive GC when idle.** `GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true)`
   (.NET 7+) compacts and decommits freed memory back to the OS. Run it a few seconds after a
   build finishes, off the critical path, the way `StaleAfterWarmUp` already collects after the
   warm-up. Expected idle RSS: live 460 MB + non-GC 270 MB, about 750 MB, growing during a build
   and shrinking again after. Zero build-time cost. Pair with `DOTNET_GCConserveMemory=9` in
   `StartDaemon`'s environment (−200 MB, measured free).
2. **Cap the heap after warm-up.** `AppContext.SetData("GCHeapHardLimit", bytes)` followed by
   `GC.RefreshMemoryLimit()` (.NET 8+) sets a hard limit at run time, so the daemon can set it to a
   multiple of the live heap it measures after warm-up (`GC.GetGCMemoryInfo().HeapSizeBytes`)
   instead of a fixed number. Measured: 768 MB on a 460 MB live heap cost +15% build time; with a
   smaller gen0 budget it cost nothing. A limit near 2× live is the safe setting to start from.
3. **Hash instead of bytes in `_published`.** `Holds` only ever compares the remembered bytes for
   equality, so a 32-byte hash (SHA-256, or `XxHash128`) does the same job. Saves the bytes of
   every output the daemon wrote: 18 MB of assemblies and PDBs plus 6 MB of reference assemblies
   on jellyfin, 30 to 40 MB with documentation files. `Image.Bytes` must stay: the
   `MetadataReference`s read from it.
4. **Drop the post-emit symbol graph.** Replace `_compilation` with a fresh
   `CSharpCompilation.Create` over the same trees and references after each emit; the next emit
   rebinds references (ReferenceManager) and rebuilds symbols, which it does anyway. Frees about
   100 MB. Needs a timing check: reference binding for a project with hundreds of references is
   tens of milliseconds, and the metadata symbol sharing between compilations
   (`AssemblyMetadata`'s cached symbols) may be lost. Try only after 1 to 3.
5. **Not worth it:** dropping the parse trees (Roslyn's IDE does this with recoverable trees that
   reparse on demand; 79 MB against a reparse of 19 MB of source on every edit), and GC heap
   count or gen0 size tuning (measured: no gain or worse).

Not measured here: the pre-started test hosts are separate processes with their own RSS, and
the daemon's RSS during a large `blaze-cs test --all`.

## Numbers used

| Measurement | Value | Source |
|---|---|---|
| Daemon, body edit in `MediaBrowser.Controller` | 320 to 430 ms (compile 280 to 350, scan ~10, write ~25) | project memory, 2026-11-06, after PR #24 |
| `blaze-cs build` body edit, wall | 0.34 s | README |
| `dotnet build` body edit, `RunAnalyzers=false` | 2.37 s; 3.01 s with analyzers | README |
| No-op `dotnet build`, jellyfin | 3956, 3315, 2697 ms (3 runs) | this note, 2026-11-06 |
| `SkipCompilerExecution` pass, jellyfin | 2703, 3016 ms | this note, 2026-11-06 |
| Test edit, 219 tests | ~700 ms wall (rebuild ~90, host run ~520) | project memory |
| Full test run, 17 projects | 44 s, 42 s of it `Jellyfin.Server.Integration.Tests` | project memory |

## Sources

- [Roslyn: Compiler Server.md](https://github.com/dotnet/roslyn/blob/main/docs/compilers/Compiler%20Server.md)
- [Roslyn: BuildServerConnection.cs](https://github.com/dotnet/roslyn/blob/main/src/Compilers/Shared/BuildServerConnection.cs)
- [Roslyn: BuildProtocol.cs](https://github.com/dotnet/roslyn/blob/main/src/Compilers/Shared/BuildProtocol.cs)
- [Roslyn: ManagedCompiler.cs (Csc task)](https://github.com/dotnet/roslyn/blob/main/src/Compilers/Core/MSBuildTask/ManagedCompiler.cs)
- [Roslyn: HotReloadService.cs](https://github.com/dotnet/roslyn/blob/main/src/Features/ExternalAccess/HotReload/Api/HotReloadService.cs)
- [Roslyn: EmitBaseline.cs](https://github.com/dotnet/roslyn/blob/main/src/Compilers/Core/Portable/Emit/EditAndContinue/EmitBaseline.cs)
- [Roslyn: SyntaxAndDeclarationManager.cs](https://github.com/dotnet/roslyn/blob/main/src/Compilers/CSharp/Portable/Compilation/SyntaxAndDeclarationManager.cs)
- [Roslyn IDE: DiagnosticIncrementalAnalyzer_GetDiagnosticsForSpan.cs](https://source.dot.net/Microsoft.CodeAnalysis.Features/Diagnostics/Service/EngineV2/DiagnosticIncrementalAnalyzer_GetDiagnosticsForSpan.cs.html)
- [API: Compilation.ReplaceSyntaxTree](https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.compilation.replacesyntaxtree)
- [API: Compilation.EmitDifference](https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.compilation.emitdifference)
- [API: CompilationWithAnalyzers.GetAnalyzerSemanticDiagnosticsAsync](https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.diagnostics.compilationwithanalyzers.getanalyzersemanticdiagnosticsasync)
- [API: MetadataUpdater.ApplyUpdate](https://learn.microsoft.com/en-us/dotnet/api/system.reflection.metadata.metadataupdater.applyupdate)
- [Docs: Microsoft.Testing.Platform hot reload](https://learn.microsoft.com/en-us/dotnet/core/testing/microsoft-testing-platform-hot-reload)
- [Docs: dotnet watch](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-watch)
- [Docs: supported code changes (C#)](https://learn.microsoft.com/en-us/visualstudio/debugger/supported-code-changes-csharp)
- [dotnet/runtime hot reload test project note on optimizations](https://github.com/radical/runtime/commit/93f407d231e31687decdc1f5b7f466af2fb94737)
- [NuGet: MonoMod.Core](https://www.nuget.org/packages/MonoMod.Core)
- Local SDK 10.0.403 (devbox): `sdk/10.0.403/Roslyn/bincore`, `sdk/10.0.403/Roslyn/Microsoft.CSharp.Core.targets`, `sdk/10.0.403/DotnetTools/dotnet-watch/10.0.403/tools/net10.0/any/`
