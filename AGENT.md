# AGENT.md — Coding Tasks

The checklist below records the completed refactor. `AGENTS.md` remains the
binding repository instruction file for future work.

## Component configuration

- [x] Replace the old `HostInstallScript` Claude configuration in
      `src/CoreBridge.Cli/appsettings.json`.
- [x] Add the Classic `Script`, `InstallPath`, `Packages`, package flags,
      `Users`, and `InstallUser` configuration.
- [x] Define the active Claude, Dotnet, OpenCode, and Python components.
- [x] Rename `src/CoreBridge.Cli/Verbs.cs` to `Options.cs`.
- [x] Update references after the rename.

## Component model

- [x] Remove `Symlinks` from the active Classic `ComponentConfig`.
- [x] Remove `ProfileExports` from the active Classic `ComponentConfig`.
- [x] Remove `VendoredLibs` from the Classic `ComponentConfig`.
- [x] Keep `Script` and `InstallPath` as the component installation contract.
- [x] Move Dotnet symlink/environment setup into
      `scripts/components/install-dotnet.sh`.
- [x] Move the Claude launcher setup into
      `scripts/components/install-claude.sh`.

## Cloud-init renderer

- [x] Aggregate component packages.
- [x] Aggregate component users into one top-level `users` section.
- [x] Reject conflicting user definitions.
- [x] Keep `package_update` and `package_upgrade` explicit.
- [x] Default `package_upgrade` to `false`.
- [x] Render Snap commands as argument lists.
- [x] Embed component scripts through `write_files`.
- [x] Render script execution through `runcmd`.
- [x] Remove the shared `runcmd` `exit 0` bug.
- [x] Validate script and install paths.
- [x] Add YAML parsing/schema validation for generated cloud-init.

## Tests

- [x] Remove duplicated `TestConfigs` production-shaped configuration.
- [x] Keep `Program.Run(AppConfig, IOptions)` and `IOptions`.
- [x] Use synthetic `ImagesConfig` objects in unit tests.
- [x] Add tests for users, packages, scripts, Snaps, and `runcmd` ordering.
- [x] Do not add a test that loads the real `appsettings.json`.
- [x] Split `CliLifecycleTests` into independent, deterministically ordered
      stages.
- [x] Preserve deterministic ordering and cleanup when splitting the lifecycle.

## Image build

- [x] Deprecate `image-build`; keep it only as a hidden, unsupported T11
      compatibility path.
- [x] Keep `ImageService.Build`, `Program`, and `Options.cs` consistent with
      that decision.
- [x] Remove the obsolete `bootstrap-agent-vm.ps1` and
      `build-coreimage.ps1` host scripts.

## Cleanup

- [x] Remove obsolete placeholder markers from active source files.
- [x] Replace obsolete markers with explicit status comments.
