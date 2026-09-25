using System.Text;
using CoreBridge.Core.Config;
using CoreBridge.Core.Vm;

namespace CoreBridge.Core.Images;

/// <summary>
/// Erzeugt die cloud-init-YAML für die Provisionierung im Gast.
/// Komponenten-Skripte werden als Dateien mit <c>write_files</c> eingebettet
/// und anschließend über <c>runcmd</c> ausgeführt. Der Host führt sie nicht
/// separat aus.
/// </summary>
public static class ImageCloudInit
{
    private const string Home = "/home/ubuntu";

    public static bool NeedsCloudInit(ImagesConfig config, IEnumerable<string> components)
    {
        return ImageComponents.Resolve(config, components).Any(component => component.NeedsCloudInit);
    }

    public static string Generate(string name, ImagesConfig config, IEnumerable<string> components)
    {
        var selected = ImageComponents.Resolve(config, components);
        var yaml = new StringBuilder();
        yaml.AppendLine("#cloud-config");
        yaml.AppendLine($"# Generiert von `cb image-configure --name {name}`.");
        yaml.AppendLine("# Gast-Provisionierung; Komponenten-Skripte werden als Dateien eingebettet.");

        var packages = selected
            .SelectMany(component => component.CloudInitPackages)
            .Where(package => !string.IsNullOrWhiteSpace(package))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (packages.Length > 0)
        {
            yaml.AppendLine("package_update: true");
            yaml.AppendLine("packages:");
            foreach (var package in packages)
            {
                yaml.AppendLine($"  - {package}");
            }
        }

        var snaps = selected
            .Where(component => !string.IsNullOrWhiteSpace(component.Snap))
            .Select(component => (Snap: component.Snap!, Classic: component.Classic))
            .DistinctBy(snap => snap.Snap, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (snaps.Length > 0)
        {
            yaml.AppendLine();
            yaml.AppendLine("snap:");
            yaml.AppendLine("  commands:");
            foreach (var (snap, classic) in snaps)
            {
                yaml.AppendLine($"    - snap install {snap}{(classic ? " --classic" : string.Empty)}");
            }
        }

        var exports = selected.SelectMany(component => component.ProfileExports)
            .Where(export => !string.IsNullOrWhiteSpace(export))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var scripts = selected
            .Where(component => !string.IsNullOrWhiteSpace(component.CloudInitScript))
            .ToArray();
        if (exports.Length > 0 || scripts.Length > 0)
        {
            yaml.AppendLine();
            yaml.AppendLine("write_files:");
            if (exports.Length > 0)
            {
                yaml.AppendLine($"  - path: {Home}/.profile");
                yaml.AppendLine("    append: true");
                yaml.AppendLine("    owner: ubuntu:ubuntu");
                yaml.AppendLine("    content: |");
                foreach (var export in exports)
                {
                    yaml.AppendLine($"      export {export}");
                }
            }

            foreach (var component in scripts)
            {
                var scriptPath = component.CloudInitScript!;
                var installPath = component.CloudInitInstallPath;
                if (string.IsNullOrWhiteSpace(installPath) || !installPath.StartsWith('/'))
                {
                    throw new MultipassException(
                        $"CloudInitInstallPath für '{scriptPath}' muss ein absoluter Unix-Pfad sein.");
                }

                var sourcePath = ImageStore.ResolvePath(scriptPath);
                if (!File.Exists(sourcePath))
                {
                    throw new MultipassException($"Cloud-init-Skript fehlt: {sourcePath}");
                }

                yaml.AppendLine($"  - path: {installPath}");
                yaml.AppendLine("    permissions: \"0755\"");
                yaml.AppendLine("    owner: root:root");
                yaml.AppendLine("    content: |");
                foreach (var line in File.ReadAllText(sourcePath).Replace("\r\n", "\n").Split('\n'))
                {
                    yaml.AppendLine($"      {line}");
                }
            }
        }

        var runCmd = new List<string>();
        foreach (var symlink in selected.SelectMany(component => component.Symlinks).Distinct(StringComparer.Ordinal))
        {
            var parts = symlink.Split('|', 2);
            if (parts.Length != 2)
            {
                throw new MultipassException(
                    $"Ungültiger Symlink in der Config (Format 'quelle|ziel'): {symlink}");
            }

            var (source, target) = (parts[0], parts[1]);
            var targetDir = Path.GetDirectoryName(target)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(targetDir))
            {
                runCmd.Add($"install -d -o ubuntu -g ubuntu {targetDir}");
            }

            runCmd.Add($"ln -sf {source} {target}");
            runCmd.Add($"chown -h ubuntu:ubuntu {target}");
        }

        foreach (var vendored in selected.Select(component => component.VendoredLibs).Where(value => value is not null))
        {
            var packagesText = string.Join(" ", vendored!.Packages.Select(package => $"\"{package}\""));
            runCmd.Add($"install -d -o ubuntu -g ubuntu {vendored.Destination}");
            runCmd.Add(string.Join('\n',
                "set -e",
                $"if ! ldd {vendored.LddTarget} 2>/dev/null | grep -q \"not found\"; then exit 0; fi",
                $"cd {Home} && rm -rf vm-libs && mkdir vm-libs && cd vm-libs",
                $"for p in {packagesText}; do apt-get download \"$p\"; done",
                "for d in *.deb; do dpkg -x \"$d\" \"ex-$d\"; done",
                $"cp ex-*.deb/{vendored.ExtractGlob} {vendored.Destination}/",
                $"chown -R ubuntu:ubuntu {vendored.Destination}",
                $"cd {Home} && rm -rf vm-libs"));
        }

        var scriptCommands = scripts
            .Select(component => component.CloudInitInstallPath!)
            .ToArray();
        if (runCmd.Count > 0 || scriptCommands.Length > 0)
        {
            yaml.AppendLine();
            yaml.AppendLine("runcmd:");
            foreach (var command in runCmd)
            {
                AppendRunCommand(yaml, command);
            }

            foreach (var command in scriptCommands)
            {
                yaml.AppendLine($"  - [{command}]");
            }
        }

        return yaml.ToString();
    }

    private static void AppendRunCommand(StringBuilder yaml, string command)
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
