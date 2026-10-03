using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text;
using System.Text.RegularExpressions;
using CoreBridge.Core.Authenticity;
using CoreBridge.Core.Config;
using CoreBridge.Core.Multipass;
using Org.BouncyCastle.Bcpg.OpenPgp;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace CoreBridge.Core.Inits;

/// <summary>Eingabeformat einer Komponenten-Definition für <c>component-add</c>.</summary>
public enum ComponentFormat
{
    Json,
    Yaml
}

/// <summary>Herkunft einer Komponente: mitgeliefert (appsettings) oder vom Benutzer angelegt.</summary>
public enum ComponentSource
{
    BuiltIn,
    User
}

public sealed record ComponentListEntry(string Name, ComponentSource Source, ComponentConfig Component);

/// <summary>
/// Benutzereigene Komponenten in <see cref="InitStore.UserSettingsFile"/>
/// (<c>&lt;StoreRoot&gt;/appsettings.user.json</c>, Form
/// <c>{ "Inits": { "Components": { … } } }</c>). Die mitgelieferten Komponenten
/// aus appsettings.json bleiben unangetastet: hinzufügen und entfernen wirkt nur
/// auf die Benutzerdatei, damit ein Update der Installation nichts überschreibt.
/// Die Datei darf unsigniert sein; ist sie signiert (clearsigned oder mit
/// <c>.clearsigned.asc</c>/<c>.asc</c> daneben), muss die Signatur gültig sein
/// (<see cref="SignaturePolicy.Optional"/>), und hinzufügen/entfernen lehnt ab,
/// statt die Signatur stillschweigend ungültig zu machen.
/// </summary>
public static class InitComponentStore
{
    private static readonly Regex NamePattern = new(
        "^[a-z](?:[a-z0-9-]*[a-z0-9])?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>Lesbar geschrieben (Umlaute, Anführungszeichen im Skript), damit man prüft, was man signiert.</summary>
    internal static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// Ergänzt <paramref name="config"/> um die Komponenten der Benutzerdatei.
    /// Nur <c>Inits:Components</c> wird gelesen — die Datei kann weder StoreRoot
    /// noch andere Einstellungen ändern. Ein Name, den auch appsettings
    /// definiert, ist ein Fehler statt eines stillen Zusammenführens.
    /// </summary>
    public static AppConfig Merge(AppConfig config, string userFile, PgpPublicKeyRingBundle? trusted = null)
    {
        var user = ReadUser(userFile, trusted);
        if (user.Count == 0)
        {
            return config;
        }

        var merged = new Dictionary<string, ComponentConfig>(config.Inits.Components, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, component) in user)
        {
            if (!merged.TryAdd(name, component))
            {
                throw new MultipassException(
                    $"Komponente '{name}' ist mitgeliefert und steht zusätzlich in {userFile} — mitgelieferte Komponenten lassen sich nicht überschreiben; Eintrag dort entfernen oder umbenennen.");
            }
        }

        return config with { Inits = config.Inits with { Components = merged } };
    }

