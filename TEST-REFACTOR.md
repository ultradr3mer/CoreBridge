# CoreBridge CLI-first refactor plan

## Objective

The public workflow must be the CLI shown in `README.md`. Users and integration tests configure CoreBridge VMs exclusively through CoreBridge commands; tests must not call `ImageService`, `VmService`, `MultipassCli`, or component installer scripts directly.

line 5

Canonical flow:

```powershell
cb multipass-status
cb image-configure --name mein-image --components claude,dotnet
cb vm-create --name meine-vm --image mein-image
cb vm-start --name meine-vm
cb vm-exec --name meine-vm --command 'echo hallo'
cb vm-connect --name meine-vm
cb vm-delete --name meine-vm
```

`vm-create --image mein-image` must create the correct base VM, attach generated VM-side configuration, and run configured host-side component installers automatically.

## Design decisions

- Add marker interface `IOptions` in the CLI project.
- Every options/verb class in `Verbs.cs` implements `IOptions`.
- `Program.Main` only loads configuration, parses arguments, and maps parser errors/help to exit codes.
- Add `public static int Program.Run(AppConfig config, IOptions o)` as the command dispatcher.
- `Run` creates/configures its services from `AppConfig`; it does no CommandLineParser work.
- Persist enough information in an image profile for `vm-create --image <name>` to resolve:
  - runtime base image (for example `core26`),
  - optional generated cloud-init,
  - configured `HostInstallScript` paths.
- A configured image name is a CoreBridge launch profile, not a raw name passed unchanged to Multipass.
- `vm-create` owns orchestration: launch VM, then run host installers. Tests never invoke `install-claude.ps1` themselves.
- Replace the direct-service `VmCycleTests` with process-level CLI tests in a CLI test project.
- Tests invoke the built CLI entrypoint (`dotnet CoreBridge.Cli.dll ...`) with the same verbs/arguments as `cb`; this avoids profile-dependent shell functions and nested `dotnet run` builds.
- Integration VMs use unique `cbx-` names and delete without `--purge`.

## Implementation checklist

### 1. Introduce the option abstraction

- [ ] Add `IOptions` marker interface.
- [ ] Make every class in `Verbs.cs` implement `IOptions`.
- [ ] Add a reflection/unit test asserting every registered verb options type implements `IOptions`.
- [ ] Preserve all existing verb names, short options, defaults, hidden verbs, and help text.

### 2. Decouple parsing from execution

- [ ] Refactor `Program.Main` to parse only.
- [ ] Add `public static int Run(AppConfig config, IOptions o)`.
- [ ] Replace the large `MapResult` dispatch with one pattern switch over `IOptions`.
- [ ] Remove static `Config` and `VmService` dependencies from command handlers.
- [ ] Initialize `ImageStore` from the `config` passed to `Run`.
- [ ] Preserve exit-code behavior:
  - help/version errors: 0,
  - parse errors: 1,
  - successful command: 0,
  - command/multipass failure: non-zero.
- [ ] Add direct unit tests for `Program.Run(config, options)` dispatch and error handling.

### 3. Make configured image names launchable profiles

- [ ] Add a persisted runtime image/profile field during `image-configure` (default from `Images:DefaultRuntimeImage`, initially `core26`).
- [ ] Persist or deterministically resolve cloud-init and host installer scripts from the selected components.
- [ ] Add an image-profile resolver used by `vm-create`.
- [ ] For `vm-create --image <configured-name>`:
  - resolve the profile,
  - launch its runtime image,
  - pass generated cloud-init when present,
  - run each configured `HostInstallScript` after successful launch.
- [ ] For a normal catalog image such as `--image core26`, preserve direct launch behavior.
- [ ] Define and test precedence for explicit `--cloud-init` versus profile cloud-init.
- [ ] Fail `vm-create` non-zero if a host installer fails; do not report a configured VM as successfully created.
- [ ] Make host installers use the already-built CLI entrypoint/`--no-build` path to avoid recursive builds.

### 4. Make lifecycle commands safe for the README sequence

- [ ] Make `vm-start` idempotent: return success with a clear message when the VM is already running.
- [ ] Keep `vm-stop`, `vm-delete`, and `vm-exec` behavior stable.
- [ ] Keep `vm-delete` defaulting to the paperbasket; tests must not pass `--purge`.
- [ ] Ensure image configuration and VM names in tests are unique enough for repeat runs without purge.

### 5. Replace direct-core integration tests with CLI tests

- [ ] Create `test/CoreBridge.Cli.Tests` and add it to `CoreBridge.slnx`.
- [ ] Build/reference the CLI so tests can locate `CoreBridge.Cli.dll` and its appsettings.
- [ ] Add a `CliRunner` test helper that starts the CLI as a child process and captures exit code, stdout, stderr, and timeout.
- [ ] Use a temporary `Images__StoreRoot` and inherited `CORE_BRIDGE_MULTIPASS_EXE` override.
- [ ] Replace the current direct `VmCycleTests` flow with ordered CLI tests that execute:
  - [ ] `multipass-status`
  - [ ] `image-configure --name <unique> --components claude,dotnet`
  - [ ] `vm-create --name <unique> --image <same-configured-name>`
  - [ ] `vm-start --name <unique>`
  - [ ] `vm-exec --name <unique> --command 'echo hallo'`
  - [ ] `vm-exec --name <unique> --command 'command -v claude && claude --version'`
  - [ ] `vm-connect --name <unique>` (see open decision below)
  - [ ] `vm-delete --name <unique>`
- [ ] Assert that `vm-create` output shows automatic host installation and that no test code references `install-claude.ps1`.
- [ ] Do not call `MultipassCli`, `VmService`, `ImageService`, or `MultipassBinaryLocator` from the CLI cycle tests.
- [ ] Skip cleanly when Multipass is unavailable, consistent with existing test conventions.

### 6. Documentation and final verification

- [ ] Update `README.md` so its quick-start exactly matches tested behavior.
- [ ] Document configured image names as CoreBridge profiles and automatic host installers.
- [ ] Update stale Snap/bootstrap wording without changing the new CLI contract.
- [ ] Run fast unit tests separately from the real CLI integration cycle.
- [ ] Run the CLI cycle only through `dotnet test`; never invoke `multipass` directly.
- [ ] Record any real VM timing/results in `EXPERIMENT_LOG.md` if this cycle is classified as an experiment.

## Acceptance criteria

- [ ] `Program.Run(AppConfig, IOptions)` is the only command-dispatch entrypoint after parsing.
- [ ] All CLI verb option classes implement `IOptions`.
- [ ] The README sequence works without manually running `bootstrap-agent-vm.ps1` or any component installer.
- [ ] `vm-create --image <configured-name>` performs all component configuration automatically.
- [ ] Integration tests call only the CLI for the documented lifecycle.
- [ ] Tests verify Claude is available without knowing its installer path.
- [ ] No purge is used by the CLI integration cycle.
- [ ] The user never has to configure the VM manually after `vm-create` returns successfully.

## Open decision: automated `vm-connect`

`vm-connect` is interactive and the current implementation delegates to `multipass shell`, which needs a real terminal/TTY. A normal redirected xUnit child process cannot reliably drive it as-is.

Choose one:

1. **Recommended:** Keep `vm-connect` as a real-terminal command; add a CLI process contract test (argument parsing/dispatch) and one manual smoke check, while all noninteractive README commands run in the automated cycle.
2. Add a hidden/noninteractive transport option for automated tests (for example an internal one-shot command mode), while preserving the public interactive command.
3. Replace/extend `vm-connect` with a testable PTY/SSH console bridge and automate the exact public command.