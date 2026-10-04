using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoreBridge.Core.Authenticity;
using CoreBridge.Core.Config;
using CoreBridge.Core.Multipass;
using Org.BouncyCastle.Bcpg.OpenPgp;

namespace CoreBridge.Core.Inits;

/// <summary>Woher eine Komponente kommt und wer sie signiert hat.</summary>
/// <param name="File">Datei der Definition (appsettings, Bundle oder Benutzerdatei).</param>
public sealed record ComponentOrigin(string File, ComponentSource Source, ComponentSignature Signature);

/// <summary>
/// Wo Komponenten liegen: <paramref name="AppDirectory"/> ist der Ordner der CLI
/// (mitgelieferte appsettings und <c>components/*.component.json</c>),
/// <paramref name="UserFolder"/> der StoreRoot (<c>appsettings.user.json</c>, weitere
/// <c>*.json</c> und <c>*.component.json</c>).
/// </summary>
public sealed record ComponentSources(string AppDirectory, string Environment, string UserFolder);

/// <summary>
/// Alle Komponenten aus allen Quellen, in dieser Reihenfolge:
/// <list type="number">
/// <item>mitgeliefert: <c>Inits:Components</c> aus appsettings.json, appsettings.{Environment}.json
/// und appsettings.local.json (diese Schichten überschreiben einander wie jede Konfiguration);</item>
/// <item>mitgelieferte Bundles: <c>&lt;app&gt;/components/*.component.json</c>;</item>
/// <item>Benutzerordner (StoreRoot): <c>appsettings.user.json</c>, alle weiteren <c>*.json</c>
/// und die Bundles <c>*.component.json</c>.</item>
/// </list>
/// Derselbe Name in zwei Quellen ist ein Fehler, der beide Dateien nennt. Jede Komponente
/// trägt ihre <see cref="ComponentOrigin"/> in einer Nebentabelle (nicht serialisiert):
/// die Signatur ihrer Definition und — bei einem <see cref="ComponentConfig.Script"/>-Pfad —
/// die des Skripts (<c>&lt;skript&gt;.asc</c>/<c>.clearsigned.asc</c>); es zählt die schwächere.
/// Ein Bundle trägt sein Skript inline (<see cref="ComponentConfig.ScriptContent"/>), eine
/// Signatur deckt dann beides. Laden wirft nie wegen einer Signatur — was eine ungültige
/// bedeutet, entscheidet der Aufrufer bei der Verwendung.
/// </summary>
public sealed class ComponentCatalog
{
    /// <summary>Endung eines Bundles: eine Komponente samt Skript in einer (clearsign-baren) Datei.</summary>
    public const string BundleExtension = ".component.json";

    /// <summary>Unterordner der mitgelieferten Bundles neben der CLI.</summary>
    public const string ShippedBundleFolder = "components";

    /// <summary>Dateien im Benutzerordner, die keine Einstellungen sind.</summary>
    private static readonly HashSet<string> NotSettings = new(StringComparer.OrdinalIgnoreCase) { "inventory.json" };

    private readonly Dictionary<string, ComponentOrigin> _origins;

    private ComponentCatalog(InitsConfig inits, Dictionary<string, ComponentOrigin> origins)
    {
        Inits = inits;
        _origins = origins;
    }

    /// <summary>Die Konfiguration mit allen Komponenten aus allen Quellen.</summary>
    public InitsConfig Inits { get; }

    public IReadOnlyDictionary<string, ComponentOrigin> Origins => _origins;

