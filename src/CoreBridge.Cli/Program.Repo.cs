using CoreBridge.Interaction;
using CoreBridge.Core.Common;
using CoreBridge.Core.Git;
using CoreBridge.Core.Multipass;

namespace CoreBridge.Cli;

public static partial class Program
{
  /// <summary>
  /// Schickt <c>main</c> eines Host-Repositorys in eine VM (wiederholbar): prüft die git-Komponente,
  /// legt das Bare-Repo an, bringt <c>main</c> samt Historie als Bundle hinein (beim
  /// nächsten Mal nur, was neu ist) und klont bzw. fetcht die Arbeitskopie. Fehlt die
  /// git-Komponente, wird nichts angelegt (fail closed).
  /// </summary>
  private static async Task RunRepoSend(CommandContext context, RepoSendOptions options)
  {
    var events = context.Events;
    var name = options.Name;
    var (root, repo) = await ResolveRepo(context, options.Repo);
    var hostMain = await RunGit(context, root, "rev-parse", "--verify", "--quiet", SandboxRepo.MainRef);
    if (hostMain.ExitCode != 0)
    {
      throw new ProgramExecutionException(1, $"Repository `{root}` has no branch `main` — repo-send transfers `main`.");
    }

    await EnsureRunning(context, name);
    await RequireGitComponent(context, name);

    var bare = SandboxRepo.BareDir(repo);
    await RunVmStep(context, name, $"bare repo {bare}", SandboxRepo.InitBareCommand(repo));

    var vmRefs = RequireVmSuccess($"refs of {name}", await VmQuery(context, name, SandboxRepo.ListRefsCommand(repo, SandboxRepo.MainRef)));
    var vmMain = SandboxRepo.ParseRefs(vmRefs.StdOut).GetValueOrDefault(SandboxRepo.MainRef);
    if (vmMain == hostMain.StdOut.Trim())
    {
      await events.Emit(events.Std($"main in {name}: up to date ({vmMain[..7]})"));
    }
    else
    {
      // Nur das Neue bündeln, wenn der Host den Stand der VM kennt; sonst alles (git
      // lehnt beim Einlesen ab, was kein Vorspulen ist — wie ein push ohne --force).
      var incremental = vmMain is not null && await HostHasCommit(context, root, vmMain);
      string[] range = incremental ? ["main", $"^{vmMain}"] : ["main"];
      await TransferBundleToVm(context, root, name, repo, range, incremental ? "new commits of main" : "main with full history");
    }

    await RunVmStep(context, name, $"workspace {SandboxRepo.WorkDir(repo)}", SandboxRepo.WorkspaceCommand(name, repo));
    var work = SandboxRepo.WorkDir(repo);
    await events.Emit(events.Std($"main of `{repo}` is in `{name}`: {work}."));
    if (options.FromWork)
    {
      return;
    }

    await events.Emit(events.Std(
        $"Work there on a branch claude/<id> and push it to origin; bring it back with `cb repo-receive -n {name} -i <id>` (from a checkout with main checked out)."));
  }

  /// <summary>Bundle auf dem Host packen, per transfer in die VM legen und dort ins Bare-Repo einlesen; temporäre Dateien werden auf beiden Seiten entfernt.</summary>
  private static async Task TransferBundleToVm(CommandContext context, string root, string name, string repo, string[] range, string what)
  {
    var local = Path.Combine(Path.GetTempPath(), $"corebridge-{Guid.NewGuid():N}.bundle");
    var remote = SandboxRepo.VmBundlePath(repo, "main");
    try
    {
      await RunGitReported(context, root, $"git bundle ({what})", ["bundle", "create", "-q", local, .. range]);
      await RunTransfer(context, $"transfer bundle ({new FileInfo(local).Length / 1024.0:F1} KiB) → {name}", () => context.Vms.Put(local, name, remote, TransferTimeoutMs(context)));
      await RunVmStep(context, name, $"main → {SandboxRepo.BareDir(repo)}", SandboxRepo.FetchMainBundleCommand(repo, remote));
    }
    finally
    {
      File.Delete(local);
    }
  }

