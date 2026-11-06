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

devbox does not provide the .NET 11 SDK: its `bin/dotnet` would collide with SDK 10's when devbox installs the profile in a fresh checkout, and SDK 11 alone has no runtime 10. `devbox run -- task check:sdk11` runs the fixture check on SDK 11 anyway (`scripts/check-sdk11.sh`): it builds SDK 11 with `nix build` from a nixpkgs revision pinned in the script (moving to GA is a bump there), assembles one dotnet root under `~/.cache/blaze-cs/dotnet-sdk11` from symlinks to both SDKs' `sdk/<version>`, `shared/*/<version>` and `host/fxr/<version>`, plus SDK 11's `packs` etc. and a copy of its `dotnet` muxer, puts it first on `PATH`, and runs `check.cs` with `BLAZE_CS_SDK` set. The check passes there, Razor and `Tests11` included; compares SDK paths by real path, so the symlinks don't send it to `dotnet build`. The official SDK 11 (dotnet-install.sh into a private directory, plus runtime 10) passes too. Run it outside devbox and put an ICU the runtime knows on `LD_LIBRARY_PATH` (one of the nix `icu4c-76` store paths): runtime 10 doesn't find this machine's ICU 78. CI runs the check on SDK 10 and 11 on Linux and Windows; `task check:windows` runs both on native Windows.
