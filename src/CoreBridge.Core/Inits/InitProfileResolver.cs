using CoreBridge.Core.Config;
using CoreBridge.Core.Multipass;
using CoreBridge.Core.Vm;

namespace CoreBridge.Core.Inits;

/// <summary>
/// Aufgelöstes Launch-Profil für einen von <c>init-configure</c> erzeugten Namen.
/// </summary>
public sealed record InitProfile(
    string Name,
    string RuntimeImage,
    string? CloudInitPath);

/// <summary>
/// Trennt normale Multipass-Katalognamen von CoreBridge-Profilen.
/// Ein Profil wird nur dann aufgelöst, wenn im <see cref="InitStore"/> tatsächlich
/// eine Konfigurationsdatei für diesen Namen existiert.
/// </summary>
public static class InitProfileResolver
{
    public static InitProfile? Resolve(
        ImagesConfig config,
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
            cloudInitPath);
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
