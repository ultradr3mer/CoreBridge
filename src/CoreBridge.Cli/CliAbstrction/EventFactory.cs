using System.Diagnostics;
using System.Threading.Channels;

namespace CoreBridge.Cli.CliAbstrction;

/// <summary>
/// Erzeugt die Ereignisse eines einzelnen Kommandos und reicht sie an den
/// Konsumenten weiter. Sie gehört zu genau einem <see cref="Program.Run"/>-
/// Aufruf (siehe <see cref="CommandContext.Events"/>) und hält dessen
/// Stopwatch — deshalb statische Zustände hier keinen Platz haben.
/// </summary>
public sealed class EventFactory
{
    private readonly ChannelWriter<CliEvent> writer;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly HashSet<IOpenInteraction> open = [];

    internal EventFactory(ChannelWriter<CliEvent> writer)
    {
        this.writer = writer;
    }

    /// <summary>Laufzeit des Kommandos — der Zeitstempel jedes Ereignisses.</summary>
    public TimeSpan Elapsed => this.clock.Elapsed;

    /// <summary>Noch unbeantwortete Rückfragen; siehe <see cref="AssertNoOpenInteractions"/>.</summary>
    public int OpenInteractionCount => this.open.Count;

    public StdEvent Std(string data) => new(this.Elapsed, data);

    public ErrEvent Err(string data) => new(this.Elapsed, data);

    public ProgressEvent Tick() => new(this.Elapsed);

    public ChoiceEvent<T> Choice<T>(string message, TimeSpan? timeout = null) where T : struct, Enum
    {
        return Track(new ChoiceEvent<T>(this.Elapsed, message, timeout));
    }

    public TextEvent Text(string message, TimeSpan? timeout = null)
    {
        return Track(new TextEvent(this.Elapsed, message, timeout));
    }

    /// <summary>
    /// Markiert eine Rückfrage als geschlossen. Der Aufruf hängt an
    /// <see cref="IOpenInteraction.Attach"/> und feuert deshalb automatisch,
    /// sobald der Konsument <c>SetResult</c> oder <c>SetCanceled</c> aufruft.
    /// </summary>
    private void Complete(IOpenInteraction interaction)
    {
        if (interaction is CliEvent cliEvent)
        {
            cliEvent.CompletedTime = this.Elapsed;
        }

        this.open.Remove(interaction);
    }

    /// <summary>
    /// Stellt eine Rückfrage und wartet auf die Antwort. Der Weg für
    /// Kommandos, die <c>await</c> können.
    /// </summary>
    public async ValueTask<T> AskAsync<T>(InteractionEvent<T> interaction)
    {
        await Emit(interaction).ConfigureAwait(false);
        return await interaction.Answer;
    }

    /// <summary>
    /// Dasselbe für <see cref="Func{TResult}"/>-Rückfragen aus
    /// CoreBridge.Core (z. B. <c>keepWaiting</c>), die auf einem
    /// Threadpool-Thread laufen. Dort kann nicht <c>await</c>et werden, also
    /// wird blockiert — sicher, weil der Konsument auf einem anderen Thread
    /// arbeitet und der Kanal die Frage schon zugestellt bekommen hat.
    /// </summary>
    public T Ask<T>(InteractionEvent<T> interaction)
    {
        // Der Kanal ist unbeschränkt, deshalb ist das hier nur ein
        // Formalismus — aber GetResult() auf dem Awaiter ist der blockierende
        // Weg, den die Func<bool>-Callbacks aus Core brauchen.
        this.Emit(interaction).AsTask().GetAwaiter().GetResult();
        return interaction.GetUserInputAsync().GetResult();
    }

    /// <summary>Reicht ein beliebiges Ereignis an den Konsumenten weiter.</summary>
    public ValueTask Emit(CliEvent cliEvent)
    {
        return this.writer.WriteAsync(cliEvent);
    }

    /// <summary>
    /// Bricht alle noch offenen Rückfragen ab (z. B. bei Strg+C), damit keine
    /// wartende Seite hängen bleibt.
    /// </summary>
    public void CancelOpen()
    {
        foreach (var interaction in this.open.ToArray())
        {
            interaction.Cancel();
            this.Complete(interaction);
        }
    }

    /// <summary>
    /// Sicherheitsnetz gegen eine vergessene Rückfrage: bleibt am Ende eines
    /// Kommandos etwas offen, ist das ein Fehler und kein Hänger.
    /// </summary>
    public void AssertNoOpenInteractions()
    {
        if (this.open.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"{this.open.Count} Rückfrage(n) wurden nie beantwortet: "
            + string.Join("; ", this.open.Select(interaction => interaction.ToString())));
    }

    private TEvent Track<TEvent>(TEvent interaction) where TEvent : IOpenInteraction
    {
        this.open.Add(interaction);
        interaction.Attach(() => this.Complete(interaction));
        return interaction;
    }
}
