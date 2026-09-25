# CoreBridge CLI-first refactor plan

## Objective

The public workflow is the CLI. Tests configure and exercise VMs only through
CoreBridge commands; they do not call `ImageService`, `VmService`,
`MultipassCli`, or installer scripts directly.

The active target is Classic Ubuntu 26.10 Edge:

- model: `ubuntu-classic-2610-amd64-dangerous.model`
- runtime: `daily:26.10`
- `26.04` is only the deprecated/unsupported image-build tool VM

Canonical flow:

```powershell
cb multipass-status
cb image-configure --name mein-image --components claude,dotnet
cb vm-create --name meine-vm --image mein-image
cb vm-start --name meine-vm
cb vm-exec --name meine-vm --command 'echo hallo'
cb vm-exec --name meine-vm --command 'command -v claude && claude --version'
cb vm-connect --name meine-vm
cb vm-delete --name meine-vm
```

`image-configure` creates a launch profile. `vm-create --image <profile>`
resolves its persisted runtime and generated cloud-init, launches the runtime,
and lets cloud-init perform all guest-side installation. There are no
host-side component installers.

## Implementation checklist

### 1. Options and dispatch

- [x] Add the `IOptions` marker interface.
- [x] Make every options/verb class in `Options.cs` implement `IOptions`.
- [x] Centralize registered option types and test the invariant.
- [x] Keep `Program.Main` limited to configuration, parsing, and parser-error
  mapping.
- [x] Make `public static int Program.Run(AppConfig config, IOptions options)`
  the only command dispatcher after parsing.
- [x] Dispatch with one pattern switch and create services from the passed
  configuration.
- [x] Preserve help/version, parse-error, success, and command-error exit codes.
- [x] Make `vm-start` idempotent for an already-running VM.

### 2. Configured image profiles

- [x] Persist `RuntimeImage` (`daily:26.10`) in the image build config.
- [x] Generate and persist the cloud-init filename.
- [x] Resolve a configured image name in `vm-create`; never pass the profile
  name unchanged to Multipass.
- [x] Keep direct catalog/URL/path launches unchanged.
- [x] Give explicit `--cloud-init` precedence over profile cloud-init.
- [x] Validate the Classic model identity and persist its model ID/SHA-256.
- [x] Embed component guest scripts in cloud-init; do not execute them from
  the host.
- [x] Parse and validate the generated cloud-init subset before persisting it.

### 3. Process-level CLI tests

- [x] Add `test/CoreBridge.Cli.Tests` to `CoreBridge.slnx`.
- [x] Reference the built CLI and locate its DLL/appsettings beside the test
  assembly.
- [x] Add a `CliRunner` that starts `dotnet CoreBridge.Cli.dll`, captures
  stdout/stderr/exit code, and enforces timeouts.
- [x] Run child processes with `DOTNET_ENVIRONMENT=Development`; lifecycle
  runs override `Images:StoreRoot` with a unique temporary directory.
- [x] Use unique `cbx-cli-*` names and delete without `--purge`.
- [x] Replace the direct `VmCycleTests` service calls with deterministically
  ordered CLI lifecycle stages and authoritative fixture cleanup.
- [x] Skip cleanly when `multipass-status` reports no Multipass installation.
- [x] Test the public `vm-connect` contract separately because it requires a
  real TTY.

> Der erste echte Classic-Lifecycle-Lauf wurde am 25.09.2026 gestartet, aber
> `vm-create` lief in einen Multipass-Daemon-Timeout. Ein alter interaktiver
> `multipass shell cbx-cycle-a`-Client blieb dabei offen. Der Testlauf ist
> deshalb noch nicht als erfolgreich verifiziert; der Nutzer möchte den
> Multipass-Zustand zunächst selbst prüfen.
### 4. Documentation and verification

- [x] Update README/AGENTS/plan documentation for Classic 26.10 Edge and
  cloud-init-only guest provisioning.
- [x] Run fast unit/contract tests separately from the real CLI integration
  stages.
- [x] Run the real cycle only through `dotnet test`; never invoke
  `multipass` directly from the agent shell.
- [ ] Record additional Classic-Edge experiment results in
  `EXPERIMENT_LOG.md` if a run is classified as a new experiment.

## Acceptance criteria

- [x] `Program.Run(AppConfig, IOptions)` is the only post-parse dispatch
  entrypoint.
- [x] All registered verb option classes implement `IOptions`.
- [x] The README sequence works without a host installer script.
- [x] `vm-create --image <configured-name>` performs guest configuration via
  cloud-init.
- [x] CLI lifecycle stages call only CLI verbs, use a unique store root, and
  verify exact-name cleanup without purge.
- [x] Tests verify Claude is available without knowing its guest-script path.
- [x] No purge is used by the CLI integration cycle.

## `vm-connect` decision

`vm-connect` remains an interactive real-terminal command (`multipass shell`).
The redirected process test verifies its help/argument contract; a manual
TTY smoke check remains the correct verification for the interactive shell.
