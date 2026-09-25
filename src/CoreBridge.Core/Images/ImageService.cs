using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using CoreBridge.Core.Config;
using CoreBridge.Core.Vm;

namespace CoreBridge.Core.Images;

public sealed record ImageConfigureResult(
    string Name,
    string ModelPath,
    string ConfigPath,
    string? CloudInitPath,
    string[] Snaps,
    string RuntimeImage);

public sealed record ImageBuildResult(string Name, string ImagePath, long DurationMs, string BuildVmName);

public sealed record ImageListEntry(
    string Name,
    string[] Components,
    string[] Snaps,
    bool HasImage,
    long ImageBytes);

public sealed record ImageDeleteResult(string Name, string? ModelDir, string? ImageFile);

/// <summary>
/// Custom-Image-Pipeline: `Configure` lädt/validiert das Classic-Modell,
/// schreibt Build-Config + generierte cloud-init unterhalb des konfigurierten
/// Image-Stores; `Build` backt in einer klassischen Ubuntu-Bau-VM mit
/// `ubuntu-image --snap` die Store-Snaps ein. Alle Komponenten-Definitionen
/// und Bau-Defaults kommen aus der AppConfig.
/// </summary>
public sealed class ImageService
{
    private readonly ImagesConfig _config;
    private readonly VmService _vms;
    private readonly HttpClient _http;

    public ImageService(ImagesConfig config, VmService? vms = null, HttpClient? http = null)
    {
        _config = config;
        _vms = vms ?? new VmService();
        _http = http ?? new HttpClient();
    }

    public ImageConfigureResult Configure(
        string name,
        string[] components,
        string? baseUrl = null,
        string? buildTo = null,
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
        File.WriteAllText(ImageStore.ModelFile(name), model);

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

        var cloudInitFile = cloudInitPath is null ? null : Path.GetFileName(cloudInitPath);
        var buildConfig = new ImageBuildConfig(
            name,
            url,
            trimmed,
            snaps,
            buildTo,
            resolvedRuntimeImage,
            cloudInitFile,
            ModelId: descriptor.Model,
            ModelSha256: modelSha256);
        File.WriteAllText(ImageStore.ConfigFile(name), buildConfig.ToJson());

        return new ImageConfigureResult(
            name,
            ImageStore.ModelFile(name),
            ImageStore.ConfigFile(name),
            cloudInitPath,
            snaps,
            resolvedRuntimeImage);
    }

    //public ImageBuildResult Build(
    //    string name,
    //    string? buildVmName = null,
    //    string? buildImage = null,
    //    string? disk = null,
    //    int? timeoutSeconds = null,
    //    string? destination = null)
    //{
    //    var vmName = buildVmName ?? _config.Build.VmName;
    //    var image = buildImage ?? _config.Build.Image;
    //    var diskSize = disk ?? _config.Build.Disk;
    //    var timeout = (timeoutSeconds ?? _config.Build.TimeoutSeconds) * 1000;

    //    var configPath = ImageStore.ConfigFile(name);
    //    if (!File.Exists(configPath))
    //    {
    //        throw new MultipassException(
    //            $"Keine Build-Konfiguration für '{name}': {configPath} — zuerst `image-configure --name {name}` ausführen.");
    //    }

    //    var buildConfig = ImageBuildConfig.FromJson(File.ReadAllText(configPath));
    //    var modelPath = ImageStore.ModelFile(name);
    //    if (!File.Exists(modelPath))
    //    {
    //        throw new MultipassException($"Modell-Datei fehlt: {modelPath} — `image-configure --name {name}` erneut ausführen.");
    //    }

    //    var stopwatch = System.Diagnostics.Stopwatch.StartNew();

    //    var existing = _vms.List().FirstOrDefault(v => v.Name == vmName);
    //    if (existing is { State: "Deleted" })
    //    {
    //        throw new MultipassException(
    //            $"Bau-VM '{vmName}' liegt gelöscht im Multipass-Papierkorb — Namen nicht wiederverwendbar. Anderen --build-vm wählen oder manuell purgen.");
    //    }