    /// <summary>
    /// Lädt alle Quellen. <paramref name="trusted"/> ist das Bundle für Komponenten
    /// (CoreBridge plus Vertrauensordner); die mitgelieferten appsettings prüft nur
    /// der CoreBridge-Schlüssel. Wirft <see cref="MultipassException"/> bei doppelten
    /// Namen und unlesbaren Dateien.
    /// </summary>
    public static ComponentCatalog Load(AppConfig config, ComponentSources sources, PgpPublicKeyRingBundle trusted, DateTime utcNow)
    {
        var components = new Dictionary<string, ComponentConfig>(StringComparer.OrdinalIgnoreCase);
        var origins = new Dictionary<string, ComponentOrigin>(StringComparer.OrdinalIgnoreCase);

        // 1. Mitgeliefert: gebunden ist schon alles, gesucht wird nur die Datei, die den Namen zuletzt setzt.
        var shippedFiles = new[] { "appsettings.local.json", $"appsettings.{sources.Environment}.json", "appsettings.json" }
            .Select(name => Path.Combine(sources.AppDirectory, name))
            .Where(File.Exists)
            .Select(file => (File: file, Read: SettingsSignatures.Read(file, File.ReadAllBytes(file), TrustedKeys.CoreBridge, utcNow)))
            .Select(entry => (entry.File, entry.Read.Check, Names: ComponentNames(entry.Read.Content)))
            .ToList();
        foreach (var (name, component) in config.Inits.Components)
        {
            var definedIn = shippedFiles.FirstOrDefault(file => file.Names.Contains(name));
            var signature = definedIn.File is null
                ? new ComponentSignature(ComponentTrust.Unsigned, "Konfiguration", "nur aus Umgebungsvariablen oder dem Aufrufer, ohne Datei.")
                : ComponentSignature.From(definedIn.Check);
            components[name] = component;
            origins[name] = new ComponentOrigin(definedIn.File ?? "Konfiguration", ComponentSource.BuiltIn, signature);
        }

        // 2. Mitgelieferte Bundles, 3. Benutzerordner.
        var shippedBundles = Path.Combine(sources.AppDirectory, ShippedBundleFolder);
        foreach (var file in Bundles(shippedBundles))
        {
            AddFile(file, ComponentSource.BuiltIn, bundle: true);
        }

        foreach (var file in UserFiles(sources.UserFolder))
        {
            AddFile(file, ComponentSource.User, bundle: file.EndsWith(BundleExtension, StringComparison.OrdinalIgnoreCase));
        }

        // Ein Skriptpfad braucht eine eigene Signatur; es zählt die schwächere.
        foreach (var (name, component) in components)
        {
            if (string.IsNullOrWhiteSpace(component.ScriptContent) && !string.IsNullOrWhiteSpace(component.Script))
            {
                var script = InitStore.ResolvePath(component.Script);
                var scriptSignature = File.Exists(script)
                    ? ComponentSignature.From(SettingsSignatures.Read(script, File.ReadAllBytes(script), trusted, utcNow).Check)
                    : new ComponentSignature(ComponentTrust.Unsigned, script, "Skript fehlt.");
                origins[name] = origins[name] with { Signature = ComponentSignature.Worst(origins[name].Signature, scriptSignature) };
            }
        }

        return new ComponentCatalog(config.Inits with { Components = components }, origins);

        void AddFile(string file, ComponentSource source, bool bundle)
        {
            var read = SettingsSignatures.Read(file, File.ReadAllBytes(file), trusted, utcNow);
            var entries = ParseComponents(read.Content, file);
            if (bundle)
            {
                RequireBundleShape(file, entries);
            }

            var signature = ComponentSignature.From(read.Check);
            foreach (var (name, component) in entries)
            {
                if (origins.TryGetValue(name, out var existing))
                {
                    throw new MultipassException(
                        $"Komponente '{name}' ist doppelt definiert: {existing.File} und {file} — einen der beiden Einträge entfernen oder umbenennen.");
                }

                components[name] = component;
                origins[name] = new ComponentOrigin(file, source, signature);
            }
        }
    }

    /// <summary>Die Herkunft von <paramref name="name"/>; <c>null</c> für unbekannte Komponenten.</summary>
    public ComponentOrigin? OriginOf(string name) => _origins.GetValueOrDefault(name);

    /// <summary>
    /// Die Komponenten mit Herkunft und Signatur, alphabetisch — für <c>component-list</c>.
    /// </summary>
    public IReadOnlyList<(string Name, ComponentConfig Component, ComponentOrigin Origin)> Entries() =>
        Inits.Components
            .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Select(entry => (entry.Key, entry.Value, _origins[entry.Key]))
            .ToList();

    /// <summary>Bundles (<c>*.component.json</c>) eines Ordners, sortiert; ein fehlender Ordner ist leer.</summary>
    public static IEnumerable<string> Bundles(string folder) =>
        Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*" + BundleExtension).Order(StringComparer.OrdinalIgnoreCase)
            : [];