  /// <summary>
  /// Holt <c>claude/&lt;id&gt;</c> als Bundle und spult <c>main</c> darauf vor (<c>merge
  /// --ff-only</c>) — nur nach Rückfrage (ohne Konsole: nein) oder mit <c>--yes</c>. Geht
  /// das nicht, weil main eigene Commits hat, bleibt alles unverändert. Ohne Id: Liste
  /// der Task-Branches.
  /// </summary>
  private static async Task RunRepoReceive(CommandContext context, RepoReceiveOptions options)
  {
    var events = context.Events;
    var name = options.Name;
    var (root, repo) = await ResolveRepo(context, options.Repo);
    var id = string.IsNullOrWhiteSpace(options.Id) ? null : options.Id.Trim();
    if (id is not null)
    {
      if (!SandboxRepo.IsValidId(id))
      {
        throw new ProgramExecutionException(1, $"Invalid id `{id}` — use letters, digits, `.`, `_` and `-` (e.g. 20260927-1432).");
      }

      await RequireCleanMain(context, root);
    }

    await EnsureRunning(context, name);
    if ((await VmQuery(context, name, SandboxRepo.BareExistsCommand(repo))).ExitCode != 0)
    {
      throw new ProgramExecutionException(1, $"VM `{name}` has no repository `{repo}` yet — send it first with `cb repo-send -n {name}`.");
    }

    if (id is null)
    {
      await ListTaskBranches(context, name, repo);
      return;
    }

    var branch = SandboxRepo.TaskBranch(id);
    var vmRefs = RequireVmSuccess(
        $"refs of {name}",
        await VmQuery(context, name, SandboxRepo.ListRefsCommand(repo, SandboxRepo.MainRef, SandboxRepo.TaskRef(id))));
    var refs = SandboxRepo.ParseRefs(vmRefs.StdOut);
    if (!refs.TryGetValue(SandboxRepo.TaskRef(id), out var taskCommit))
    {
      throw new ProgramExecutionException(1, $"VM `{name}` has no branch {branch} — list the ids with `cb repo-receive -n {name}`.");
    }

    var tracking = SandboxRepo.TrackingRef(name, id);
    if (await HostHasCommit(context, root, taskCommit))
    {
      // Schon einmal geholt (oder nichts Neues): kein Transfer nötig.
      RequireGitSuccess($"update-ref {tracking}", await RunGit(context, root, "update-ref", tracking, taskCommit));
      await events.Emit(events.Std($"{branch}: already on the host ({taskCommit[..7]})"));
    }
    else
    {
      // Nur bündeln, was über den main der VM hinausgeht — sofern der Host ihn kennt.
      var vmMain = refs.GetValueOrDefault(SandboxRepo.MainRef);
      var exclude = vmMain is not null && await HostHasCommit(context, root, vmMain) ? vmMain : null;
      await TransferBundleFromVm(context, root, name, repo, id, exclude, tracking);
    }

    if (await IsAncestor(context, root, tracking, SandboxRepo.MainRef))
    {
      await events.Emit(events.Std($"Nothing new — {branch} is already contained in main."));
      return;
    }

    // Nur Vorspulen: main muss Vorfahr des Task-Branches sein. Hat main seit dem Task-Start
    // eigene Commits bekommen, wird nichts verändert — kein Merge-Commit, kein Konflikt.
    if (!await IsAncestor(context, root, SandboxRepo.MainRef, tracking))
    {
      throw new ProgramExecutionException(
          1,
          $"main has commits that {branch} doesn't have — a fast-forward isn't possible, nothing was changed. "
              + $"Rebase the task onto the current main in the VM (send it first with `cb repo-send -n {name}`), or merge by hand: `git merge {tracking}`.");
    }

    var count = RequireGitSuccess("rev-list --count", await RunGit(context, root, "rev-list", "--count", $"main..{tracking}")).StdOut.Trim();
    await EmitGit(context, await RunGit(context, root, "log", "--oneline", "--no-decorate", "--reverse", $"main..{tracking}"));
    await EmitGit(context, await RunGit(context, root, "diff", "--stat", "main", tracking));
    if (options.Diff)
    {
      await EmitGit(context, await RunGit(context, root, "diff", "main", tracking));
    }

    var commits = count == "1" ? "1 commit" : $"{count} commits";
    if (!options.Yes && !await context.ConfirmAsync($"Fast-forward main by {commits} from {branch}?"))
    {
      await events.Emit(events.Std($"Not merged — run again with `--yes` or answer yes: `cb repo-receive -n {name} -i {id}`."));
      return;
    }

    var merge = await RunGit(context, root, "merge", "--ff-only", "-q", tracking);
    if (merge.ExitCode != 0)
    {
      throw new ProgramExecutionException(1, $"`git merge --ff-only {tracking}` failed: {FirstLine(merge.StdErr, merge.StdOut)} — main is unchanged.");
    }

    await events.Emit(events.Std($"main fast-forwarded to {taskCommit[..7]} ({commits} from {branch})."));
  }

