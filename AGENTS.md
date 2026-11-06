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

devbox also provides the .NET 11 SDK (`dotnet-sdk_11`), but both SDKs' `dotnet` collide in the profile, so plain `dotnet` is always 10, and SDK 11 alone has no runtime 10. To run the fixture on SDK 11 with net10.0 tests still on runtime 10, make a dotnet root that symlinks SDK 11's `packs`, `sdk-manifests` etc., both SDKs' `sdk/<version>`, `shared/*/<version>` and `host/fxr/<version>` directories (the unwrapped store paths are behind `share/dotnet` of the `devbox.lock` ones; the repo's own `global.json` still needs SDK 10), and a copy of SDK 11's `dotnet` muxer. Then, in a script run with `devbox run -- bash <script>` (devbox puts its own `dotnet` first on PATH), export `DOTNET_ROOT=<root> PATH=<root>:$PATH` and run `BLAZE_CS_SDK=<SDK 11 version> dotnet run tests/fixture/check.cs` after `task build`.

The full fixture check passes on that root, Razor and the `Tests11` case included. blaze-cs compares SDK paths by real path, so the symlinks don't send it to `dotnet build`. The official SDK 11 (dotnet-install.sh into a private directory, plus runtime 10) passes too. Run it outside devbox and put an ICU the runtime knows on `LD_LIBRARY_PATH` (one of the nix `icu4c-76` store paths): runtime 10 doesn't find this machine's ICU 78. CI runs the check on SDK 10 and 11 on Linux and Windows; `task check:windows` runs both on native Windows.
