using CoreBridge.Core.Common;
using CoreBridge.Core.Config;
using CoreBridge.Core.Multipass;

namespace CoreBridge.Core.Inits;

/// <summary>
/// Ein Schritt von <c>vm-install</c>: entweder ein Shell-Kommando im Gast oder
/// das Hochladen eines Komponenten-Skripts.
/// </summary>
/// <param name="Title">Kurzbeschreibung für Ausgabe und Fehlermeldung.</param>
/// <param name="Command">Shell-Zeile für <c>multipass exec … /bin/sh -c</c>; <c>null</c> bei einem Upload.</param>
/// <param name="ScriptSource">Lokales Skript (LF-normalisiert hochzuladen); nur bei einem Upload.</param>
/// <param name="RemotePath">Ziel des Uploads im Gast.</param>
/// <param name="Marker">Bei einer Prüfung: der <c>.ready</c>-Marker, dessen Fehlen ein Fehler ist.</param>
public sealed record InstallStep(
    string Title,
    string? Command,
    string? ScriptSource = null,
    string? RemotePath = null,
    string? Marker = null)
{
    public bool IsUpload => ScriptSource is not null;
}

/// <summary>
/// Übersetzt Komponenten in die Schritte, die cloud-init beim Anlegen ausführen
/// würde — für VMs, die schon laufen (<c>vm-install</c>). Reihenfolge wie cloud-init:
/// Paketquellen, Pakete, Snaps, dann je Komponente Skript hochladen, installieren,
/// ausführen und den <c>.ready</c>-Marker prüfen.
/// </summary>
public static class InitInstall
{
    public const string MarkerDirectory = "/var/lib/corebridge";

    /// <summary>
    /// Wartet im Gast, bis cloud-init fertig ist — auch wenn es mit Fehlern endet.
    /// Ob die Komponenten installiert sind, sagt erst ihr Marker: das runcmd-Skript
    /// von cloud-init ist ein schlichtes <c>sh</c> ohne <c>set -e</c>, sein Exit-Code
    /// ist der des letzten Skripts, cloud-init meldet also auch nach einem
    /// gescheiterten früheren Skript Erfolg.
    /// </summary>
    public const string WaitForCloudInitCommand = "cloud-init status --wait >/dev/null 2>&1 || true";

    public static string MarkerPath(string component) => $"{MarkerDirectory}/{component}.ready";

    /// <summary>Shell-Zeile, die genau dann mit 0 endet, wenn der Marker existiert.</summary>
    public static string MarkerProbe(string marker) => $"test -f {Quote(marker)}";

    /// <summary>
    /// Die <c>.ready</c>-Marker der Komponenten: jede Komponente mit Skript setzt
    /// ihren eigenen, als letzten Schritt nach ihren Prüfungen. Reine Snap-Komponenten
    /// haben kein Skript und damit keinen Marker.
    /// </summary>
    public static IReadOnlyList<(string Component, string Marker)> Markers(InitsConfig config, IEnumerable<string> components)
    {
        var names = components.Select(c => c.Trim()).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var resolved = InitComponents.Resolve(config, names);
        return names
            .Zip(resolved)
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Second.Script))
            .Select(pair => (pair.First, MarkerPath(pair.First)))
            .ToList();
    }

    public static IReadOnlyList<InstallStep> Plan(InitsConfig config, IEnumerable<string> components)
    {
        var names = components.Select(c => c.Trim()).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var resolved = InitComponents.Resolve(config, names);
        var steps = new List<InstallStep>
        {
            // Beim ersten Start einer VM kann cloud-init noch apt sperren.
            new("warten, bis cloud-init fertig ist", WaitForCloudInitCommand)
        };

        if (resolved.Any(c => c.PackageUpdate))
        {
            steps.Add(new("Paketquellen aktualisieren", "sudo apt-get -o DPkg::Lock::Timeout=300 update"));
        }

        var packages = resolved.SelectMany(c => c.Packages).Distinct(StringComparer.Ordinal).ToArray();
        if (packages.Length > 0)
        {
            steps.Add(new(
                $"Pakete installieren ({string.Join(", ", packages)})",
                "sudo DEBIAN_FRONTEND=noninteractive apt-get -o DPkg::Lock::Timeout=300 install -y "
                    + string.Join(' ', packages.Select(Quote))));
        }

        foreach (var component in resolved.Where(c => !string.IsNullOrWhiteSpace(c.Snap)))
        {
            steps.Add(new(
                $"Snap {component.Snap} installieren",
                $"sudo snap wait system seed.loaded && sudo snap install {Quote(component.Snap!)}{(component.Classic ? " --classic" : string.Empty)}"));
        }

        for (var i = 0; i < names.Length; i++)
        {
            var (name, component) = (names[i], resolved[i]);
            if (string.IsNullOrWhiteSpace(component.Script))
            {
                continue;
            }

            var source = InitStore.ResolvePath(component.Script);
            if (!File.Exists(source))
            {
                throw new MultipassException($"Komponenten-Skript fehlt: {source}");
            }

            if (string.IsNullOrWhiteSpace(component.InstallPath))
            {
                throw new MultipassException($"Komponente '{name}' hat ein Skript, aber keinen InstallPath.");
            }

            var upload = $"/tmp/corebridge-install-{name}.sh";
            var user = component.InstallUser;
            steps.Add(new($"{name}: Skript hochladen", null, source, upload));
            steps.Add(new(
                $"{name}: Skript nach {component.InstallPath} installieren",
                $"sudo install -m 0755 -o root -g root {Quote(upload)} {Quote(component.InstallPath)}"));
            steps.Add(new($"{name}: Skript ausführen (als {user})", $"sudo {Quote(component.InstallPath)} {Quote(user)}"));
            var marker = MarkerPath(name);
            steps.Add(new($"{name}: Marker {marker} prüfen", MarkerProbe(marker), Marker: marker));
        }

        return steps;
    }

    /// <summary>Skriptinhalt mit LF-Zeilenenden — wie cloud-init ihn einbettet; CRLF bräche die Shebang-Zeile.</summary>
    public static string ReadScript(string path)
    {
        var content = File.ReadAllText(path).Replace("\r\n", "\n");
        return content.EndsWith('\n') ? content : content + '\n';
    }

    private static string Quote(string value) => Shell.Quote(value);
}
