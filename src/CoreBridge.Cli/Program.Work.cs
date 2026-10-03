using CoreBridge.Interaction;
using CoreBridge.Core.Git;
using CoreBridge.Core.Multipass;
using CoreBridge.Core.Vm;

namespace CoreBridge.Cli;

public static partial class Program
{
  /// <summary>Agenten, die <c>cb work</c> starten kann.</summary>
  private static readonly string[] KnownAgents = ["claude", "opencode"];

  /// <summary>
  /// <c>cb</c> im Repo: VM wählen (gemerkt, passend oder nach Rückfrage migriert), starten,
  /// Code schicken, Task-Branch anlegen, den Agenten mit geerbter Konsole starten und danach
  /// die Commits zurückholen. Exit-Code ist der des Agenten.
  /// </summary>
  private static async Task RunWork(CommandContext context, WorkOptions options)
  {
    var events = context.Events;
    var (root, repo) = await ResolveRepo(context, options.Repo);
    var agent = ChooseAgent(context, options.Agent, root);
    await events.Emit(events.Std($"Repository `{repo}` ({root}) — Agent: {agent}"));

    var vm = await ChooseVm(context, options, root, agent);
    UpdateInventory(inventory => inventory.Repos[root] = vm);

    await EnsureRunning(context, vm, ask: false);
    await EnsureComponents(context, vm, ["git", agent]);

    await Dispatch(context, new RepoSendOptions { Name = vm, Repo = root });

    var id = await StartTask(context, vm, repo);
    var work = SandboxRepo.WorkDir(repo);
    await events.Emit(events.Std($"Task claude/{id} in `{vm}`:{work} — starte {agent} ..."));
    var exitCode = await LaunchAgent(context, vm, repo, agent);
    await events.Emit(events.Std($"{agent} beendet (Exit {exitCode})."));

    await ReceiveTask(context, vm, repo, root, id, options.Yes);
    if (exitCode != 0)
    {
      throw new ProgramExecutionException(exitCode, $"{agent} in `{vm}` endete mit Exit {exitCode}.");
    }
  }

  /// <summary><c>--agent</c>, sonst das Repo (CLAUDE.md → claude, AGENTS.md/opencode.json → opencode), sonst die Config.</summary>
  private static string ChooseAgent(CommandContext context, string? requested, string root)
  {
    var agent = requested?.Trim().ToLowerInvariant()
        ?? (File.Exists(Path.Combine(root, "CLAUDE.md")) ? "claude"
            : File.Exists(Path.Combine(root, "AGENTS.md")) || File.Exists(Path.Combine(root, "opencode.json")) ? "opencode"
            : context.Config.Work.DefaultAgent.Trim().ToLowerInvariant());
    if (!KnownAgents.Contains(agent))
    {
      throw new ProgramExecutionException(1, $"Unknown agent `{agent}` — use {string.Join(" or ", KnownAgents)}.");
    }

    if (context.Catalog.OriginOf(agent) is null)
    {
      throw new ProgramExecutionException(1, $"There is no component `{agent}` (cb component-list) — the agent has to be installable in the VM.");
    }

    return agent;
  }

  /// <summary>
  /// <c>--vm</c>, sonst die für dieses Repo gemerkte VM (ohne Rückfrage), sonst repo-match:
  /// passt eine VM, wird sie genommen; sonst wird der Migrationspfad gezeigt und nach
  /// Ja (<c>--yes</c>) ausgeführt.
  /// </summary>
  private static async Task<string> ChooseVm(CommandContext context, WorkOptions options, string root, string agent)
  {
    var events = context.Events;
    var vms = await Task.Run(() => context.Vms.List(context.DefaultTimeoutMs), context.CancellationToken);
    if (!string.IsNullOrWhiteSpace(options.Vm))
    {
      return (await RequireVm(context, options.Vm.Trim())).Name;
    }

    if (LoadInventory().Repos.GetValueOrDefault(root) is { } remembered
        && vms.FirstOrDefault(vm => string.Equals(vm.Name, remembered, StringComparison.OrdinalIgnoreCase) && !vm.IsDeleted) is { } known)
    {
      await events.Emit(events.Std($"VM `{known.Name}` (gemerkt für dieses Repository)."));
      return known.Name;
    }

    var report = await MatchRepo(context, root, [agent]);
    var plan = report.Plan;
    if (plan.Kind == MigrationKind.UseVm)
    {
      await events.Emit(events.Std($"VM `{plan.Vm}` passt ({string.Join(",", plan.Required)})."));
      return plan.Vm;
    }

    await EmitMatchReport(context, report);
    if (!options.Yes && !await context.ConfirmAsync("Diesen Weg jetzt gehen?"))
    {
      throw new ProgramExecutionException(1, "Abgebrochen — nichts geändert. Die Befehle oben gehen auch einzeln; `cb work --vm <name>` nimmt eine bestimmte VM.");
    }

    switch (plan.Kind)
    {
      case MigrationKind.RestoreSnapshot:
        await Dispatch(context, new VmRestoreOptions { Name = plan.Vm, Snapshot = plan.Source!.Snapshot! });
        break;
      case MigrationKind.CloneAndInstall:
        // Vorher selbst stoppen: vm-clone fragte sonst noch einmal.
        var source = await RequireVm(context, plan.Source!.Vm);
        if (!source.IsStopped)
        {
          await StopVm(context, source);
        }

        await Dispatch(context, new VmCloneOptions { Source = plan.Source.Vm, Name = plan.Vm });
        await EnsureRunning(context, plan.Vm, ask: false);
        await Dispatch(context, new VmInstallOptions { Name = plan.Vm, Components = string.Join(",", plan.Missing), Snapshot = "base" });
        break;
      default:
        await Dispatch(context, new InitConfigureOptions { Name = plan.Profile!, Components = string.Join(",", plan.Required), Force = true });
        await Dispatch(context, new VmCreateOptions { Name = plan.Vm, Profile = plan.Profile, Snapshot = "base" });
        break;
    }

    return plan.Vm;
  }