  /// <summary>Bundle von <c>claude/&lt;id&gt;</c> in der VM packen, per transfer holen und nach <paramref name="tracking"/> einlesen; temporäre Dateien werden auf beiden Seiten entfernt.</summary>
  private static async Task TransferBundleFromVm(CommandContext context, string root, string name, string repo, string id, string? exclude, string tracking)
  {
    var branch = SandboxRepo.TaskBranch(id);
    var local = Path.Combine(Path.GetTempPath(), $"corebridge-{Guid.NewGuid():N}.bundle");
    var remote = SandboxRepo.VmBundlePath(repo, id);
    try
    {
      await RunVmStep(context, name, $"git bundle {branch} in {name}", SandboxRepo.CreateTaskBundleCommand(repo, id, remote, exclude));
      await RunTransfer(context, $"transfer bundle {name} → host", () => IgnoreLocalPermissionError(context.Vms.Get(name, remote, local, TransferTimeoutMs(context)), local));
      await RunGitReported(context, root, $"git fetch bundle {branch} → {tracking}", ["fetch", "-q", local, $"+{SandboxRepo.TaskRef(id)}:{tracking}"]);
    }
    finally
    {
      File.Delete(local);
      await VmQuery(context, name, $"rm -f {Shell.Quote(remote)}");
    }
  }

  private static async Task ListTaskBranches(CommandContext context, string name, string repo)
  {
    var events = context.Events;
    var refs = RequireVmSuccess($"refs of {name}", await VmQuery(context, name, SandboxRepo.ListRefsCommand(repo, "refs/heads/claude/")));
    var ids = SandboxRepo.ParseRefs(refs.StdOut).Keys
        .Where(reference => reference.StartsWith("refs/heads/claude/", StringComparison.Ordinal))
        .Select(reference => reference["refs/heads/claude/".Length..])
        .ToArray();
    if (ids.Length == 0)
    {
      await events.Emit(events.Std($"No claude/* branches in `{name}` yet."));
      return;
    }

    await events.Emit(events.Std($"Task branches in `{name}` (receive one with `cb repo-receive -n {name} -i <id>`):"));
    foreach (var taskId in ids)
    {
      await events.Emit(events.Std($"  {taskId}"));
    }
  }

