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
Studio C++ build tools on Windows.

## Try it

In a directory with one `.sln` or `.slnx` (or below one):

```text
blaze-cs build                  like dotnet build
blaze-cs test                   build, then run the test projects the edit affects
blaze-cs run [dotnet run args]  build, then dotnet run --no-build
blaze-cs stop                   stop this solution's daemon
```

From a build in a clone instead of the tool, run `/path/to/blaze-cs/blaze-cs build` (on Windows, `\path\to\blaze-cs\blaze-cs.cmd build`).

The first command starts a daemon for that solution and runs a real `dotnet build`, so it takes as long
as one. Then change a method and build again: the daemon, still running, rebuilds only what the edit can
affect, and `blaze-cs test` runs the affected test projects in test hosts that are already started.

The daemon exits on its own after two hours without a command, and the next command starts a new one.
After you rebuild blaze-cs, the next command stops the daemon of the old build and starts one of the new,
saying so. The daemon's log is `.blaze-cs/daemon.log` next to the solution, capped at 1 MiB.

## How fast

On jellyfin (41 projects, 17 xUnit v3 test projects), Linux x86-64 with 32 cores, mean of 8 runs per edit.
Each edit is in `MediaBrowser.Controller`, a project in the middle of the dependency graph:

| Edit | `dotnet build` | `dotnet build -p:RunAnalyzers=false` | `blaze-cs build` | Speed increase |
|---|---:|---:|---:|---:|
| Method body | 3.01 s | 2.37 s | **0.34 s** | **+597%** |
| Public signature (rename a parameter) | 10.30 s | 3.84 s | **0.64 s** | **+500%** |
| New file | 10.63 s | 3.90 s | **0.46 s** | **+748%** |

blaze-cs doesn't run analyzers, so the fair comparison is the middle column, and the speed increase is
against it: 6 to 8.5× faster. Against plain `dotnet build` it is +785%, +1509% and +2211%.

| Test loop | `dotnet test` | `blaze-cs test` | Speed increase |
|---|---:|---:|---:|
| Edit a test file, rerun its project (219 tests) | 4.54 s | **0.68 s** | **+568%** |

Speed increase is how much more work fits in the same time: `(old time / new time − 1) × 100`.

To reproduce, clone jellyfin into `bench/jellyfin`, then run `task bench` (timings) and
`task verify -- body|signature|file` (compares output with `dotnet build --no-incremental`). For
bitwarden/server, clone it into `bench/bitwarden` (its RustSdk project needs `cargo`) and run
`task verify -- bitwarden <edit>`, with the edits listed in `scripts/edit-bitwarden.cs`.

## How it works

A daemon keeps your solution compiled in memory, with Roslyn and MSBuild under it instead of a compiler of
its own:

