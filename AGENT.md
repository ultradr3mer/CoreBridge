# AGENT.md — CoreBridge Work Tracker

> This file is the actionable task tracker for CoreBridge.
> `AGENTS.md` remains the binding repository instruction file; this document does
> not replace its safety rules or the experiment protocol.

## Current target and source of truth

- Runtime target: **Classic Ubuntu 26.10 Edge**
- Default profile model:
  `ubuntu-classic-2610-amd64-dangerous.model`
- Default runtime image: `daily:26.10`
- `26.04` is only the tool/build VM image.
- Ubuntu Core measurements are historical evidence, not current defaults.
- Guest provisioning is intended to happen through generated cloud-init.
- The public workflow is the CLI; tests should not call installers or services
  directly for the VM lifecycle.
- Never invoke `multipass` directly from an agent shell. Use the wrapper or
  reproducible `dotnet test` workflows.
- Never purge arbitrary VMs. Experiment VMs use the `cbx-` prefix and remain in
  the Multipass trash unless a test explicitly handles the name/state.

## Status legend

- `[x]` completed or present in the current working tree
- `[~]` partially implemented or implemented but not verified
- `[ ]` open
- `[!]` blocker or decision required before dependent work

## P0 — blockers before feature work

### [ ] [!] Synchronize production component configuration

`src/CoreBridge.Cli/appsettings.json` still contains the old
`HostInstallScript` Claude definition and commented-out component definitions,
while `ComponentConfig` expects the Classic guest-script fields.

- [ ] Replace `HostInstallScript` with the active `CloudInitScript` fields.
- [ ] Define or intentionally remove `dotnet`, `opencode`, and `python`.
- [ ] Make the default `claude,dotnet` invocation valid.
- [ ] Remove stale Core-only Claude Snap configuration.
- [ ] Do not add a test that loads the real `appsettings.json`; use synthetic
      `AppConfig` objects in tests.

### [ ] [!] Fix the generated `runcmd` control flow

`ImageCloudInit` currently emits a vendor block containing `exit 0` inside the
shared `runcmd` script. With `claude,dotnet`, that can skip the later Claude
installation command.

- [ ] Replace the exit-based guard with a conditional/subshell.
- [ ] Ensure later component scripts always execute.
- [ ] Add a focused generator test for command ordering and absence of a
      script-terminating `exit 0`.

### [ ] [!] Complete the first Classic CLI lifecycle verification

The previous Classic lifecycle run ended in a Multipass daemon timeout and left
an interactive shell open. It is not a successful verification.

- [ ] Have the user inspect/close the stale interactive session.
- [ ] Re-run only through `dotnet test`.
- [ ] Record the result in `EXPERIMENT_LOG.md` if it is a new experiment.
- [ ] Ensure cleanup handles partially created profiles and VMs.
- [ ] Do not treat a successful launch alone as provisioning success.

### [ ] [!] Add a provisioning health contract

- [ ] Check `cloud-init status --wait --format json`.
- [ ] Reject crash/error/degraded states.
- [ ] Check the component success marker, for example
      `/var/lib/corebridge/claude.ready`.
- [ ] Check the installed component command/version.
- [ ] Check the expected Ubuntu release.
- [ ] Include relevant cloud-init logs in failure output.

## Component/cloud-init refactor

### [ ] [~] Keep the production dispatch boundary

Keep:

```csharp
Program.Run(AppConfig config, IOptions options)
```

`Main` owns configuration loading and parser invocation. `Run` owns typed
dispatch. This is a normal application boundary, not a test-only hook.

- [ ] Keep `IOptions` as the small registered-verb contract.
- [ ] Do not add a test-specific `Program` mode or test-only production branch.
- [ ] Remove duplicated `TestConfigs`/production-shaped configuration from
      tests after the production config is corrected.
- [ ] Use small synthetic `ImagesConfig` instances for focused unit tests.

### [ ] [~] Simplify `ComponentConfig`

Target shape for the active Classic path:

```csharp
public sealed record ComponentConfig
{
    public string? Script { get; init; }
    public string? InstallPath { get; init; }
    public string[] Packages { get; init; } = [];
    public bool PackageUpdate { get; init; }
    public bool PackageUpgrade { get; init; }
    public CloudInitUserConfig[] Users { get; init; } = [];
}
```

- [ ] Remove generic `Symlinks` from the active Classic model.
- [ ] Remove generic `ProfileExports` from the active Classic model.
- [ ] Remove `VendoredLibs` from the default Classic model.
- [ ] Keep legacy fields only if a supported profile still needs them.
- [ ] Move component-specific symlinks into the component script.
- [ ] Move persistent environment setup into a wrapper or an idempotent
      component-owned profile block.
- [ ] Keep `Script` and `InstallPath` as the primary component contract.

### [ ] [~] Aggregate component users

Component user definitions must be merged into one top-level cloud-init
`users` list.

