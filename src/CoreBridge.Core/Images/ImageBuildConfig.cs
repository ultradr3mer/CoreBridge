using System.Text.Json;
using CoreBridge.Core.Vm;

namespace CoreBridge.Core.Images;

/// <summary>
/// Persistierte Build-Konfiguration eines Custom-Images, erzeugt von
/// `image-configure` und gelesen von `image-build`.
/// </summary>
public sealed record ImageBuildConfig(string Name, string BaseModelUrl, string[] Components, string[] Snaps)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string ToJson()
    {
        return JsonSerializer.Serialize(this, Options);
    }

    public static ImageBuildConfig FromJson(string json)
    {
        return JsonSerializer.Deserialize<ImageBuildConfig>(json)
            ?? throw new MultipassException("Build-Konfiguration ist leer.");
    }
}
