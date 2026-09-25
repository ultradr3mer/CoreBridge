# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

CoreBridge is a Windows-hosted .NET 10 tool that drives **Canonical Multipass** to create and manage Ubuntu VMs, plus "Classic image profiles" that provision guests (Claude Code, .NET SDK, snaps) via generated cloud-init. Code comments, XML docs, CLI help texts and user-facing messages are written in **German** — keep that convention.

## Commands

SDK is pinned in `global.json` (10.0.401, latestPatch). Solution file is `CoreBridge.slnx`.

```powershell
dotnet build CoreBridge.slnx
dotnet test test/CoreBridge.Core.Tests                               # unit tests, no Multipass needed (mostly)
dotnet test test/CoreBridge.Core.Tests --filter "FullyQualifiedName~ImageTests"   # single class
dotnet test test/CoreBridge.Core.Tests --filter "FullyQualifiedName~ImageTests.SomeMethod"
dotnet test test/CoreBridge.Cli.Tests                                # full VM lifecycle (Category=Integration), needs Multipass
dotnet test --filter "Category!=Integration"                         # skip integration lifecycle
dotnet run --project src/CoreBridge.Cli -- vm-list                   # run a CLI verb
dotnet run --project src/CoreBridge.Tui                              # TUI app
```

Building `CoreBridge.Cli` runs an MSBuild target that executes `scripts/install-cb.ps1`, which (idempotently, between `# >>> CoreBridge cb >>>` markers) installs a `cb` function from `scripts/cb.ps1` into the user's PowerShell profiles. `cb` wraps `dotnet run --project <path>/src/CoreBridge.Cli` with a hard-coded path — that path points at the main checkout, not at worktrees.

