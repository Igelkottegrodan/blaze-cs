# blaze-cs

> **Very experimental.** This is a spike: it has been tried on one real solution
> ([jellyfin](https://github.com/jellyfin/jellyfin)) on Linux, and nothing else. Expect rough edges,
> and keep `dotnet build` and your CI as the final word.

Faster rebuilds for C# solutions. Type `blaze-cs build` where you would type `dotnet build`: a daemon
keeps your solution compiled in memory, and after an edit it rebuilds only what the edit can affect.
`blaze-cs test` then runs the affected test projects in warm test hosts.

It is influenced by **[blaze](https://github.com/indexable-inc/blaze)**, which does this for Rust ("type
`blaze` wherever you'd type `cargo`"). blaze-cs borrows the idea, a warm daemon that holds the project
compiled and does minimal work per edit, and applies it to .NET on top of Roslyn and MSBuild instead
of writing a compiler.

## What it does

```text
blaze-cs build                  like dotnet build
blaze-cs test                   build, then run the test projects the edit affects
blaze-cs run [dotnet run args]  build, then dotnet run --no-build
blaze-cs stop                   stop this solution's daemon
```

Run it in a directory with one `.sln` or `.slnx` (or below one). The first command starts a daemon for
that solution and runs a real `dotnet build`, so it takes as long as one. Later commands talk to the warm
daemon:

- **Same output as `dotnet build`.** The daemon takes each project's exact compiler arguments from the
  binlog of that build and compiles with the Roslyn that ships with your SDK. It writes the same files
  to `obj/` and `bin/`, so your IDE, `dotnet run`, `dotnet test` and later `dotnet build`s keep working.
  On jellyfin, every one of its 41 assemblies comes out byte-identical to `dotnet build`'s.
- **Only what changed.** It recompiles the projects whose sources changed. When a project's public API
  changes, it diffs the API member by member and recompiles only the dependents that use a changed
  member (or whose source generators' output changes). Changes that code can reach without naming them
  (interfaces, operators, `foreach`/`using` patterns, base types, …) recompile every dependent.
- **Warm tests.** xUnit v3 test assemblies run in a small pool of long-lived test host processes, each
  run in a fresh collectible load context. Other test frameworks go through `dotnet test --no-build`.
- **Falls back when unsure.** A changed `.csproj`, `.props`, `.targets`, `.editorconfig`, `global.json`
  or NuGet file, or a project that an IL weaver (such as Fody) rewrites after compile, goes through a
  real `dotnet build`.

## Trying it

It needs the .NET 10 SDK. The repo pins one with [devbox](https://www.jetify.com/devbox):

```sh
git clone https://github.com/igelkottegrodan/blaze-cs && cd blaze-cs
devbox shell          # .NET SDK 10.0.403, hyperfine, fontconfig
dotnet build BlazeCs.slnx
cd /path/to/your/solution
/path/to/blaze-cs/blaze-cs build
```

## Numbers

On jellyfin (41 projects, 17 xUnit v3 test projects), Linux x86-64 with 32 cores, mean of 8 runs per edit.
Each edit is in `MediaBrowser.Controller`, a project in the middle of the dependency graph:

| Edit | `dotnet build` | `dotnet build -p:RunAnalyzers=false` | `blaze-cs build` |
|---|---:|---:|---:|
| Method body | 3.01 s | 2.37 s | **0.34 s** |
| Public signature (rename a parameter) | 10.30 s | 3.84 s | **0.64 s** |
| New file | 10.63 s | 3.90 s | **0.46 s** |

blaze-cs doesn't run analyzers, so the fair comparison is the middle column: 6 to 8.5× faster.

| Test loop | `dotnet test` | `blaze-cs test` |
|---|---:|---:|
| Edit a test file, rerun its project (219 tests) | 4.54 s | **1.04 s** |

To reproduce, clone jellyfin into `bench/jellyfin`, then run `scripts/bench.sh` (timings) and
`scripts/verify.sh body|signature|file` (compares output with `dotnet build --no-incremental`).

## Limitations

- **Warnings:** analyzers and diagnostic suppressors don't run, and warnings-as-errors are reported
  as warnings. Run `dotnet build` or CI for warnings.
- **Skipped dependents' PDBs:** a dependent the API diff skipped keeps its DLL. Its code is identical, but
  its PDB still records the old ID of the reference that changed, so its bytes differ from a fresh build
  until the next `dotnet build` recompiles it.
- **Platforms:** Linux only so far. The client uses a Unix socket and `setsid`.
- **Tested on:** one solution. Multi-targeting, Razor, NUnit and MSTest in the warm host, and large
  graphs are untested.
- **Memory:** the daemon holds a few GB (about 2.3 GB for jellyfin).

## Layout

| Path | What |
|---|---|
| `src/BlazeCs` | CLI client and daemon: binlog reading (`BuildLog`), per-project compiler (`ProjectCompiler`), scheduling and output (`SolutionBuilder`), API diff (`ApiSurface`), test runner |
| `src/BlazeCs.TestHost` | the warm xUnit v3 test host |
| `scripts/` | benchmark edits, timing, and output verification |