    //    if (existing is null)
    //    {
    //        var created = _vms.Create(vmName, image, diskSize);
    //        EnsureSuccess(created, $"vm-create {vmName}");
    //    }

    //    var install = _vms.Exec(
    //        vmName,
    //        "snap list ubuntu-image >/dev/null 2>&1 || sudo snap install ubuntu-image --classic 2>&1 | tail -1");
    //    EnsureSuccess(install, "snap install ubuntu-image");

    //    var put = _vms.Put(modelPath, vmName, "model.model");
    //    EnsureSuccess(put, "transfer Modell -> Bau-VM");

    //    var snapArgs = string.Join(" ", buildConfig.Snaps.Select(s => $"--snap {s}"));
    //    var buildCommand =
    //        "sudo rm -rf ~/imgout && mkdir ~/imgout && "
    //        + $"sudo ubuntu-image snap ~/model.model {snapArgs} -O ~/imgout 2>&1 | tail -2 && ls -lh ~/imgout/ | head -3";
    //    var build = _vms.Exec(vmName, buildCommand, timeout);
    //    if (build.ExitCode != 0)
    //    {
    //        throw new MultipassException(
    //            $"ubuntu-image fehlgeschlagen (exit {build.ExitCode}): {Tail(build.StdOut, 10)} / {Tail(build.StdErr, 10)}");
    //    }

    //    var imagePath = destination ?? buildConfig.BuildTo ?? ImageStore.ImageFile(name);
    //    var imageDir = Path.GetDirectoryName(Path.GetFullPath(imagePath));
    //    if (!string.IsNullOrEmpty(imageDir))
    //    {
    //        Directory.CreateDirectory(imageDir);
    //    }
    //    var get = _vms.Get(vmName, "/home/ubuntu/imgout/pc.img", imagePath);
    //    if (!File.Exists(imagePath))
    //    {
    //        throw new MultipassException(
    //            $"Image-Transfer fehlgeschlagen (exit {get.ExitCode}): {Tail(get.StdErr, 10)}");
    //    }

    //    return new ImageBuildResult(name, imagePath, stopwatch.ElapsedMilliseconds, vmName);
    //} # TODO REMOVE DEPRECRATED

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

            ImageBuildConfig buildConfig;
            try
            {
                buildConfig = ImageBuildConfig.FromJson(File.ReadAllText(configPath));
            }
            catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException)
            {
                throw new MultipassException($"Build-Config von '{name}' ist unlesbar: {configPath}", exception);
            }

            var imagePath = buildConfig.BuildTo ?? ImageStore.ImageFile(name);
            var fileInfo = new FileInfo(imagePath);
            entries.Add(new ImageListEntry(
                name,
                buildConfig.Components,
                buildConfig.Snaps,
                fileInfo.Exists,
                fileInfo.Exists ? fileInfo.Length : 0));
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
        var configPath = ImageStore.ConfigFile(name);
        var imagePath = File.Exists(configPath)
            ? ImageBuildConfig.FromJson(File.ReadAllText(configPath)).BuildTo ?? ImageStore.ImageFile(name)
            : ImageStore.ImageFile(name);

        string? deletedModelDir = null;
        string? deletedImage = null;
        if (Directory.Exists(modelDir))
        {
            Directory.Delete(modelDir, recursive: true);
            deletedModelDir = modelDir;
        }

        if (File.Exists(imagePath))
        {
            File.Delete(imagePath);
            deletedImage = imagePath;
        }

        if (deletedModelDir is null && deletedImage is null)
        {
            throw new MultipassException($"Für '{name}' existiert weder eine Konfiguration noch ein Image.");
        }

        return new ImageDeleteResult(name, deletedModelDir, deletedImage);
    }

    private void EnsureSuccess(MultipassRunResult result, string label)
    {
        if (result.ExitCode != 0)
        {
            throw new MultipassException(
                $"{label} fehlgeschlagen (exit {result.ExitCode}): {Tail(result.StdErr, 10)} / {Tail(result.StdOut, 10)}");
        }
    }

    private static string Tail(string text, int lines)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var parts = text.TrimEnd().Split('\n');
        return string.Join(" | ", parts.Skip(Math.Max(0, parts.Length - lines))).Trim();
    }
}
