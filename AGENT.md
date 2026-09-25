# AGENT.md — Coding Tasks

Only actionable coding work belongs here. `AGENTS.md` remains the binding
repository instruction file.

## Component configuration

- [ ] Replace the old `HostInstallScript` Claude configuration in
      `src/CoreBridge.Cli/appsettings.json`.
- [ ] Add the Classic `Script`, `InstallPath`, `Packages`, package flags,
      `Users`, and `InstallUser` configuration.
- [ ] Define the active Claude, Dotnet, OpenCode, and Python components.
- [ ] Rename `src/CoreBridge.Cli/Verbs.cs` to `Options.cs`.
- [ ] Update references after the rename.

## Component model

- [ ] Remove `Symlinks` from the active Classic `ComponentConfig`.
- [ ] Remove `ProfileExports` from the active Classic `ComponentConfig`.
- [ ] Remove `VendoredLibs` from the Classic `ComponentConfig`.
- [ ] Keep `Script` and `InstallPath` as the component installation contract.
- [ ] Move Dotnet symlink/environment setup into
      `scripts/components/install-dotnet.sh`.
- [ ] Move the Claude launcher setup into
      `scripts/components/install-claude.sh`.

## Cloud-init renderer

- [ ] Aggregate component packages.
- [ ] Aggregate component users into one top-level `users` section.
- [ ] Reject conflicting user definitions.
- [ ] Keep `package_update` and `package_upgrade` explicit.
- [ ] Default `package_upgrade` to `false`.
- [ ] Render Snap commands as argument lists.
- [ ] Embed component scripts through `write_files`.
- [ ] Render script execution through `runcmd`.
- [ ] Remove the shared `runcmd` `exit 0` bug.
- [ ] Validate script and install paths.
- [ ] Add YAML parsing/schema validation for generated cloud-init.

## Tests

- [ ] Remove duplicated `TestConfigs` production-shaped configuration.
- [ ] Keep `Program.Run(AppConfig, IOptions)` and `IOptions`.
- [ ] Use synthetic `ImagesConfig` objects in unit tests.
- [ ] Add tests for users, packages, scripts, Snaps, and `runcmd` ordering.
- [ ] Do not add a test that loads the real `appsettings.json`.
- [ ] Decide whether to split `CliLifecycleTests` into independent stages.
- [ ] Preserve deterministic ordering and cleanup when splitting the lifecycle.

## Image build

- [ ] Decide whether to keep, deprecate, or remove `image-build`.
- [ ] Keep `ImageService.Build`, `Program`, and `Verbs` consistent with that
      decision.
- [ ] Remove obsolete host bootstrap/image-build code after the decision.

## Cleanup

- [ ] Remove literal `TODO` markers from active source files.
- [ ] Replace obsolete markers with explicit status comments.
