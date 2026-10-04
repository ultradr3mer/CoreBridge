using System.Text.RegularExpressions;
using CoreBridge.Core.Config;

namespace CoreBridge.Core.Vm;

/// <summary>Eine Komponente, die das Repository braucht, und woran das zu sehen ist.</summary>
public sealed record Requirement(string Component, string Reason);

/// <summary>
/// Was ein Repository braucht: eingebaute Regeln (dotnet, node, python, rust, go, java; git
/// immer) plus das <see cref="ComponentConfig.Detect"/>-Feld jeder Komponente. Gezählt werden
/// nur Komponenten, die es gibt.
/// </summary>
public static class RepoRequirements
{
    private static readonly (string Component, string[] Patterns)[] BuiltIn =
    [
        ("dotnet", ["*.csproj", "*.fsproj", "*.vbproj", "*.sln", "*.slnx", "global.json"]),
        ("node", ["package.json"]),
        ("python", ["pyproject.toml", "requirements.txt", "setup.py", "Pipfile"]),
        ("rust", ["Cargo.toml"]),
        ("go", ["go.mod"]),
        ("java", ["pom.xml", "build.gradle", "build.gradle.kts"]),
    ];

    /// <summary>Ordner, die nichts über die Bedürfnisse des Repositorys sagen (Abhängigkeiten, Build-Ausgaben).</summary>
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "target", ".venv", "venv", "dist", "build", "artifacts", ".vs", ".idea", "packages"
    };

    private const int MaxFiles = 50_000;

    public static IReadOnlyList<Requirement> Detect(string repoRoot, InitsConfig inits)
    {
        var rules = BuiltIn
            .Concat(inits.Components.Where(c => c.Value.Detect.Length > 0).Select(c => (c.Key, c.Value.Detect)))
            .Where(rule => inits.Components.ContainsKey(rule.Item1))
            .Select(rule => (Component: rule.Item1, Patterns: rule.Item2.Select(Glob).ToArray()))
            .ToList();

        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Files(repoRoot))
        {
            var name = Path.GetFileName(file);
            foreach (var (component, patterns) in rules)
            {
                if (!found.ContainsKey(component) && patterns.Any(pattern => pattern.IsMatch(name)))
                {
                    found[component] = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
                }
            }
        }

        var requirements = found
            .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Select(entry => new Requirement(entry.Key, entry.Value))
            .ToList();
        if (inits.Components.ContainsKey("git"))
        {
            // Code reist per git bundle (repo-send) — ohne git in der VM geht nichts.
            requirements.Add(new Requirement("git", "immer (repo-send)"));
        }

        return requirements;
    }

    private static IEnumerable<string> Files(string root)
    {
        var count = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<string> files;
            IEnumerable<string> children;
            try
            {
                files = Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal).ToList();
                children = Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal).Reverse().ToList();
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (var file in files)
            {
                if (++count > MaxFiles)
                {
                    yield break;
                }

                yield return file;
            }

            foreach (var child in children)
            {
                if (!Skipped.Contains(Path.GetFileName(child)))
                {
                    pending.Push(child);
                }
            }
        }
    }

    private static Regex Glob(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}

/// <summary>Eine VM oder ein Snapshot als Kandidat für ein Repository.</summary>
/// <param name="Snapshot"><c>null</c>: die VM selbst.</param>
/// <param name="Known"><c>false</c>: Komponenten unbekannt (nicht über CoreBridge entstanden, nie geprüft).</param>
public sealed record MatchCandidate(
    string Vm,
    string? Snapshot,
    string? State,
    IReadOnlyList<string> Components,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Surplus,
    bool Known)
{
    public bool Covers => Missing.Count == 0 && Known;

    public string Label => Snapshot is null ? Vm : $"{Vm}.{Snapshot}";
}

public enum MigrationKind
{
    /// <summary>Eine VM hat schon alles.</summary>
    UseVm,

    /// <summary>Ein Snapshot hat alles: zurücksetzen (vorher sichert vm-restore den aktuellen Stand).</summary>
    RestoreSnapshot,

    /// <summary>Die nächste VM klonen, Fehlendes installieren, Snapshot.</summary>
    CloneAndInstall,

    /// <summary>Nichts Brauchbares: Profil anlegen und VM daraus erzeugen.</summary>
    CreateFromProfile,
}

