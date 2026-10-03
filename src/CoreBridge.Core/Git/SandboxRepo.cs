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
