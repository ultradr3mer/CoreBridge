using CoreBridge.Core.Config;
using CoreBridge.Core.Multipass;
using CoreBridge.Core.Vm;

namespace CoreBridge.Core.Inits;

public sealed record InitConfigureResult(
    string Name,
    string ConfigPath,
    string? CloudInitPath,
    string[] Snaps,
    string RuntimeImage,
    VmResources? Resources = null);

public sealed record InitListEntry(
    string Name,
    string[] Components,
    string[] Snaps,
    VmResources? Resources = null);

public sealed record InitDeleteResult(string Name, string? ProfileDir);

/// <summary>
/// Profil-Pipeline: <c>Configure</c> rendert cloud-init aus den konfigurierten
/// Komponenten und schreibt sie zusammen mit einer kleinen Profilkonfiguration in
/// den <see cref="InitStore"/>. Der Aufruf ist rein lokal — es wird nichts
/// heruntergeladen und kein Image verwaltet. Das eigentliche Image startet
/// <c>vm-create</c> zur Laufzeit aus dem Multipass-Katalog.
/// </summary>
public sealed class InitService
{
    private readonly InitsConfig _config;

    public InitService(InitsConfig config)
    {
        _config = config;
    }

    public InitConfigureResult Configure(
        string name,
        string[] components,
        bool force = false,
        string? runtimeImage = null,
        VmResources? resources = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new MultipassException("Profilname fehlt (--name).");
        }

        if (!force && File.Exists(InitStore.ConfigFile(name)))
        {
            throw new MultipassException(
                $"Profil '{name}' existiert bereits ({InitStore.ProfileDir(name)}) — `init-delete -n {name}` zuerst oder --force zum Überschreiben.");
        }

        var trimmed = components
            .Select(c => c.Trim())
            .Where(c => c.Length > 0)
            .ToArray();
        if (trimmed.Length == 0)
        {
            throw new MultipassException("Keine Komponenten angegeben.");
        }

        var snaps = InitComponents.ResolveSnaps(_config, trimmed);
        var resolvedRuntimeImage = string.IsNullOrWhiteSpace(runtimeImage)
            ? _config.DefaultRuntimeImage
            : runtimeImage.Trim();
        if (string.IsNullOrWhiteSpace(resolvedRuntimeImage))
        {
            throw new MultipassException("Runtime-Image fehlt (Inits:DefaultRuntimeImage).");
        }

        Directory.CreateDirectory(InitStore.ProfileDir(name));

        string? cloudInitPath = null;
        var generatedCloudInitPath = InitStore.CloudInitFile(name);
        if (InitCloudInit.NeedsCloudInit(_config, trimmed))
        {
            cloudInitPath = generatedCloudInitPath;
            File.WriteAllText(cloudInitPath, InitCloudInit.Generate(name, _config, trimmed));
        }
        else if (File.Exists(generatedCloudInitPath))
        {
            // Bei --force darf kein altes Profil-Cloud-init unbemerkt
            // an einem neuen Profil hängen bleiben.
            File.Delete(generatedCloudInitPath);
        }

        var cloudInitFile = cloudInitPath is null ? null : Path.GetFileName(cloudInitPath);
        var profileConfig = new InitProfileConfig(
            name,
            trimmed,
            snaps,
            resolvedRuntimeImage,
            cloudInitFile,
            Cpus: resources?.Cpus,
            Memory: resources?.Memory,
            Disk: resources?.Disk);
        File.WriteAllText(InitStore.ConfigFile(name), profileConfig.ToJson());

        return new InitConfigureResult(
            name,
            InitStore.ConfigFile(name),
            cloudInitPath,
            snaps,
            resolvedRuntimeImage,
            resources);
    }

    public IReadOnlyList<InitListEntry> List()
    {
        if (!Directory.Exists(InitStore.ProfilesRoot))
        {
            return Array.Empty<InitListEntry>();
        }

        var entries = new List<InitListEntry>();
        foreach (var dir in Directory.GetDirectories(InitStore.ProfilesRoot).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(dir);
            var configPath = InitStore.ConfigFile(name);
            if (!File.Exists(configPath))
            {
                continue;
            }

            InitProfileConfig profileConfig;
            try
            {
                profileConfig = InitProfileConfig.FromJson(File.ReadAllText(configPath));
            }
            catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException or InvalidDataException)
            {
                throw new MultipassException($"Profilkonfiguration von '{name}' ist unlesbar: {configPath}", exception);
            }

            entries.Add(new InitListEntry(
                name,
                profileConfig.Components,
                profileConfig.Snaps,
                new VmResources(profileConfig.Cpus, profileConfig.Memory, profileConfig.Disk)));
        }

        return entries;
    }

    public InitDeleteResult Delete(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new MultipassException("Profilname fehlt (--name).");
        }

        var profileDir = InitStore.ProfileDir(name);
        if (!Directory.Exists(profileDir))
        {
            throw new MultipassException($"Für '{name}' existiert kein Profil.");
        }

        Directory.Delete(profileDir, recursive: true);
        return new InitDeleteResult(name, profileDir);
    }
}
