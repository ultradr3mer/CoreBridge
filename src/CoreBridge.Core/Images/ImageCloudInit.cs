using System.Text;
using CoreBridge.Core.Config;

namespace CoreBridge.Core.Images;

/// <summary>
/// Erzeugt die cloud-init-YAML für den Nicht-Snap-Rest der Komponenten —
/// vollständig config-getrieben (Symlinks, ProfileExports, VendoredLibs aus
/// appsettings.json). Die Snaps selbst sind bereits per `ubuntu-image --snap`
/// ins Image gebacken.
/// </summary>
public static class ImageCloudInit
{
    private const string Home = "/home/ubuntu";

    /// <summary>Immer true, solange Komponenten gewählt sind — die Snaps selbst
    /// werden inzwischen ebenfalls per cloud-init installiert (root), nicht nur
    /// der Nicht-Snap-Rest.</summary>
    public static bool NeedsCloudInit(ImagesConfig config, IEnumerable<string> components)
    {
        return components.Select(c => c.Trim()).Any(c => c.Length > 0);
    }

    public static string Generate(string name, ImagesConfig config, IEnumerable<string> components)
    {
        var selected = components
            .Select(c => c.Trim())
            .Where(c => config.Components.ContainsKey(c))
            .Select(c => config.Components[c])
            .ToArray();

        var yaml = new StringBuilder();
        yaml.AppendLine("#cloud-config");
        yaml.AppendLine($"# Generiert von `cb image-configure --name {name}`.");
        yaml.AppendLine("# Vollständiger Bootstrap beim Launch (cloud-init läuft als root, kein sudo nötig):");
        yaml.AppendLine("#   cb vm-create --name <vm> --cloud-init <diese-datei>");
        yaml.AppendLine("# (Image-Bau via `image-build` optional — Snaps sind hier ohnehin enthalten und");
        yaml.AppendLine("#  eine gebackene Version wäre idempotent.)");

        var snaps = selected.Select(c => c.Snap).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (snaps.Length > 0)
        {
            yaml.AppendLine();
            yaml.AppendLine("# Store-Snaps (als root installiert)");
            yaml.AppendLine("snap:");
            yaml.AppendLine("  commands:");
            foreach (var snap in snaps)
            {
                yaml.AppendLine($"    - snap install {snap}");
            }
        }

        var exports = selected.SelectMany(c => c.ProfileExports).Distinct().ToArray();
        if (exports.Length > 0)
        {
            yaml.AppendLine();
            yaml.AppendLine("# .profile-Erweiterungen");
            yaml.AppendLine("write_files:");
            yaml.AppendLine($"  - path: {Home}/.profile");
            yaml.AppendLine("    append: true");
            yaml.AppendLine("    owner: ubuntu:ubuntu");
            yaml.AppendLine("    content: |");
            foreach (var export in exports)
            {
                yaml.AppendLine($"      export {export}");
            }
        }

        var runCmd = new List<string>();
        foreach (var symlink in selected.SelectMany(c => c.Symlinks).Distinct())
        {
            var parts = symlink.Split('|', 2);
            if (parts.Length != 2)
            {
                throw new CoreBridge.Core.Vm.MultipassException(
                    $"Ungültiger Symlink in der Config (Format 'quelle|ziel'): {symlink}");
            }

            var (source, target) = (parts[0], parts[1]);
            var targetDir = System.IO.Path.GetDirectoryName(target)!.Replace('\\', '/');
            if (targetDir.Length > 0)
            {
                runCmd.Add($"mkdir -p {targetDir}");
            }

            runCmd.Add($"ln -sf {source} {target}");
        }

        foreach (var vendored in selected.Select(c => c.VendoredLibs).Where(v => v is not null))
        {
            var packages = string.Join(" ", vendored!.Packages.Select(p => $"\"{p}\""));
            runCmd.Add(string.Join('\n',
                $"ldd {vendored.LddTarget} 2>/dev/null | grep -q \"not found\" || exit 0",
                $"cd {Home} && rm -rf vm-libs && mkdir vm-libs && cd vm-libs",
                $"for p in {packages}; do apt-get download \"$p\" 2>/dev/null || echo \"SKIP $p\"; done",
                "for d in *.deb; do dpkg -x \"$d\" \"ex-$d\"; done",
                $"cp ex-*.deb/{vendored.ExtractGlob} {vendored.Destination}/ 2>/dev/null",
                $"cd {Home} && rm -rf vm-libs"));
        }

        if (runCmd.Count > 0)
        {
            // Core26 legt cloud-init-Pfade als root an — das ganze $HOME zurückgeben,
            // sonst können Snaps als user ubuntu kein ~/snap anlegen.
            runCmd.Add($"chown -R ubuntu:ubuntu {Home}");
            yaml.AppendLine();
            yaml.AppendLine("runcmd:");
            foreach (var command in runCmd)
            {
                if (command.Contains('\n'))
                {
                    yaml.AppendLine("  - |");
                    foreach (var line in command.Split('\n'))
                    {
                        yaml.AppendLine($"    {line}");
                    }
                }
                else
                {
                    yaml.AppendLine($"  - {command}");
                }
            }
        }

        return yaml.ToString();
    }
}
