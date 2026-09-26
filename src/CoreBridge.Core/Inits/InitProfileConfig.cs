using System.Text.Json;

namespace CoreBridge.Core.Inits;

/// <summary>
/// Persistierte Profilkonfiguration eines generierten cloud-init-Profils.
/// Das Profil wird durch <c>init-configure</c> erzeugt und von <c>vm-create</c>
/// gelesen. Es beschreibt ausschließlich, welcher Runtime-Katalogeintrag
/// gestartet und welche generierte cloud-init dabei übergeben wird — ein
/// Profil lädt und verwaltet kein Image.
/// </summary>
public sealed record InitProfileConfig(
    string Name,
    string[] Components,
    string[] Snaps,
    string RuntimeImage = "daily:26.10",
    string? CloudInitFile = null,
    int SchemaVersion = 4)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string ToJson()
    {
        return JsonSerializer.Serialize(this, Options);
    }

    public static InitProfileConfig FromJson(string json)
    {
        var config = JsonSerializer.Deserialize<InitProfileConfig>(json)
            ?? throw new InvalidDataException("Profilkonfiguration ist leer.");

        if (string.IsNullOrWhiteSpace(config.Name)
            || config.Components is null
            || config.Snaps is null)
        {
            throw new InvalidDataException("Profilkonfiguration enthält unvollständige Pflichtfelder.");
        }

        return config with
        {
            Components = config.Components,
            Snaps = config.Snaps
        };
    }
}
