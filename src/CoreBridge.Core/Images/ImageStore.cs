namespace CoreBridge.Core.Images;

/// <summary>
/// Wo Modell-, Config-, cloud-init- und Image-Dateien liegen (unter %LOCALAPPDATA%\CoreBridge).
/// </summary>
public static class ImageStore
{
    public const string DefaultModelUrl =
        "https://raw.githubusercontent.com/canonical/models/main/ubuntu-core-26-amd64-dangerous.model";

    public static string ModelsRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CoreBridge", "models");

    public static string ImagesRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CoreBridge", "images");

    public static string ModelDir(string name) => Path.Combine(ModelsRoot, name);

    public static string ModelFile(string name) => Path.Combine(ModelDir(name), $"{name}.model");

    public static string ConfigFile(string name) => Path.Combine(ModelDir(name), $"{name}.build.json");

    public static string CloudInitFile(string name) => Path.Combine(ModelDir(name), "cloud-init.yaml");

    public static string ImageFile(string name) => Path.Combine(ImagesRoot, $"{name}.img");
}