    /// <summary>
    /// Einstellungsdateien des Benutzerordners: <c>appsettings.user.json</c> zuerst, dann alle
    /// weiteren <c>*.json</c> (ohne <c>inventory.json</c>) samt Bundles, sortiert.
    /// </summary>
    public static IEnumerable<string> UserFiles(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }

        return Directory.EnumerateFiles(folder, "*.json")
            .Where(file => !NotSettings.Contains(Path.GetFileName(file)))
            .OrderBy(file => Path.GetFileName(file).Equals("appsettings.user.json", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(file => file, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Name eines Bundles laut Dateiname: <c>node.component.json</c> → <c>node</c>.</summary>
    public static string BundleName(string file)
    {
        var name = Path.GetFileName(file);
        return name.EndsWith(BundleExtension, StringComparison.OrdinalIgnoreCase)
            ? name[..^BundleExtension.Length]
            : Path.GetFileNameWithoutExtension(name);
    }

    /// <summary>
    /// Ein Bundle ist eine Einstellungsdatei mit genau einer Komponente, deren Name dem
    /// Dateinamen entspricht — so deckt die Signatur auch den Namen, und Umbenennen
    /// einer signierten Datei macht aus <c>node</c> kein <c>claude</c>.
    /// </summary>
    public static void RequireBundleShape(string file, IReadOnlyDictionary<string, ComponentConfig> entries)
    {
        var expected = BundleName(file);
        if (entries.Count != 1 || !entries.ContainsKey(expected))
        {
            throw new MultipassException(
                $"{file}: ein Bundle enthält genau die Komponente '{expected}' unter Inits:Components (gefunden: {(entries.Count == 0 ? "keine" : string.Join(", ", entries.Keys))}).");
        }
    }

    /// <summary>Die Komponenten einer Einstellungsdatei (<c>Inits:Components</c>); fehlt der Abschnitt, keine.</summary>
    public static IReadOnlyDictionary<string, ComponentConfig> ParseComponents(byte[] content, string file)
    {
        var components = new Dictionary<string, ComponentConfig>(StringComparer.OrdinalIgnoreCase);
        var node = ParseRoot(content, file)["Inits"]?["Components"];
        if (node is null)
        {
            return components;
        }

        if (node is not JsonObject entries)
        {
            throw new MultipassException($"{file}: Inits:Components muss ein Objekt sein.");
        }

        foreach (var (name, value) in entries)
        {
            components[name] = InitComponentStore.Deserialize(value?.ToJsonString() ?? "null", $"{file} (Komponente '{name}')");
        }

        return components;
    }

    /// <summary>Bundle-Inhalt für <c>component-bundle</c>: <c>{ "Inits": { "Components": { name: … } } }</c>.</summary>
    public static string BundleJson(string name, ComponentConfig component)
    {
        var root = new JsonObject
        {
            ["Inits"] = new JsonObject
            {
                ["Components"] = new JsonObject { [name] = JsonNode.Parse(InitComponentStore.ToJson(component)) }
            }
        };
        return root.ToJsonString(InitComponentStore.WriteOptions) + "\n";
    }

    internal static JsonObject ParseRoot(byte[] content, string file)
    {
        try
        {
            return JsonNode.Parse(
                    Encoding.UTF8.GetString(content).TrimStart('﻿'),
                    documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                as JsonObject
                ?? throw new MultipassException($"{file}: erwartet ein JSON-Objekt.");
        }
        catch (JsonException exception)
        {
            throw new MultipassException($"{file}: kein gültiges JSON — {exception.Message}", exception);
        }
    }

    /// <summary>Namen unter <c>Inits:Components</c>, ohne die Definitionen zu prüfen (die bindet die Konfiguration).</summary>
    private static HashSet<string> ComponentNames(byte[] content)
    {
        try
        {
            var root = JsonNode.Parse(
                Encoding.UTF8.GetString(content).TrimStart('﻿'),
                documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return root?["Inits"]?["Components"] is JsonObject entries
                ? entries.Select(entry => entry.Key).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