CLI verbs (CommandLineParser): `multipass-status|restart` (restart asks to relaunch itself elevated via UAC; if the stop hangs it asks y/n before killing the service's `multipassd` PID; prompts go through the `confirm` callback of `Program.Run`, which defaults to "no"), `vm-list|create|start|stop|delete|exec|connect|put|get` (every VM verb first looks the VM up via `vm-list` and reports missing/trashed VMs with a ready message: `vm-create` asks before replacing an existing VM of the same name (stop + `delete --purge`), `vm-start`/`vm-stop` are no-ops when already running/stopped, `vm-delete` asks before deleting a running VM, `vm-exec|connect|put|get` offer to start a stopped VM; start/stop/delete ask "keep waiting?" after each `DefaultTimeout`; `vm-create` waits in slices of `DefaultTimeoutVmCreation` and asks "keep waiting?" after each; on "no" the daemon keeps launching in the background and the `prompt` callback reads a follow-up command that is parsed and dispatched like normal CLI args), `image-configure|list|delete`. Typical flow: `cb image-configure -n dev -c claude,dotnet` → `cb vm-create -n myvm -i dev`.

## Architecture

- **CoreBridge.Core** — all logic; no console I/O.
  - `Multipass/` (namespace `CoreBridge.Core.Multipass`): `MultipassCli` spawns `multipass.exe` and returns `MultipassRunResult` (exit code, stdout, stderr, duration); it is `virtual`/injectable for tests — `Run` waits once, `RunWithExtensions` waits in slices and asks a `keepWaiting` callback. `MultipassBinaryLocator` resolves the binary: env `CORE_BRIDGE_MULTIPASS_EXE` → `PATH` → `%ProgramFiles%\Multipass\bin\multipass.exe`. `MultipassService` backs `multipass-restart` (controls the Windows service via `sc` output parsing); its waits stream `Progress.ProgressTick` (`.`) lines, which `Main` prints on one line, and throw `MultipassServiceTimeoutException`.
  - `Vm/`: `VmService` wraps the verbs. `Doctor.Check` backs `multipass-status`. `VmSession` is the abstract scripted-session base (`SendLine`/`WaitFor`) with `VmConsoleSession` (arbitrary process, test hook), `VmExecSession` (each command is its own `multipass exec … /bin/sh -c`, the reliable path on Windows) — all three live in `VmConsoleSession.cs` — and `VmSshSession` (SSH.NET).
  - `Images/`: `ImageService.Configure` downloads the Canonical *dangerous* model (kept unmodified/signed), and writes a `<name>.profile.json` and cloud-init into `ImageStore`. `ImageCloudInit` renders cloud-init YAML from `Images:Components` (packages, users, snaps, and component shell scripts from `scripts/components/*.sh` embedded as files and run **in the guest only** — the host never executes them; each script takes the install user as `$1`, checks its own postconditions and touches `/var/lib/corebridge/<component>.ready`; shipped: `claude`, `dotnet`, `python` (3.14 from the archive, else via uv), `cline` (npm, Node ≥ 20 via NodeSource if needed), plus snap-only `opencode`; `ComponentScriptTests` renders them from the real `appsettings.json`); `CloudInitYamlValidator` checks it. `ImageProfileResolver` decides whether a `vm-create --image` value is a CoreBridge profile (only if a profile file exists in the store) or a plain Multipass catalog name, and maps profiles to their runtime image (default `daily:26.10`) + cloud-init.
  - `ImageStore` is static state: `Initialize(StoreRoot)` once at startup. Relative roots resolve against the repo root (the directory containing `CoreBridge.slnx`, found by walking up from the DLL); default is `<repo>/artifacts` (gitignored). Layout: `models/<name>/<name>.model|.profile.json|…`.
- **CoreBridge.Cli** — `Program.RegisteredOptionTypes` is the single list of verbs; `Program.Run(config, options, ct, confirm, prompt)` is the one public dispatcher (internally `Dispatch(CommandContext, IOptions)`, where `CommandContext` bundles config, `VmService`, callbacks and token) and returns `IAsyncEnumerable<string>` lines streamed as they are produced. Every expected failure leaves `Program.Run` as a single `ProgramExecutionException` whose message is ready to display and which carries the exit code: `Program.ToExecutionException` is the one place that translates `MultipassException`, JSON and I/O exceptions and cancellation (Ctrl+C → 130). `Main` only prints it; anything else is a bug and propagates. To add a verb: add an `[Verb]` options class implementing `IOptions` in `Options.cs`, register it in `RegisteredOptionTypes`, and add a case to `Dispatch`.
- **Config** (`AppConfig`, shared by CLI and TUI): `appsettings.json` → `appsettings.{DOTNET_ENVIRONMENT}.json` (defaults to `Development`, which sets `StoreRoot=artifacts`) → `appsettings.local.json` (gitignored) → env vars (`Images__StoreRoot`). Timeouts are top-level strings `DefaultTimeout` (restart / `sc`, vm-list/start/stop/delete/exec/put/get) and `DefaultTimeoutVmCreation`, parsed by `StringExtensions.ReadTime` (`10s`, `1000ms` only). Verbs deriving from `MultipassOptions` (`Options.cs`) take `-t/--timeout`, whose getter is private: only `Program.ApplyTimeoutOverride` (called from `Run` and for vm-create's follow-up command) reads it via `ITimeoutOverride` and replaces both config values for that call. Everything else reads only the config. No secrets in config; passwords come only from the `CORE_BRIDGE_[ALL|<NAME>]_PASSWORD` env chain.
- **CoreBridge.Tui** — Terminal.Gui **v2** scaffold (currently a template, not yet wired to Core). v2 is a full rewrite and v1-style code will not compile: read `src/CoreBridge.Tui/AGENTS.md` before touching UI code. Use the instance lifecycle `Application.Create().Run<T>().Dispose()`, not static `Init/Run/Shutdown`.

## Tests

- **CoreBridge.Core.Tests** — xUnit unit tests; Multipass calls are faked via `MultipassCli` subclasses / locator parameters.
- **CoreBridge.Cli.Tests** — one ordered integration lifecycle (`CliLifecycleTests`, `[Trait("Category","Integration")]`): methods are named `_0_…`, `_1_…` and sorted ordinally by `LifecycleTestCaseOrderer`; each stage depends on the previous one via the shared `CliLifecycleFixture`. It calls `Program.Run` in-process (no child CLI process), uses a temp `ImageStore`, creates real VMs/images named `cbx-cli-*`, and skips itself when Multipass is unavailable. Run the whole class, not individual stages.
- **Multipass.Experiments** — exploratory tests T01–T11 against real Multipass (`[MultipassFact]` skips when not installed). Each run writes raw JSON to `experiment-data/` and appends to `EXPERIMENT_LOG.md` at the repo root; the log is append-only by design.
- VMs created by tests use the `cbx-` prefix; `scripts/group-vms.ps1` groups them into a Hyper-V VM group (`-Remove` to undo).
