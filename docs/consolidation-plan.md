# CoreBridge consolidation plan

## Context
CoreBridge works, but it is still too basic to use day to day:
- VMs can't be given CPU or memory.
- Nothing records which components a VM or snapshot has, so nothing can answer "which VM can build this repo?".
- There are only 6 components.
- Settings come only from the shipped files plus one user file, and individual components and scripts carry no signature.
- Open GitHub issues: #5 (the vm-start timeout of 30 s is too short) and #4 (vm-create still uses the old image naming for profiles).

This machine is Linux with no Multipass, no pwsh and no gh, and about 1.1 GB of free disk. All checking runs against the simulator (`CoreBridge.Simulation`, `multipass-sim`, and `cb-sim` started as `DOTNET_ENVIRONMENT=Simulation dotnet run --project src/CoreBridge.Cli`). Once you confirm, I work through everything without asking further questions.

**Delivery:** a local branch `claude/consolidation` with one commit per work package (WP). Nothing is pushed. Your local, uncommitted changes (`global.json`, `Core.csproj`, the deleted `NuGet.config`, `CoreBridge.NoTui.slnf`) stay out of the commits.

**Signing with my own key "claude-vm" (`vm@cla.de`, Ed25519, unprotected):**
- **Storage:**
  - The secret key goes **only** to `~/.config/corebridge-claude-vm/claude-vm.sec.asc` (chmod 600). Never into the repo or a commit, and I don't echo it.
  - The armor arrived without line breaks, so I rebuild it and check it with BouncyCastle (`PgpFileSigner.ReadSigningKey`).
- **Trust model:**
  - claude-vm is **not** the CoreBridge key and does not go into `TrustedKeys.CoreBridge`.
  - It becomes a key the *user* trusts: a new trust folder `<StoreRoot>/trusted-keys/*.asc` (public keys only). Its signatures show as "signiert von claude-vm <vm@cla.de> (Benutzer-vertraut, Fingerprint …)", visibly different from "CoreBridge".
  - Without the trust entry the same signature shows "unbekannter Schlüssel" with the loud warning.
  - Only the public key goes into the repo, as a test fixture or documentation sample. The trust entry lives in my `artifacts/` (gitignored).
- **What I sign:** the new bundles and scripts in `src/CoreBridge.Cli/components/`, using the new `CoreBridge.Signer --clearsign`.
- **What stays as is:**
  - The shipped `appsettings*.json` still need the CoreBridge key. I leave them unchanged, so the checkout keeps starting.
  - For a shipped release, sign the bundles again yourself with the CoreBridge key (`sign-settings.ps1 -ClearSign` also covers bundles and scripts after WP3).
- **Note:** the key was in the chat, so treat it as a key for this VM only and don't trust it outside your test setup.

## WP0: Basis on Linux
- Create the branch.
- Run a baseline of `dotnet test CoreBridge.NoTui.slnf --filter "Category!=Integration"` and record which tests fail only on Linux (cmd.exe, hostname.exe, pwsh, Windows paths).
- `SimulatedBinaryMultipassCli.Locate` also finds `multipass-sim` without `.exe`. Mark Windows-only tests with a skip on non-Windows, so the fast run is green here.

## WP1: Issues #5 and #4
- **#5:** new `DefaultTimeoutVmStart`, with a code default of `3m` in `AppConfig`. This needs no change to the signed file. It is used by vm-start, `EnsureRunning`, the resize restart and the restore/snapshot restart. `-t` still overrides it. Also align the code default of `DefaultTimeout` (10 s) with appsettings (30 s) and the comments.
- **#4** (I can't read the issue text; this is my reading of the code):
  - vm-create gets `-p/--profile`. `-i/--image` stays a catalog image only; for compatibility it still accepts a profile name, with a note to use `--profile`.
  - A mistyped `--profile` gives an error listing the existing profiles, instead of a catalog lookup.
  - The output says "Profil: dev → Image: daily:26.10".
  - Update the `init-configure` hint, `cb-suite.ps1`, the lifecycle test, CLAUDE.md, and old comments ("Modell-").

## WP2: CPU, memory, disk
- `vm-create --cpus N --memory 4G` (plus the existing `--disk`) → `multipass launch --cpus/--memory`. Memory is validated with `MultipassSize`.
- Profile defaults: optional `Cpus`/`Memory`/`Disk` in `InitProfileConfig`. This stays schema v4, since the fields are optional. Set via `init-configure --cpus/--memory/--disk`. A CLI value beats the profile value.
- `vm-resize` gets `--cpus`/`--memory` alongside `-d`, using `multipass set local.<vm>.cpus|memory`. Same stop/restart logic; validation happens before stopping.
- Simulator: `SimVm.Cpus/Memory`; `launch` stores them; `info`/`get`/`set` support cpus/memory, with error texts modelled on the disk errors.