- [ ] Validate required user fields.
- [ ] Deduplicate identical user definitions.
- [ ] Reject conflicting definitions for the same user name.
- [ ] Preserve the existing Multipass default user unless explicitly changed.
- [ ] Make the runtime user explicit and use it consistently in scripts and
      postconditions.
- [ ] Treat `NOPASSWD:ALL` as an explicit opt-in, never an implicit default.
- [ ] Do not put passwords, private keys, or API secrets in cloud-init.

### [ ] [~] Aggregate package policy

- [ ] Merge component package lists case-insensitively.
- [ ] Emit `package_update: true` only when requested.
- [ ] Default `package_upgrade` to false.
- [ ] Document that an upgrade is VM-wide, not package/component-local.
- [ ] Pin package versions where reproducibility requires it.

### [ ] [~] Make `ImageCloudInit` a safe renderer

It should only:

1. resolve selected components,
2. aggregate packages/users,
3. embed configured scripts,
4. render valid YAML.

It should not download models, create VMs, or execute guest commands.

- [ ] Use a YAML data model or safe serializer instead of raw interpolation.
- [ ] Quote package names, paths, user fields, and shell arguments correctly.
- [ ] Emit permissions as quoted octal strings.
- [ ] Normalize script line endings and indentation.
- [ ] Validate script paths and install paths.
- [ ] Add YAML parsing/schema tests.
- [ ] Keep component-specific behavior in the script.

## Claude guest installation

### [ ] [~] Choose and document the Claude update policy

- [ ] Choose `pinned`, `managed-stable`, or an explicit official apt policy.
- [ ] Do not use an unrecorded moving `latest` by accident.
- [ ] Record installed version and provenance.
- [ ] Decide whether native updates are allowed in managed profiles.
- [ ] Use `DISABLE_UPDATES=1` only after successful installation for a pinned
      runtime policy.
- [ ] Validate any installer signature/checksum before relying on it.

### [ ] [~] Harden `scripts/components/install-claude.sh`

- [ ] Keep it guest-side; do not restore the old host installer path.
- [ ] Install for the explicit runtime user.
- [ ] Create parent paths explicitly.
- [ ] Create the final launcher atomically.
- [ ] Check the actual installed version.
- [ ] Write the success marker only after all postconditions pass.
- [ ] Preserve useful stderr and return a meaningful failure status.
- [ ] Remove unrelated dashboard/CodeHero/password logic if present.

## Profile, model, runtime, and caching

### [ ] [~] Persist a reproducible profile

- [ ] Persist the model ID and SHA-256.
- [ ] Persist the model source revision or immutable URL where possible.
- [ ] Persist the resolved runtime image identity, not only `daily:26.10`.
- [ ] Persist component configuration/script fingerprints.
- [ ] Reject or clearly migrate old schema versions.
- [ ] Validate the persisted model digest when used.

### [ ] Implement model caching

`ImageService.Configure` currently downloads the model on every invocation.

- [ ] Reuse a valid local model by default.
- [ ] Add an explicit refresh/force path.
- [ ] Store URL, ETag/Last-Modified, digest, and fetch time.
- [ ] Write model/config files atomically.
- [ ] Clean up partial profiles after failed configuration.

### [ ] Decide the custom-image path

- [ ] Decide whether `image-build` remains public, hidden, or deprecated.
- [ ] If retained, make it include the same component semantics as profiles.
- [ ] Resolve the Windows `.img` import blocker.
- [ ] Do not use `purge` to recover a deleted builder name.
- [ ] Choose a new builder name when the old name is in the trash.

## Testing and experiments

### [ ] [~] Fast tests

- [ ] Remove duplicated test configuration after production config is fixed.
- [ ] Test `ComponentConfig` aggregation directly with synthetic config.
- [ ] Test user merging and conflicts.
- [ ] Test package aggregation and update flags.
- [ ] Test script embedding and safe paths.
- [ ] Test generated YAML parsing/schema.
- [ ] Test `runcmd` ordering and the `exit 0` regression.
- [ ] Keep parser/dispatch contract tests independent of the real config file.

### [ ] [~] CLI contract tests

- [ ] Keep help/version/parse-error exit behavior.
- [ ] Keep `vm-start` idempotency.
- [ ] Keep profile-name resolution and explicit cloud-init precedence.
- [ ] Keep `vm-connect` as a real-TTY command; do not fake interactive success.
- [ ] Add a manual TTY smoke-test checklist rather than a redirected
      `vm-connect` integration assertion.

### [ ] [~] Classic component validation

After the lifecycle is green:

- [ ] Claude installation and version.
- [ ] `claude doctor` if available.
- [ ] Dotnet runtime and NuGet restore.
- [ ] OpenCode.
- [ ] Python/pip.
- [ ] Symlinks and environment setup.
- [ ] Rerun/idempotency behavior.
- [ ] Failed component and degraded cloud-init behavior.

### [ ] [ ] New experiments require explicit approval

Potential experiments:

