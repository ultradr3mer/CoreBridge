using System.Text.RegularExpressions;
using CoreBridge.Core.Common;

namespace CoreBridge.Core.Git;

/// <summary>
/// Wo ein Host-Repository in der VM liegt und wie es hin- und zurückkommt: je VM ein
/// Bare-Repo <c>~/repos/&lt;repo&gt;.git</c> und eine Arbeitskopie <c>~/work/&lt;repo&gt;</c>,
/// aus der Claude in <c>claude/&lt;id&gt;</c> ins Bare-Repo pusht. Zwischen Host und VM
/// wandern die Commits als <c>git bundle</c>-Datei per <c>multipass transfer</c> — kein
/// Datenstrom, denn <c>multipass exec</c> reicht unter Windows kein stdin in die VM
/// durch, und damit kein SSH.
/// </summary>
public static partial class SandboxRepo
{
    /// <summary>Gast-Benutzer, dem Bare-Repo und Arbeitskopie gehören (wie die git-Komponente).</summary>
    public const string User = "ubuntu";

    /// <summary>Marker der git-Komponente.</summary>
    public const string ReadyMarker = "/var/lib/corebridge/git.ready";

    public const string MainRef = "refs/heads/main";

    public static string BareDir(string repo) => $"/home/{User}/repos/{repo}.git";

    public static string WorkDir(string repo) => $"/home/{User}/work/{repo}";

    public static string TaskBranch(string id) => $"claude/{id}";

    public static string TaskRef(string id) => $"refs/heads/{TaskBranch(id)}";

    /// <summary>Wohin <c>repo-receive</c> den Task-Branch auf dem Host holt.</summary>
    public static string TrackingRef(string vm, string id) => $"refs/remotes/{vm}/{TaskBranch(id)}";

    /// <summary>Ablage eines Bundles in der VM, bis es eingelesen bzw. abgeholt ist.</summary>
    public static string VmBundlePath(string repo, string what) => $"/tmp/corebridge-{repo}-{what}.bundle";

    /// <summary>Task-Id: Buchstaben, Ziffern, <c>.</c>, <c>_</c>, <c>-</c>; beginnt mit Buchstabe oder Ziffer.</summary>
    public static bool IsValidId(string id)
    {
        return IdPattern().IsMatch(id) && !id.Contains("..") && !id.EndsWith(".lock", StringComparison.Ordinal);
    }

    /// <summary>Repo-Name, wie er in Pfaden der VM steht.</summary>
    public static bool IsValidRepoName(string name)
    {
        return IdPattern().IsMatch(name) && !name.Contains("..");
    }

    /// <summary>Exit 0 nur mit fertiger git-Komponente (Marker und git im PATH).</summary>
    public static string ReadyCommand()
    {
        return $"test -f {Shell.Quote(ReadyMarker)} && command -v git >/dev/null";
    }

    /// <summary>Legt das Bare-Repo an, falls es fehlt.</summary>
    public static string InitBareCommand(string repo)
    {
        var bare = Shell.Quote(BareDir(repo));
        return $"test -d {bare} || git init --bare -q --initial-branch=main {bare}";
    }

    /// <summary>Exit 0, wenn das Bare-Repo existiert (<c>repo-send</c> lief).</summary>
    public static string BareExistsCommand(string repo)
    {
        return $"test -d {Shell.Quote(BareDir(repo))}";
    }

    /// <summary>
    /// Refs des Bare-Repos als Zeilen <c>&lt;sha&gt; &lt;ref&gt;</c>; <paramref name="patterns"/>
    /// wie bei <c>git for-each-ref</c> (ganze Namen oder Präfixe bis <c>/</c>).
    /// </summary>
    public static string ListRefsCommand(string repo, params string[] patterns)
    {
        return $"git -C {Shell.Quote(BareDir(repo))} for-each-ref --format='%(objectname) %(refname)' "
            + string.Join(' ', patterns.Select(Shell.Quote));
    }

    /// <summary>
    /// Liest ein hochgeladenes Bundle mit <c>main</c> ins Bare-Repo (nur vorspulen, wie ein
    /// <c>git push</c> ohne <c>--force</c>) und löscht es danach — auch bei Fehlern.
    /// </summary>
    public static string FetchMainBundleCommand(string repo, string bundle)
    {
        var quoted = Shell.Quote(bundle);
        return $"git -C {Shell.Quote(BareDir(repo))} fetch -q {quoted} {MainRef}:{MainRef}; rc=$?; rm -f {quoted}; exit $rc";
    }