  /// <summary>Installiert, was der VM für <c>work</c> fehlt (git, Agent) — laut Marker im Gast.</summary>
  private static async Task EnsureComponents(CommandContext context, string vm, IReadOnlyList<string> components)
  {
    var missing = new List<string>();
    foreach (var component in components.Distinct(StringComparer.OrdinalIgnoreCase))
    {
      var probe = await Task.Run(
          () => context.Vms.Exec(vm, Core.Inits.InitInstall.MarkerProbe(Core.Inits.InitInstall.MarkerPath(component)), context.DefaultTimeoutMs),
          context.CancellationToken);
      var hasScript = context.Config.Inits.Components.GetValueOrDefault(component)?.HasScript ?? false;
      if (probe.ExitCode != 0 && hasScript)
      {
        missing.Add(component);
      }
    }

    if (missing.Count > 0)
    {
      await context.Events.Emit(context.Events.Std($"`{vm}` fehlt: {string.Join(", ", missing)} — wird installiert."));
      await Dispatch(context, new VmInstallOptions { Name = vm, Components = string.Join(",", missing) });
      await EnsureRunning(context, vm, ask: false);
    }
  }

  /// <summary>Task-Start: Arbeitskopie muss sauber sein; <c>claude/&lt;id&gt;</c> frisch aus main.</summary>
  private static async Task<string> StartTask(CommandContext context, string vm, string repo)
  {
    var dirty = RequireVmSuccess("status of the workspace", await VmQuery(context, vm, SandboxRepo.DirtyCommand(repo)));
    if (dirty.StdOut.Trim().Length > 0)
    {
      throw new ProgramExecutionException(
          1,
          $"The workspace {SandboxRepo.WorkDir(repo)} in `{vm}` has uncommitted changes — commit or discard them there first (`cb vm-connect -n {vm}`).");
    }

    var refs = SandboxRepo.ParseRefs(RequireVmSuccess(
        "task branches",
        await VmQuery(context, vm, SandboxRepo.ListRefsCommand(repo, "refs/heads/claude"))).StdOut);
    var id = SandboxRepo.TaskId(DateTime.Now);
    for (var i = 2; refs.ContainsKey(SandboxRepo.TaskRef(id)); i++)
    {
      id = $"{SandboxRepo.TaskId(DateTime.Now)}-{i}";
    }

    await RunVmStep(context, vm, $"task claude/{id}", SandboxRepo.TaskStartCommand(repo, id));
    return id;
  }

  /// <summary>
  /// Übergibt die Konsole an den Agenten: <c>multipass exec … --working-directory</c>, oder —
  /// mit <c>Work:AgentLaunch=shell</c> — <c>multipass shell</c> mit einmaligem Autostart.
  /// </summary>
  private static async Task<int> LaunchAgent(CommandContext context, string vm, string repo, string agent)
  {
    var cli = context.Vms.Cli;
    if (string.Equals(context.Config.Work.AgentLaunch, "shell", StringComparison.OrdinalIgnoreCase))
    {
      await RunVmStep(context, vm, "autostart", SandboxRepo.AutostartCommand(repo, agent));
      return await Task.Run(() => cli.Attach(["shell", vm]), context.CancellationToken);
    }

    return await Task.Run(
        () => cli.Attach(["exec", vm, "--working-directory", SandboxRepo.WorkDir(repo), "--", agent]),
        context.CancellationToken);
  }

  /// <summary>
  /// Nach dem Agenten: neue Commits auf <c>claude/&lt;id&gt;</c> ins Bare-Repo legen und
  /// repo-receive anbieten (Ja oder <c>--yes</c> spult main vor); ohne Commits nur eine Meldung.
  /// </summary>
  private static async Task ReceiveTask(CommandContext context, string vm, string repo, string root, string id, bool yes)
  {
    var events = context.Events;
    var count = RequireVmSuccess("task commits", await VmQuery(context, vm, SandboxRepo.TaskCommitCountCommand(repo, id)));
    if (!int.TryParse(count.StdOut.Trim(), out var commits) || commits == 0)
    {
      await events.Emit(events.Std($"Keine neuen Commits auf claude/{id} — main bleibt, wie es ist."));
      return;
    }

    await RunVmStep(context, vm, $"claude/{id} ({commits} commit{(commits == 1 ? string.Empty : "s")}) → {SandboxRepo.BareDir(repo)}", SandboxRepo.PushTaskCommand(repo, id));
    await Dispatch(context, new RepoReceiveOptions { Name = vm, Id = id, Repo = root, Yes = yes });
  }
}
