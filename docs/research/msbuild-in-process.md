# Hosting MSBuild in the daemon for project-file edits

Design note for [#92](https://github.com/Igelkottegrodan/blaze-cs/issues/92), 2026-11-06. Question: how
should the daemon host the SDK's MSBuild so that a `.csproj`, `.props` or `.targets` edit re-evaluates the
projects it reaches and recompiles them in memory, and when must it still run `dotnet build`?

Short answer: it works, and the time budget holds. A throwaway spike hosted the SDK's own MSBuild in a
process the way the daemon would, evaluated jellyfin's 41 projects and ran the design-time compile pass.
The compiler arguments it got back were identical, argument for argument and in order, to the ones the
daemon caches from `dotnet build`, for all 41 projects in all 5 runs. That needed two things the issue
didn't anticipate: the real build's global properties, and the targets before `Compile`. A `DefineConstants`
edit to `MediaBrowser.Controller.csproj` went from file change to new compiler arguments in **121 ms**
(median of 5). Today that edit costs **4.74 s** through `dotnet build`. NuGet's own restore spec, regenerated
in the same process, tells exactly when a restore is needed. It is byte-identical to the one in `obj/` unless
restore inputs changed, and costs about 250 ms. With the daemon's ~0.3 s compile of that project, the edit
lands around 0.7 s.

The costs are memory and process-wide state. 41 held evaluations are 48 MB of live heap, and 59 MB after
the passes have filled MSBuild's caches. A process of its own reached 290 MB RSS on workstation GC and
437 MB on server GC. MSBuild's in-process node changes the process's current directory and restores its
environment variables while it builds. Recommended: host it in the daemon, started on the first
project-file edit, behind the fallback rules below. Every rule is detectable from the evaluation or from the
pass itself.

## Numbers

A console app in the session scratchpad (not committed) referenced `Microsoft.Build.dll` and
`Microsoft.Build.Framework.dll` from the SDK directory (compile only, not copied) and
`Microsoft.Build.Locator.dll` from the SDK's `DotnetTools/dotnet-watch/10.0.403/tools/net10.0/any/`. It ran
the way the daemon runs: `dotnet exec --runtimeconfig <sdk>/Roslyn/bincore/csc.runtimeconfig.json`, with
the daemon's `DOTNET_gcServer=1`, `DOTNET_TieredPGO=0` and `DOTNET_TC_CallCountingDelayMs=0`. The machine
had 32 cores and SDK 10.0.403. jellyfin (41 projects) was restored and built by `blaze-cs build`. The timed
runs held `bench/.measure.lock`, but other agents were compiling at the same time (load average 19 to 35), so
single numbers vary by ±30%. Medians of 5 runs, or of 15 to 25 samples where a run repeats a step.

| Step | Time | Notes |
|---|---:|---|
| Register MSBuild (`MSBuildLocator.RegisterMSBuildPath`) | 4 ms | |
| `ProjectCollection` | 33 ms | |
| Evaluate one project, cold | 205 ms | JIT, SDK resolution, parsing ~150 imports |
| Evaluate the other 40, sequential | 604 ms | XML cache warm |
| Re-evaluate one project, warm | **16 ms** | new `EvaluationContext` |
| Re-evaluate all 41, warm, sequential | 469 ms | |
| Re-evaluate all 41, warm, `Parallel.ForEach` | 121 ms | evaluations are thread-safe in one collection |
| Design-time pass, one project, cold | 341 ms | |
| Re-evaluate + design-time pass, one project, warm | **152 ms** | 8 references asked `GetTargetFrameworks`/`GetTargetPath` |
| Design-time pass, all 41, first | 1,638 ms | one in-process node |
| Re-evaluate + design-time pass, all 41 | 1,745 ms | the research note's whole-solution pass through `dotnet build` was 2.7 to 3.0 s |
| Same, on 8 out-of-process nodes | 2,442 to 3,203 ms (3 runs) | slower: nodes start cold and evaluate again |
| **`.csproj` edit** (`DefineConstants` in `MediaBrowser.Controller`): reload, re-evaluate, pass | **121 ms** (re-evaluation 16 ms) | the new define in `/define:`, nothing else changed |
| **`Directory.Build.props` edit** (41 projects reached): reload, re-evaluate, pass | 1,856 ms (re-evaluation 454 ms) | |
| Restore check: regenerate the dgspec, one project, warm | 222 to 391 ms (unlocked, 5 runs) | 1,069 ms cold |
| New process to first arguments (start, register, evaluate, pass, exit) | 869 ms wall | 806 to 921 ms |
| **Today**: the same `.csproj` edit through `blaze-cs build` | **4,739 ms** | 4,463 to 5,457 ms. The edit falls back to `dotnet build`, which compiles only Controller; the daemon then reloads and checks it |
| Arguments identical to the daemon's cached ones | **41/41** | in each of the 5 runs |

Where a warm single-project pass goes (profile logger, 5 runs): 103 to 166 ms between the first and last
build event. 66 to 120 ms of that is `_GetProjectReferenceTargetFrameworkProperties`, which asks each of
Controller's 8 references for `GetTargetFrameworks` and so evaluates them under new global properties.
`ResolveAssemblyReference` takes 5 to 7 ms and `CoreCompile` (no csc) 2 ms.

Memory, measured in a mode that holds only the evaluations (heap is `GC.GetTotalMemory` after a full
collection):

| State | RSS, server GC | RSS, workstation GC | Live heap |
|---|---:|---:|---:|
| Runtime only | 29 MB | 29 MB | 0 |
| MSBuild registered, one project evaluated (cold) | 100 MB | | 19 MB |
| One design-time pass done (cold process) | 160 MB | | 50 MB (not yet collected) |
| Holding 41 evaluations (`Project`) | 193 MB | 145 MB | 48 MB |
| After 1, 2, 3 passes over all 41 | 392, 434, 437 MB | 280, 283, 292 MB | 58, 58, 58 MB |
| Holding 41 evaluations as `ProjectInstance` instead | | | 39 MB |

The daemon itself was at 1,367 MB RSS on jellyfin after load and warm-up (measured this session; the
research note's 1.4 to 1.5 GB).

## Hosting

### Locating MSBuild

The daemon already knows where MSBuild is. `SdkRoslyn.FindSdkDirectory` returns the SDK `dotnet build`
picks for the solution (`dotnet --version` under its `global.json`), and MSBuild lives at that SDK's root:
`MSBuild.dll`, `Microsoft.Build*.dll`, `NuGet.*.dll` and the SDK resolvers. A locator adds little, and the
spike shows what. `MSBuildLocator.RegisterMSBuildPath(sdkDirectory)` installed an
`AssemblyLoadContext.Default.Resolving` handler for that directory and set three environment variables:

```
MSBUILD_EXE_PATH=<sdk>/MSBuild.dll
MSBuildExtensionsPath=<sdk>
MSBuildSDKsPath=<sdk>/Sdks
```

SDK 10.0.403 ships `Microsoft.Build.Locator.dll` 1.10.2 in two places. One is next to dotnet-watch, the
other in `DotnetTools/dotnet-format/BuildHost-netcore/` (Roslyn's out-of-process project loader). It is not
a public part of the SDK, so a later SDK may move it or drop it. There are two ways to use it:

- Load it by path from the dotnet-watch folder, the way `SdkRoslyn` loads Roslyn from `Roslyn/bincore`. If it
  isn't there, fall back to `dotnet build` with a one-line reason. This is what the issue asks for.
- Do its job in `SdkRoslyn` itself: one more resolving handler for the SDK root, and the three variables.
  That is about 20 lines, and it doesn't depend on where the SDK keeps a copy of the locator.

The second has less that can go wrong. Either way the daemon compiles against `Microsoft.Build` and
`Microsoft.Build.Framework` with `ExcludeAssets="runtime"`, the way it references `Microsoft.CodeAnalysis.CSharp`
today. It uses a version no newer than the MSBuild in SDK 10.0.100, the oldest SDK the daemon supports.
The API used here (`Project.FromFile`, `EvaluationContext`, `BuildManager`) is old and stable. As with
Roslyn, no method that touches an MSBuild type may be JIT-compiled before registration. The spike keeps
those calls in a separate class behind a `NoInlining` method.

### Assembly loading next to the daemon's Roslyn

MSBuild and the daemon share the default load context. That is where `MSBuildLocator` puts MSBuild and
where `SdkRoslyn` already puts Roslyn. MSBuild isn't meant to load into a custom context: its task loader
decides by comparing against `AssemblyLoadContext.Default`. Three facts make sharing safe:

- **MSBuild doesn't load Roslyn into the default context.** The `Csc` task is in
  `Roslyn/Microsoft.Build.Tasks.CodeAnalysis.dll`. That assembly compiles in Roslyn's protocol code as shared
  source and references no `Microsoft.CodeAnalysis.dll`. Task assemblies outside MSBuild's own set load into
  an `MSBuildLoadContext` per path, so a package's task brings its own dependencies.
- **The SDK root has a second copy of Roslyn, and it is the same file.** On 10.0.403,
  `<sdk>/Microsoft.CodeAnalysis.dll` and `<sdk>/Microsoft.CodeAnalysis.CSharp.dll` are byte-identical to the
  ones in `Roslyn/bincore` (same SHA-1). Whichever resolving handler answers, the default context gets one
  Roslyn. The daemon should still register its `bincore` handler first, and warn if the two ever differ.
- **Same runtime.** `MSBuild.runtimeconfig.json` and `csc.runtimeconfig.json` both name
  `Microsoft.NETCore.App` 10.0.14.

What the default context gains is MSBuild's own dependencies from the SDK root: `Microsoft.Build.*`,
`Microsoft.NET.StringTools`, `NuGet.*`, `System.Reflection.MetadataLoadContext`,
`System.Configuration.ConfigurationManager` and `Microsoft.Extensions.*`. The daemon references none of them.
`AnalyzerLoader` shares only the compiler's and the framework's assemblies with generators, so none of these
reach generator contexts.

Not covered by the spike: the spike didn't load the daemon's Roslyn next to MSBuild. The first ticket below
runs both in the daemon on all four CI legs, which settles this.

### Process-wide state

The probe ran the pass while a second thread sampled the current directory and set an environment
variable.

- **Current directory: confirmed.** During one project's pass the process's current directory was, in turn,
  `MediaBrowser.Controller/` and each of its 8 references' directories. It was restored afterwards. The
  daemon serves one command at a time, but test host prestarts and the first load's warm-up run beside
  commands. The daemon must not resolve a relative path while a pass runs. Every process it starts must set
  `WorkingDirectory` explicitly; the test host start in `TestRunner` doesn't today.
- **Environment: confirmed.** An environment variable that another thread set during the pass was gone
  afterwards: the in-process node restores the environment it saved when the request started. One set
  before the pass survived. The daemon doesn't set environment variables after it starts, but this makes it
  a rule: never set one while a pass might run.
- **The locator's variables leak to child processes.** The daemon starts `dotnet build` (the fallback),
  `dotnet test --no-build` and test hosts. Suppose `global.json` changed since the daemon started. A child
  `dotnet build` that inherits `MSBuildSDKsPath` would then build with the old SDK's targets. `RunDotnet`
  and the test host start must remove `MSBUILD_EXE_PATH`, `MSBuildExtensionsPath` and `MSBuildSDKsPath`
  from the child's environment. Node reuse and the like go through `BuildParameters`, never through
  process environment variables, for the same reason. An inherited `MSBUILDDISABLENODEREUSE=1`, for example,
  would slow every fallback build.
- **Environment-derived properties.** Evaluation turns every environment variable into a property. The
  daemon's environment is the first command's shell plus three `DOTNET_*` GC and JIT variables, which is what
  its `dotnet build` children see today. So in-process evaluation sees the properties a fallback build would.
- **`DOTNET_HOST_PATH`.** `dotnet build` sets it for its MSBuild, and tasks that start `dotnet` read it (the
  `Csc` task when it runs csc, `RoslynCodeTaskFactory`). The pass never runs csc, and the arguments came back
  identical without it. For parity, the daemon should still set it, once at startup, to the `dotnet` that
  `DotnetHost()` finds.

### Nodes

The pass runs on MSBuild's in-process node only:
`BuildParameters { MaxNodeCount = 1, DisableInProcNode = false, EnableNodeReuse = false }`. The spike
counted child processes after every step, and there were none. On 8 out-of-process nodes the whole-solution
pass was slower (2.4 to 3.2 s against 1.3 to 1.9 s in the same session), because each node starts cold and
evaluates again. The nodes were still running when the pass returned. With node reuse they would outlive
the pass, holding memory and files. In-process only, then.

Evaluations, unlike builds, can run on parallel threads in one process. `Project.FromFile` with a shared
`EvaluationContext` re-evaluated all 41 projects in 121 ms in parallel, against 469 ms sequentially.

A task declared with `TaskFactory="TaskHostFactory"`, or for another runtime or architecture, starts an
`MSBuildTaskHost` process even with one node. None ran on jellyfin. The rules below send a project that
declares one to `dotnet build`.

### Lifetime and shutdown

- **One `ProjectCollection` per daemon.** It is created on the first project-file edit (see "Memory" for why
  then), with the design-time global properties below. It holds the parsed XML of every import
  (`ProjectRootElementCache`). That cache is why a re-evaluation takes 16 ms instead of 205.
- **One evaluation per unit, replaced on each edit.** Replacing means `ProjectCollection.UnloadProject`, then
  `Project.FromFile` with a new `EvaluationContext`. An evaluation context caches the file system, so a new
  edit needs a new context or the evaluation won't see the change.
- **The changed file must be reloaded explicitly.** The collection's XML cache doesn't check the disk. Before
  re-evaluating, the daemon calls `ProjectRootElement.Open(path, collection).Reload(throwIfUnsavedChanges:
  false)` for each changed project or import file. It knows which files changed from its MSBuild input
  stamps, so this is exact. In the spike, the re-evaluated `DefineConstants` had the new value only after
  the reload.
- **One `BuildManager` per pass**: `new BuildManager(name)`, `BeginBuild`, the requests, `EndBuild`,
  `Dispose`. Not `BuildManager.DefaultBuildManager`. After `EndBuild`, its `ConfigCache` still holds the
  last pass's `ProjectInstance`s and all their execution-time items, reachable from a static. A heap dump of
  the spike showed them rooted there.
- **Shutdown.** The daemon calls `ProjectCollection.Dispose()` when it exits. Nothing else outlives a pass:
  no nodes, no node reuse, no MSBuild server. MSBuild's process-wide caches live as long as the daemon: the
  `ResolveAssemblyReference` state, loaded task assemblies, and task parameter reflection
  (`ReflectableTaskPropertyInfo`, 13 MB in the dump). That is why hosting counts as long-lived memory once it
  starts.
- **Loaded task assemblies can't be unloaded or reloaded.** MSBuild loads a task assembly once per path, for
  the life of the process. Package tasks live in the NuGet cache by version and never change. A task the
  solution builds itself (`UsingTask AssemblyFile` inside the solution directory) would be stale after its
  project is rebuilt. On Windows the daemon would also hold the file open, so `dotnet build` couldn't
  overwrite it. A rule below sends such a project to `dotnet build`.

### In the daemon, or in a process of its own

The alternative is a small host process: `blaze-cs msbuild-host`, the daemon's own assembly in another mode,
which the daemon talks to over a pipe. Roslyn's tooling does exactly this
(`Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost`, shipped next to dotnet-format), for the reasons in
the last two sections. The interface would be narrow. In goes "these files changed". Out comes, for each
reached unit, its new arguments or the reason it can't have them.

| | In the daemon | Host process |
|---|---|---|
| First project-file edit, cold | 0.6 to 0.7 s in-process (register, evaluate, pass) | ~870 ms wall, measured |
| Later edits | 121 ms (`.csproj`) | the same, plus a round trip of ~1,000 strings |
| Memory | +60 MB live in the daemon's heap, kept until it exits | 290 to 440 MB RSS of its own, freed when it exits after being idle |
| Current directory, environment | shared with the daemon: rules needed | isolated |
| Task assembly locks on Windows | held until the daemon exits | held until the host exits, which it can do after each fallback |
| Code | one more loader like `SdkRoslyn`, and the rules | the same, plus a protocol and a second process to start, stop and supervise |

Recommendation: in the daemon, as the issue proposes. Each hazard is specific and has a rule: explicit
working directories, no environment changes during a pass, scrubbed environments for children, and
fallback for tasks the solution builds. The daemon already lives with the same kind of hazard for Roslyn
and generators. The host process is the plan B if Windows CI or real solutions show the hazards are worse
than they look. The slicing below keeps all MSBuild code behind one class, so moving it out of process later
changes that class's implementation only.

## Which projects an edit reaches

Each held evaluation's `Project.Imports` lists every file it imported (`ResolvedImport.ImportedProject.FullPath`),
including SDK and package files. It does not list the project file itself. The daemon keeps the reverse map,
from file to units, and adds each unit's project file to its own entry. On jellyfin:

| Imported files | Count | Reached units |
|---|---:|---|
| Per project | 118 to 168 | |
| Distinct, all projects | 330 | |
| `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props` | 3 | 41 each |
| `src/Directory.Build.props` | 1 | 10 |
| `tests/Directory.Build.props` | 1 | 17 |
| Project files | 41 | 1 each |
| `obj/*.csproj.nuget.g.props` and `.nuget.g.targets` (restore output) | 82 | 1 each |
| NuGet package `build/` and `buildTransitive/` files | 43 | 1 to 41 (`Microsoft.CodeAnalysis.BannedApiAnalyzers` 41, the test platform's 17) |
| SDK files | 159 | |

An edit reaches the units in its file's entry and nothing else, because a project's evaluation depends only
on its own imports. Two effects reach further, and both fall back instead of re-evaluating:

- **A file that isn't imported yet.** A new `Directory.Build.props` is in no entry, and neither is any file a
  wildcard or `GetDirectoryNameOfFileAbove` import would now find. A new or deleted `.props`, `.targets`,
  `.csproj` or `Directory.*` file falls back to `dotnet build`, as today.
- **An edit that changes what dependents see.** A dependent reads a referenced project only through the
  ProjectReference protocol (`GetTargetFrameworks`, `GetTargetPath`): its target frameworks, output path,
  reference assembly path and assembly name. An edit that changes any of these for a reached unit falls
  back. Any other change (a define, the nullable setting, a warning level) changes that unit's arguments
  only. The daemon's API diff then picks the dependents to recompile, as for a source edit.

Restore output is in the map too. Every project imports its own `obj/<project>.csproj.nuget.g.props` and
`.nuget.g.targets`, which change only when NuGet restores. A change there means someone restored outside the
daemon (see the rules).

The map also closes a blind spot the daemon has today. `MSBuildInputs` scans only the solution directory, so
a `Directory.Build.props` above it, as in a repository whose solution sits in a subfolder, isn't watched at
all. Every import outside the SDK and the NuGet package cache is an input, and the map lists them.

A `.csproj` edit doesn't need the map: the edited file is the project. So the first slice can evaluate only
the edited units and leave the map, and its 41 evaluations, to the `.props` slice.

## The design-time compile pass

### Request

For each reached unit, the daemon builds
`BuildRequestData(projectInstance, targets, null, BuildRequestDataFlags.ProvideProjectStateAfterBuild)` and
reads the `CscCommandLineArgs` items of `BuildResult.ProjectStateAfterBuild`, in order. Their
`EvaluatedInclude`s are csc's arguments, one per item, in the form the daemon already caches
(`CompileUnit.Arguments`).

The targets are the head of `CoreBuildDependsOn`, up to and including `Compile`, without the build event:

```
BuildOnlySettings;PrepareForBuild;ResolveReferences;PrepareResources;ResolveKeySource;Compile
```

`Compile` alone isn't enough for the daemon, though Roslyn's own design-time builds and the research note
use it. It skips `PrepareResources`, so every `/resource:` argument was missing. `Emby.Server.Implementations`
got 152 fewer arguments than in its real build, and three other projects one fewer each.
`BuildOnlySettings` sets `BuildingProject=true`, as in a real build.

The global properties are the real build's plus four:

| Property | Why |
|---|---|
| `SkipCompilerExecution=true` | the `Csc` task returns its arguments without running csc |
| `ProvideCommandLineArgs=true` | and puts them in `CscCommandLineArgs` |
| `BuildProjectReferences=false` | references are asked for `GetTargetPath`, not built (as with `--no-dependencies`) |
| `NonExistentFile=__NonExistentSubDir__\__NonExistentFile__` | makes `CoreCompile` out of date, so `Csc` runs even when the outputs are newer than the inputs. Visual Studio's design-time builds set the same property through `_ComputeNonExistentFileProperty` |

**The real build's global properties matter.** Evaluated without them, all 41 projects came back with
different arguments. When `dotnet build` builds a `.slnx`, it passes each project `Configuration=Debug`,
`Platform=AnyCPU`, `SolutionDir`, `SolutionPath`, `SolutionName`, `SolutionFileName` and `SolutionExt` as
global properties. jellyfin's `Directory.Build.props` reads `Configuration` before the SDK sets its default:
it sets `<AnalysisMode>AllEnabledByDefault</AnalysisMode>` when `'$(Configuration)' == 'Debug'`. So the
`/analyzerconfig:` and `/warnaserror-:` arguments differed. With those properties set, all 41 projects
matched. The spike hard-coded them, but the daemon must take them from the build. The logger already tracks
each `ProjectStartedEventArgs` and reads `TargetFramework` from its `GlobalProperties`. It should record all
of them into `CompileRecord`, and the daemon then evaluates each unit with exactly those plus the four
above. A multi-targeting project's units are its inner builds, each with its own `TargetFramework` global
property, so per-framework evaluation comes for free.

### Side effects

The pass runs the real build's targets up to `CoreCompile`, so it writes what they write:

- generated `AssemblyInfo.cs`, `GlobalUsings.g.cs` and `.AssemblyAttributes.cs`
- the generated `.editorconfig` and `CoreCompileInputs.cache`
- `.resources` files from `.resx`
- on test projects, the test platform's generated entry point

All of them are written only when their content changes. They are what csc reads, so writing them is
required, not a side effect to put up with: the daemon then compiles from the inputs `dotnet build` would
have used. The pass writes nothing csc writes (`SkipCompilerExecution`), copies nothing to `bin/` and runs no
build events.

### Mapping to the cached arguments

`CscCommandLineArgs` came back in the same order and spelling as the arguments the logger splits out of
`TaskCommandLineEventArgs`: 41 of 41 projects identical, 166 to 771 arguments each. So a pass result is a
`CompileUnit` with the unit's name, project file and directory, the new arguments, and the `SlowPathReason`
of its last real build. The daemon's existing code does the rest:

1. If the arguments didn't change (a comment, a property csc doesn't see), `ProjectCompiler.Matches(unit)` is
   true. The unit stays as it is and nothing compiles.
2. Otherwise a new `ProjectCompiler(unit, …)` replaces it, as `Load` does for a unit whose arguments a real
   build changed, and the build schedules it as changed. Dependents follow through the API diff.
3. The daemon rewrites `units.json` with the new arguments and reads the unit's `RunSettingsFilePath` from
   the new evaluation (`_runSettings`). It also refreshes the MSBuild input stamps, so the next build doesn't
   see the same edit again.

The second half of `Load` builds images, producers, dependencies and analyzer units. It gets factored out so
the in-process path can call it with the copies from the last real build and the units from the pass. A unit
whose `/out:` or `/refout:` changed falls back instead, because copies and image paths are keyed by those
paths.

## When the daemon still runs dotnet build

Each rule names what it catches, how the daemon detects it, and the one line it logs before running
`dotnet build`.

| Rule | Detection | Logged reason (example) |
|---|---|---|
| Restore needed | Regenerate NuGet's restore spec in-process: target `GenerateRestoreGraphFile` with `RestoreGraphOutputPath` set to a file under `.blaze-cs/` and `RestoreRecursive=false`. Compare it byte for byte with `obj/<project>.nuget.dgspec.json`, which the last restore wrote. In the spike it was identical for an unchanged project (4 runs) and after a `DefineConstants` edit, and differed after a `PackageReference` was added (`+ "Humanizer.Core": {`). This is NuGet's own no-op check, so it covers central package versions, framework references, project references, sources and target frameworks without a list kept by hand. 222 to 391 ms warm per edited project | `MediaBrowser.Controller: restore inputs changed (its NuGet restore spec differs)` |
| Restore ran outside the daemon | The stamp of a reached unit's `obj/project.assets.json`, `.nuget.g.props` or `.nuget.g.targets` changed since the daemon last loaded | `Emby.Naming: its restore output changed` |
| The pass needs a restore | The pass fails with `NETSDK1004` (no assets file), or with `NETSDK1005` or `NETSDK1047` (no target for the framework or runtime) | `<unit>: NETSDK1005: …` (the error's first line) |
| A custom target produces inputs | The pass logger sees a task run in a target defined in a file inside the solution directory, restore output excepted. A target there that only orders SDK targets runs no task of its own and is fine: jellyfin's `_ProduceContentAssetsSkippedByClean` (`Directory.Build.targets`, `BeforeTargets="CoreCompile"`) ran in every pass and only depends on `RunProduceContentAssets`. The real build's logger records the same thing for targets before `CoreCompile`, so a unit known to have one falls back before any pass | `Lib: target GenerateVersion (Directory.Build.targets) runs a task before compile` |
| Build events or `BeforeBuild` customized | The evaluation has a non-empty `PreBuildEvent`; or a `BeforeBuild` target, or a target whose `BeforeTargets`/`AfterTargets` names `Build`, `BeforeBuild` or `CoreBuild`, is defined outside the SDK. The pass skips these and the real build doesn't | `Lib: it has a pre-build step` |
| Tasks the solution builds, or out-of-process tasks | A `UsingTask` whose evaluated `AssemblyFile` is inside the solution directory, or that has `TaskFactory="TaskHostFactory"`, or a `Runtime` or `Architecture` other than the current one | `Lib: task MyTask comes from the solution` |
| Outputs or identity changed | Before and after the edit: `AssemblyName`, `TargetPath`, `TargetRefPath`, `ProduceReferenceAssembly`, `OutputType`, `IntermediateOutputPath`, `OutDir`, `TargetFramework(s)`; and the pass's `/out:` and `/refout:` | `Lib: its output path changed` |
| An edit the map doesn't cover | A new or deleted project file or import candidate (`*.props`, `*.targets`, `*.csproj`, `Directory.*`); a changed `.sln`/`.slnx`, `global.json`, `nuget.config` or `packages.lock.json` | as today: `<file> changed, running dotnet build` |
| SDK or package import edited | A changed import under the SDK directory or the NuGet package cache. Both are immutable in practice, short of a local feed or a hand edit | `<file> changed, running dotnet build` |
| Anything else | The pass fails for another reason or throws; a reached unit's arguments don't parse; a child process appears; MSBuild couldn't be loaded (registration failed, the locator is missing, a version mismatch). The daemon logs the failure's first line. If registration failed, the in-process path stays off for the daemon's lifetime | `in-process MSBuild: <first line>; running dotnet build` |

"Before" needs the evaluation from before the edit, and lazy hosting doesn't have one: the file has already
changed when MSBuild starts. The daemon keeps the text of every in-solution `.csproj`, `.props` and `.targets`
from its last load. That is a few hundred KB, and `MSBuildInputs` already lists the files. The "before"
evaluation comes from that text: `ProjectRootElement.Create` over a reader of the old text, with the file's
real path. The safety net below uses it too.

What no longer falls back: edits to properties and to `Compile`, `EmbeddedResource` and `AdditionalFiles`
items, in a project file or in a `.props`/`.targets` in the solution directory, as long as no rule above
fires. That covers `DefineConstants`, `Nullable`, `LangVersion`, `WarningLevel`, `NoWarn`,
`TreatWarningsAsErrors`, `AllowUnsafeBlocks`, `RootNamespace`, analyzer settings, and comments.

Two cases stay as they are, by choice. `.editorconfig` and `.globalconfig` edits could go through the pass,
but that is a separate change to how the daemon classifies inputs. New or deleted `.razor`/`.cshtml` files
(`StaleArguments`) are in the same position. Both are follow-ups.

### A safety net for unknown solutions

Everything above assumes the pass reproduces the real build's arguments. On jellyfin it does, but a
solution can do anything in a target (say, a define set only when `SkipCompilerExecution` isn't `true`).
The assumption can be checked per unit. The first time the in-process path reaches a unit, the daemon runs
the pass once more on the pre-edit evaluation, built from the saved text, and compares the result with the
arguments the last real build logged. A mismatch turns the in-process path off for that unit for the
daemon's lifetime. That edit falls back, and the daemon logs a warning naming the first argument that
differs. It costs one extra pass per unit per daemon (~150 ms warm).

## Memory

What hosting adds to the daemon, from the spike's numbers:

- **Live data: about 60 MB.** 48 MB for 41 held evaluations, 58 to 59 MB once passes have filled MSBuild's
  caches. It was flat across 3 whole-solution cycles and 20 `.csproj` edit cycles, so this is cache, not a
  leak. Holding `ProjectInstance`s instead of `Project`s brings the 48 down to 39. The daemon needs the
  imports list (`ProjectInstance.ImportPaths` has it) and the before/after properties, both of which a
  `ProjectInstance` provides.
- **Code and mapped assemblies: about 40 to 70 MB.** RSS went from 44 MB after registration to 100 MB after
  the first evaluation (19 MB of it heap), and to 160 MB after the first pass (50 MB heap, not yet collected).
- **Transient garbage.** A whole-solution pass on one node allocates 150 to 300 MB of garbage (heap at
  the end of a pass, before collection: 202 to 234 MB). Under server GC it stays in RSS as GC slack until
  the GC decommits it. That is the same slack the research note measured at ~500 MB for the daemon. Standalone,
  server GC took the process to 437 MB RSS for 59 MB of live data; workstation GC to 292 MB.

Expected daemon growth, then: about +100 to 150 MB for a `.csproj` edit (one evaluation, its 8 references'
evaluations inside the pass, MSBuild's code), and up to +400 MB right after a `.props` edit that reaches
every project. The second shrinks again once the idle GC of
[#80](https://github.com/Igelkottegrodan/blaze-cs/issues/80) runs. That is an estimate. The issue's
criterion asks for the real figure, so the first ticket measures daemon RSS before and after the first and
fifth project-file edit on jellyfin, and records it here.

Choices that follow from this:

- **Start MSBuild lazily**, on the first project-file edit. A daemon that never sees one pays nothing. The
  first edit pays a cold start on top of its own work: 0.6 to 0.7 s for MSBuild and the pass, and the
  restore check's first run (1.1 s cold, its evaluation included). So it lands around 2 s, against 4.7 s
  today. Later edits are warm.
- **Evaluate only what an edit needs.** A `.csproj` edit evaluates that project's units. Only an import edit
  needs the map, and so the 41 evaluations.
- **The heap cap of [#82](https://github.com/Igelkottegrodan/blaze-cs/issues/82) must leave room.** A cap set
  to about 2× the live heap after warm-up (~900 MB on jellyfin) has room for MSBuild's 60 MB, but not
  necessarily for a whole-solution pass's 300 MB of garbage at full speed. The cap should be computed after
  MSBuild is hosted, or raised when it starts.
- **Optional later**: dispose the `ProjectCollection` after some minutes without a project-file edit, and
  start cold again on the next one.

## Failure modes

| Failure | Effect | Handling |
|---|---|---|
| The pass gives arguments that differ from what `dotnet build` would use (a target conditioned on design-time properties, a global property the logger missed) | output not byte-identical to `dotnet build` | the safety net turns the unit off on first use; the fixture compares bytes with `dotnet build --no-incremental` for every case |
| A task in the pass hangs (a custom target waiting on a network or a lock) | the command hangs | a time limit on the pass: past it, `BuildManager.CancelAllSubmissions()`, then fall back. The rules already keep in-solution tasks out of the pass |
| A task in the pass crashes the process (stack overflow, native crash in a package task) | the daemon dies; the next command starts a new one with a real build | accepted: same as a crashing generator today. The host-process alternative is the cure if it happens in practice |
| The current directory or environment changes under the daemon while a pass runs | relative paths resolve wrong; an environment variable set during the pass is lost | daemon code uses full paths (it does today), processes it starts set `WorkingDirectory`, nothing sets environment variables after startup |
| The locator's variables reach children | a fallback `dotnet build` uses the old SDK's targets after a `global.json` change | removed from every child's environment |
| The locator is missing in a future SDK, or MSBuild's API changes | MSBuild can't load | registration fails once; the in-process path stays off for the daemon's lifetime, and project-file edits fall back as today with one line saying why |
| A task assembly the solution builds is loaded | the daemon holds a stale copy; on Windows `dotnet build` can't overwrite it | such projects fall back before any pass |
| Restore state is out of date | the pass resolves old packages, or fails with `NETSDK1004`/`NETSDK1005` | the dgspec check before the pass; the `NETSDK` errors fall back |
| Memory | the daemon grows by 100 to 400 MB | lazy start; idle GC (#80); heap cap computed with MSBuild in mind (#82) |
| Windows | file locks, path casing, `\` in `NonExistentFile` | CI runs every fixture case on Windows; the global property is the one Visual Studio uses there |

## Proposed slicing

Five tracer-bullet tickets under #92, in order. Each ends in a working `blaze-cs build` and its own fixture
cases. Two follow-ups sit outside #92's scope. Blocked-by edges in brackets.

### 1. A `.csproj` edit compiles in the daemon through an in-process design-time pass

Host the SDK's MSBuild in the daemon, behind one class: located from the SDK directory `SdkRoslyn` already
finds, started on the first project-file edit, and registered before any MSBuild type is JIT-compiled. The
logger records each compile unit's global properties. When every changed MSBuild input is a project file of
held units, the daemon does the following:

1. Re-evaluates those units with their recorded global properties plus the four design-time ones.
2. Runs the restore check (dgspec).
3. Runs the design-time pass.
4. Hands the new arguments to the same machinery `Load` uses, then builds as for a source edit.

Every other changed input, and any failure, falls back as today with a one-line reason. Children get the
locator's environment variables removed, and test host starts set `WorkingDirectory`.

Acceptance criteria:
- [ ] Fixture: the existing "project file edit" case (a comment in `Lib.csproj`) no longer runs `dotnet build`, and every unit stays kept.
- [ ] Fixture: adding a `DefineConstants` value to `Lib.csproj` (with an `#if` that changes a public constant) recompiles `Lib` and the dependents the API diff selects, without running `dotnet build`, byte-identical to `dotnet build --no-incremental`.
- [ ] Fixture: adding a `PackageReference` to `Lib.csproj` falls back to `dotnet build` with a one-line reason naming the project and "restore", then matches `dotnet build --no-incremental`.
- [ ] jellyfin: a `DefineConstants` edit to `MediaBrowser.Controller.csproj` completes in under 1 s wall (median of ≥ 5, second edit onwards; the first edit's time is recorded too), measured under the bench lock against main's path.
- [ ] Daemon RSS on jellyfin before, after the first, and after the fifth project-file edit, recorded in this note's "Memory" section.
- [ ] `task test` and `task check` pass on Linux and Windows CI, SDK 10 and 11.

### 2. The daemon checks the pass against the real build before trusting it for a unit [blocked by 1]

Keep the text of every in-solution `.csproj`, `.props` and `.targets` from the last load. The first time a
unit is reached, run the pass on its pre-edit evaluation, built from that text, and compare the result with
its logged arguments. On a mismatch, turn the unit off for the daemon's lifetime and warn once with the
first differing argument. The same pre-edit evaluation feeds the before/after rules from then on.

Acceptance criteria:
- [ ] Fixture: a project whose `.props` adds a define only when `'$(SkipCompilerExecution)' != 'true'` gets an edit that falls back with a warning naming that project and the `/define:` argument. A second edit to it falls back without a second pass.
- [ ] Fixture: every case of ticket 1 still recompiles in the daemon, each unit checked once.
- [ ] jellyfin: the extra time on the first edit of `MediaBrowser.Controller.csproj` is recorded.
- [ ] `task test` and `task check` pass on Linux and Windows CI.

### 3. A `.props` or `.targets` edit re-evaluates the projects that import it [blocked by 1, 2]

Evaluate every unit (on parallel threads) when an import changes, keep the import map, and run the pass for
the units the edited file reaches. Units whose arguments didn't change stay kept. Imports outside the
solution directory, other than the SDK's and the NuGet cache's, become MSBuild inputs. A new or deleted
import candidate falls back.

Acceptance criteria:
- [ ] Fixture: a property edit in the fixture's `Directory.Build.props` that changes the arguments of some units (for example `<Deterministic>` or a define) recompiles exactly those units in the daemon, byte-identical to `dotnet build --no-incremental`.
- [ ] Fixture: a comment-only edit to `Directory.Build.props` builds nothing ("up to date") and runs no `dotnet build`.
- [ ] Fixture: a new `Directory.Build.props` in a project folder falls back with a reason naming the file.
- [ ] Fixture: a `.props` edit in a directory above the solution, imported by it, is noticed (today it isn't).
- [ ] jellyfin: the time of a `Directory.Build.props` property edit is recorded, against main's `dotnet build` path.
- [ ] `task test` and `task check` pass on Linux and Windows CI.

### 4. Build steps the pass can't stand in for fall back with a reason [blocked by 1]

These are the rules for custom targets that run tasks before compile, a customized pre-build step or
`BeforeBuild`, tasks the solution builds, task hosts, output or identity changes, restore output changed
outside the daemon, and `NETSDK1004`/`NETSDK1005`/`NETSDK1047` from the pass.

Acceptance criteria:
- [ ] Fixture: one case per rule. Each edit logs its one-line reason, runs `dotnet build`, and then matches `dotnet build --no-incremental`.
- [ ] Fixture: a target in `Directory.Build.targets` that only orders SDK targets (like jellyfin's `_ProduceContentAssetsSkippedByClean`) does not cause a fallback.
- [ ] jellyfin: the `MediaBrowser.Controller.csproj` edit still recompiles in the daemon.
- [ ] `task test` and `task check` pass on Linux and Windows CI.

### 5. A multi-targeting project's edit recompiles each target framework in the daemon [blocked by 1]

Each inner-build unit is evaluated with its recorded `TargetFramework`. A `TargetFrameworks` change goes
through the restore check, so it falls back.

Acceptance criteria:
- [ ] Fixture: a `DefineConstants` edit in the `net8.0;net10.0` project recompiles both units in the daemon, byte-identical to `dotnet build --no-incremental`.
- [ ] Fixture: adding a framework to `TargetFrameworks` falls back with the restore reason.
- [ ] `task test` and `task check` pass on Linux and Windows CI.

### Follow-ups outside #92

- `.editorconfig`/`.globalconfig` edits and new or deleted `.razor`/`.cshtml` files (`StaleArguments`) through
  the pass instead of `dotnet build`.
- Replace the `dotnet build --no-incremental` reload that "cached compiler arguments are stale" triggers
  (an IDE build changed a project's arguments) with a pass for that unit. This overlaps with
  [#89](https://github.com/Igelkottegrodan/blaze-cs/issues/89), which would hand the daemon those arguments
  through the compiler-server pipe; whichever lands second should reuse the first.

## Decisions for a human

1. **In the daemon or in a host process.** Recommended: in the daemon (the hazards are listed with their
   rules above). Choose the host process if Windows file locks or a crashing package task are judged likely
   on the solutions blaze-cs targets.
2. **The locator: by path from the SDK's dotnet-watch folder, or its job done in `SdkRoslyn`.** Recommended:
   in `SdkRoslyn`. It is about 20 lines, and the locator isn't a public part of the SDK.
3. **Lazy or eager start.** Recommended: lazy (the first project-file edit pays ~1.5 s of cold start, once). Eager, after the
   warm-up, makes the first edit fast too, but costs every daemon ~100 MB.
4. **The safety net's cost.** One extra pass per unit per daemon, ~150 ms on its first edit. Recommended:
   keep it. It is the only check that the pass agrees with `dotnet build` on a solution nobody has tried.

## Sources

- Spike: a scratch console app, not committed. It hosted MSBuild through `MSBuildLocator.RegisterMSBuildPath`,
  built with `Project.FromFile`/`ProjectCollection`/`BuildManager`, read the `CscCommandLineArgs` items,
  diffed them with `.blaze-cs/units.json`, and took a heap dump with `dotnet-dump` (`dumpheap -stat`, `gcroot`).
- Local SDK 10.0.403 (devbox): `Microsoft.Common.CurrentVersion.targets` (`CoreBuildDependsOn`,
  `_ComputeNonExistentFileProperty`), `Roslyn/Microsoft.CSharp.Core.targets` (`CoreCompile`,
  `SkipCompilerExecution`, `ProvideCommandLineArgs`, `CscCommandLineArgs`),
  `DotnetTools/dotnet-watch/10.0.403/tools/net10.0/any/` and `DotnetTools/dotnet-format/BuildHost-netcore/`
  (`Microsoft.Build.Locator.dll`), `MSBuild.runtimeconfig.json`, `Roslyn/bincore/csc.runtimeconfig.json`.
- Research note `docs/research/roslyn-and-msbuild-hooks.md` (branch `t3/research-roslyn-compiler-hooks`),
  section 4 and the memory section.
- [MSBuild: Microsoft.Build.Locator](https://github.com/microsoft/MSBuildLocator)
- [Roslyn: Workspaces.MSBuild BuildHost](https://github.com/dotnet/roslyn/tree/main/src/Workspaces/MSBuild/BuildHost)
