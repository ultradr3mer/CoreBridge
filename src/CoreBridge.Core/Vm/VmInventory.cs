using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CoreBridge.Core.Vm;

/// <summary>Was CoreBridge über eine VM weiß: Profil, Komponenten, Ressourcen, Snapshots.</summary>
public sealed class InventoryVm
{
    public string? Profile { get; set; }

    /// <summary>Installierte Komponenten; die Wahrheit sind die <c>.ready</c>-Marker im Gast (<c>vm-inventory --refresh</c>).</summary>
    public List<string> Components { get; set; } = [];

    /// <summary><c>false</c>: die VM ist nicht über CoreBridge entstanden und noch nie geprüft worden.</summary>
    public bool Known { get; set; } = true;

    public int? Cpus { get; set; }

    public string? Memory { get; set; }

    public string? Disk { get; set; }

    public DateTime? Created { get; set; }

    /// <summary>Zuletzt per Marker im Gast abgeglichen.</summary>
    public DateTime? Verified { get; set; }

    public Dictionary<string, InventorySnapshot> Snapshots { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Stand einer VM zum Zeitpunkt des Snapshots.</summary>
public sealed class InventorySnapshot
{
    public List<string> Components { get; set; } = [];

    public string? Profile { get; set; }

    public DateTime? Taken { get; set; }
}

/// <summary>
/// Host-seitiges Inventar (<c>&lt;StoreRoot&gt;/inventory.json</c>): je VM Profil, Komponenten,
/// CPU/Memory/Disk und Anlagedatum, je Snapshot die Komponenten; dazu die gemerkte VM je
/// Repository (<c>cb work</c>). Gepflegt von create, install, clone, snapshot, restore,
/// resize und delete --purge. Die Wahrheit über Komponenten sind die <c>.ready</c>-Marker
/// im Gast — <c>vm-inventory --refresh</c> gleicht ab.
/// </summary>
public sealed class VmInventory
{
    public const string FileName = "inventory.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    public int SchemaVersion { get; set; } = 1;

    public Dictionary<string, InventoryVm> Vms { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gemerkte VM je Repository-Wurzel (voller Pfad).</summary>
    public Dictionary<string, string> Repos { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Lädt das Inventar; fehlt die Datei, ist es leer. Unlesbar ist ein Fehler, kein stilles Verwerfen.</summary>
    public static VmInventory Load(string path)
    {
        if (!File.Exists(path))
        {
            return new VmInventory();
        }

        try
        {
            var inventory = JsonSerializer.Deserialize<VmInventory>(File.ReadAllText(path), Options) ?? new VmInventory();
            // Wörterbücher aus JSON unterscheiden Groß-/Kleinschreibung — Multipass-Namen nicht.
            inventory.Vms = new Dictionary<string, InventoryVm>(inventory.Vms, StringComparer.OrdinalIgnoreCase);
            inventory.Repos = new Dictionary<string, string>(inventory.Repos, StringComparer.OrdinalIgnoreCase);
            foreach (var vm in inventory.Vms.Values)
            {
                vm.Snapshots = new Dictionary<string, InventorySnapshot>(vm.Snapshots, StringComparer.OrdinalIgnoreCase);
            }

            return inventory;
        }
        catch (JsonException exception)
        {
            throw new Multipass.MultipassException($"Inventar unlesbar: {path} — {exception.Message} (löschen und mit `cb vm-inventory --refresh` neu aufbauen).", exception);
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Options) + Environment.NewLine);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Lädt, ändert und speichert in einem Zug.</summary>
    public static void Update(string path, Action<VmInventory> change)
    {
        var inventory = Load(path);
        change(inventory);
        inventory.Save(path);
    }

    /// <summary>Der Eintrag der VM; legt ihn an (als unbekannt), wenn es ihn noch nicht gibt.</summary>
    public InventoryVm Vm(string name)
    {
        if (!Vms.TryGetValue(name, out var vm))
        {
            vm = new InventoryVm { Known = false };
            Vms[name] = vm;
        }

        return vm;
    }

    /// <summary>Komponenten zusammenführen, sortiert und ohne Dubletten.</summary>
    public static List<string> Merge(IEnumerable<string> existing, IEnumerable<string> added) =>
        existing.Concat(added)
            .Select(name => name.Trim())
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
}

/// <summary>
/// Maschinenlesbarer Teil eines Snapshot-Kommentars: <c>corebridge: components=claude,dotnet,git; profile=dev</c>.
/// So kennt auch ein Snapshot, den das Inventar nicht (mehr) hat, seine Komponenten.
/// </summary>
public static partial class SnapshotComment
{
    /// <summary>Freitext (optional) plus der maschinenlesbare Teil.</summary>
    public static string Format(string? text, IEnumerable<string> components, string? profile)
    {
        var tag = $"corebridge: components={string.Join(",", components)}" + (string.IsNullOrWhiteSpace(profile) ? string.Empty : $"; profile={profile}");
        return string.IsNullOrWhiteSpace(text) ? tag : $"{text.Trim()} — {tag}";
    }

    /// <summary>Komponenten und Profil aus einem Kommentar; <c>null</c>, wenn er keinen CoreBridge-Teil hat.</summary>
    public static (List<string> Components, string? Profile)? Parse(string? comment)
    {
        var match = comment is null ? Match.Empty : Pattern().Match(comment);
        if (!match.Success)
        {
            return null;
        }

        var components = match.Groups["components"].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        return (components, match.Groups["profile"].Success ? match.Groups["profile"].Value : null);
    }

    [GeneratedRegex(@"corebridge: components=(?<components>[A-Za-z0-9_,-]*)(?:; profile=(?<profile>[A-Za-z0-9_.-]+))?", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
