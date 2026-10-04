using CoreBridge.Core.Config;
using CoreBridge.Core.Multipass;
using CoreBridge.Core.Vm;

namespace CoreBridge.Core.Inits;

/// <summary>
/// Aufgelöstes Launch-Profil für einen von <c>init-configure</c> erzeugten Namen.
/// </summary>
/// <param name="Components">Die Komponenten, für die <c>init-configure</c> die cloud-init gerendert hat.</param>
/// <param name="Resources">CPU/Memory/Disk-Vorgaben des Profils; Angaben an <c>vm-create</c> schlagen sie.</param>
public sealed record InitProfile(
    string Name,
    string RuntimeImage,
    string? CloudInitPath,
    IReadOnlyList<string> Components,
    VmResources? Resources = null);

/// <summary>
/// Trennt normale Multipass-Katalognamen von CoreBridge-Profilen.
/// Ein Profil wird nur dann aufgelöst, wenn im <see cref="InitStore"/> tatsächlich
/// eine Konfigurationsdatei für diesen Namen existiert.
/// </summary>
public static class InitProfileResolver
{
    /// <summary>
    /// Löst <paramref name="name"/> ausdrücklich als Profil auf (<c>vm-create --profile</c>):
    /// fehlt es, gibt es eine Meldung mit den vorhandenen Profilen statt eines
    /// Katalog-Lookups durch Multipass.
    /// </summary>
    public static InitProfile ResolveRequired(InitsConfig config, string name, string? explicitCloudInit = null)
    {
        var profile = IsProfileCandidate(name) ? Resolve(config, name, explicitCloudInit) : null;
        if (profile is not null)
        {
            return profile;
        }

        var existing = ExistingProfiles();
        throw new MultipassException(
            $"Profil '{name}' existiert nicht. "
            + (existing.Count == 0
                ? "Es gibt noch keine Profile — anlegen mit `cb init-configure -n <profil> -c claude,dotnet`."
                : $"Vorhandene Profile: {string.Join(", ", existing)}."));
    }

    /// <summary>Namen aller Profile im <see cref="InitStore"/>, alphabetisch.</summary>
    public static IReadOnlyList<string> ExistingProfiles()
    {
        if (!Directory.Exists(InitStore.ProfilesRoot))
        {
            return [];
        }

        return Directory.EnumerateDirectories(InitStore.ProfilesRoot)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(name => File.Exists(InitStore.ConfigFile(name)))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static InitProfile? Resolve(
        InitsConfig config,
        string requestedImage,
        string? explicitCloudInit = null)
    {
        if (!IsProfileCandidate(requestedImage))
        {
            return null;
        }

        var configPath = InitStore.ConfigFile(requestedImage);
        if (!File.Exists(configPath))
        {
            return null;
        }

        InitProfileConfig profileConfig;
        try
        {
            profileConfig = InitProfileConfig.FromJson(File.ReadAllText(configPath));
        }
        catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException or InvalidDataException)
        {
            throw new MultipassException(
                $"Profil '{requestedImage}' ist unlesbar: {configPath}", exception);
        }

        if (profileConfig.SchemaVersion < 4)
        {
            throw new MultipassException(
                $"Profil '{requestedImage}' stammt vor der Umstellung auf reine cloud-init-Profile. " +
                "Bitte init-delete und init-configure erneut ausführen.");
        }

        var runtimeImage = string.IsNullOrWhiteSpace(profileConfig.RuntimeImage)
            ? config.DefaultRuntimeImage
            : profileConfig.RuntimeImage.Trim();
        if (string.IsNullOrWhiteSpace(runtimeImage))
        {
            throw new MultipassException(
                $"Profil '{requestedImage}' enthält kein gültiges Runtime-Image.");
        }

        var cloudInitPath = ResolveCloudInit(requestedImage, profileConfig, configPath, explicitCloudInit);

        return new InitProfile(
            requestedImage,
            runtimeImage,
            cloudInitPath,
            profileConfig.Components,
            new VmResources(profileConfig.Cpus, profileConfig.Memory, profileConfig.Disk));
    }

    private static string? ResolveCloudInit(
        string name,
        InitProfileConfig profileConfig,
        string configPath,
        string? explicitCloudInit)
    {
        if (explicitCloudInit is not null)
        {
            return explicitCloudInit;
        }

        string? candidate = null;
        if (!string.IsNullOrWhiteSpace(profileConfig.CloudInitFile))
        {
            candidate = Path.IsPathRooted(profileConfig.CloudInitFile)
                ? profileConfig.CloudInitFile
                : Path.Combine(Path.GetDirectoryName(configPath)!, profileConfig.CloudInitFile);
        }
        else
        {
            // Ältere Profile speicherten den Dateinamen nicht. Der Pfad ist
            // deterministisch, daher funktioniert die Migration ohne Metadaten.
            candidate = InitStore.CloudInitFile(name);
            if (!File.Exists(candidate))
            {
                return null;
            }
        }

        if (!File.Exists(candidate))
        {
            throw new MultipassException(
                $"Cloud-init für Profil '{name}' fehlt: {candidate}. " +
                "init-configure erneut ausführen.");
        }

        return Path.GetFullPath(candidate);
    }

    private static bool IsProfileCandidate(string image)
    {
        return !string.IsNullOrWhiteSpace(image)
            && !image.Contains(':')
            && !Path.IsPathRooted(image);
    }
}
