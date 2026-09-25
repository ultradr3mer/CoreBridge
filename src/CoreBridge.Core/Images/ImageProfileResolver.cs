using CoreBridge.Core.Config;
using CoreBridge.Core.Vm;

namespace CoreBridge.Core.Images;

/// <summary>
/// Aufgelöstes Launch-Profil für einen von <c>image-configure</c> erzeugten Namen.
/// </summary>
public sealed record ImageProfile(
    string Name,
    string RuntimeImage,
    string? CloudInitPath);

/// <summary>
/// Trennt normale Multipass-Katalognamen von CoreBridge-Image-Profilen.
/// Ein Profil wird nur dann aufgelöst, wenn im ImageStore tatsächlich eine
/// Konfigurationsdatei für diesen Namen existiert.
/// </summary>
public static class ImageProfileResolver
{
    public static ImageProfile? Resolve(
        ImagesConfig config,
        string requestedImage,
        string? explicitCloudInit = null)
    {
        if (!IsProfileCandidate(requestedImage))
        {
            return null;
        }

        var configPath = ImageStore.ConfigFile(requestedImage);
        if (!File.Exists(configPath))
        {
            return null;
        }

        ImageBuildConfig buildConfig;
        try
        {
            buildConfig = ImageBuildConfig.FromJson(File.ReadAllText(configPath));
        }
        catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException or InvalidDataException)
        {
            throw new MultipassException(
                $"Image-Profil '{requestedImage}' ist unlesbar: {configPath}", exception);
        }

        if (buildConfig.SchemaVersion < 3
            || string.IsNullOrWhiteSpace(buildConfig.ModelId)
            || string.IsNullOrWhiteSpace(buildConfig.ModelSha256))
        {
            throw new MultipassException(
                $"Image-Profil '{requestedImage}' stammt vor dem Classic-Profil-Schema. " +
                "Bitte image-delete und image-configure erneut ausführen.");
        }

        var runtimeImage = string.IsNullOrWhiteSpace(buildConfig.RuntimeImage)
            ? config.DefaultRuntimeImage
            : buildConfig.RuntimeImage.Trim();
        if (string.IsNullOrWhiteSpace(runtimeImage))
        {
            throw new MultipassException(
                $"Image-Profil '{requestedImage}' enthält kein gültiges Runtime-Image.");
        }

        var cloudInitPath = ResolveCloudInit(requestedImage, buildConfig, configPath, explicitCloudInit);

        return new ImageProfile(
            requestedImage,
            runtimeImage,
            cloudInitPath);
    }

    private static string? ResolveCloudInit(
        string name,
        ImageBuildConfig buildConfig,
        string configPath,
        string? explicitCloudInit)
    {
        if (explicitCloudInit is not null)
        {
            return explicitCloudInit;
        }

        string? candidate = null;
        if (!string.IsNullOrWhiteSpace(buildConfig.CloudInitFile))
        {
            candidate = Path.IsPathRooted(buildConfig.CloudInitFile)
                ? buildConfig.CloudInitFile
                : Path.Combine(Path.GetDirectoryName(configPath)!, buildConfig.CloudInitFile);
        }
        else
        {
            // Ältere Profile speicherten den Dateinamen nicht. Der Pfad ist
            // deterministisch, daher funktioniert die Migration ohne Metadaten.
            candidate = ImageStore.CloudInitFile(name);
            if (!File.Exists(candidate))
            {
                return null;
            }
        }

        if (!File.Exists(candidate))
        {
            throw new MultipassException(
                $"Cloud-init für Image-Profil '{name}' fehlt: {candidate}. " +
                "image-configure erneut ausführen.");
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