  /// <summary>Vorspulen nur auf ausgechecktem <c>main</c> ohne Änderungen an getrackten Dateien.</summary>
  private static async Task RequireCleanMain(CommandContext context, string root)
  {
    var head = await RunGit(context, root, "symbolic-ref", "--short", "-q", "HEAD");
    if (head.ExitCode != 0 || head.StdOut.Trim() != "main")
    {
      throw new ProgramExecutionException(1, $"`{root}` is not on main (HEAD: {(head.ExitCode == 0 ? head.StdOut.Trim() : "detached")}) — repo-receive fast-forwards main; switch with `git switch main`.");
    }

    var status = await RunGit(context, root, "status", "--porcelain", "--untracked-files=no");
    RequireGitSuccess("status", status);
    if (GitLines(status.StdOut).Any())
    {
      throw new ProgramExecutionException(1, $"`{root}` has uncommitted changes — commit or stash them before repo-receive.");
    }
  }

  /// <summary>git-Komponente in der VM (Marker und git im PATH); sonst der Hinweis auf vm-install.</summary>
  private static async Task RequireGitComponent(CommandContext context, string name)
  {
    if ((await VmQuery(context, name, SandboxRepo.ReadyCommand())).ExitCode != 0)
    {
      throw new ProgramExecutionException(1, $"VM `{name}` has no git component yet — install it with `cb vm-install -n {name} -c git`.");
    }

    await context.Events.Emit(context.Events.Std($"git component in {name}: OK"));
  }

  /// <summary><c>git merge-base --is-ancestor</c>: ist <paramref name="ancestor"/> in <paramref name="descendant"/> enthalten?</summary>
  private static async Task<bool> IsAncestor(CommandContext context, string root, string ancestor, string descendant)
  {
    var result = await RunGit(context, root, "merge-base", "--is-ancestor", ancestor, descendant);
    return result.ExitCode switch
    {
      0 => true,
      1 => false,
      _ => throw new ProgramExecutionException(1, $"`git merge-base --is-ancestor {ancestor} {descendant}` failed: {FirstLine(result.StdErr, result.StdOut)}")
    };
  }

  private static async Task<bool> HostHasCommit(CommandContext context, string root, string commit)
  {
    return (await RunGit(context, root, "cat-file", "-e", $"{commit}^{{commit}}")).ExitCode == 0;
  }

  /// <summary>Host-Repository zu <paramref name="path"/> (Standard: aktuelles Verzeichnis); Name aus dem gemeinsamen .git — in einem Worktree also der des Haupt-Repos.</summary>
  private static async Task<(string Root, string Name)> ResolveRepo(CommandContext context, string? path)
  {
    var start = Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? Environment.CurrentDirectory : path);
    if (!Directory.Exists(start))
    {
      throw new ProgramExecutionException(1, $"Folder `{start}` does not exist.");
    }

    var top = await RunGit(context, start, "rev-parse", "--show-toplevel");
    if (top.ExitCode != 0)
    {
      throw new ProgramExecutionException(1, $"`{start}` is not inside a git repository.");
    }

    var common = await RunGit(context, start, "rev-parse", "--path-format=absolute", "--git-common-dir");
    RequireGitSuccess("rev-parse --git-common-dir", common);
    var commonDir = Path.GetFullPath(common.StdOut.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    var name = Path.GetFileName(commonDir) == ".git"
        ? Path.GetFileName(Path.GetDirectoryName(commonDir))!
        : Path.GetFileNameWithoutExtension(commonDir);
    if (!SandboxRepo.IsValidRepoName(name))
    {
      throw new ProgramExecutionException(1, $"Repository name `{name}` can't be used in the VM — use letters, digits, `.`, `_` and `-`.");
    }

    return (Path.GetFullPath(top.StdOut.Trim()), name);
  }

  private static Task<GitRunResult> RunGit(CommandContext context, string root, params string[] args)
  {
    return Task.Run(() => context.Git.Run(args, root, context.DefaultTimeoutMs), context.CancellationToken);
  }