- **Same output as `dotnet build`.** The daemon takes each project's exact compiler arguments from the
  binlog of that build and compiles with the Roslyn that ships with your SDK. It writes the same files
  to `obj/` and `bin/`, so your IDE, `dotnet run`, `dotnet test` and later `dotnet build`s keep working.
  On jellyfin, every one of its 41 assemblies comes out byte-identical to `dotnet build`'s; on
  bitwarden/server, 88 of its 89 (the 89th lists a source file twice, [#56](https://github.com/Igelkottegrodan/blaze-cs/issues/56)).
- **Only what changed.** It recompiles the projects whose sources changed. When a project's public API
  changes, it diffs the API member by member and recompiles only the dependents that use a changed
  member (or whose source generators' output changes). Changes that code can reach without naming them
  (interfaces, operators, `foreach`/`using` patterns, base types, …) recompile every dependent.
- **Started tests.** xUnit (v2 and v3), NUnit and MSTest assemblies run in-process in test hosts that the
  daemon starts ahead of time; each host runs one assembly and exits, and a new one takes its place. A
  `.runsettings` that sets only `RunConfiguration` `EnvironmentVariables` and `TestRunParameters` is
  applied in the host; projects with any other `.runsettings`, and anything a host can't run, go through
  `dotnet test --no-build`.
- **Falls back when unsure.** A changed `.csproj`, `.props`, `.targets`, `.editorconfig`, `global.json`
  or NuGet file, a new or deleted `.razor`/`.cshtml`, a changed scoped CSS file (`.razor.css`), or a
  project that an IL weaver (such as Fody) rewrites after compile, goes through a real `dotnet build`.
  Edits to existing `.razor` files, `_Imports.razor` included, compile in the daemon.

## Good to know

- **Linux and Windows** (10 1803+ / Server 2019+); CI checks both on the fixture solution. macOS is
  untested. jellyfin and the numbers above are Linux, and the benchmark scripts are Linux-only.
- **Warnings:** analyzers and diagnostic suppressors don't run, and warnings-as-errors are reported
  as warnings. Run `dotnet build` or CI for warnings.
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
  larger than 89 projects are untested. The host finds a `.runsettings` only in the test project's
  directory or a `RunSettingsFilePath` in its project file, not one set in a `Directory.Build.props` ([#51](https://github.com/Igelkottegrodan/blaze-cs/issues/51)).
- **Known bugs:** one edit that adds a member to a project and starts using it in a dependent can fail
  with no error shown, and leave a project that references both stale until `dotnet build`
  ([#57](https://github.com/Igelkottegrodan/blaze-cs/issues/57)). A project whose Compile items list a
  file twice fails to compile in the daemon ([#56](https://github.com/Igelkottegrodan/blaze-cs/issues/56)).
- **Custom build steps** (an `Exec` target, such as bitwarden's `cargo build` before compile) run only in
  `dotnet build`, not in the daemon's builds. Run `dotnet build` after changing what they read.
- **It trades memory for speed:** the daemon holds a few GB (about 2.3 GB for jellyfin).

## Contributing

`task test` runs the unit tests (`tests/BlazeCs.Tests`). `task check` runs the fixture check
(`tests/fixture/check.cs`); `BLAZE_CS=<path>` runs it with another blaze-cs command, which is how CI checks the installed tool (`task pack`, then `dotnet tool install --tool-path`). In devbox on WSL, `task check:windows` also runs the fixture check on native Windows, for SDK 10 and 11: it installs them into `%LOCALAPPDATA%\blaze-cs\dotnet`
on first use and leaves the system-wide SDK alone. On Linux, CI checks Windows.

| Path | What |
|---|---|
| `src/BlazeCs.Client` | the Native AOT client: sends build, test and stop to a running daemon, and hands everything else to `blaze-cs.dll` |
| `src/BlazeCs` | CLI client and daemon: binlog reading (`BuildLog`), per-project compiler (`ProjectCompiler`), scheduling and output (`SolutionBuilder`), API diff (`ApiSurface`), test runner |
| `src/BlazeCs.TestHost` | the single-use test host (xUnit v2 and v3, NUnit, MSTest) |
| `scripts/` | benchmark edits, timing, and output verification |
| `tests/BlazeCs.Tests` | xUnit v3 unit tests (`task test`, runs in CI): a table of API edits and which dependents each recompiles, and what counts as a dependent naming an identifier |
| `tests/fixture/` | a small solution and `check.cs` (`task check`), which makes each kind of edit, checks which projects blaze-cs rebuilt, and compares its output with `dotnet build --no-incremental` (runs in CI) |
| `docs/brand/outrun/` | the header above and `gen.cs`, which draws it (`dotnet run docs/brand/outrun/gen.cs`) |

## Credits

blaze-cs is influenced by **[blaze](https://github.com/indexable-inc/blaze)**, which does this for Rust
("type `blaze` wherever you'd type `cargo`"). blaze-cs borrows the idea, a warm daemon that holds the
project compiled and does minimal work per edit, and its README's layout and pixel-letter header. Thank you
also to the people who built:

- [Roslyn](https://github.com/dotnet/roslyn), the compiler blaze-cs drives
- [MSBuild](https://github.com/dotnet/msbuild) and [MSBuild Structured Log](https://github.com/KirillOsenkov/MSBuildStructuredLog), whose binlogs give each project's compiler arguments
- [xUnit](https://xunit.net), [NUnit](https://nunit.org) and [MSTest](https://github.com/microsoft/testfx), whose runners the test host loads

None of these projects endorses blaze-cs.