/// <summary>Der Weg zu einer VM, die das Repository bauen kann.</summary>
public sealed record MigrationPlan(
    MigrationKind Kind,
    MatchCandidate? Source,
    string Vm,
    IReadOnlyList<string> Required,
    IReadOnlyList<string> Missing,
    string? Profile = null)
{
    /// <summary>Die Befehle, wie man sie eintippen würde.</summary>
    public IReadOnlyList<string> Commands() => Kind switch
    {
        MigrationKind.UseVm => [$"cb vm-start -n {Vm}", $"cb repo-send -n {Vm}"],
        MigrationKind.RestoreSnapshot => [$"cb vm-restore -n {Vm} -s {Source!.Snapshot}", $"cb vm-start -n {Vm}", $"cb repo-send -n {Vm}"],
        MigrationKind.CloneAndInstall =>
        [
            $"cb vm-clone -s {Source!.Vm} -n {Vm}",
            $"cb vm-install -n {Vm} -c {string.Join(",", Missing)} --snapshot base",
            $"cb repo-send -n {Vm}"
        ],
        _ =>
        [
            $"cb init-configure -n {Profile} -c {string.Join(",", Required)}",
            $"cb vm-create -n {Vm} --profile {Profile} --snapshot base",
            $"cb repo-send -n {Vm}"
        ]
    };
}

/// <summary>
/// Ordnet VMs und Snapshots nach ihrer Eignung für ein Repository: volle Abdeckung zuerst,
/// dann die wenigsten fehlenden, dann der kleinste Überschuss; VMs vor Snapshots.
/// </summary>
public static class RepoMatcher
{
    public static IReadOnlyList<MatchCandidate> Rank(
        IReadOnlyList<string> required,
        VmInventory inventory,
        IReadOnlyList<VmInfo> vms,
        IReadOnlyList<VmSnapshotInfo> snapshots)
    {
        var candidates = new List<MatchCandidate>();
        foreach (var vm in vms.Where(vm => !vm.IsDeleted))
        {
            var entry = inventory.Vms.GetValueOrDefault(vm.Name);
            candidates.Add(Candidate(required, vm.Name, null, vm.State, entry?.Components ?? [], entry?.Known ?? false));
            foreach (var snapshot in snapshots.Where(s => string.Equals(s.Instance, vm.Name, StringComparison.OrdinalIgnoreCase)))
            {
                var recorded = entry?.Snapshots.GetValueOrDefault(snapshot.Name);
                var parsed = SnapshotComment.Parse(snapshot.Comment);
                var components = recorded?.Components ?? parsed?.Components;
                candidates.Add(Candidate(required, vm.Name, snapshot.Name, vm.State, components ?? [], components is not null));
            }
        }

        return candidates
            .OrderBy(c => c.Covers ? 0 : 1)
            .ThenBy(c => c.Known ? 0 : 1)
            .ThenBy(c => c.Missing.Count)
            .ThenBy(c => c.Surplus.Count)
            .ThenBy(c => c.Snapshot is null ? 0 : 1)
            .ThenBy(c => c.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Der Weg zur passenden VM: eine passende VM nutzen, sonst einen passenden Snapshot
    /// zurückholen, sonst die nächste bekannte VM klonen und ergänzen, sonst aus einem
    /// Profil neu anlegen. <paramref name="repoName"/> gibt Namen für neue VM und Profil.
    /// </summary>
    public static MigrationPlan Plan(IReadOnlyList<string> required, IReadOnlyList<MatchCandidate> ranked, string repoName, IEnumerable<string> existingVms)
    {
        if (ranked.FirstOrDefault(c => c.Covers && c.Snapshot is null) is { } vm)
        {
            return new MigrationPlan(MigrationKind.UseVm, vm, vm.Vm, required, []);
        }

        if (ranked.FirstOrDefault(c => c.Covers) is { } snapshot)
        {
            return new MigrationPlan(MigrationKind.RestoreSnapshot, snapshot, snapshot.Vm, required, []);
        }

        var name = NewName(repoName, existingVms);
        var closest = ranked.FirstOrDefault(c => c.Snapshot is null && c.Known && c.Components.Intersect(required, StringComparer.OrdinalIgnoreCase).Any());
        return closest is not null
            ? new MigrationPlan(MigrationKind.CloneAndInstall, closest, name, required, closest.Missing)
            : new MigrationPlan(MigrationKind.CreateFromProfile, null, name, required, required, Profile: name);
    }

    /// <summary>VM-Name aus dem Repo-Namen (Multipass-Regel), bei Kollision mit <c>-2</c>, <c>-3</c> …</summary>
    public static string NewName(string repoName, IEnumerable<string> existing)
    {
        var slug = Regex.Replace(repoName.ToLowerInvariant(), "[^a-z0-9-]+", "-").Trim('-');
        if (slug.Length == 0 || !char.IsAsciiLetter(slug[0]))
        {
            slug = "repo-" + slug;
        }

        slug = slug.Length > 40 ? slug[..40].TrimEnd('-') : slug;
        var taken = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = slug;
        for (var i = 2; taken.Contains(name); i++)
        {
            name = $"{slug}-{i}";
        }

        return name;
    }

    private static MatchCandidate Candidate(IReadOnlyList<string> required, string vm, string? snapshot, string? state, IReadOnlyList<string> components, bool known)
    {
        var have = components.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var need = required.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new MatchCandidate(
            vm,
            snapshot,
            state,
            components,
            required.Where(c => !have.Contains(c)).ToList(),
            components.Where(c => !need.Contains(c)).ToList(),
            known);
    }
}
