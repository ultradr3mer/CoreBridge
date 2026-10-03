namespace CoreBridge.Interaction;

/// <summary>
/// Basis jedes Ereignisses im Ausgabestrom eines Kommandos. Der Zeitstempel
/// kommt von der <see cref="EventFactory"/> des laufenden Kommandos, damit
/// Ereignisse zweier parallel laufender Kommandos sich nicht mischen.
/// </summary>
/// <param name="emissionTime">Laufzeit des Kommandos beim Entstehen des Ereignisses.</param>
/// <param name="printable">Der Text, den die Konsole schreibt.</param>
public abstract class CliEvent
{
    protected CliEvent(TimeSpan emissionTime, string printable)
    {
        this.EmissionTime = emissionTime;
        this.Printable = printable;
    }

    /// <summary>Wann das Ereignis entstanden ist (Laufzeit des Kommandos).</summary>
    public TimeSpan EmissionTime { get; }

    /// <summary>Der Text, den die Konsole schreibt.</summary>
    public string Printable { get; }

    /// <summary>
    /// Wann das Ereignis abgeschlossen wurde, <c>null</c> solange es offen ist.
    /// </summary>
    public TimeSpan? CompletedTime { get; internal set; }

    public override string ToString() => $"{this.EmissionTime:hh\\:mm\\:ss\\.fff} {this.Printable}";
}

/// <summary>Eine reguläre Ausgabezeile (stdout).</summary>
public class StdEvent : CliEvent
{
    public StdEvent(TimeSpan emissionTime, string printable)
        : base(emissionTime, printable)
    {
    }
}

/// <summary>Eine Fehlermeldung (stderr).</summary>
public class ErrEvent : CliEvent
{
    /// <param name="emissionTime">Laufzeit des Kommandos beim Entstehen des Ereignisses.</param>
    /// <param name="printable">Der Text, den die Konsole schreibt.</param>
    /// <param name="isNotice">Siehe <see cref="IsNotice"/>.</param>
    public ErrEvent(TimeSpan emissionTime, string printable, bool isNotice = false)
        : base(emissionTime, printable)
    {
        this.IsNotice = isNotice;
    }

    /// <summary>
    /// Ein Hinweis, der auf stderr gehört, aber kein Fehler ist (z. B. dass
    /// gegen eine Simulation gearbeitet wird). Die Oberfläche stellt ihn
    /// zurückhaltend dar statt als Fehler.
    /// </summary>
    public bool IsNotice { get; }
}

/// <summary>
/// Ein Fortschrittspunkt eines Warte-Loops. Die Konsole schreibt solche Punkte
/// hintereinander in dieselbe Zeile, ohne sie abzuschließen.
/// </summary>
/// <remarks>
/// Mit <see cref="Offer"/> trägt der Tick eine offene Auswahl, die der Benutzer
/// beantworten darf, während weiter gewartet wird (überspringen, abbrechen). Das
/// Angebot reist nur auf Ticks, nie als eigenes Ereignis: ein Konsument, der Ticks
/// übergeht (ohne Konsole, die Lifecycle-Tests), bekommt es nie zu sehen, und der
/// Erzeuger schließt es am Ende des Wartens mit der Standardantwort.
/// </remarks>
public class ProgressEvent : CliEvent
{
    /// <summary>Der Punkt, den eine Konsole ohne Spinner je Tick schreibt.</summary>
    public const string Tick = ".";

    /// <param name="emissionTime">Laufzeit des Kommandos beim Entstehen des Ereignisses.</param>
    /// <param name="offer">Optional: eine Auswahl, die während des Wartens offen ist.</param>
    public ProgressEvent(TimeSpan emissionTime, IChoiceInteraction? offer = null)
        : base(emissionTime, Tick)
    {
        this.Offer = offer;
    }

    /// <summary>Die während des Wartens offene Auswahl, <c>null</c> = reiner Fortschritt.</summary>
    public IChoiceInteraction? Offer { get; }
}

/// <summary>
/// Innenleben eines Kommandos (Diagnose-Ausgabe neben stdout und stderr): Einstellungen,
/// Eingriffe ins System, Entscheidungen. Die Konsole zeigt es nicht; das CLI-Protokoll
/// schreibt es mit, und jeder andere Konsument entscheidet selbst.
/// </summary>
public class DiagnosticEvent : CliEvent
{
    public DiagnosticEvent(TimeSpan emissionTime, string printable)
        : base(emissionTime, printable)
    {
    }
}

/// <summary>
/// Ein beendeter Aufruf eines externen Werkzeugs (multipass, sc, git, UAC-Neustart) mit
/// Argumenten, Ergebnis und vollständiger Ausgabe. Entsteht, wenn der Aufruf endet;
/// <see cref="CliEvent.EmissionTime"/> minus <see cref="Duration"/> ist sein Start.
/// </summary>
public sealed class ToolCallEvent : DiagnosticEvent
{
    public ToolCallEvent(
        TimeSpan emissionTime,
        string tool,
        IReadOnlyList<string> args,
        int? exitCode,
        TimeSpan duration,
        string stdOut,
        string stdErr,
        string? note)
        : base(emissionTime, $"{FormatCommandLine(tool, args)} → {FormatResult(exitCode, duration, note)}")
    {
        this.Tool = tool;
        this.Args = args;
        this.ExitCode = exitCode;
        this.Duration = duration;
        this.StdOut = stdOut;
        this.StdErr = stdErr;
        this.Note = note;
    }

    /// <summary>Das gestartete Programm (Pfad oder Name).</summary>
    public string Tool { get; }

    public IReadOnlyList<string> Args { get; }

    /// <summary><c>null</c>: kein Exit-Code (Start gescheitert, Timeout, Prozess beendet).</summary>
    public int? ExitCode { get; }

    public TimeSpan Duration { get; }

    public string StdOut { get; }

    public string StdErr { get; }

    /// <summary>Ergänzung zum Ergebnis, z. B. „Timeout, Prozess beendet“.</summary>
    public string? Note { get; }

    /// <summary>Die Kommandozeile, Argumente mit Leerzeichen in Anführungszeichen.</summary>
    public string CommandLine => FormatCommandLine(this.Tool, this.Args);

    /// <summary>Exit-Code, Dauer und Notiz in einer Zeile.</summary>
    public string Result => FormatResult(this.ExitCode, this.Duration, this.Note);

    private static string FormatCommandLine(string tool, IEnumerable<string> args) =>
        string.Join(' ', args.Prepend(tool).Select(arg => arg.Length == 0 || arg.Any(char.IsWhiteSpace) ? $"\"{arg}\"" : arg));

    private static string FormatResult(int? exitCode, TimeSpan duration, string? note) =>
        (exitCode is { } code ? $"Exit {code}" : "kein Exit-Code") + $" nach {duration.TotalMilliseconds:F0} ms"
        + (note is null ? string.Empty : $" — {note}");
}
