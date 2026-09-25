namespace CoreBridge.Core.Images;

/// <summary>
/// Die wenigen top-level Felder, die für die Classic-Ubuntu-Entscheidung
/// relevant sind. Snaps und Signaturblöcke werden bewusst nicht interpretiert.
/// </summary>
public sealed record ModelDescriptor(
    string Model,
    string? Base,
    string? Distribution,
    string? Grade,
    bool Classic);

public static class ModelDescriptorParser
{
    public static ModelDescriptor Parse(string modelText)
    {
        if (string.IsNullOrWhiteSpace(modelText) || !modelText.StartsWith("type: model", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Modellantwort beginnt nicht mit 'type: model'.");
        }

        string? model = null;
        string? modelBase = null;
        string? distribution = null;
        string? grade = null;
        var classic = false;

        foreach (var rawLine in modelText.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            // Nur top-level Felder (ohne Einrückung) betrachten.
            if (char.IsWhiteSpace(line[0]))
            {
                continue;
            }

            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            switch (key)
            {
                case "model":
                    model = value;
                    break;
                case "base":
                    modelBase = value;
                    break;
                case "distribution":
                    distribution = value;
                    break;
                case "grade":
                    grade = value;
                    break;
                case "classic":
                    classic = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidDataException("Modell enthält kein top-level model-Feld.");
        }

        return new ModelDescriptor(model, modelBase, distribution, grade, classic);
    }

    public static void ValidateClassic(string modelText, string? expectedModel = null)
    {
        var descriptor = Parse(modelText);
        if (!descriptor.Classic
            || !string.Equals(descriptor.Distribution, "ubuntu", StringComparison.OrdinalIgnoreCase)
            || !descriptor.Model.StartsWith("ubuntu-classic-", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(descriptor.Grade, "dangerous", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Erwartet wurde ein klassisches Ubuntu-Modell (ubuntu-classic-*, distribution: ubuntu, classic: true, grade: dangerous); geladen wurde '{descriptor.Model}'.");
        }

        if (expectedModel is not null
            && !string.Equals(descriptor.Model, expectedModel, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Modell-ID '{descriptor.Model}' entspricht nicht der erwarteten ID '{expectedModel}'.");
        }
    }
}
