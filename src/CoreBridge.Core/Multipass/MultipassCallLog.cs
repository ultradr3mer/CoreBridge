using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoreBridge.Core.Multipass;

/// <summary>
/// Ein Aufruf des <see cref="IMultipassCli"/>, so wie er im Protokoll landet.
/// Eine Zeile pro Aufruf (JSON Lines), damit sich Eingabe und Ausgabe
/// maschinell auswerten und per <see cref="ReplayMultipassCli"/> wiedergeben lassen.
/// </summary>
/// <param name="TimedOut">
/// Der Aufruf endete mit <see cref="MultipassTimeoutException"/>; <see cref="StdOut"/>
/// und <see cref="StdErr"/> sind dann die bis dahin gelesenen Teilausgaben.
/// </param>
public sealed record MultipassCallLogEntry(
    [property: JsonPropertyName("seq")] int Seq,
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("verb")] string Verb,
    [property: JsonPropertyName("args")] string[] Args,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("timeoutMs")] int? TimeoutMs,
    [property: JsonPropertyName("exitCode")] int ExitCode,
    [property: JsonPropertyName("stdout")] string StdOut,
    [property: JsonPropertyName("stderr")] string StdErr,
    [property: JsonPropertyName("durationMs")] long DurationMs,
    [property: JsonPropertyName("at")] DateTimeOffset At,
    [property: JsonPropertyName("timedOut")] bool TimedOut = false);

/// <summary>Kopfzeile, die jeden Prozessstart im Protokoll markiert.</summary>
public sealed record MultipassCallLogSession(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("cwd")] string Cwd,
    [property: JsonPropertyName("at")] DateTimeOffset At);

/// <summary>Lesen und Schreiben des Multipass-Aufrufprotokolls.</summary>
public static class MultipassCallLog
{
    public const string FileExtension = ".jsonl";

    public const string SessionKind = "session";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// Liest alle Aufrufe aus einer <c>.jsonl</c>-Datei oder aus allen
    /// <c>*.jsonl</c> eines Verzeichnisses (nach Dateiname sortiert, also
    /// chronologisch). Kopfzeilen werden übersprungen.
    /// </summary>
    public static IReadOnlyList<MultipassCallLogEntry> Read(string path)
    {
        var files = Directory.Exists(path)
            ? Directory.GetFiles(path, "*" + FileExtension).Order(StringComparer.Ordinal).ToArray()
            : File.Exists(path) ? [path] : [];

        var entries = new List<MultipassCallLogEntry>();
        foreach (var file in files)
        {
            foreach (var line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("kind", out var kind)
                    && kind.GetString() == SessionKind)
                {
                    continue;
                }

                entries.Add(document.RootElement.Deserialize<MultipassCallLogEntry>(JsonOptions)!);
            }
        }

        return entries;
    }

    /// <summary>Neue Protokolldatei dieses Prozesses: <c>&lt;dir&gt;/&lt;yyyyMMdd-HHmmss&gt;-&lt;pid&gt;.jsonl</c>.</summary>
    public static string NewFilePath(string directory)
    {
        return Path.Combine(
            directory,
            $"{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}{FileExtension}");
    }

    /// <summary>Hängt eine Zeile an; I/O-Fehler dürfen den Aufruf nie scheitern lassen.</summary>
    public static void Append(string file, object line)
    {
        try
        {
            var directory = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.AppendAllText(
                file,
                JsonSerializer.Serialize(line, JsonOptions) + Environment.NewLine,
                Encoding.UTF8);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
