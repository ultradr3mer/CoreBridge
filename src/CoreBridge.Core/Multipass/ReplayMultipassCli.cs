using CoreBridge.Core.Common;
using CoreBridge.Core.Config;

namespace CoreBridge.Core.Multipass;

/// <summary>
/// Multipass-Double für Entwicklung ohne installiertes Multipass: beantwortet
/// jeden Aufruf aus einem von <see cref="LoggingMultipassCli"/> aufgezeichneten
/// Protokoll (<see cref="AppConfig.MultipassLogPath"/>). Aktiviert wird er über
/// <see cref="AppConfig.Bootstrap"/>, nicht über Umgebungsvariablen.
/// </summary>
/// <remarks>
/// Zuordnung über das exakte Argument-Array. Gleiche Aufrufe werden in
/// aufgezeichneter Reihenfolge beantwortet — ein <c>list</c> vor und nach
/// <c>launch</c> liefert also verschiedene Antworten —, der letzte Eintrag je
/// Aufruf wiederholt sich. Ein nie aufgezeichneter Aufruf wirft
/// <see cref="MultipassException"/>, statt eine Antwort zu erfinden.
/// Nebenwirkungen auf dem Host (etwa die lokale Datei aus
/// <c>transfer vm:… lokal</c>) entstehen nicht; ebenso installiert eine
/// „angelegte“ VM im Gast nichts.
/// </remarks>
public sealed class ReplayMultipassCli : IMultipassCli
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Queue<MultipassCallLogEntry>> _answers = new(StringComparer.Ordinal);

    /// <summary>Einziger öffentlicher Konstruktor — den wählt der Container.</summary>
    public ReplayMultipassCli(AppConfig config)
        : this(MultipassCallLog.Read(ResolveSource(config)))
    {
    }

    private ReplayMultipassCli(IEnumerable<MultipassCallLogEntry> entries)
    {
        foreach (var entry in entries)
        {
            var key = Key(entry.Args);
            if (!_answers.TryGetValue(key, out var queue))
            {
                _answers[key] = queue = new Queue<MultipassCallLogEntry>();
            }

            queue.Enqueue(entry);
        }
    }

    /// <summary>
    /// Wiedergabe aus bereits gelesenen Einträgen. Bewusst kein öffentlicher
    /// Konstruktor: der Container löst <c>IEnumerable&lt;T&gt;</c> als leere Liste
    /// auf, die Konstruktorwahl wäre sonst mehrdeutig.
    /// </summary>
    public static ReplayMultipassCli FromEntries(IEnumerable<MultipassCallLogEntry> entries) => new(entries);

    public MultipassRunResult Run(IReadOnlyList<string> args, int timeoutMs = 30000, string? exePath = null)
    {
        return Answer(args);
    }

    public MultipassRunResult RunWithExtensions(
        IReadOnlyList<string> args,
        TimeSpan slice,
        Func<bool> keepWaiting,
        string? exePath = null)
    {
        // Bewusst OHNE keepWaiting(): es gibt keinen Prozess, auf den gewartet
        // würde, also gibt es auch nichts zu fragen. keepWaiting aufzurufen würde
        // die interaktive Rückfrage "keep waiting?" auslösen und die Wiedergabe
        // genau dort blockieren, wo sie beschleunigen soll.
        return Answer(args);
    }

    public int Attach(IReadOnlyList<string> args, string? exePath = null)
    {
        return Answer(args).ExitCode;
    }

    private MultipassRunResult Answer(IReadOnlyList<string> args)
    {
        MultipassCallLogEntry entry;
        lock (_gate)
        {
            if (!_answers.TryGetValue(Key(args), out var queue))
            {
                throw new MultipassException(
                    $"Kein aufgezeichneter Aufruf für: multipass {string.Join(' ', args)} (Wiedergabe).");
            }

            entry = queue.Count > 1 ? queue.Dequeue() : queue.Peek();
        }

        if (entry.TimedOut)
        {
            throw new MultipassTimeoutException(
                string.Join(' ', args),
                entry.TimeoutMs ?? 0,
                entry.StdOut,
                entry.StdErr);
        }

        return new MultipassRunResult(entry.ExitCode, entry.StdOut, entry.StdErr, entry.DurationMs);
    }

    private static string ResolveSource(AppConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.MultipassLogPath))
        {
            throw new MultipassException(
                $"{nameof(ReplayMultipassCli)} braucht {nameof(AppConfig.MultipassLogPath)} als Quelle der Wiedergabe.");
        }

        return RepoPaths.Resolve(config.MultipassLogPath);
    }

    private static string Key(IEnumerable<string> args) => string.Join('\u001F', args);
}
