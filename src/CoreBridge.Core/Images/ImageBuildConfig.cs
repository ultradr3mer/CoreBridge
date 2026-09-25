using System.Text.Json;

namespace CoreBridge.Core.Images;

/// <summary>
/// Persistierte Profil- und Launch-Konfiguration eines Classic-Profils.
/// Das Profil wird durch <c>image-configure</c> erzeugt und von <c>vm-create</c>
/// gelesen. <c>BuildTo</c> und die Snaps werden für den deprecated T11-
/// Kompatibilitätspfad bzw. für die Profilauflistung weiterhin persistiert.
/// Die zusätzlichen Felder sind optional, damit bereits erzeugte Konfigurationen
/// aus der Core-Phase weiterlesbar bleiben.
/// </summary>
public sealed record ImageBuildConfig(
    string Name,
    string BaseModelUrl,
    string[] Components,
    string[] Snaps,
    string? BuildTo = null,
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

    public static ImageBuildConfig FromJson(string json)
    {
        var config = JsonSerializer.Deserialize<ImageBuildConfig>(json)
            ?? throw new InvalidDataException("Build-Konfiguration ist leer.");

        if (string.IsNullOrWhiteSpace(config.Name)
            || string.IsNullOrWhiteSpace(config.BaseModelUrl)
            || config.Components is null
            || config.Snaps is null)
        {
            throw new InvalidDataException("Build-Konfiguration enthält unvollständige Pflichtfelder.");
        }

        return config with
        {
            Components = config.Components,
            Snaps = config.Snaps
        };
    }
}