## WP3: Settings layering, signatures per component, bundles
- **Sources, in order:**
  1. shipped: `appsettings.json` + environment file + `local`
  2. shipped bundles: `<app>/components/*.component.json`
  3. user folder (`InitStore.Root`): `appsettings.user.json` **plus all other `*.json` / `*.component.json` there**
- Every component carries its origin: `ComponentOrigin(File, Signature status)`, a side table, not serialized. The same component name in two sources is an error naming both files.
- **Bundle** = a single (clearsignable) JSON file holding the definition plus the script inline (new field `ScriptContent`). One signature then covers both. `InitCloudInit` and `InitInstall` read `ScriptContent` before `Script`.
- **Script signatures:** a component that uses a `Script` path additionally needs `<script>.asc` / `.clearsigned.asc`. Otherwise the component counts as unsigned, even if its JSON is signed.
- **Trusted keys:** `TrustedKeys.CoreBridge` (embedded) plus the user trust folder `<StoreRoot>/trusted-keys/*.asc`. The latter applies **only to components and bundles**, never to the shipped `appsettings*.json`. Manage it with `key-trust -f <pub.asc>`, `key-list` and `key-untrust -k <fpr>`.
- **Status per component:**
  - signed by CoreBridge, or signed by a user-trusted key (signer, fingerprint, date)
  - unsigned
  - unknown key (fingerprint is shown)
  - invalid (content changed after signing)
- **Policy:** invalid → error (exit 3). Unsigned / unknown key → **loud warning** (a framed block on stderr, highlighted in red on a console).
- **Display:** shown on every use (`init-configure`, `vm-create` with a profile, `vm-install`), and as a signature column in `component-list`. `settings-verify` also checks the user folder, bundles and scripts.
- **New verbs/options:**
  - `component-bundle -n <c> [-o <file>]` turns a component into a bundle, ready to sign.
  - `component-add --bundle <file>` copies it unchanged into the user folder, so the signature stays intact.
- **Tooling:**
  - `CoreBridge.Signer --clearsign` (BouncyCastle).
  - `sign-settings.ps1` also covers bundles and scripts.
  - The trusted key becomes injectable in `InitComponentStore`.
  - A shared test-key helper is extracted from `PgpFileVerifierTests`.
- Remove the dead `Program.LoadUserConfig`.

## WP4: Component catalog
New shipped bundles in `src/CoreBridge.Cli/components/`, unsigned. Each script uses `set -Eeuo pipefail`, takes the install user as `$1`, checks its own postconditions, and writes a `.ready` marker.

| Bundle | What it installs |
|---|---|
| `node` | Node LTS via NodeSource |
| `docker` | docker.io + group |
| `rust` | rustup for the install user |
| `go` | Go |
| `java` | OpenJDK 21 + Maven + Gradle |
| `build-tools` | build-essential, cmake, pkg-config |
| `gh` | GitHub CLI |
| `kubectl` | snap |
| `powershell` | snap |
| `uv` | uv |

- New optional `Detect` field on the component (file patterns such as `*.csproj`, `package.json`). It is used by WP5.
- `ComponentScriptTests` renders every bundle. The simulator recognizes their markers.

## WP5: VM inventory and repo matching
- **Recording:** for each snapshot, the comment gets a machine-readable list: `corebridge: components=claude,dotnet,git; profile=dev`. Additionally a host-side inventory `<StoreRoot>/inventory.json` stores per VM: profile, components, CPU/memory/disk and created date, plus per snapshot: components.
- **Maintained by:** create, install, clone (inherits from the source), snapshot, restore (the state of that snapshot), delete --purge.
- **The guest markers are the truth:** `vm-inventory [--refresh]` reads `/var/lib/corebridge/*.ready` from running VMs and reconciles. Without `--refresh` it shows VMs, snapshots, components and resources.
- **`repo-match [-r <path>]`:**
  - Detects what the repo needs: built-in rules (dotnet: `*.csproj`/`*.sln*`/`global.json`; node; python; rust; go; java; git always) plus the `Detect` field of user components.
  - Ranks VMs and snapshots: full coverage first, then the fewest missing components and the smallest surplus.
  - For a full match: the VM/snapshot and the next command.
  - Otherwise a **migration path**, either:
    - `vm-clone <closest> -n <new>` → `vm-install -n <new> -c <missing>` → `vm-snapshot`, or
    - `init-configure` + `vm-create --profile`, when nothing usable exists.

## WP6: The big finale, `cb` in a repo with no options
Goal: start `cb` in a repo and land in the agent running in the matching VM, as easily as running `claude` or `opencode` locally.

**New verb `work [-r <path>] [--agent claude|opencode] [--vm <name>] [--yes]`.** With no arguments, `cb` runs `work` when the current directory is inside a git repo; otherwise it starts the TUI as before. This changes `cb.ps1` and `Program.Main`. Steps:

