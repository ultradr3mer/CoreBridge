using System.Runtime.CompilerServices;
using CoreBridge.Interaction;

namespace CoreBridge.Cli;

/// <summary>
/// Protokoll jedes CLI-Aufrufs in <c>&lt;StoreRoot&gt;/logs/cli-&lt;Datum&gt;.log</c> (in
/// Development also <c>&lt;Repo&gt;/artifacts/logs</c>): Argumente, jede Ausgabezeile, jede
/// Rückfrage samt Antwort, Fehler und Exit-Code, dazu das Innenleben aus den
/// <see cref="DiagnosticEvent"/>s — jeder Werkzeugaufruf (<see cref="ToolCallEvent"/>) mit
/// Kommandozeile, Ergebnis, stdout und stderr. Ein erhöhtes Fenster schreibt in dieselbe
/// Datei; die PID in jeder Zeile hält die Fenster auseinander. Fortschritts-Ticks fehlen
/// (nur Rauschen), Freitext-Antworten stehen nur mit ihrer Länge darin. Das Protokoll darf
/// die CLI nie stören: lässt sich die Datei nicht öffnen oder schreiben, entfällt es still.
/// </summary>
/// <remarks>
/// Jede Zeile wird einzeln angehängt (öffnen, ans Ende, schließen), geschützt durch einen
/// prozessübergreifenden Mutex. Ein einmal geöffneter Stream merkte sich das Dateiende vom
/// Öffnen und überschriebe, was das andere Fenster inzwischen angehängt hat.
/// </remarks>
public sealed class CommandLog : IDisposable
{
    private readonly string? path;
    private readonly Mutex? mutex;
    private readonly int processId = Environment.ProcessId;
    private string? lastToolCall;
    private int toolCallRepeats;

    /// <summary>Höchstens so viele Zeichen je Ausgabestrom eines Werkzeugaufrufs; der Rest wird nur gezählt.</summary>
    public const int MaxStreamLength = 16_000;

    private CommandLog(string? path)
    {
        this.path = path;
        this.mutex = path is null ? null : new Mutex(initiallyOwned: false, @"Local\CoreBridge.CommandLog");
    }

