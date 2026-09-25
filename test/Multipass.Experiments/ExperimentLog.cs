using System.Text;
using System.Text.Json;
using CoreBridge.Core.Multipass;
using CoreBridge.Core.Vm;

namespace Multipass.Experiments;

internal sealed record ExperimentRunData(
    string Id,
    string Title,
    string Command,
    string? Binary,
    int ExitCode,
    long DurationMs,
    DateTime StartedAt,
    string? Observation,
    string StdOut,
    string StdErr);

public static class ExperimentLog
{
    private const string DataDirectoryName = "experiment-data";

    private const string Header = """
        # EXPERIMENT_LOG — Multipass-Experimente

        Jeder Lauf eines Experiments (T01–T10, IMPLEMENTATION_PLAN.md §4) protokolliert
        Kommando, Binary, Exit-Code, Dauer und die gekürzte Roh-Ausgabe. Die volle
        Roh-Ausgabe liegt je Lauf als JSON in `experiment-data/`. Neue Einträge werden
        unten angehängt — nichts wird nachträglich geändert oder sortiert.

        ---
        """;

    public static void Append(
        string id,
        string title,
        string command,
        string? binaryPath,
        MultipassRunResult result,
        string? observation = null)
    {
        var root = FindRepoRoot();
        var startedAt = DateTime.Now;
        var dataFile = WriteRawData(root, new ExperimentRunData(
            id,
            title,
            command,
            binaryPath,
            result.ExitCode,
            result.DurationMs,
            startedAt,
            observation,
            result.StdOut,
            result.StdErr));

        var path = Path.Combine(root, "EXPERIMENT_LOG.md");
        if (!File.Exists(path))
        {
            File.WriteAllText(path, Header + Environment.NewLine);
        }

        var builder = new StringBuilder();
        builder.AppendLine();
        builder.AppendLine($"## {id} — {title} — {startedAt:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine();
        builder.AppendLine($"- Kommando: `multipass {command}`");
        builder.AppendLine($"- Binary: `{binaryPath ?? "unbekannt"}`");
        builder.AppendLine($"- Exit-Code: {result.ExitCode}");
        builder.AppendLine($"- Dauer: {result.DurationMs} ms");
        builder.AppendLine($"- Rohdaten: `{dataFile}`");
        if (!string.IsNullOrWhiteSpace(observation))
        {
            builder.AppendLine($"- Beobachtung: {observation}");
        }

        builder.AppendLine();
        builder.AppendLine("Roh-Ausgabe:");
        builder.AppendLine();
        builder.AppendLine("```");
        builder.AppendLine(Abbreviate(result.StdOut.TrimEnd()));
        if (!string.IsNullOrWhiteSpace(result.StdErr))
        {
            builder.AppendLine();
            builder.AppendLine("StdErr:");
            builder.AppendLine(Abbreviate(result.StdErr.TrimEnd()));
        }

        builder.AppendLine("```");
        File.AppendAllText(path, builder.ToString());
    }

    private static string WriteRawData(string root, ExperimentRunData data)
    {
        var directory = Path.Combine(root, DataDirectoryName);
        Directory.CreateDirectory(directory);
        var fileName = $"{data.Id}-{data.StartedAt:yyyyMMdd-HHmmss-fff}.json";
        var index = 1;
        while (File.Exists(Path.Combine(directory, fileName)))
        {
            fileName = $"{data.Id}-{data.StartedAt:yyyyMMdd-HHmmss-fff}-{index++}.json";
        }

        var relativePath = $"{DataDirectoryName}/{fileName}";
        File.WriteAllText(
            Path.Combine(directory, fileName),
            JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        return relativePath;
    }

    private static string Abbreviate(string text, int contextLines = 15)
    {
        var lines = text.Split('\n');
        if (lines.Length <= contextLines * 2)
        {
            return text;
        }

        return string.Join('\n', lines.Take(contextLines))
            + "\n... [" + (lines.Length - contextLines * 2) + " Zeilen gekürzt] ...\n"
            + string.Join('\n', lines.Skip(lines.Length - contextLines));
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CoreBridge.slnx"))
                || File.Exists(Path.Combine(directory.FullName, "CoreBridge.sln"))
                || File.Exists(Path.Combine(directory.FullName, "IMPLEMENTATION_PLAN.md")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repo-Root nicht gefunden (weder CoreBridge.slnx noch IMPLEMENTATION_PLAN.md).");
    }
}
