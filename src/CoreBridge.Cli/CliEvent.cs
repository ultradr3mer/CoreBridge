using CoreBridge.Core.Common;

namespace CoreBridge.Cli;

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
    public ErrEvent(TimeSpan emissionTime, string printable)
        : base(emissionTime, printable)
    {
    }
}

/// <summary>
/// Ein Fortschrittspunkt eines Warte-Loops. Die Konsole schreibt solche Punkte
/// hintereinander in dieselbe Zeile, ohne sie abzuschließen — das ersetzt den
/// früheren Trick, <see cref="Common.Progress.ProgressTick"/> als Textzeile zu
/// missbrauchen.
/// </summary>
public class ProgressEvent : CliEvent
{
    public ProgressEvent(TimeSpan emissionTime)
        : base(emissionTime, Progress.ProgressTick)
    {
    }
}