    /// <summary>
    /// Öffnet (oder ergänzt) das Tagesprotokoll unter <paramref name="directory"/>; <c>null</c>
    /// = Protokoll aus (<c>CommandLog:Enabled = false</c>), dann schreibt die Instanz nichts.
    /// </summary>
    public static CommandLog Open(string? directory, IEnumerable<string> args)
    {
        if (directory is null)
        {
            return new CommandLog(null);
        }

        CommandLog log;
        try
        {
            Directory.CreateDirectory(directory);
            log = new CommandLog(Path.Combine(directory, $"cli-{DateTime.Now:yyyyMMdd}.log"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new CommandLog(null);
        }

        log.Write("start", $"cb {string.Join(' ', args.Select(Quote))} (cwd {Environment.CurrentDirectory}, {(IsElevated() ? "erhöht" : "nicht erhöht")})");
        return log;
    }

    /// <summary>
    /// Reicht den Ereignisstrom unverändert weiter und protokolliert dabei. Eine Rückfrage
    /// wird nach der Verarbeitung durch den Konsumenten noch einmal angesehen — der
    /// <see cref="ConsoleAdapter"/> beantwortet sie dabei, also steht die Antwort dann fest.
    /// </summary>
    public async IAsyncEnumerable<CliEvent> Tee(IAsyncEnumerable<CliEvent> events, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var cliEvent in events.WithCancellation(cancellationToken))
        {
            switch (cliEvent)
            {
                case ProgressEvent:
                    break;
                case ToolCallEvent call:
                    this.WriteToolCall(call);
                    break;
                default:
                    this.Write(Kind(cliEvent), cliEvent.Printable);
                    break;
            }

            yield return cliEvent;

            switch (cliEvent)
            {
                case IChoiceInteraction choice:
                    this.Write("antwort", choice.AnswerDisplay ?? "(keine — abgebrochen)");
                    break;
                case TextEvent text:
                    this.Write("antwort", text.TryGetAnswer(out var value)
                        ? (value is null ? "(keine Eingabe)" : $"({value.Length} Zeichen)")
                        : "(keine — abgebrochen)");
                    break;
            }
        }

        this.FlushRepeats();
    }

    /// <summary>
    /// Kommandozeile, Ergebnis, stdout, stderr. Derselbe Aufruf mit derselben Antwort direkt
    /// hintereinander (Polling: <c>sc queryex</c> alle 500 ms) steht nur einmal da, plus eine Zählzeile.
    /// </summary>
    private void WriteToolCall(ToolCallEvent call)
    {
        // Dauer gehört nicht zur Gleichheit: dieselbe Abfrage mit derselben Antwort ist auch nach 12 statt 15 ms dieselbe.
        var key = string.Join("\n", call.CommandLine, call.ExitCode, call.Note, call.StdOut, call.StdErr);
        if (key == this.lastToolCall)
        {
            this.toolCallRepeats++;
            return;
        }

        this.FlushRepeats();
        this.lastToolCall = key;
        this.Append("aufruf", call.CommandLine);
        this.Append("ergebn", call.Result);
        this.WriteStream("  out", call.StdOut);
        this.WriteStream("  err", call.StdErr);
    }

    private void WriteStream(string kind, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var trimmed = text.TrimEnd();
        this.Append(kind, trimmed.Length > MaxStreamLength
            ? trimmed[..MaxStreamLength] + $"\n… ({trimmed.Length - MaxStreamLength} weitere Zeichen ausgelassen)"
            : trimmed);
    }

    private void FlushRepeats()
    {
        if (this.toolCallRepeats > 0)
        {
            this.Append("ergebn", $"(derselbe Aufruf mit derselben Antwort noch {this.toolCallRepeats}× wiederholt)");
            this.toolCallRepeats = 0;
        }

        this.lastToolCall = null;
    }

    /// <summary>Eine Zeile (mehrzeiliger Text: je Zeile ein Eintrag); vorher die Zählzeile offener Wiederholungen.</summary>
    public void Write(string kind, string text)
    {
        this.FlushRepeats();
        this.Append(kind, text);
    }

    private void Append(string kind, string text)
    {
        if (this.path is null || this.mutex is null)
        {
            return;
        }

        var stamp = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{this.processId}] {kind,-7} ";
        var lines = string.Concat(text.Split('\n').Select(line => stamp + line.TrimEnd('\r') + Environment.NewLine));
        try
        {
            this.mutex.WaitOne();
        }
        catch (AbandonedMutexException)
        {
            // Das andere Fenster ist beim Schreiben beendet worden; der Mutex gehört jetzt uns.
        }

        try
        {
            File.AppendAllText(this.path, lines);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Ein volles oder gesperrtes Laufwerk darf das Kommando nicht beenden.
        }
        finally
        {
            this.mutex.ReleaseMutex();
        }
    }

    /// <summary>
    /// Die Ursache hinter einer fertigen Fehlermeldung (<see cref="Exception.InnerException"/>,
    /// samt Stacktrace): die Meldung für den Benutzer verschweigt sie oft.
    /// </summary>
    public void WriteCause(Exception exception)
    {
        if (exception.InnerException is { } cause)
        {
            this.Write("ursache", cause.ToString());
        }
    }

    public void Dispose()
    {
        this.mutex?.Dispose();
    }

    private static string Kind(CliEvent cliEvent) => cliEvent switch
    {
        IChoiceInteraction => "frage",
        TextEvent => "eingabe",
        ErrEvent => "stderr",
        DiagnosticEvent => "info",
        _ => "stdout"
    };

    private static string Quote(string arg) => arg.Contains(' ') ? $"\"{arg}\"" : arg;

    private static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }
}
