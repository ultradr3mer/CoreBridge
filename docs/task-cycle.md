# Task cycle: send a repo to a VM, let Claude work, get the changes back

Use this cycle when Claude should work on one of your repositories inside a VM and only reviewed commits should reach your host `main`. It has three steps:

1. **Send:** `cb repo-send` copies `main` from the host into the VM.
2. **Work:** in the VM, Claude commits on a branch `claude/<id>`, and you push that branch to the VM's `origin`.
3. **Receive:** `cb repo-receive` brings `claude/<id>` back and, after you confirm, fast-forwards the host's `main`.

The examples use `cb`. Any other alias for the CLI works the same way.

## How it works

The VM holds two copies of each repository:

| Path in the VM | What it is |
|---|---|
| `~/repos/<repo>.git` | Bare repository. It is the exchange point between host and VM. |
| `~/work/<repo>` | Working copy cloned from the bare repo, which is its `origin`. Claude works here. |

`<repo>` is the name of the host repository's folder. A worktree counts as its main repository, so it sends as that repository.

There is no SSH. Commits travel as `git bundle` files over `multipass transfer`. `repo-receive` only sees what is in the **bare repo**, so a commit that exists only in `~/work/<repo>` stays invisible until you push it.

## Before you start

- The VM needs the `git` component. `repo-send` checks for it and stops if it is missing. Install it with:

  ```bash
  cb vm-install -n claude-vm1 -c git
  ```

  You can also put `git` in the profile when you create the VM, for example `cb init-configure -n dev -c claude,git`.
- The host repository needs a branch `main`.
- `repo-send` and `repo-receive` refuse to run under `cb-sim`.

## 1. Send `main` into the VM

Run this on the host, from anywhere inside the repository, or point to it with `-r <path>`:

```bash
cb repo-send -n claude-vm1
```

- The first time, it sends `main` with its full history. After that it sends only the new commits.
- It only fast-forwards, like a `git push` without `--force`. If the VM's `main` has commits the host doesn't, it fails.
- It clones `~/work/<repo>` the first time and only fetches into it afterwards. It does **not** reset the working copy. If you start a new task, base it on `origin/main` (see below).
- Commits in the VM are made as `Claude (<vm>) <claude@<vm>.corebridge.local>`.

## 2. Work in the VM

```bash
cb vm-connect -n claude-vm1
```

Inside the VM:

```sh
cd ~/work/<repo>
git fetch origin
git switch -c claude/my-task origin/main
claude
```

Let Claude make its changes and commit them. Then **push the branch to the bare repo**:

```sh
git push origin claude/my-task
```

> **Don't skip the push.** Without it, `repo-receive` won't find the branch. CoreBridge doesn't create or push the task branch for you yet; a task-start command is planned (see [consolidation-plan.md](consolidation-plan.md)).

The branch must be named `claude/<id>`. Rules for `<id>`:

- 1 to 64 characters: letters, digits, `.`, `_` and `-`.
- It must start with a letter or digit.
- It must not contain `..` or end with `.lock`.

A timestamp works well, for example `claude/20261003-1430`.

You can push more commits to the same branch later and receive it again.

## 3. Get the changes back

On the host, inside the repository, with **`main` checked out and no uncommitted changes**:

```bash
cb repo-receive -n claude-vm1
```

Without `-i`, it lists the `claude/*` branches in the VM's bare repo by id. Then receive one:

```bash
cb repo-receive -n claude-vm1 -i my-task
```

What happens:

1. The branch is fetched to `refs/remotes/claude-vm1/claude/my-task`. Your `main` is not touched yet.
2. You see the commit log and a diffstat. Add `--diff` to see the full diff.
3. It asks `Fast-forward main by N commits from claude/my-task?`
   - **Yes** (or `--yes`): `main` is fast-forwarded with `git merge --ff-only`. The commit ids stay the same as in the VM.
   - **No:** nothing changes. You can still look at the fetched branch:

     ```bash
     git log -p main..claude-vm1/claude/my-task
     ```

When there's no interactive console, the answer is *no*, so a script must pass `--yes` to merge.

### When `main` has moved on: integration in the VM

If your host `main` got commits after the task started, a fast-forward isn't possible. `repo-receive` then asks:

```
Integrate main into claude/my-task in `claude-vm1` (send main, merge there, claude resolves conflicts, receive again)?
```

On *yes* (or with `--integrate`) it runs the whole round trip with no further input from you:

1. It sends the current `main` to the VM, as `repo-send` does.
2. In `~/work/<repo>` it checks out `claude/my-task` and runs `git merge origin/main`. The workspace must be clean.
3. **No conflicts:** the merge commit is made right away and no agent is started.
4. **Conflicts:** the agent runs in the VM without a console (`claude -p`, with `--continue` when the task's Claude session is there, so it knows the task). It resolves the conflicts, builds and tests if the repository says how, finishes the merge and writes its verdict to `~/.corebridge/integrate-my-task.result` (first line `success` or `failure`, then the reason). It may run for up to `Work:IntegrateTimeout` (default `60m`).
5. CoreBridge checks the result itself: the verdict is `success`, the merge is closed, the workspace is clean, the branch contains `main`, and no conflict markers are left. Only then does it push the branch to the bare repo.
6. It fetches the branch again and continues as usual: log, diffstat, then the question before `main` is fast-forwarded.

If any step fails, nothing is pushed and nothing changes on the host. The VM is left as it is for you to look at (`cb vm-connect -n claude-vm1`; `git merge --abort` in `~/work/<repo>` discards the attempt).

On *no*, or without a console, nothing changes and it exits with code 1.

The integration merges rather than rebases, like GitHub's "Update branch": the task's commits keep their ids and no force-push is needed. The agent runs with `--dangerously-skip-permissions`; that is acceptable only because it runs inside the VM.

## When `repo-receive` refuses

| Message | What to do |
|---|---|
| `… is not on main` | `git switch main` |
| `… has uncommitted changes` | Commit or stash them first. |
| `VM … has no repository … yet` | Run `cb repo-send -n <vm>` first. |
| `VM … has no branch claude/<id>` | The branch wasn't pushed to `origin` in the VM, or the id is wrong. List the ids with `cb repo-receive -n <vm>`. |
| `Nothing was changed. Integrate in the VM with … --integrate` | You declined the integration, or there was no console. Run it again with `--integrate`, or merge by hand (`git merge <vm>/claude/<id>`). |
| `Integration in … failed: …` | The reason follows in the message. Nothing was pushed or changed on the host; look in the VM and fix or abort the merge there. |
| `The workspace … has uncommitted changes` | The VM's working copy must be clean before the integration. Commit or discard the changes there. |
| `Nothing new — … is already contained in main` | The branch was already received. |

## Example

```bash
cb repo-send -n claude-vm1
cb vm-connect -n claude-vm1
```

In the VM:

```sh
cd ~/work/CoreBridge && git fetch origin && git switch -c claude/consolidation origin/main
claude
git push origin claude/consolidation
exit
```

Back on the host:

```bash
cb repo-receive -n claude-vm1
cb repo-receive -n claude-vm1 -i consolidation
```