    /// <summary>
    /// Komponenten der Benutzerdatei; fehlt die Datei, ist die Liste leer. Eine ungültige
    /// Signatur wirft (<see cref="SignaturePolicy.Optional"/>), geprüft gegen
    /// <paramref name="trusted"/> (Standard: der CoreBridge-Schlüssel).
    /// </summary>
    public static IReadOnlyDictionary<string, ComponentConfig> ReadUser(string userFile, PgpPublicKeyRingBundle? trusted = null)
    {
        return File.Exists(userFile)
            ? ComponentCatalog.ParseComponents(Unwrap(userFile, trusted), userFile)
            : new Dictionary<string, ComponentConfig>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Alle wirksamen Komponenten mit Herkunft, alphabetisch.</summary>
    /// <param name="inits">Die bereits per <see cref="Merge"/> ergänzte Konfiguration.</param>
    public static IReadOnlyList<ComponentListEntry> List(InitsConfig inits, string userFile)
    {
        var user = ReadUser(userFile);
        return inits.Components
            .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Select(entry => new ComponentListEntry(
                entry.Key,
                user.ContainsKey(entry.Key) ? ComponentSource.User : ComponentSource.BuiltIn,
                entry.Value))
            .ToArray();
    }

    /// <summary>
    /// Liest eine Definition aus einer Datei oder, wenn <paramref name="value"/>
    /// keine existierende Datei ist, direkt aus dem Wert.
    /// </summary>
    public static ComponentConfig ParseArgument(string value, ComponentFormat format)
    {
        if (File.Exists(value))
        {
            return Parse(File.ReadAllText(value), format, value);
        }

        var trimmed = value.Trim();
        if (trimmed.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
        {
            throw new MultipassException($"Datei nicht gefunden: {Path.GetFullPath(trimmed)}");
        }

        return Parse(value, format, format == ComponentFormat.Json ? "--json" : "--yaml");
    }

    /// <summary>
    /// Parst eine einzelne <see cref="ComponentConfig"/>. Schlüssel wie in
    /// appsettings.json (Groß-/Kleinschreibung egal); unbekannte Schlüssel sind
    /// ein Fehler, damit Tippfehler nicht still verschwinden.
    /// </summary>
    public static ComponentConfig Parse(string text, ComponentFormat format, string source = "Eingabe")
    {
        var json = format == ComponentFormat.Json ? text : YamlToJson(text, source);
        return Deserialize(json, source);
    }

    /// <summary>
    /// Prüft Name und Definition und schreibt sie in die Benutzerdatei. Ein
    /// relativer <see cref="ComponentConfig.Script"/> wird gegen das aktuelle
    /// Arbeitsverzeichnis aufgelöst und absolut gespeichert.
    /// </summary>
    /// <returns>Die gespeicherte (normalisierte) Definition.</returns>
    public static ComponentConfig Add(InitsConfig inits, string userFile, string name, ComponentConfig component, bool replace)
    {
        RequireValidName(name);
        var user = ReadUser(userFile);
        var exists = user.ContainsKey(name);
        if (!exists && inits.Components.ContainsKey(name))
        {
            throw new MultipassException(
                $"Komponente '{name}' ist mitgeliefert (appsettings.json) und lässt sich nicht ändern — anderen Namen wählen.");
        }

        if (exists && !replace)
        {
            throw new MultipassException($"Komponente '{name}' existiert bereits ({userFile}) — --force zum Ersetzen.");
        }

        var normalized = Normalize(component);
        Validate(name, normalized);

        var root = File.Exists(userFile) ? ReadRoot(userFile) : new JsonObject();
        var entries = Section(root, userFile);
        var key = entries.Select(entry => entry.Key).FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
        if (key is not null)
        {
            entries.Remove(key);
        }

        entries[name] = JsonSerializer.SerializeToNode(normalized, WriteOptions);
        Write(userFile, root);
        return normalized;
    }

    /// <summary>Entfernt eine Benutzerkomponente; mitgelieferte sind nicht entfernbar.</summary>
    public static void Remove(InitsConfig inits, string userFile, string name)
    {
        var user = ReadUser(userFile);
        if (!user.ContainsKey(name))
        {
            throw new MultipassException(inits.Components.ContainsKey(name)
                ? $"Komponente '{name}' ist mitgeliefert (appsettings.json) und lässt sich nicht entfernen."
                : $"Unbekannte Komponente '{name}'. Übersicht: `cb component-list`.");
        }

        var root = ReadRoot(userFile);
        var entries = Section(root, userFile);
        var key = entries.Select(entry => entry.Key).First(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
        entries.Remove(key);
        Write(userFile, root);
    }

    /// <summary>Signaturdateien, die neben einer Datei liegen können.</summary>
    private static readonly string[] Companions = [SettingsSignatures.ClearsignedExtension, PgpFileVerifier.SignatureExtension];

    /// <summary>
    /// Kopiert ein Bundle <em>unverändert</em> als <c>&lt;name&gt;.component.json</c> in den
    /// Benutzerordner — samt <c>.clearsigned.asc</c>/<c>.asc</c> daneben, damit die Signatur
    /// gültig bleibt. Geprüft wird vorher: Form (genau eine Komponente, Name wie die Datei),
    /// Definition (Probe-Rendern) und dass der Name in keiner anderen Quelle steht.
    /// </summary>
    /// <param name="catalog">Die geladenen Komponenten aller Quellen (für den Namenskonflikt).</param>
    /// <returns>Name, Definition und Zieldatei.</returns>
    public static (string Name, ComponentConfig Component, string File) AddBundle(
        ComponentCatalog catalog, string userFolder, string bundleFile, bool replace)
    {
        if (!File.Exists(bundleFile))
        {
            throw new MultipassException($"Bundle nicht gefunden: {Path.GetFullPath(bundleFile)}");
        }

        if (!bundleFile.EndsWith(ComponentCatalog.BundleExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new MultipassException($"{bundleFile}: ein Bundle heißt <name>{ComponentCatalog.BundleExtension} (erzeugen: cb component-bundle -n <name>).");
        }

        // Nur zum Lesen auspacken; kopiert wird die Datei, wie sie ist.
        var read = SettingsSignatures.Read(bundleFile, File.ReadAllBytes(bundleFile), TrustedKeys.CoreBridge, DateTime.UtcNow);
        var entries = ComponentCatalog.ParseComponents(read.Content, bundleFile);
        ComponentCatalog.RequireBundleShape(bundleFile, entries);
        var (name, component) = entries.Single();
        RequireValidName(name);
        if (!string.IsNullOrWhiteSpace(component.Script))
        {
            throw new MultipassException($"{bundleFile}: ein Bundle trägt sein Skript inline (ScriptContent), nicht als Pfad (Script).");
        }

        Validate(name, component);

        var target = Path.Combine(userFolder, name + ComponentCatalog.BundleExtension);
        if (catalog.OriginOf(name) is { } existing)
        {
            var sameFile = string.Equals(Path.GetFullPath(existing.File), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase);
            if (!sameFile)
            {
                throw new MultipassException(
                    $"Komponente '{name}' gibt es schon ({existing.File}) — {(existing.Source == ComponentSource.BuiltIn ? "mitgelieferte lassen sich nicht ersetzen" : "dort entfernen (cb component-remove -n " + name + ")")}.");
            }

            if (!replace)
            {
                throw new MultipassException($"Komponente '{name}' existiert bereits ({target}) — --force zum Ersetzen.");
            }
        }

        Directory.CreateDirectory(userFolder);
        File.Copy(bundleFile, target, overwrite: true);
        foreach (var extension in Companions)
        {
            if (File.Exists(bundleFile + extension))
            {
                File.Copy(bundleFile + extension, target + extension, overwrite: true);
            }
            else if (File.Exists(target + extension))
            {
                // Eine alte Signatur gehört nicht zum neuen Inhalt.
                File.Delete(target + extension);
            }
        }

        return (name, component, target);
    }

    /// <summary>Entfernt ein Bundle aus dem Benutzerordner samt Signaturdateien.</summary>
    public static void RemoveBundle(string bundleFile)
    {
        File.Delete(bundleFile);
        foreach (var extension in Companions)
        {
            File.Delete(bundleFile + extension);
        }
    }

    /// <summary>
    /// Eine Komponente als Bundle: das Skript (aus <see cref="ComponentConfig.Script"/>)
    /// wandert inline nach <see cref="ComponentConfig.ScriptContent"/>, der Pfad entfällt.
    /// </summary>
    public static string Bundle(string name, ComponentConfig component)
    {
        var inline = component.HasScript
            ? component with { ScriptContent = InitInstall.ScriptText(component), Script = null }
            : component;
        return ComponentCatalog.BundleJson(name, inline);
    }

    /// <summary>Eine Definition als eingerücktes JSON, wie sie in der Benutzerdatei steht.</summary>
    public static string ToJson(ComponentConfig component) => JsonSerializer.Serialize(component, WriteOptions);

    private static void RequireValidName(string name)
    {
        if (!NamePattern.IsMatch(name))
        {
            throw new MultipassException(
                $"Ungültiger Komponentenname '{name}' — Kleinbuchstaben, Ziffern und Bindestriche, beginnend mit einem Buchstaben (z. B. my-tool).");
        }
    }

    private static ComponentConfig Normalize(ComponentConfig component)
    {
        if (string.IsNullOrWhiteSpace(component.Script))
        {
            return component with { Script = null };
        }

        var expanded = Environment.ExpandEnvironmentVariables(component.Script.Trim());
        var absolute = Path.GetFullPath(expanded);
        if (!File.Exists(absolute))
        {
            throw new MultipassException($"Script nicht gefunden: {absolute}");
        }

        return component with { Script = absolute };
    }

    /// <summary>
    /// Probe-Rendern nur dieser Komponente: so greifen dieselben Prüfungen wie
    /// bei <c>init-configure</c> (InstallPath, InstallUser, Pakete, YAML).
    /// </summary>
    private static void Validate(string name, ComponentConfig component)
    {
        if (!component.NeedsCloudInit)
        {
            throw new MultipassException(
                $"Komponente '{name}' ist leer — mindestens Snap, Script, Packages oder Users angeben.");
        }

        if (component.HasScript && string.IsNullOrWhiteSpace(component.InstallPath))
        {
            throw new MultipassException(
                $"Komponente '{name}': Script braucht einen InstallPath (absoluter Unix-Pfad im Gast, z. B. /usr/local/sbin/corebridge-install-{name}).");
        }

        var probe = new InitsConfig
        {
            Components = new Dictionary<string, ComponentConfig>(StringComparer.OrdinalIgnoreCase) { [name] = component }
        };
        CloudInitYamlValidator.Validate(InitCloudInit.Generate(name, probe, [name]));
    }

    internal static ComponentConfig Deserialize(string json, string source)
    {
        try
        {
            return JsonSerializer.Deserialize<ComponentConfig>(json, ReadOptions)
                ?? throw new MultipassException($"{source}: leere Komponenten-Definition.");
        }
        catch (JsonException exception)
        {
            throw new MultipassException($"{source}: ungültige Komponenten-Definition — {exception.Message}", exception);
        }
    }

    private static JsonObject ReadRoot(string userFile) => ComponentCatalog.ParseRoot(Unwrap(userFile, null), userFile);

    private static byte[] Unwrap(string userFile, PgpPublicKeyRingBundle? trusted) =>
        SettingsSignatures.Unwrap(
            userFile, File.ReadAllBytes(userFile), SignaturePolicy.Optional, trusted ?? TrustedKeys.CoreBridge, DateTime.UtcNow);

    /// <summary>Liefert <c>Inits:Components</c> und legt fehlende Ebenen an.</summary>
    private static JsonObject Section(JsonObject root, string userFile)
    {
        if (root["Inits"] is not JsonObject inits)
        {
            if (root["Inits"] is not null)
            {
                throw new MultipassException($"{userFile}: Inits muss ein Objekt sein.");
            }

            inits = new JsonObject();
            root["Inits"] = inits;
        }

        if (inits["Components"] is not JsonObject components)
        {
            if (inits["Components"] is not null)
            {
                throw new MultipassException($"{userFile}: Inits:Components muss ein Objekt sein.");
            }

            components = new JsonObject();
            inits["Components"] = components;
        }

        return components;
    }

    private static void Write(string userFile, JsonObject root)
    {
        if (SettingsSignatures.IsSigned(userFile))
        {
            throw new MultipassException(
                $"{userFile} ist signiert — eine Änderung machte die Signatur ungültig. Signatur entfernen, ändern und neu signieren.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(userFile))!);
        var temp = userFile + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(WriteOptions) + Environment.NewLine);
        File.Move(temp, userFile, overwrite: true);
    }

    /// <summary>
    /// YAML → JSON über das Repräsentationsmodell. Ungequotete Skalare werden wie
    /// in YAML 1.2 typisiert (true/false, Zahlen, null); gequotete bleiben Text.
    /// </summary>
    private static string YamlToJson(string text, string source)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(text));
        }
        catch (YamlException exception)
        {
            throw new MultipassException($"{source}: kein gültiges YAML — {exception.Message}", exception);
        }

        if (stream.Documents.Count != 1)
        {
            throw new MultipassException($"{source}: erwartet genau ein YAML-Dokument.");
        }

        return ToNode(stream.Documents[0].RootNode)?.ToJsonString() ?? "null";
    }

    private static JsonNode? ToNode(YamlNode node)
    {
        switch (node)
        {
            case YamlMappingNode mapping:
                var obj = new JsonObject();
                foreach (var (key, child) in mapping.Children)
                {
                    obj[((YamlScalarNode)key).Value ?? string.Empty] = ToNode(child);
                }

                return obj;
            case YamlSequenceNode sequence:
                return new JsonArray(sequence.Children.Select(ToNode).ToArray());
            case YamlScalarNode scalar:
                var value = scalar.Value ?? string.Empty;
                if (scalar.Style is not ScalarStyle.Plain)
                {
                    return JsonValue.Create(value);
                }

                return value switch
                {
                    "" or "~" or "null" or "Null" or "NULL" => null,
                    "true" or "True" or "TRUE" => JsonValue.Create(true),
                    "false" or "False" or "FALSE" => JsonValue.Create(false),
                    _ when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => JsonValue.Create(number),
                    _ => JsonValue.Create(value)
                };
            default:
                throw new MultipassException($"Nicht unterstützter YAML-Knoten: {node.NodeType}");
        }
    }
}
