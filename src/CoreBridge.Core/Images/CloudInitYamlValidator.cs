using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using CoreBridge.Core.Vm;

namespace CoreBridge.Core.Images;

/// <summary>
/// Parses and validates the cloud-init subset emitted by <see cref="ImageCloudInit"/>.
/// This is intentionally stricter than a generic YAML parse: malformed generated
/// component provisioning must fail before a profile is persisted or launched.
/// </summary>
internal static class CloudInitYamlValidator
{
    private static readonly Regex SafeUserName = new(
        "^[a-z_][a-z0-9_-]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> TopLevelKeys =
    [
        "package_update",
        "package_upgrade",
        "packages",
        "users",
        "snap",
        "write_files",
        "runcmd"
    ];

    public static void Validate(string yaml)
    {
        if (!yaml.StartsWith("#cloud-config\n", StringComparison.Ordinal)
            && !yaml.StartsWith("#cloud-config\r\n", StringComparison.Ordinal))
        {
            throw new MultipassException("Generierte cloud-init-YAML beginnt nicht mit #cloud-config.");
        }

        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException exception)
        {
            throw new MultipassException("Generierte cloud-init-YAML ist nicht parsebar.", exception);
        }

        if (stream.Documents.Count != 1
            || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new MultipassException("Generierte cloud-init-YAML muss genau eine Mapping-Sektion enthalten.");
        }

        ValidateKeys(root, TopLevelKeys, "Top-Level");
        ValidateBoolean(RequiredScalar(root, "package_update"), "package_update");
        ValidateBoolean(RequiredScalar(root, "package_upgrade"), "package_upgrade");

        if (TryGet(root, "packages", out var packages))
        {
            foreach (var package in RequireSequence(packages, "packages").Children)
            {
                ValidateScalar(RequireScalar(package, "package"), "package");
            }
        }

        if (TryGet(root, "users", out var users))
        {
            ValidateUsers(RequireSequence(users, "users"));
        }

        if (TryGet(root, "snap", out var snap))
        {
            ValidateSnaps(RequireMapping(snap, "snap"));
        }

        var hasWriteFiles = TryGet(root, "write_files", out var writeFiles);
        var hasRunCommands = TryGet(root, "runcmd", out var runCommands);
        if (hasWriteFiles != hasRunCommands)
        {
            throw new MultipassException("Cloud-init muss write_files und runcmd gemeinsam rendern.");
        }

        if (hasWriteFiles)
        {
            ValidateWriteFiles(RequireSequence(writeFiles!, "write_files"));
            ValidateRunCommands(RequireSequence(runCommands!, "runcmd"));
        }
    }

    private static void ValidateUsers(YamlSequenceNode users)
    {
        foreach (var userNode in users.Children)
        {
            var user = RequireMapping(userNode, "user");
            ValidateKeys(
                user,
                new HashSet<string>(StringComparer.Ordinal)
                {
                    "name", "sudo", "shell", "groups", "lock_passwd"
                },
                "user");

            var name = RequiredScalar(user, "name");
            if (!SafeUserName.IsMatch(name))
            {
                throw new MultipassException($"Ungültiger Benutzername im generierten cloud-init: '{name}'.");
            }

            ValidateAbsoluteUnixPath(RequiredScalar(user, "shell"), $"shell für Benutzer '{name}'");
            ValidateBoolean(RequiredScalar(user, "lock_passwd"), $"lock_passwd für Benutzer '{name}'");

            if (TryGet(user, "sudo", out var sudo))
            {
                ValidateScalar(RequireScalar(sudo, "sudo"), "sudo");
            }

            if (TryGet(user, "groups", out var groups))
            {
                foreach (var group in RequireSequence(groups, "groups").Children)
                {
                    ValidateScalar(RequireScalar(group, "group"), "group");
                }
            }
        }
    }

    private static void ValidateSnaps(YamlMappingNode snap)
    {
        ValidateKeys(snap, new HashSet<string>(StringComparer.Ordinal) { "commands" }, "snap");
        foreach (var commandNode in RequireSequence(RequiredNode(snap, "commands"), "snap.commands").Children)
        {
            var command = RequireSequence(commandNode, "snap command");
            if (command.Children.Count is < 3 or > 4)
            {
                throw new MultipassException("Snap-Argumentlisten müssen aus snap, install, Name und optional --classic bestehen.");
            }

            var values = command.Children.Select(item => RequireScalar(item, "snap argument")).ToArray();
            if (values[0] != "snap" || values[1] != "install")
            {
                throw new MultipassException("Snap-Argumentlisten müssen mit 'snap install' beginnen.");
            }

            ValidateScalar(values[2], "snap");
            if (values.Length == 4 && values[3] != "--classic")
            {
                throw new MultipassException("Der vierte Snap-Parameter muss --classic sein.");
            }
        }
    }

