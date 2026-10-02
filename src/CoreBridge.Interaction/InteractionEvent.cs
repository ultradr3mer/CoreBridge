using System.Runtime.CompilerServices;

namespace CoreBridge.Interaction;

/// <summary>
/// Ein Ereignis, das eine Antwort des Benutzers braucht. Das erzeugende
/// Kommando behält eine lokale Kopie, leitet das Ereignis an den Konsumenten
/// weiter und wartet auf <see cref="GetUserInputAsync"/>; der Konsument
/// beantwortet es (siehe <see cref="ChoiceEvent{T}.TryParseChoice"/>), worauf
/// die wartende Seite fortgesetzt wird.
/// </summary>
/// <remarks>
/// Das <see cref="TaskCompletionSource{TResult}"/> benutzt
/// <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>. Ohne das
/// liefe die Fortsetzung des Erzeugers <em>inline</em> auf dem Thread des
/// Konsumenten — also mitten in dessen Schleife, die gerade den Kanal leert.
/// </remarks>
/// <typeparam name="T">Antworttyp, z. B. ein Aufzählungswert.</typeparam>
public abstract class InteractionEvent<T> : StdEvent, IOpenInteraction
{
    private readonly TaskCompletionSource<T> completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected InteractionEvent(TimeSpan emissionTime, string message, TimeSpan? timeout)
        : base(emissionTime, message)
    {
        this.Timeout = timeout;
        this.ValidUntil = timeout is null ? null : emissionTime + timeout.Value;
    }

    /// <summary>
    /// Wie lange auf eine Antwort gewartet wird, <c>null</c> = unbegrenzt.
    /// </summary>
    public TimeSpan? Timeout { get; }

    /// <summary>
    /// Laufzeit, bis zu der das Ereignis gültig ist, <c>null</c> = unbegrenzt.
    /// Nach Ablauf gilt die Standardantwort; ein nie beantwortetes Ereignis
    /// darf den Prozess nicht hängen lassen.
    /// </summary>
    public TimeSpan? ValidUntil { get; }

    /// <summary>
    /// Die Antwort. Warten darauf, bis der Konsument <see cref="SetResult"/>
    /// oder <see cref="SetCanceled"/> aufgerufen hat.
    /// </summary>
    public TaskAwaiter<T> GetUserInputAsync()
    {
        return this.completion.Task.GetAwaiter();
    }

    /// <summary>
    /// Dieselbe Antwort als <see cref="Task{TResult}"/> — für Stellen, die
    /// <c>await</c>en. Ein Awaiter selbst lässt sich in C# nicht awaiten,
    /// deshalb gibt es beide Wege.
    /// </summary>
    internal Task<T> Answer => this.completion.Task;

    /// <summary>Noch unbeantwortet?</summary>
    public bool IsOpen => !this.completion.Task.IsCompleted;

    /// <summary>
    /// Beantwortet das Ereignis. Mehrfaches Beantworten ist ein Fehler des
    /// Konsumenten, kein Grund für eine Ausnahme — deshalb <c>Try</c>.
    /// </summary>
    public bool SetResult(T value)
    {
        if (!this.completion.TrySetResult(value))
        {
            return false;
        }

        this.onCompleted?.Invoke();
        return true;
    }

    /// <summary>Verwirft die Frage (Abbruch, z. B. Strg+C).</summary>
    public bool SetCanceled()
    {
        if (!this.completion.TrySetCanceled())
        {
            return false;
        }

        this.onCompleted?.Invoke();
        return true;
    }

    void IOpenInteraction.Cancel() => this.SetCanceled();

    void IOpenInteraction.Attach(Action onCompleted) => this.onCompleted = onCompleted;

    private Action? onCompleted;
}

/// <summary>
/// Das typfreie Gesicht einer Rückfrage: genug, um offene Rückfragen zu
/// verfolgen und abzubrechen, ohne den Antworttyp zu kennen.
/// </summary>
public interface IOpenInteraction
{
    /// <summary>Noch unbeantwortet?</summary>
    bool IsOpen { get; }

    /// <summary>Verwirft die Frage, damit keine wartende Seite hängen bleibt.</summary>
    void Cancel();

    /// <summary>
    /// Meldet, wann die Frage geschlossen wurde. Die erzeugende
    /// <see cref="EventFactory"/> hängt hier ihr Buch führen, damit eine
    /// beantwortete Frage am Kommandoende nicht als vergessen gilt.
    /// </summary>
    void Attach(Action onCompleted);
}
