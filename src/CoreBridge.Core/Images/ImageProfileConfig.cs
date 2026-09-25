using System.Text.Json;

namespace CoreBridge.Core.Images;

/// <summary>
/// Persistierte Profil- und Launch-Konfiguration eines Classic-Profils.
/// Das Profil wird durch <c>image-configure</c> erzeugt und von <c>vm-create</c>
/// gelesen.
/// </summary>
public sealed record ImageProfileConfig(
    string Name,
    string BaseModelUrl,
    string[] Components,
    string[] Snaps,
    string RuntimeImage = "daily:26.10",
    string? CloudInitFile = null,
    int SchemaVersion = 3,
    string? ModelId = null,
    string? ModelSha256 = null)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string ToJson()
    {
        return JsonSerializer.Serialize(this, Options);
    }

    public static ImageProfileConfig FromJson(string json)
    {
        var config = JsonSerializer.Deserialize<ImageProfileConfig>(json)
            ?? throw new InvalidDataException("Image-Profil-Konfiguration ist leer.");

        if (string.IsNullOrWhiteSpace(config.Name)
            || string.IsNullOrWhiteSpace(config.BaseModelUrl)
            || config.Components is null
            || config.Snaps is null)
        {
            throw new InvalidDataException("Image-Profil-Konfiguration enthält unvollständige Pflichtfelder.");
        }

        return config with
        {
            Components = config.Components,
            Snaps = config.Snaps
        };
    }
}