    /// <summary>
    /// Packt <c>claude/&lt;id&gt;</c> in ein Bundle; mit <paramref name="excludeCommit"/> (einem
    /// Commit, den der Host schon hat) nur das, was darüber hinausgeht.
    /// </summary>
    public static string CreateTaskBundleCommand(string repo, string id, string bundle, string? excludeCommit)
    {
        var exclude = excludeCommit is null ? string.Empty : $" ^{excludeCommit}";
        return $"git -C {Shell.Quote(BareDir(repo))} bundle create -q {Shell.Quote(bundle)} {TaskRef(id)}{exclude}";
    }

    /// <summary>
    /// Arbeitskopie klonen oder — wenn sie schon da ist — nur fetchen (zurückgesetzt wird
    /// erst beim Task-Start); dazu die git-Identität für Claudes Commits.
    /// </summary>
    public static string WorkspaceCommand(string vm, string repo)
    {
        var (bare, work) = (Shell.Quote(BareDir(repo)), Shell.Quote(WorkDir(repo)));
        return $"if [ -d {work}/.git ]; then git -C {work} fetch -q origin; else git clone -q {bare} {work}; fi"
            + $" && git -C {work} config user.name {Shell.Quote($"Claude ({vm})")}"
            + $" && git -C {work} config user.email {Shell.Quote($"claude@{vm}.corebridge.local")}";
    }

