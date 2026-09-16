using System.Text;
using CoreBridge.Core.Vm;

namespace Multipass.Experiments;

public static class ExperimentLog
{
    private const string Header = """
        # EXPERIMENT_LOG — Multipass-Experimente

        Jeder Lauf eines Experiments (T01–T10, IMPLEMENTATION_PLAN.md §4) protokolliert
        Kommando, Binary, Exit-Code, Dauer und Roh-Ausgabe. Neue Einträge werden unten
        angehängt — nichts wird nachträglich geändert oder sortiert.

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
        var path = Path.Combine(FindRepoRoot(), "EXPERIMENT_LOG.md");
        if (!File.Exists(path))
        {
            File.WriteAllText(path, Header + Environment.NewLine);
        }

        var builder = new StringBuilder();
        builder.AppendLine();
        builder.AppendLine($"## {id} — {title} — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine();
        builder.AppendLine($"- Kommando: `multipass {command}`");
        builder.AppendLine($"- Binary: `{binaryPath ?? "unbekannt"}`");
        builder.AppendLine($"- Exit-Code: {result.ExitCode}");
        builder.AppendLine($"- Dauer: {result.DurationMs} ms");
        if (!string.IsNullOrWhiteSpace(observation))
        {
            builder.AppendLine($"- Beobachtung: {observation}");
        }

        builder.AppendLine();
        builder.AppendLine("Roh-Ausgabe:");
        builder.AppendLine();
        builder.AppendLine("```");
        builder.AppendLine(result.StdOut.TrimEnd());
        if (!string.IsNullOrWhiteSpace(result.StdErr))
        {
            builder.AppendLine();
            builder.AppendLine("StdErr:");
            builder.AppendLine(result.StdErr.TrimEnd());
        }

        builder.AppendLine("```");
        File.AppendAllText(path, builder.ToString());
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

        throw new InvalidOperationException("Repo-Root nicht gefunden (weder CoreBridge.sln noch IMPLEMENTATION_PLAN.md).");
    }
}
