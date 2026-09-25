using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using CoreBridge.Core.Config;
using CoreBridge.Core.Multipass;
using CoreBridge.Core.Vm;

namespace CoreBridge.Core.Images;

public sealed record ImageConfigureResult(
    string Name,
    string ModelPath,
    string ConfigPath,
    string? CloudInitPath,
    string[] Snaps,
    string RuntimeImage);

public sealed record ImageListEntry(
    string Name,
    string[] Components,
    string[] Snaps);

public sealed record ImageDeleteResult(string Name, string? ModelDir);

/// <summary>
/// Classic-Profil-Pipeline: `Configure` lädt/validiert das Modell und schreibt
/// Profilkonfiguration sowie generierte cloud-init. Der aktive Workflow startet
/// die Runtime und provisioniert den Gast über cloud-init.
/// </summary>
public sealed class ImageService
{
    private readonly ImagesConfig _config;
    private readonly HttpClient _http;

    public ImageService(ImagesConfig config, HttpClient? http = null)
    {
        _config = config;
        _http = http ?? new HttpClient();
    }

    public ImageConfigureResult Configure(
        string name,
        string[] components,
        string? baseUrl = null,
        bool force = false,
        string? runtimeImage = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new MultipassException("Image-Name fehlt (--name).");
        }

        if (!force && File.Exists(ImageStore.ConfigFile(name)))
        {
            throw new MultipassException(
                $"Image '{name}' ist bereits konfiguriert ({ImageStore.ModelDir(name)}) — `image-delete -n {name}` zuerst oder --force zum Überschreiben.");
        }

        var trimmed = components
            .Select(c => c.Trim())
            .Where(c => c.Length > 0)
            .ToArray();
        if (trimmed.Length == 0)
        {
            throw new MultipassException("Keine Komponenten angegeben.");
        }

        var snaps = ImageComponents.ResolveSnaps(_config, trimmed);
        var url = baseUrl ?? _config.DefaultModelUrl;
        var resolvedRuntimeImage = string.IsNullOrWhiteSpace(runtimeImage)
            ? _config.DefaultRuntimeImage
            : runtimeImage.Trim();
        if (string.IsNullOrWhiteSpace(resolvedRuntimeImage))
        {
            throw new MultipassException("Runtime-Image fehlt (Images:DefaultRuntimeImage).");
        }

        Directory.CreateDirectory(ImageStore.ModelDir(name));

        string model;
        try
        {
            model = _http.GetStringAsync(url).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            throw new MultipassException($"Basis-Modell konnte nicht geladen werden: {url}", exception);
        }

        ModelDescriptor descriptor;
        try
        {
            ModelDescriptorParser.ValidateClassic(model);
            descriptor = ModelDescriptorParser.Parse(model);
        }
        catch (InvalidDataException exception)
        {
            throw new MultipassException(
                $"Antwort von {url} ist kein gültiges Classic-Ubuntu dangerous-Modell — Download abgebrochen.",
                exception);
        }

        var modelSha256 = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(model))).ToLowerInvariant();

        string? cloudInitPath = null;
        var generatedCloudInitPath = ImageStore.CloudInitFile(name);
        if (ImageCloudInit.NeedsCloudInit(_config, trimmed))
        {
            cloudInitPath = generatedCloudInitPath;
            File.WriteAllText(cloudInitPath, ImageCloudInit.Generate(name, _config, trimmed));
        }
        else if (File.Exists(generatedCloudInitPath))
        {
            // Bei --force darf kein altes Profil-Cloud-init unbemerkt
            // an einem neuen Profil hängen bleiben.
            File.Delete(generatedCloudInitPath);
        }

        // Erst nach erfolgreicher Validierung der Cloud-init-Ausgabe schreiben:
        // ein fehlerhaftes Profil darf keine halbfertigen Modell-/YAML-Dateien
        // hinterlassen.
        File.WriteAllText(ImageStore.ModelFile(name), model);

        var cloudInitFile = cloudInitPath is null ? null : Path.GetFileName(cloudInitPath);
        var profileConfig = new ImageProfileConfig(
            name,
            url,
            trimmed,
            snaps,
            resolvedRuntimeImage,
            cloudInitFile,
            ModelId: descriptor.Model,
            ModelSha256: modelSha256);
        File.WriteAllText(ImageStore.ConfigFile(name), profileConfig.ToJson());

        return new ImageConfigureResult(
            name,
            ImageStore.ModelFile(name),
            ImageStore.ConfigFile(name),
            cloudInitPath,
            snaps,
            resolvedRuntimeImage);
    }

    public IReadOnlyList<ImageListEntry> List()
    {
        if (!Directory.Exists(ImageStore.ModelsRoot))
        {
            return Array.Empty<ImageListEntry>();
        }

        var entries = new List<ImageListEntry>();
        foreach (var dir in Directory.GetDirectories(ImageStore.ModelsRoot).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(dir);
            var configPath = ImageStore.ConfigFile(name);
            if (!File.Exists(configPath))
            {
                continue;
            }

            ImageProfileConfig profileConfig;
            try
            {
                profileConfig = ImageProfileConfig.FromJson(File.ReadAllText(configPath));
            }
            catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException or InvalidDataException)
            {
                throw new MultipassException($"Profilkonfiguration von '{name}' ist unlesbar: {configPath}", exception);
            }

            entries.Add(new ImageListEntry(
                name,
                profileConfig.Components,
                profileConfig.Snaps));
        }

        return entries;
    }

    public ImageDeleteResult Delete(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new MultipassException("Image-Name fehlt (--name).");
        }

        var modelDir = ImageStore.ModelDir(name);
        if (!Directory.Exists(modelDir))
        {
            throw new MultipassException($"Für '{name}' existiert kein konfiguriertes Classic-Profil.");
        }

        Directory.Delete(modelDir, recursive: true);
        return new ImageDeleteResult(name, modelDir);
    }
}
