<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/brand/outrun/header-dark.png">
    <img alt="blaze-cs" src="docs/brand/outrun/header-light.png" width="440" height="190">
  </picture>
</p>

<h3 align="center">Faster rebuilds for C# solutions.</h3>

<p align="center">Type <code>blaze-cs build</code> wherever you'd type <code>dotnet build</code>.<br>A warm daemon, and only the work your edit needs.</p>

<p align="center">
  <a href="#install">Install</a> ·
  <a href="#try-it">Try it</a> ·
  <a href="#how-fast">How fast</a> ·
  <a href="#how-it-works">How it works</a> ·
  <a href="#good-to-know">Good to know</a>
</p>

<p align="center"><b>Very experimental.</b> Tried on two real solutions (<a href="https://github.com/jellyfin/jellyfin">jellyfin</a> and <a href="https://github.com/bitwarden/server">bitwarden/server</a>), on Linux.<br>Keep <code>dotnet build</code> and your CI as the final word.</p>

## Install

It needs .NET SDK 10.0.100 or later (10 or 11) for the solution you build; older SDKs are refused with that message.
CI runs the fixture check on 10.0.100 (the oldest), the newest 10 and 11. blaze-cs is a .NET tool, not on nuget.org yet:
pack it from a clone and install it from the folder it was packed into.

```sh
git clone https://github.com/igelkottegrodan/blaze-cs && cd blaze-cs
dotnet pack src/BlazeCs -c Release          # artifacts/packages/blaze-cs.<version>.nupkg
dotnet tool install -g blaze-cs --add-source artifacts/packages
```

Then `blaze-cs build` works in any directory on Linux and Windows. CI packs the tool on every run and
uploads it as an artifact (`blaze-cs-tool-*`), which you can install from the same way. The package carries
the CLI, the daemon and the test host, and no Roslyn: the daemon uses the one in your solution's SDK.
Update with `dotnet tool update -g blaze-cs --add-source artifacts/packages`.