- [ ] Classic `daily:26.10` catalog/model/runtime resolution.
- [ ] Classic cold/warm launch latency.
- [ ] Full generated cloud-init and component postconditions.
- [ ] Classic network/SSH/lifecycle measurements.
- [ ] Custom-image HTTPS import workaround.
- [ ] Launch-name reuse from a `Deleted` VM.
- [ ] Snapshot/clone semantics if the template factory remains active.
- [ ] Record every approved run in `EXPERIMENT_LOG.md` and `experiment-data/`.

## Product roadmap

### [ ] Git/sync topology

- [ ] Per-VM local/bare repository design.
- [ ] Host-to-VM sync.
- [ ] VM-to-host sync.
- [ ] Diff/review flow.
- [ ] Commit creation and approval.
- [ ] No VM-initiated connection to the host.

### [ ] Autobridge

- [ ] Get-or-create VM/profile.
- [ ] Repository preparation and sync.
- [ ] Claude/OpenCode selection.
- [ ] Runtime/language selection.
- [ ] Agent launch and reconnect.
- [ ] Session/status loop.

### [ ] Secret handling

- [ ] Secret scanning before every sync.
- [ ] Environment password chain implementation.
- [ ] No credentials in config, cloud-init, logs, or generated scripts.
- [ ] Reviewed merge/return path to the host.

### [ ] TUI and installation

- [ ] Define TUI scope before implementation.
- [ ] Use Terminal.Gui v2 patterns and tests.
- [ ] Define installable `core-bridge` command/package.
- [ ] Define release/version strategy.

### [ ] Contracts

- [ ] Decide whether a global JSON output contract is required.
- [ ] Define stable exit-code semantics before publishing it.
- [ ] Define public behavior for VM-not-found, degraded cloud-init, and timeout.

## Documentation and repository hygiene

### [ ] Resolve missing/stale references

- [ ] Decide whether to restore root
      `COMMANDLINEPARSER_COMMANDLINE_README.md` or update references to `refs/`.
- [ ] Restore `PLAN_TEMPLATE_FACTORY.md` or remove references to it.
- [ ] Choose the canonical location for `CLOUD-INIT.md`.
- [ ] Remove obsolete Core-only sections from the active Classic document.
- [ ] Update README status and command examples.
- [ ] Update IMPLEMENTATION_PLAN.md with current Classic verification status.
- [ ] Update TEST-REFACTOR.md after the lifecycle succeeds.
- [ ] Add the missing Classic experiment results to EXPERIMENT_LOG.md.
- [ ] Classify or remove stale T12 raw-data files.

### [x] Remove or rewrite legacy markers

The stale literal markers were replaced with explicit legacy/historical status
notes or removed. The remaining work items are tracked in this document.

- [x] `src/CoreBridge.Cli/appsettings.json`: stale component-definition marker.
- [x] `src/CoreBridge.Core/Config/AppConfig.cs`: explanatory marker.
- [x] `src/CoreBridge.Cli/Verbs.cs`: rename marker.
- [x] `scripts/build-coreimage.ps1`: remove-file marker.
- [x] `scripts/bootstrap-agent-vm.ps1`: remove-file marker.
- [x] `PROJECT_MULTIPASS_BRIDGE.md`: historical shortcut/TUI markers.

### [ ] Clean stale artifacts

- [ ] Confirm and remove `test.zip` if it is disposable.
- [ ] Review stale Core profiles under `artifacts/`.
- [ ] Review stale `.iso`/`.img` artifacts.
- [ ] Decide whether `scripts/components/install-claude.ps1` is removed or kept
      as an explicitly unsupported legacy file.
- [ ] Decide whether `bootstrap-agent-vm.ps1` is removed or retained as a
      compatibility notice.
- [ ] Decide whether `build-coreimage.ps1` is removed or integrated into the
      supported CLI path.
- [ ] Never purge old `cbx-*` VMs merely to clean the working directory.

## Suggested execution order

1. Fix production component configuration.
2. Remove duplicated test-specific configuration.
3. Fix the `runcmd` `exit 0` bug.
4. Simplify `ComponentConfig` and user/package aggregation.
5. Move Symlinks/ProfileExports/VendoredLibs into component scripts.
6. Harden the Claude guest script and health marker.
7. Add synthetic renderer/config tests.
8. Run the fast test suite.
9. Obtain approval and run the Classic CLI lifecycle.
10. Record Classic measurements.
11. Add model/runtime caching and provenance.
12. Decide the custom-image path.
13. Implement Git/sync and Autobridge.
14. Consider TUI and template work only after the core workflow is stable.

## Definition of “Classic workflow ready”

- [ ] `cb image-configure` succeeds with the documented components.
- [ ] `cb vm-create --image <profile>` launches the stored Classic runtime.
- [ ] Cloud-init reports healthy completion.
- [ ] All selected components satisfy their postconditions.
- [ ] The CLI lifecycle test passes through public verbs only.
- [ ] Cleanup leaves no unintended VM/profile behind.
- [ ] No host installer is used for the active Classic path.
- [ ] No secrets appear in generated YAML or logs.
- [ ] The result is recorded reproducibly in the experiment documentation.
