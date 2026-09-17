using System.Net.Http;
using CoreBridge.Core.Config;
using CoreBridge.Core.Vm;

namespace CoreBridge.Core.Images;

public sealed record ImageConfigureResult(
    string Name,
    string ModelPath,
    string ConfigPath,
    string? CloudInitPath,
    string[] Snaps);

public sealed record ImageBuildResult(string Name, string ImagePath, long DurationMs, string BuildVmName);

/// <summary>
/// Custom-Image-Pipeline: `Configure` lädt das Basis-Modell (Canonical dangerous,
/// unverändert signiert), schreibt Build-Config + generierte cloud-init unter
/// %LOCALAPPDATA%\CoreBridge\models; `Build` backt in einer klassischen Ubuntu-Bau-VM
/// mit `ubuntu-image --snap` die Store-Snaps ein und holt die pc.img auf den Host.
/// Alle Komponenten-Definitionen und Bau-Defaults kommen aus der AppConfig.
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

    public ImageConfigureResult Configure(string name, string[] components, string? baseUrl = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new MultipassException("Image-Name fehlt (--name).");
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

        if (string.IsNullOrWhiteSpace(model) || !model.StartsWith("type: model", StringComparison.Ordinal))
        {
            throw new MultipassException(
                $"Antwort von {url} sieht nicht nach einer signierten Modell-Assertion aus — Download abgebrochen.");
        }

        File.WriteAllText(ImageStore.ModelFile(name), model);

        var buildConfig = new ImageBuildConfig(name, url, trimmed, snaps);
        File.WriteAllText(ImageStore.ConfigFile(name), buildConfig.ToJson());

        string? cloudInitPath = null;
        if (ImageCloudInit.NeedsCloudInit(_config, trimmed))
        {
            cloudInitPath = ImageStore.CloudInitFile(name);
            File.WriteAllText(cloudInitPath, ImageCloudInit.Generate(name, _config, trimmed));
        }

        return new ImageConfigureResult(name, ImageStore.ModelFile(name), ImageStore.ConfigFile(name), cloudInitPath, snaps);
    }

    public ImageBuildResult Build(
        string name,
        string? buildVmName = null,
        string? buildImage = null,
        string? disk = null,
        int? timeoutSeconds = null,
        string? destination = null)
    {
        var vmName = buildVmName ?? _config.Build.VmName;
        var image = buildImage ?? _config.Build.Image;
        var diskSize = disk ?? _config.Build.Disk;
        var timeout = (timeoutSeconds ?? _config.Build.TimeoutSeconds) * 1000;

        var configPath = ImageStore.ConfigFile(name);
        if (!File.Exists(configPath))
        {
            throw new MultipassException(
                $"Keine Build-Konfiguration für '{name}': {configPath} — zuerst `image-configure --name {name}` ausführen.");
        }

        var buildConfig = ImageBuildConfig.FromJson(File.ReadAllText(configPath));
        var modelPath = ImageStore.ModelFile(name);
        if (!File.Exists(modelPath))
        {
            throw new MultipassException($"Modell-Datei fehlt: {modelPath} — `image-configure --name {name}` erneut ausführen.");
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var existing = _vms.List().FirstOrDefault(v => v.Name == vmName);
        if (existing is { State: "Deleted" })
        {
            throw new MultipassException(
                $"Bau-VM '{vmName}' liegt gelöscht im Multipass-Papierkorb — Namen nicht wiederverwendbar. Anderen --build-vm wählen oder manuell purgen.");
        }

        if (existing is null)
        {
            var created = _vms.Create(vmName, image, diskSize);
            EnsureSuccess(created, $"vm-create {vmName}");
        }

        var install = _vms.Exec(
            vmName,
            "snap list ubuntu-image >/dev/null 2>&1 || sudo snap install ubuntu-image --classic 2>&1 | tail -1");
        EnsureSuccess(install, "snap install ubuntu-image");

        var put = _vms.Put(modelPath, vmName, "model.model");
        EnsureSuccess(put, "transfer Modell -> Bau-VM");

        var snapArgs = string.Join(" ", buildConfig.Snaps.Select(s => $"--snap {s}"));
        var buildCommand =
            "sudo rm -rf ~/imgout && mkdir ~/imgout && "
            + $"sudo ubuntu-image snap ~/model.model {snapArgs} -O ~/imgout 2>&1 | tail -2 && ls -lh ~/imgout/ | head -3";
        var build = _vms.Exec(vmName, buildCommand, timeout);
        if (build.ExitCode != 0)
        {
            throw new MultipassException(
                $"ubuntu-image fehlgeschlagen (exit {build.ExitCode}): {Tail(build.StdOut, 10)} / {Tail(build.StdErr, 10)}");
        }

        Directory.CreateDirectory(ImageStore.ImagesRoot);
        var imagePath = destination ?? ImageStore.ImageFile(name);
        var get = _vms.Get(vmName, "/home/ubuntu/imgout/pc.img", imagePath);
        if (!File.Exists(imagePath))
        {
            throw new MultipassException(
                $"Image-Transfer fehlgeschlagen (exit {get.ExitCode}): {Tail(get.StdErr, 10)}");
        }

        return new ImageBuildResult(name, imagePath, stopwatch.ElapsedMilliseconds, vmName);
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
