# AGENTS.md

## Agent skills

### Issue tracker

Issues are tracked in GitHub Issues (Igelkottegrodan/blaze-cs) via `gh`. See `docs/agents/issue-tracker.md`.

### Triage labels

Default vocabulary: `needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, `wontfix`. See `docs/agents/triage-labels.md`.

### Domain docs

Single-context: root `GLOSSARY.md` + `docs/adr/`. See `docs/agents/domain.md`.

## Running the .NET toolchain

Use `devbox run -- <command>` (e.g. `devbox run -- task check`): the machine has no SDK outside devbox, and the repo pins SDK 10 in `global.json`.

devbox also provides the .NET 11 SDK (`dotnet-sdk_11`), but both SDKs' `dotnet` collide in the profile, so plain `dotnet` is always 10, and SDK 11 alone has no runtime 10. To run the fixture on SDK 11 with net10.0 tests still on runtime 10, make a dotnet root that symlinks SDK 11's `sdk`, `packs` etc. and both SDKs' `shared/*` and `host/fxr` versions (store paths are in `devbox.lock`), copy SDK 11's `dotnet` muxer into it, then run with `DOTNET_ROOT=<root> PATH=<root>:$PATH` and a fixture `global.json` pinning SDK 11 (`BLAZE_CS_SDK`).

Known limits of that nix SDK 11 (rc.3), none caused by blaze-cs: `dotnet build` of a Razor project fails (StaticWebAssets cannot load System.IO.Pipelines 11), so the full `task check` cannot run on it; and blaze-cs falls back to `dotnet build` for it (`CopyFilesToOutputDirectory`/`_CreateAppHost` rewrite the assembly). Without Razor projects, `blaze-cs test` with no edits runs every test project in the warm host. The full check on SDK 11 runs in CI and through `task check:windows`.
