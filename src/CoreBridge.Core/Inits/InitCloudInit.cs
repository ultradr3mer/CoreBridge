using System.Text;
using System.Text.RegularExpressions;
using CoreBridge.Core.Config;
using CoreBridge.Core.Multipass;
using CoreBridge.Core.Vm;

namespace CoreBridge.Core.Inits;

/// <summary>
/// Erzeugt ausschließlich die cloud-init-YAML für die Gast-Provisionierung.
/// Das Rendern lädt keine Images, installiert keine Pakete und führt keine
/// Komponenten auf dem Host aus.
/// </summary>
public static class InitCloudInit
{
    private static readonly Regex SafeUserName = new("^[a-z_][a-z0-9_-]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool NeedsCloudInit(ImagesConfig config, IEnumerable<string> components)
    {
        return InitComponents.Resolve(config, components).Any(component => component.NeedsCloudInit);
    }

    /// <param name="hostPublicKey">
    /// Öffentlicher Host-Schlüssel (<see cref="HostSshKey"/>); Pflicht, sobald eine
    /// Komponente <see cref="ComponentConfig.AuthorizeHostKey"/> setzt.
    /// </param>
    public static string Generate(string name, ImagesConfig config, IEnumerable<string> components, string? hostPublicKey = null)
    {
        var selected = InitComponents.Resolve(config, components);
        if (selected.Any(component => component.AuthorizeHostKey) && string.IsNullOrWhiteSpace(hostPublicKey))
        {
            throw new MultipassException("Eine Komponente braucht den Host-SSH-Schlüssel, er wurde aber nicht übergeben.");
        }

        var hostKey = selected.Any(component => component.AuthorizeHostKey) ? hostPublicKey!.Trim() : null;
        var packages = selected
            .SelectMany(component => component.Packages)
            .Where(package => !string.IsNullOrWhiteSpace(package))
            .Select(package => package.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        ValidateScalarValues(packages, "package");
        var users = MergeUsers(selected.SelectMany(component => component.Users));
        var packageUpdate = selected.Any(component => component.PackageUpdate) || packages.Length > 0;
        var packageUpgrade = selected.Any(component => component.PackageUpgrade);
        var scripts = selected
            .Where(component => !string.IsNullOrWhiteSpace(component.Script))
            .ToArray();

        var yaml = new StringBuilder();
        yaml.AppendLine("#cloud-config");
        yaml.AppendLine($"# Generiert von `cb init-configure --name {SafeComment(name)}`.");
        yaml.AppendLine("# Gast-Provisionierung; Komponenten-Skripte werden als Dateien eingebettet.");

        yaml.AppendLine($"package_update: {packageUpdate.ToString().ToLowerInvariant()}");
        yaml.AppendLine($"package_upgrade: {packageUpgrade.ToString().ToLowerInvariant()}");

        if (packages.Length > 0)
        {
            yaml.AppendLine("packages:");
            foreach (var package in packages)
            {
                yaml.AppendLine($"  - {YamlQuote(package)}");
            }
        }

        AppendUsers(yaml, users);
        AppendSnaps(yaml, selected);
        AppendScripts(yaml, scripts, hostKey);
        AppendRunCommands(yaml, scripts);

        var rendered = yaml.ToString();
        CloudInitYamlValidator.Validate(rendered);
        return rendered;
    }

    private static void AppendUsers(StringBuilder yaml, IReadOnlyCollection<CloudInitUserConfig> users)
    {
        if (users.Count == 0)
        {
            return;
        }

        yaml.AppendLine();
        yaml.AppendLine("users:");
        foreach (var user in users)
        {
            yaml.AppendLine($"  - name: {YamlQuote(user.Name)}");
            if (!string.IsNullOrWhiteSpace(user.Sudo))
            {
                yaml.AppendLine($"    sudo: {YamlQuote(user.Sudo.Trim())}");
            }

            yaml.AppendLine($"    shell: {YamlQuote(user.Shell)}");
            if (user.Groups.Length > 0)
            {
                yaml.Append("    groups: [");
                yaml.Append(string.Join(", ", user.Groups.Select(YamlQuote)));
                yaml.AppendLine("]");
            }

            yaml.AppendLine($"    lock_passwd: {user.LockPassword.ToString().ToLowerInvariant()}");
        }
    }

    private static void AppendSnaps(StringBuilder yaml, IReadOnlyCollection<ComponentConfig> components)
    {
        var snaps = components
            .Where(component => !string.IsNullOrWhiteSpace(component.Snap))
            .Select(component => (Name: component.Snap!.Trim(), Classic: component.Classic))
            .DistinctBy(snap => snap.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (snaps.Length == 0)
        {
            return;
        }

        ValidateScalarValues(snaps.Select(snap => snap.Name), "snap");
        yaml.AppendLine();
        yaml.AppendLine("snap:");
        yaml.AppendLine("  commands:");
        foreach (var snap in snaps)
        {
            yaml.Append("    - [snap, install, ");
            yaml.Append(YamlQuote(snap.Name));
            if (snap.Classic)
            {
                yaml.Append(", --classic");
            }

            yaml.AppendLine("]");
        }
    }

    private static void AppendScripts(StringBuilder yaml, IReadOnlyCollection<ComponentConfig> scripts, string? hostKey)
    {
        if (scripts.Count == 0 && hostKey is null)
        {
            return;
        }

        yaml.AppendLine();
        yaml.AppendLine("write_files:");
        if (hostKey is not null)
        {
            if (hostKey.Any(char.IsControl))
            {
                throw new MultipassException("Der Host-SSH-Schlüssel muss eine einzelne Zeile sein.");
            }

            yaml.AppendLine($"  - path: {YamlQuote(HostSshKey.GuestKeyFile)}");
            yaml.AppendLine("    permissions: '0644'");
            yaml.AppendLine("    owner: 'root:root'");
            yaml.AppendLine("    content: |");
            yaml.AppendLine($"      {hostKey}");
        }

        foreach (var component in scripts)
        {
            var scriptPath = component.Script!;
            var installPath = component.InstallPath;
            ValidateInstallPath(installPath, scriptPath);
            ValidateScriptPath(scriptPath);

            var sourcePath = InitStore.ResolvePath(scriptPath);
            if (!File.Exists(sourcePath))
            {
                throw new MultipassException($"Cloud-init-Skript fehlt: {sourcePath}");
            }

            var content = File.ReadAllText(sourcePath).Replace("\r\n", "\n");
            if (!content.EndsWith('\n'))
            {
                content += '\n';
            }

            yaml.AppendLine($"  - path: {YamlQuote(installPath!)}");
            yaml.AppendLine("    permissions: '0755'");
            yaml.AppendLine("    owner: 'root:root'");
            yaml.AppendLine("    content: |");
            foreach (var line in content.Split('\n'))
            {
                yaml.AppendLine($"      {line}");
            }
        }
    }

    private static void AppendRunCommands(StringBuilder yaml, IReadOnlyCollection<ComponentConfig> scripts)
    {
        if (scripts.Count == 0)
        {
            return;
        }

        yaml.AppendLine();
        yaml.AppendLine("runcmd:");
        foreach (var component in scripts)
        {
            var installUser = component.InstallUser;
            if (string.IsNullOrWhiteSpace(installUser) || !SafeUserName.IsMatch(installUser))
            {
                throw new MultipassException(
                    $"InstallUser für '{component.Script}' ist kein sicherer Unix-Benutzername: '{installUser}'.");
            }

            yaml.AppendLine($"  - [{YamlQuote(component.InstallPath!)}, {YamlQuote(installUser)}]");
        }
    }

    private static CloudInitUserConfig[] MergeUsers(IEnumerable<CloudInitUserConfig> users)
    {
        var result = new List<CloudInitUserConfig>();
        foreach (var group in users.GroupBy(user => user.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(group.Key))
            {
                throw new MultipassException("Cloud-init-Benutzer benötigt einen Namen.");
            }

            var first = NormalizeUser(group.First());
            foreach (var candidate in group.Skip(1))
            {
                if (!UsersEqual(first, candidate))
                {
                    throw new MultipassException(
                        $"Benutzer '{group.Key}' wird durch ausgewählte Komponenten unterschiedlich konfiguriert.");
                }
            }

            result.Add(first);
        }

        return result.ToArray();
    }

    private static CloudInitUserConfig NormalizeUser(CloudInitUserConfig user)
    {
        if (!SafeUserName.IsMatch(user.Name))
        {
            throw new MultipassException($"Cloud-init-Benutzername ist ungültig: '{user.Name}'.");
        }

        if (string.IsNullOrWhiteSpace(user.Shell)
            || !user.Shell.StartsWith('/')
            || user.Shell.Contains('\r')
            || user.Shell.Contains('\n'))
        {
            throw new MultipassException($"Shell für Benutzer '{user.Name}' muss ein absoluter Unix-Pfad sein.");
        }

        var groups = user.Groups
            .Where(group => !string.IsNullOrWhiteSpace(group))
            .Select(group => group.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var group in groups)
        {
            ValidateScalar(group, "group");
        }

        return user with
        {
            Name = user.Name.Trim(),
            Sudo = string.IsNullOrWhiteSpace(user.Sudo) ? null : user.Sudo.Trim(),
            Shell = user.Shell.Trim(),
            Groups = groups
        };
    }

    private static bool UsersEqual(CloudInitUserConfig left, CloudInitUserConfig right)
    {
        return string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.Sudo, right.Sudo, StringComparison.Ordinal)
            && string.Equals(left.Shell, right.Shell, StringComparison.Ordinal)
            && left.LockPassword == right.LockPassword
            && left.Groups.SequenceEqual(right.Groups, StringComparer.OrdinalIgnoreCase);
    }

    private static void ValidateInstallPath(string? installPath, string scriptPath)
    {
        var segments = installPath?.Split('/').Skip(1).ToArray() ?? [];
        if (string.IsNullOrWhiteSpace(installPath)
            || !installPath.StartsWith('/')
            || installPath.Contains('\\')
            || installPath.Contains('\r')
            || installPath.Contains('\n')
            || segments.Length == 0
            || segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or ".."))
        {
            throw new MultipassException(
                $"InstallPath für '{scriptPath}' muss ein normalisierter absoluter Unix-Pfad sein.");
        }
    }

    private static void ValidateScriptPath(string scriptPath)
    {
        var expanded = Environment.ExpandEnvironmentVariables(scriptPath);
        var rooted = Path.IsPathRooted(expanded);
        var segments = expanded.Replace('\\', '/').Split('/');
        var pathSegments = (rooted && segments.Length > 0 && segments[0].Length == 0
            ? segments.Skip(1)
            : segments).ToArray();
        if (string.IsNullOrWhiteSpace(scriptPath)
            || expanded.Contains('\r')
            || expanded.Contains('\n')
            || (!rooted && expanded.Contains(':'))
            || pathSegments.Length == 0
            || pathSegments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or ".."))
        {
            throw new MultipassException(
                $"Script für '{scriptPath}' muss ein normalisierter relativer oder absoluter Pfad sein.");
        }
    }

    private static void ValidateScalarValues(IEnumerable<string> values, string label)
    {
        foreach (var value in values)
        {
            ValidateScalar(value, label);
        }
    }

    private static void ValidateScalar(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\r') || value.Contains('\n'))
        {
            throw new MultipassException($"Ungültiger {label}-Wert: '{value}'.");
        }
    }

    private static string SafeComment(string value)
    {
        return value.Replace('\r', ' ').Replace('\n', ' ').Trim();
    }

    private static string YamlQuote(string value)
    {
        return $"'{value.Replace("'", "''")}'";
    }
}