  /// <summary>Lange git-Aufrufe (bundle/fetch): mit Fortschritt und <c>DefaultTimeoutVmCreation</c>; bei Exit ≠ 0 die fertige Meldung.</summary>
  private static async Task<GitRunResult> RunGitReported(CommandContext context, string root, string label, params string[] args)
  {
    await context.Events.Emit(context.Events.Std($"{label} ..."));
    var result = await WithProgress(context, () => context.Git.Run(args, root, TransferTimeoutMs(context)));
    RequireGitSuccess(label, result);
    await context.Events.Emit(context.Events.Std($"{label}: OK ({result.DurationMs / 1000.0:F1} s)"));
    return result;
  }

  /// <summary>Shell-Zeile in der VM (multipass exec); bei Exit ≠ 0 die fertige Meldung.</summary>
  private static async Task RunVmStep(CommandContext context, string name, string label, string command)
  {
    var result = await RunReported(context, label, () => context.Vms.Exec(name, command, TransferTimeoutMs(context)));
    await context.Events.Emit(context.Events.Std($"{label}: OK ({result.DurationMs / 1000.0:F1} s)"));
  }

  /// <summary>Kurze Abfrage in der VM ohne Ausgabe; der Aufrufer wertet Exit-Code und stdout aus.</summary>
  private static Task<MultipassRunResult> VmQuery(CommandContext context, string name, string command)
  {
    return Task.Run(() => context.Vms.Exec(name, command, context.DefaultTimeoutMs), context.CancellationToken);
  }

  private static MultipassRunResult RequireVmSuccess(string label, MultipassRunResult result)
  {
    if (result.ExitCode != 0)
    {
      throw new ProgramExecutionException(1, $"`{label}` failed (exit {result.ExitCode}): {FirstLine(result.StdErr, result.StdOut)}");
    }

    return result;
  }

  /// <summary>multipass transfer eines Bundles, mit Fortschritt; bei Exit ≠ 0 die fertige Meldung.</summary>
  private static async Task RunTransfer(CommandContext context, string label, Func<MultipassRunResult> transfer)
  {
    var result = await RunReported(context, label, transfer);
    await context.Events.Emit(context.Events.Std($"{label}: OK ({result.DurationMs / 1000.0:F1} s)"));
  }

  /// <summary>
  /// Multipass holt Dateien unter Windows vollständig, meldet dann aber Exit 2, weil es
  /// die Unix-Rechte nicht setzen kann („cannot set permissions for local file“). Nur
  /// genau das gilt als Erfolg; ob die Datei heil ist, prüft danach git selbst.
  /// </summary>
  private static MultipassRunResult IgnoreLocalPermissionError(MultipassRunResult result, string localPath)
  {
    var onlyPermissions = result.ExitCode != 0
        && File.Exists(localPath)
        && Lines(result.StdErr).Any()
        && Lines(result.StdErr).All(line => line.Contains("cannot set permissions for local file", StringComparison.Ordinal));
    return onlyPermissions ? result with { ExitCode = 0, StdErr = string.Empty } : result;
  }

  /// <summary>Bundles mit ganzer Historie können dauern: <c>DefaultTimeoutVmCreation</c> statt <c>DefaultTimeout</c>.</summary>
  private static int TransferTimeoutMs(CommandContext context)
  {
    return (int)context.Config.DefaultTimeoutVmCreation.ReadTime().TotalMilliseconds;
  }

  private static GitRunResult RequireGitSuccess(string label, GitRunResult result)
  {
    if (result.ExitCode != 0)
    {
      throw new ProgramExecutionException(1, $"`{label}` failed (exit {result.ExitCode}): {FirstLine(result.StdErr, result.StdOut)}");
    }

    return result;
  }

  private static async Task EmitGit(CommandContext context, GitRunResult result)
  {
    foreach (var line in GitLines(result.StdOut).Concat(GitLines(result.StdErr)))
    {
      await context.Events.Emit(context.Events.Std("  " + line));
    }
  }

  private static IEnumerable<string> GitLines(string text)
  {
    return text.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0);
  }

  private static string FirstLine(params string[] texts)
  {
    return texts.SelectMany(GitLines).FirstOrDefault() ?? "no output";
  }
}
