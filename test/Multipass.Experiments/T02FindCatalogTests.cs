using System.Text.Json;

namespace Multipass.Experiments;

[Collection("MultipassExperiments")]
[Trait("Category", "Integration")]
public class T02FindCatalogTests : MultipassExperiment
{
    [MultipassFact]
    public void FindLiefertImageKatalogAlsJson()
    {
        var result = RunExperiment(
            "T02",
            "`find --format json` — Image-Katalog",
            new[] { "find", "--format", "json" },
            180_000,
            r => DescribeCatalog(r.StdOut));

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.StdOut);
        Assert.True(
            document.RootElement.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Object,
            $"images fehlt in: {result.StdOut}");
    }

    private static string? DescribeCatalog(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Object)
            {
                return "Katalog-JSON ohne images-Objekt — Struktur in der Roh-Ausgabe prüfen.";
            }

            var entries = new List<string>();
            var coreNames = new List<string>();
            foreach (var property in images.EnumerateObject())
            {
                var image = property.Value;
                var aliases = image.TryGetProperty("aliases", out var aliasesElement)
                    && aliasesElement.ValueKind == JsonValueKind.Array
                        ? string.Join(", ", aliasesElement.EnumerateArray().Select(a => a.GetString()))
                        : null;
                var os = image.TryGetProperty("os", out var osElement) ? osElement.GetString() : null;
                var release = image.TryGetProperty("release", out var releaseElement) ? releaseElement.GetString() : null;
                entries.Add($"{property.Name} (OS: {os ?? "?"}, Release: {release ?? "?"}, Aliases: {aliases ?? "-"})");
                if (property.Name.Contains("core", StringComparison.OrdinalIgnoreCase))
                {
                    coreNames.Add(property.Name);
                }
            }

            var blueprintCount = root.TryGetProperty("blueprints (deprecated)", out var blueprints)
                && blueprints.ValueKind == JsonValueKind.Object
                    ? blueprints.EnumerateObject().Count()
                    : 0;

            return $"{entries.Count} Images, {blueprintCount} (deprecated) Blueprints. "
                + (coreNames.Count > 0
                    ? $"Ubuntu Core IST im Katalog: {string.Join(", ", coreNames)}."
                    : "Keine Core-Einträge im Katalog.")
                + "\n" + string.Join("\n", entries);
        }
        catch (JsonException exception)
        {
            return $"Katalog konnte nicht ausgewertet werden: {exception.Message}";
        }
    }
}