1. **Repo and agent:** find the repo root (as `repo-send` does). The agent comes from `--agent`, or from the repo (`CLAUDE.md` → claude, `AGENTS.md`/`opencode.json` → opencode), or from config `Work:DefaultAgent` (code default `claude`). The agent is added to the required components.
2. **Choose a VM (WP5 `repo-match`):**
   - Remember the choice per repo in the inventory (`repos: { <path>: <vm> }`), so the second call doesn't ask.
   - Full match → use it.
   - Otherwise show the migration path and run it after a yes (`--yes` skips the question): clone + `vm-install` + snapshot, or profile + create, with the CPU/memory defaults of the profile.
3. **Start** the VM if needed (`DefaultTimeoutVmStart`).
4. **Send the code:** `repo-send` (the git component is required and installed if missing). Then in the VM, a task branch `claude/<yyyyMMdd-HHmm>` is created from `main` in `~/work/<repo>`. This is the "task start" from to-do 7, so `claude/<id>` no longer has to be created by hand.
5. **Hand over to the agent:**
   - `multipass exec <vm> --working-directory ~/work/<repo> -- <agent>`, started as a child process **with the inherited console** (no redirection, unlike `MultipassCli`). Its exit code becomes cb's exit code.
   - Risk: CLAUDE.md says that on Windows `multipass exec` forwards no stdin. That is probably about redirected stdin, but it is unverified. Fallback, switchable in config: `multipass shell <vm>` plus a one-time autostart file `~/.corebridge/autostart` in the guest (cd + start the agent), picked up by a `.bashrc` hook that the git/agent component installs.
6. **After the agent exits:** show the task branch's commits and offer `repo-receive -i <id>` (yes → fast-forward main). On no, give the follow-up command.

**Abstraction:** new seam `IInteractiveRunner` (Core), with the real implementation in `MultipassCli` and a simulator implementation. `multipass-sim exec` with a console runs `SimShell` interactively (the `shell` code already exists), and the simulated agent `claude` is a small stand-in: it prints its banner and cwd, writes a commit file, and exits, so the whole round trip can be tested.

**Tests:**
- A lifecycle-like `WorkFlowTests` (simulator): empty inventory → migration → start → send → agent (stand-in) → receive.
- A second run picks the remembered VM without asking.
- I can't verify on Windows here, so the fallback switch and the open point "real `multipass exec` with a TTY" go into the final report.

## WP7: Usability pass with the simulator
- Play through the cb-suite flow (normal + edge) on Linux with cb-sim. Since pwsh is missing, I use a small bash driver in `$CLAUDE_JOB_DIR/tmp`, not in the repo.
- Then the new flows: profile with cpus/memory → create → inventory → repo-match on the CoreBridge repo itself (→ dotnet VM) and on a node repo (→ migration path).
- Fix what is unclear along the way: messages, missing hints, inconsistent options. Write the open issues down.
- Extend the simulated shell (if/for) only if a flow needs it.

## WP8: Documentation and wrap-up
- Update CLAUDE.md for: new verbs, bundles, signature status, inventory, timeouts.
- Final report with what stays open:
  - real lifecycle run on Windows
  - purge of the leftover VM `cbx-cli-vm-audio-record-auto-enter-80941f` (Stage00 of the next real run cleans it up)
  - real `multipass info` for the simulator
  - re-signing the new bundles with the CoreBridge key for a release
  - PR #3
- Not part of this plan: TUI wiring, the Stop hook `claude-checkpoint`, and task locking. Task start is covered by WP6. These are only a stretch goal if time is left. `tools/CoreBridge.VoiceEnter` doesn't exist here, so there is nothing to delete.

## Verification
- After every WP:
  - `dotnet build CoreBridge.NoTui.slnf`
  - `dotnet test CoreBridge.NoTui.slnf --filter "Category!=Integration"` (no new failures compared with the baseline)
  - new unit tests for each feature
  - a short cb-sim playthrough
- **Signatures:** tests with a throwaway key cover all four statuses (signed, unsigned, unknown key, invalid), including a changed script in a signed bundle.
- **End to end with cb-sim:**
  - `init-configure -n web -c node,git --cpus 2 --memory 4G`: with claude-vm trusted it shows "Benutzer-vertraut: claude-vm"; without the trust entry it shows the loud warning; with a changed script it ends with exit 3
  - `vm-create -n w1 --profile web` → `vm-inventory` → `repo-match -r .` → migration path is printed and works when executed
  - **Finale:** in a test repo (a copy of CoreBridge in `$CLAUDE_JOB_DIR/tmp`), `cb-sim` with no arguments runs migration → start → send → stand-in agent → receive offer; the second call goes straight to the agent.
- Watch the disk with `df` and delete `bin/obj` of test projects if needed.