    /// <summary>Task-Id aus der Startzeit: <c>yyyyMMdd-HHmm</c>.</summary>
    public static string TaskId(DateTime start) => start.ToString("yyyyMMdd-HHmm", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Ausgabe leer genau dann, wenn die Arbeitskopie sauber ist.</summary>
    public static string DirtyCommand(string repo) => $"git -C {Shell.Quote(WorkDir(repo))} status --porcelain";

    /// <summary>Task-Start: frisches <c>main</c> holen und <c>claude/&lt;id&gt;</c> darauf auschecken.</summary>
    public static string TaskStartCommand(string repo, string id)
    {
        var work = Shell.Quote(WorkDir(repo));
        return $"git -C {work} fetch -q origin && git -C {work} checkout -q -B {TaskBranch(id)} origin/main";
    }

    /// <summary>Zahl der Commits auf <c>claude/&lt;id&gt;</c>, die <c>main</c> noch nicht hat.</summary>
    public static string TaskCommitCountCommand(string repo, string id) =>
        $"git -C {Shell.Quote(WorkDir(repo))} rev-list --count origin/main..{TaskBranch(id)}";

    /// <summary>Legt <c>claude/&lt;id&gt;</c> aus der Arbeitskopie ins Bare-Repo, wo repo-receive es holt.</summary>
    public static string PushTaskCommand(string repo, string id) =>
        $"git -C {Shell.Quote(WorkDir(repo))} push -q origin {TaskBranch(id)}:{TaskRef(id)}";

    /// <summary>
    /// Vor der Integration: <c>claude/&lt;id&gt;</c> in der Arbeitskopie auschecken (ohne lokalen
    /// Branch aus <c>origin</c> angelegt) und auf den Stand des Bare-Repos bringen.
    /// </summary>
    public static string CheckoutTaskCommand(string repo, string id)
    {
        var work = Shell.Quote(WorkDir(repo));
        return $"git -C {work} fetch -q origin && git -C {work} checkout -q {TaskBranch(id)} && git -C {work} merge -q --ff-only origin/{TaskBranch(id)}";
    }

    /// <summary>Holt das frische <c>main</c> in den Task-Branch; Exit ≠ 0 bei Konflikten (der Merge bleibt dann offen).</summary>
    public static string MergeMainCommand(string repo) =>
        $"git -C {Shell.Quote(WorkDir(repo))} merge -q --no-edit origin/main";

    /// <summary>Dateien mit offenen Konflikten, eine je Zeile.</summary>
    public static string ConflictsCommand(string repo) =>
        $"git -C {Shell.Quote(WorkDir(repo))} diff --name-only --diff-filter=U";

    /// <summary>Exit 0, solange ein Merge offen ist.</summary>
    public static string MergeOpenCommand(string repo) =>
        $"git -C {Shell.Quote(WorkDir(repo))} rev-parse -q --verify MERGE_HEAD";

    /// <summary>Exit 0, wenn <c>origin/main</c> im ausgecheckten Task-Branch enthalten ist.</summary>
    public static string ContainsMainCommand(string repo) =>
        $"git -C {Shell.Quote(WorkDir(repo))} merge-base --is-ancestor origin/main HEAD";

    /// <summary>Exit 0, wenn in <paramref name="files"/> noch Konfliktmarker stehen.</summary>
    public static string ConflictMarkersCommand(string repo, IEnumerable<string> files) =>
        $"git -C {Shell.Quote(WorkDir(repo))} grep -q -E '^(<<<<<<<|>>>>>>>)( |$)' HEAD -- "
        + string.Join(' ', files.Select(Shell.Quote));

    /// <summary>Urteil des Agenten nach der Integration: erste Zeile <c>success</c> oder <c>failure</c>, danach der Grund.</summary>
    public static string IntegrationResultFile(string id) => $"/home/{User}/.corebridge/integrate-{id}.result";

    /// <summary>Auftrag an den Agenten, einen offenen Merge von <c>origin/main</c> aufzulösen.</summary>
    public static string IntegrationPrompt(string id, IReadOnlyList<string> conflicts)
    {
        return $"CoreBridge forward integration of task branch {TaskBranch(id)}. "
            + $"`git merge origin/main` stopped with conflicts in: {string.Join(", ", conflicts)}. "
            + "Resolve them so that both the task's changes and the changes from main are kept. "
            + "If the repository describes how to build and run its fast tests (e.g. CLAUDE.md), do that and fix what the merge broke. "
            + "Do not commit environment workarounds (SDK version pins, NuGet sources, local paths). "
            + "Finish the merge with `git commit --no-edit`. Do not push and do not change main. "
            + $"Finally write the result to {IntegrationResultFile(id)}: first line `success` or `failure`, second line the reason.";
    }

    /// <summary>
    /// Startet den Agenten ohne Konsole in der Arbeitskopie. Gibt es dort schon eine
    /// Claude-Sitzung (die des Tasks), wird sie fortgesetzt — sie kennt die Arbeit am besten.
    /// </summary>
    public static string AgentIntegrateCommand(string repo, string agent, string prompt, string id)
    {
        var result = Shell.Quote(IntegrationResultFile(id));
        var prepare = $"mkdir -p /home/{User}/.corebridge && rm -f {result} && cd {Shell.Quote(WorkDir(repo))} && ";
        var quoted = Shell.Quote(prompt);
        if (agent == "opencode")
        {
            return prepare + $"opencode run --continue {quoted} < /dev/null";
        }

        var sessions = Shell.Quote(ClaudeProjectDir(repo));
        const string Run = "claude --dangerously-skip-permissions";
        return prepare
            + $"if [ -n \"$(ls {sessions} 2>/dev/null)\" ]; then {Run} --continue -p {quoted} < /dev/null; else {Run} -p {quoted} < /dev/null; fi";
    }

    /// <summary>Wo Claude Code die Sitzungen der Arbeitskopie ablegt (Pfad mit <c>-</c> statt Sonderzeichen).</summary>
    public static string ClaudeProjectDir(string repo) =>
        $"/home/{User}/.claude/projects/{Regex.Replace(WorkDir(repo), "[^A-Za-z0-9]", "-")}";

    /// <summary>Datei, die der <c>.bashrc</c>-Haken beim nächsten <c>multipass shell</c> einmal ausführt.</summary>
    public const string AutostartFile = $"/home/{User}/.corebridge/autostart";

    /// <summary>
    /// Ausweg für <c>Work:AgentLaunch=shell</c>: schreibt die einmalige Autostart-Datei (cd +
    /// Agent) und hängt — nur einmal — den Haken an <c>~/.bashrc</c>, der sie beim nächsten
    /// interaktiven Login ausführt und dabei verbraucht.
    /// </summary>
    public static string AutostartCommand(string repo, string agent)
    {
        var bashrc = $"/home/{User}/.bashrc";
        const string Hook = "if [ -f ~/.corebridge/autostart ]; then mv ~/.corebridge/autostart ~/.corebridge/autostart.run; . ~/.corebridge/autostart.run; rm -f ~/.corebridge/autostart.run; fi";
        return $"mkdir -p /home/{User}/.corebridge"
            + $"; printf '%s\\n' {Shell.Quote($"cd {WorkDir(repo)}")} {Shell.Quote(agent)} > {AutostartFile}"
            + $"; grep -q corebridge-autostart {bashrc} || printf '%s\\n' '# corebridge-autostart' {Shell.Quote(Hook)} >> {bashrc}";
    }

    /// <summary>Zeilen <c>&lt;sha&gt; &lt;ref&gt;</c> (aus <see cref="ListRefsCommand"/>) als Ref → Commit.</summary>
    public static IReadOnlyDictionary<string, string> ParseRefs(string output)
    {
        return output.Split('\n')
            .Select(line => line.Trim().Split(' ', 2))
            .Where(parts => parts.Length == 2 && parts[0].Length > 0)
            .ToDictionary(parts => parts[1], parts => parts[0], StringComparer.Ordinal);
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();
}