To work on blaze-cs itself, the repo pins SDK 10 and builds on it with [devbox](https://www.jetify.com/devbox):

```sh
devbox shell          # .NET SDK 10.0.403, go-task, hyperfine, fontconfig
task build
```

On Windows, build it with the .NET SDK: `dotnet build BlazeCs.slnx`.

The build also compiles the small `blaze-cs` client Native AOT, so a command that reaches a running daemon
starts in about 5 ms instead of 60. That needs a C toolchain: clang and zlib headers on Linux, the Visual
Studio C++ build tools on Windows. The `blaze-cs` and `blaze-cs.cmd` launchers in the clone use it; the
installed tool is the managed CLI alone and starts in about 60 ms.

## Try it

In a directory with one `.sln` or `.slnx` (or below one):

```text
blaze-cs build                  like dotnet build
blaze-cs build --all            build, then run every analyzer on every project and report all their warnings
blaze-cs test                   build, then run the test projects whose inputs changed
blaze-cs test --all             build, then run every test project
blaze-cs run [dotnet run args]  build, then dotnet run --no-build
blaze-cs stop                   stop this solution's daemon
```

From a build in a clone instead of the tool, run `/path/to/blaze-cs/blaze-cs build` (on Windows, `\path\to\blaze-cs\blaze-cs.cmd build`).

The first command starts a daemon for that solution and runs a real `dotnet build`, so it takes as long
as one. It returns once the daemon has loaded what that build did; the daemon then compiles the solution
once in memory, to check it matches the build byte for byte, and the next command waits for that. Then change a method and build again: the daemon, still running, rebuilds only what the edit can
affect, and `blaze-cs test` runs the test projects the edit changed in test hosts that are already started
and ready for them.

The daemon exits on its own after two hours without a command, and the next command starts a new one.
After you rebuild blaze-cs, the next command stops the daemon of the old build and starts one of the new,
saying so. The daemon's log is `.blaze-cs/daemon.log` next to the solution, capped at 1 MiB.

A few seconds after a command, when no other has started, the daemon runs a full collection that gives
the memory the build used back to the OS. It waits for the idle work before it (the first load's warm-up,
then the background analysis of compilation-end rules), so it gives back what that grew too; a command that arrives meanwhile waits only for that
collection (tens to hundreds of milliseconds). Each one logs a line of its own:

```
2026-11-06T15:35:02.0047935+01:00 blaze-cs: idle collection took 27 ms; heap 64 MB, committed 64 MB, working set 320 MB (was 400 MB)
```

`heap` is what the collection left, which after a full compacting collection is the daemon's live data.

## How fast

On jellyfin (41 projects, 17 xUnit v3 test projects), Linux x86-64 with 32 cores, mean of 8 runs per edit.
Each edit is in `MediaBrowser.Controller`, a project in the middle of the dependency graph:

| Edit | `dotnet build` | `dotnet build -p:RunAnalyzers=false` | `blaze-cs build` | Speed increase |
|---|---:|---:|---:|---:|
| Method body | 3.01 s | 2.37 s | **0.34 s** | **+597%** |
| Public signature (rename a parameter) | 10.30 s | 3.84 s | **0.64 s** | **+500%** |
| New file | 10.63 s | 3.90 s | **0.46 s** | **+748%** |

blaze-cs runs analyzers only on the files an edit touched (see [Good to know](#good-to-know)), not on
whole projects, so the fair comparison is the middle column, and the speed increase is against it: 6 to 8.5× faster. Against plain `dotnet build` it is +785%, +1509% and +2211%.

| Test loop | `dotnet test` | `blaze-cs test` | Speed increase |
|---|---:|---:|---:|
| Edit a test file, rerun its project (219 tests) | 4.54 s | **0.47 s** | **+868%** |

The test loop row is the median of 7 runs.

Speed increase is how much more work fits in the same time: `(old time / new time − 1) × 100`.

To reproduce, clone jellyfin into `bench/jellyfin`, then run `task bench` (timings) and
`task verify -- body|signature|file` (compares output with `dotnet build --no-incremental`). For
bitwarden/server, clone it into `bench/bitwarden` (its RustSdk project needs `cargo`) and run
`task verify -- bitwarden <edit>`, with the edits listed in `scripts/edit-bitwarden.cs`.

## How it works

A daemon keeps your solution compiled in memory, with Roslyn and MSBuild under it instead of a compiler of
its own:

- **Same output as `dotnet build`.** The daemon takes each project's exact compiler arguments from that
  build, through a small MSBuild logger of its own, and compiles with the Roslyn that ships with your
  SDK. It writes the same files to `obj/` and `bin/`, so your IDE, `dotnet run`, `dotnet test` and later
  `dotnet build`s keep working. On jellyfin, every one of its 41 assemblies comes out byte-identical to
  `dotnet build`'s; on bitwarden/server, 88 of its 89 (the 89th lists a source file twice, [#56](https://github.com/Igelkottegrodan/blaze-cs/issues/56)).
- **Only what changed.** It recompiles the projects whose sources changed. When a project's public API
  changes, it diffs the API member by member and recompiles only the dependents that use a changed
  member (or whose source generators' output changes). Changes that code can reach without naming them
  (interfaces, operators, `foreach`/`using` patterns, base types, …) recompile every dependent.
- **Started tests.** xUnit (v2 and v3), NUnit and MSTest assemblies run in-process in test hosts that the
  daemon starts ahead of time; each host runs one assembly. A host waits for each of the four
  test projects run most recently, with the project's NuGet packages loaded and its test framework
  already run once on a few tests of blaze-cs's own, so the framework's code is compiled before the
  build finishes. It loads none of the solution's outputs until the run, so a rebuilt reference is always
  the new bytes. Any other test project takes a spare host, one per runtime, already started. A
  `.runsettings` (the project's evaluated `RunSettingsFilePath`, wherever it is set, as for `dotnet test`)
  that sets only `RunConfiguration` `EnvironmentVariables` and `TestRunParameters` is applied in the
  host; projects with any other `.runsettings`, and anything a host can't run, go through
  `dotnet test --no-build`.
- **Hot reload into the test host.** A Debug test project's host stays up after its run. When the next
  edit is method bodies only, the daemon diffs it with the SDK's own Edit and Continue engine (the one
  `dotnet watch` uses), sends the host the metadata deltas, and the tests run again in the same process:
  `blaze-cs: Tests.dll: hot reloaded Lib (net10.0) (1 type) into its test host (pid …)`. Any other edit
  (a signature, a new member, a static field's initializer, a changed resource, package or `.runsettings`),
  a delta the host fails to apply, or a run after it that finds no tests ends that host and runs the project
  in a new one as before, which then stays up for the next edit; the output says so in one line:
  `blaze-cs: Tests.dll: not hot reloaded: <why>; running it in a new test host`. A run with nothing changed
  (`--all`, or a project whose last run failed) runs in a new host too. A Release project (compiled with
  optimizations), or an SDK whose engine blaze-cs doesn't know (such as 10.0.100's), runs in a new host
  each time; `.blaze-cs/daemon.log` says why. The assemblies on disk are still what `dotnet build` makes.
  Limits:
  - Static state carries over from one hot reloaded run to the next, which `dotnet test` starts fresh, so
    tests that depend on it can pass or fail where `dotnet test` doesn't ([#129](https://github.com/Igelkottegrodan/blaze-cs/issues/129)).
    That includes statics computed by code an edit changed: a `static readonly` field whose initializer
    calls an edited method, a `Lazy<T>` whose lambda was edited, a cache filled before the edit. They keep
    the value the old code computed. So a hot reloaded run's summary line ends in `(hot reloaded)`, and its
    pass doesn't count: the next `test` runs that project again in a new host, even with nothing changed.
  - A delta to a project output the tests hadn't loaded yet loads it from the daemon's copy in
    `.blaze-cs/hotreload/`, so its `Assembly.Location` is that copy, not the `bin/` file.
  - Not on Windows yet: a host that stays up would keep the project outputs it loaded locked.
- **Only the test classes a hot reload can reach.** After a hot reload, only the test classes that can run
  the updated code run: those whose code, through a chain of types each naming the next (in the
  solution's assemblies the tests run with; code that names a type's base class or interface there names
  the type too), uses an updated type, plus those whose last run failed and those that ran hot reloaded
  before (such a pass may rest on stale statics, see above). The others, whose last pass was in a new
  host, are listed as unchanged since their last run, with their result:
  `blaze-cs: Tests.dll: unchanged since last run: RuntimeTests (1 passed)`. Every test runs when code may
  reach the updated type without naming it (an interface or a base class, a type that implements or
  derives from one, a generic type, a type with a framework attribute), when a type on the chain
  implements an interface or derives from a class from outside the solution (a component, a handler a
  library scans for, an xUnit collection fixture) or carries a framework attribute, when code that runs
  around every test (a module initializer, an assembly fixture, assembly initialize) reaches it, or when the framework
  names a test class after no type (a generic test class); the daemon log says which. A test class that
  looks types up by name (`Type.GetType`, `Assembly.GetTypes`, `Activator.CreateInstance`) runs after
  every hot reload. A delta that reaches no test class of a project runs none of its tests: its result is
  the classes' passes in a new host, so it counts as such a pass (not hot reloaded), and as no run when
  the most recently run projects keep their hosts. `blaze-cs test --all` runs every test.
- **Only tests whose inputs changed.** `blaze-cs test` runs a test project when an assembly it runs with
  (its own, and every project output copied next to it), its `.deps.json` or its `.runsettings` differs
  from when it last ran in this daemon, when it hasn't run in this daemon yet, or when its last run
  failed. Assemblies are compared without their debug identity (PDB ID, timestamp, MVID), so a
  comment-only edit in a core library reruns nothing. The others are listed as unchanged since their last run, with their result.
  `blaze-cs test --all` runs every test project.
- **Analyzer warnings for what you edited.** Each build runs the project's analyzers (the ones `dotnet build`
  runs, from the same `/analyzer:` assemblies its generators come from) on the files the edit changed or
  added, while the project compiles, and prints their warnings as the compiler does. It honours the
  project's `.editorconfig` and global analyzer config severities and its diagnostic suppressors. After an
  edit inside one method's (or accessor's) body, analyzers that look at one member at a time analyze only
  that member again; their other warnings in the file are the ones their last analysis found, moved to
  where they now are. Analyzers that look across a type's members (CA2213 or IDISP002, from a type's
  symbol-end action) or at the whole file analyze the whole file again, as Visual Studio does for them.
  Analyzers never change the emitted bytes.
- **Compilation-end rules in the background.** Rules that need the whole project (an internal class never
  instantiated, unused members and the like; the analyzers mark them `CompilationEnd`) can't run on single
  files. A second after a command, with no other command started, the daemon runs them on each project
  compiled since they last reported on it (by the daemon, or by a fallback `dotnet build`, which prints
  errors only), one project at a time. The next `build` or `test` shows what they found after its own
  output, under a line naming the projects and when the run finished:
  ```text
  blaze-cs: background analysis (compilation-end rules) of Lib (net10.0), Lib (net8.0), finished at 16:17:51 in 25 ms: 2 warning(s)
  ```
  A command never waits for it: it cancels the run as it arrives, and the projects the run didn't finish
  run at the next idle, as do projects the command changed, whose results it drops. `blaze-cs build --all`
  runs every analyzer on every project's whole compilation at once and reports all their warnings, under
  a `blaze-cs: full analysis` line.
- **Falls back when unsure.** A changed `.csproj`, `.props`, `.targets`, `.editorconfig`, `global.json`
  or NuGet file, a new or deleted `.razor`/`.cshtml`, a changed scoped CSS file (`.razor.css`), or a
  project that an IL weaver (such as Fody) rewrites after compile, goes through a real `dotnet build`.
  Edits to existing `.razor` files, `_Imports.razor` included, compile in the daemon. After the real
  build, every project whose compiler arguments didn't change stays compiled in memory; only the others
  are loaded again, and only projects whose assembly that build changed are compiled once to check it.
- **`dotnet build` compiles through it too.** While the daemon runs, it serves the pipe the SDK's `Csc` task
  sends every compile to (the one the SDK's own `VBCSCompiler` listens on, named for your user and the SDK), so a
  plain `dotnet build`, `dotnet test`, `dotnet watch` or IDE build needs no property: each of the solution's
  projects compiles from the daemon's warm compilation, which writes what csc would. The daemon takes each
  project's compiler arguments from the request, so such a build keeps it current instead of leaving its cached
  arguments stale. Each such compile runs the project's analyzers and suppressors over the whole project,
  compilation-end rules included, as csc does, and reports what csc would; the background analysis then has
  nothing left to report on that project. Every other compile goes, byte for byte, to a `VBCSCompiler` of the same
  SDK that the daemon starts on a private pipe: another solution's (the pipe is the SDK's, not the solution's),
  and one of this solution's it can't answer as csc would (a warning csc makes an error: warnings as errors, an
  `error` severity in a config file; or any compile while a `blaze-cs` command runs). A `Csc` task from another
  Roslyn (compiler hash) is rejected and runs csc itself. The pipe has one server at a time: the daemon asks a
  running `VBCSCompiler` to shut down to take it, `dotnet build-server shutdown` makes the daemon give it up
  until its next command, and when the daemon stops the next build starts the SDK's server as usual. The private
  `VBCSCompiler` is shared by the daemons of every solution on that SDK: it exits after its own idle timeout, or
  on `dotnet build-server shutdown`.
  `BLAZE_CS_COMPILER_SERVER=0` in the environment the daemon starts in turns the pipe off.

## Good to know

- **Linux and Windows** (10 1803+ / Server 2019+); CI checks both on the fixture solution. macOS is
  untested. jellyfin and the numbers above are Linux, and the benchmark scripts are Linux-only.
- **Warnings:** a build reports the compiler's warnings for the projects it compiles, and the analyzers'
  for the files the edit touched only: not for the other files of those projects, nor for a dependent
  the edit changes without touching its files. After a body edit, the rest of the file reports what the
  member-at-a-time analyzers reported at its last analysis, even if an edit elsewhere (a signature in
  another file) has changed that since. An analyzer counts as member-at-a-time unless it registers a
  symbol-start or semantic-model action, as in Visual Studio; so one that reads the members' bodies from
  an action on the type (a symbol action on the type, or a node action on its declaration) keeps its
  warnings outside the edited member until the file's next analysis that isn't a body edit. (Counting those
  as whole-file too would rerun 73 to 130 of jellyfin's 504 analyzers on the whole file, mostly naming,
  design and documentation rules, and add 100 to 150 ms to a body edit.) A file's first edit after the daemon starts runs every analyzer
  on the whole file, which on a large file with many analyzers takes longer than the compile (jellyfin's
  3,100-line `BaseItem.cs`: that build takes 0.15 to 0.4 s longer; later body edits in it add 5 to 40 ms).
  An analyzer that reports in another file than the one it analyzes (a partial type's other part) is
  reported only when that file is analyzed. Compilation-end rules
  (unused members, unused parameters and the like) come from the background analysis, on the command
  after it finished, or from `blaze-cs build --all`, which reports every analyzer's warnings (the
  compiler's still only for the projects it compiles). Diagnostic suppressors don't silence compiler
  warnings.
  Warnings-as-errors, and rules set to `error`, are reported as warnings and don't fail the build. Run
  `dotnet build` or CI for every warning: a `dotnet build` through the daemon's pipe reports them all as csc
  does, analyzers on every file and errors failing the build.
- **Two solutions, one SDK:** their daemons share the SDK's pipe, and the one that ran a command last holds
  it. The other solution's `dotnet build` then compiles through the SDK's server, correctly, and leaves its
  daemon's arguments stale as without the pipe.
- **Skipped dependents' PDBs:** a dependent the API diff skipped keeps its DLL. Its code is identical, but
  its PDB still records the old ID of the reference that changed, so its bytes differ from a fresh build
  until the next `dotnet build` recompiles it.
- **Tested on:** two solutions. jellyfin: 41 projects, xUnit v3. bitwarden/server: 89 projects, an MVC
  app with `.cshtml` views, 37 xUnit v2 and 4 xUnit v3 test projects; method body, signature, new file,
  test and view edits all match `dotnet build --no-incremental` ([results](https://github.com/Igelkottegrodan/blaze-cs/issues/33#issuecomment-6680682555)).
  Multi-targeting is covered by the fixture check (`net8.0;net10.0`), not by a real solution.
  .NET 11 (RC3) is covered by the fixture check (`BLAZE_CS_SDK=<version> task check`) and by hand on a `net11.0` fixture. xUnit v2 in the test host, NUnit, MSTest and `.runsettings` are covered only by the fixture check (a few tests each).
  Blazor components (`.razor`) are covered only by the fixture check: a Razor class library (markup,
  `@code` API changes, `_Imports.razor`, new and deleted `.razor`, scoped CSS) and an xUnit project that
  renders one. MVC views (`.cshtml`) are covered by bitwarden/server's Admin app: a view edit, and a
  constant in a referenced project that only a view reads. Razor Pages, Blazor WebAssembly, other
  static web assets (`wwwroot/`, `.razor.js`), NUnit and MSTest on a real solution, and graphs much
  larger than 89 projects are untested.
- **Known bugs:** one edit that adds a member to a project and starts using it in a dependent can fail
  with no error shown, and leave a project that references both stale until `dotnet build`
  ([#57](https://github.com/Igelkottegrodan/blaze-cs/issues/57)). A project whose Compile items list a
  file twice fails to compile in the daemon ([#56](https://github.com/Igelkottegrodan/blaze-cs/issues/56)).
- **Custom build steps** (an `Exec` target, such as bitwarden's `cargo build` before compile) run only in
  `dotnet build`, not in the daemon's builds. Run `dotnet build` after changing what they read.
- **Test inputs are assemblies:** a test project's other files (data files copied to its output,
  environment variables, anything it reads at run time) don't make it run again; use `blaze-cs test --all`.
- **It trades memory for speed:** on jellyfin the daemon peaks at about 1.5 GB during builds and falls
  back to about 0.9 GB a few seconds after each, when the idle collection returns memory to the OS
  (1.6 GB before it did).

## Contributing

`task test` runs the unit tests (`tests/BlazeCs.Tests`). `task check` runs the fixture check
(`tests/fixture/check.cs`); `BLAZE_CS=<path>` runs it with another blaze-cs command, which is how CI checks the installed tool (`task pack`, then `dotnet tool install --tool-path`). In devbox on WSL, `task check:windows` also runs the fixture check on native Windows, for SDK 10 and 11: it installs them into `%LOCALAPPDATA%\blaze-cs\dotnet`
on first use and leaves the system-wide SDK alone. On Linux, CI checks Windows.

| Path | What |
|---|---|
| `src/BlazeCs.Client` | the Native AOT client: sends build, test and stop to a running daemon, and hands everything else to `blaze-cs.dll` |
| `src/BlazeCs` | CLI client and daemon: build log reading (`BuildLog`), per-project compiler (`ProjectCompiler`), scheduling and output (`SolutionBuilder`), API diff (`ApiSurface`), test runner, compiler-server pipe (`CompilerServer`, `CompilerServerProtocol`), hot reload through the SDK's Edit and Continue engine (`HotReload`), and the test classes a hot reload can reach (`TestSelection`) |
| `src/BlazeCs.Logger` | the MSBuild logger the daemon's `dotnet build` runs with: compiler command lines, copies, tasks after compile, each project's `RunSettingsFilePath` |
| `src/BlazeCs.TestHost` | the test host (xUnit v2 and v3, NUnit, MSTest), and its protocol with the daemon (`Protocol.cs`): one run, then, started with `DOTNET_MODIFIABLE_ASSEMBLIES=debug`, metadata deltas applied and the tests run again in the same process, all of them or those of the classes the daemon names |
| `scripts/` | benchmark edits, timing, and output verification |
| `tests/BlazeCs.Tests` | xUnit v3 unit tests (`task test`, runs in CI): a table of API edits and which dependents each recompiles, what counts as a dependent naming an identifier, the test host applying a delta (with `tests/TestHostKits`, each test framework's packages), and the compiler-server wire format against the SDK's own implementation |
| `tests/fixture/` | a small solution and `check.cs` (`task check`), which makes each kind of edit, checks which projects blaze-cs rebuilt, and compares its output with `dotnet build --no-incremental` (runs in CI) |
| `docs/brand/outrun/` | the header above and `gen.cs`, which draws it (`dotnet run docs/brand/outrun/gen.cs`) |

## Credits

blaze-cs is influenced by **[blaze](https://github.com/indexable-inc/blaze)**, which does this for Rust
("type `blaze` wherever you'd type `cargo`"). blaze-cs borrows the idea, a warm daemon that holds the
project compiled and does minimal work per edit, and its README's layout and pixel-letter header. Thank you
also to the people who built:

- [Roslyn](https://github.com/dotnet/roslyn), the compiler blaze-cs drives
- [MSBuild](https://github.com/dotnet/msbuild), whose build events give each project's compiler arguments
- [xUnit](https://xunit.net), [NUnit](https://nunit.org) and [MSTest](https://github.com/microsoft/testfx), whose runners the test host loads

None of these projects endorses blaze-cs.