    private static void ValidateWriteFiles(YamlSequenceNode writeFiles)
    {
        foreach (var fileNode in writeFiles.Children)
        {
            var file = RequireMapping(fileNode, "write_files entry");
            ValidateKeys(
                file,
                new HashSet<string>(StringComparer.Ordinal)
                {
                    "path", "permissions", "owner", "content"
                },
                "write_files entry");

            ValidateAbsoluteUnixPath(RequiredScalar(file, "path"), "write_files path");
            if (RequiredScalar(file, "permissions") != "0755")
            {
                throw new MultipassException("Eingebettete Gast-Skripte müssen die Berechtigung '0755' besitzen.");
            }

            if (RequiredScalar(file, "owner") != "root:root")
            {
                throw new MultipassException("Eingebettete Gast-Skripte müssen 'root:root' gehören.");
            }

            var content = RequireScalar(RequiredNode(file, "content"), "content");
            if (string.IsNullOrWhiteSpace(content))
            {
                throw new MultipassException("Eingebettete Gast-Skripte dürfen keinen leeren Inhalt besitzen.");
            }
        }
    }

    private static void ValidateRunCommands(YamlSequenceNode runCommands)
    {
        foreach (var commandNode in runCommands.Children)
        {
            var command = RequireSequence(commandNode, "runcmd entry");
            if (command.Children.Count != 2)
            {
                throw new MultipassException("runcmd muss ausschließlich [Skriptpfad, Benutzer]-Argumentlisten rendern.");
            }

            var path = RequireScalar(command.Children[0], "runcmd path");
            var user = RequireScalar(command.Children[1], "runcmd user");
            ValidateAbsoluteUnixPath(path, "runcmd path");
            if (!SafeUserName.IsMatch(user))
            {
                throw new MultipassException($"Ungültiger Benutzername im generierten runcmd: '{user}'.");
            }
        }
    }

    private static void ValidateAbsoluteUnixPath(string value, string label)
    {
        var segments = value.Split('/').Skip(1).ToArray();
        if (!value.StartsWith('/')
            || value.Contains('\\')
            || value.Contains('\r')
            || value.Contains('\n')
            || segments.Length == 0
            || segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or ".."))
        {
            throw new MultipassException($"Ungültiger absoluter Unix-Pfad für {label}: '{value}'.");
        }
    }

    private static void ValidateBoolean(string value, string label)
    {
        if (value is not ("true" or "false"))
        {
            throw new MultipassException($"{label} muss ein YAML-Boolean sein.");
        }
    }

    private static void ValidateScalar(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\r') || value.Contains('\n'))
        {
            throw new MultipassException($"Ungültiger {label}-Wert im generierten cloud-init.");
        }
    }

    private static void ValidateKeys(YamlMappingNode mapping, IReadOnlySet<string> allowed, string label)
    {
        foreach (var key in mapping.Children.Keys)
        {
            if (key is not YamlScalarNode scalar || !allowed.Contains(scalar.Value ?? string.Empty))
            {
                throw new MultipassException($"Unbekannter Schlüssel in cloud-init {label}.");
            }
        }
    }

    private static YamlNode RequiredNode(YamlMappingNode mapping, string key)
    {
        if (!TryGet(mapping, key, out var node))
        {
            throw new MultipassException($"Generiertes cloud-init fehlt der Pflichtschlüssel '{key}'.");
        }

        return node!;
    }

    private static string RequiredScalar(YamlMappingNode mapping, string key)
    {
        return RequireScalar(RequiredNode(mapping, key), key);
    }

    private static string RequireScalar(YamlNode? node, string label)
    {
        return node is YamlScalarNode scalar
            ? scalar.Value ?? string.Empty
            : throw new MultipassException(
                $"'{label}' muss im generierten cloud-init ein YAML-Skalar sein, war {node?.GetType().Name ?? "null"}.");
    }

    private static YamlSequenceNode RequireSequence(YamlNode? node, string label)
    {
        return node as YamlSequenceNode
            ?? throw new MultipassException($"'{label}' muss im generierten cloud-init eine YAML-Liste sein.");
    }

    private static YamlMappingNode RequireMapping(YamlNode? node, string label)
    {
        return node as YamlMappingNode
            ?? throw new MultipassException($"'{label}' muss im generierten cloud-init ein YAML-Mapping sein.");
    }

    private static bool TryGet(YamlMappingNode mapping, string key, out YamlNode? node)
    {
        return mapping.Children.TryGetValue(new YamlScalarNode(key), out node);
    }
}
