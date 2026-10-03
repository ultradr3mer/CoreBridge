using System.Threading.Channels;
using CoreBridge.Interaction;

// Aufruf: dotnet run --project tools/CoreBridge.Interaction.Demo
// Spielt nacheinander alles durch, was CoreBridge.Interaction kann — mit derselben
// Verdrahtung wie Program.Run in der CLI: das „Kommando“ (Produce) schreibt Ereignisse
// in einen Kanal, der ConsoleAdapter liest sie, zeichnet sie und beantwortet Rückfragen.
// Ohne Konsole (umgeleitete Eingabe) gewinnen überall die Standardantworten.

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

var channel = Channel.CreateUnbounded<CliEvent>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
var events = new EventFactory(channel.Writer);
var producer = Task.Run(async () =>
{
    try
    {
        await Produce(events, cancellation.Token);
        events.AssertNoOpenInteractions();
        channel.Writer.TryComplete();
    }
    catch (Exception exception)
    {
        events.CancelOpen();
        channel.Writer.TryComplete(exception);
    }
});

try
{
    await new ConsoleAdapter().AcceptEventStream(channel.Reader.ReadAllAsync());
    await producer;
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Abgebrochen.");
    return 130;
}
catch (ProgramExecutionException exception)
{
    Console.Error.WriteLine(exception.Message);
    return exception.ExitCode;
}

static async Task Produce(EventFactory events, CancellationToken cancellationToken)
{
    // 1. Ausgabe: stdout und stderr; ein alleinstehendes „OK“ wird grün.
    await Heading(events, "1. Ausgabe");
    await events.Emit(events.Std("Eine normale Zeile (StdEvent)."));
    await events.Emit(events.Err("Eine Fehlerzeile (ErrEvent) — rot an einer Konsole."));
    await events.Emit(events.Std("OK"));

    // 2. Fortschritt: reine Ticks; der ConsoleAdapter dreht einen Spinner mit Sekunden.
    await Heading(events, "2. Spinner (3 s)");
    await Ticks(events, TimeSpan.FromSeconds(3), offer: null, cancellationToken);
    await events.Emit(events.Std("Fertig gewartet."));

    // 3. Ja/Nein: das Kommando wartet auf die Antwort; ohne Konsole gilt „No“.
    await Heading(events, "3. Ja/Nein-Rückfrage");
    var yes = await events.AskAsync(events.Choice<YesNoOptions>("Weiter mit der Demo?")) == YesNoOptions.Yes;
    await events.Emit(events.Std(yes ? "Antwort: Ja." : "Antwort: Nein (oder Standard) — die Demo läuft trotzdem weiter."));

    // 4. Angebot auf Ticks: die Arbeit läuft weiter, die Frage bleibt offen und wird per
    //    Taste beantwortet (OptionDef.Keys). Ohne Antwort schließt das Kommando sie mit
    //    dem Standard „weiter warten“ — so bleibt am Ende nichts offen.
    await Heading(events, "4. Warten mit Angebot (10 s): k = überspringen, Esc = abbrechen");
    var offer = events.Choice<WaitOptions>("Lange Arbeit läuft:");
    try
    {
        var answer = await Ticks(events, TimeSpan.FromSeconds(10), offer, cancellationToken);
        await events.Emit(events.Std(answer switch
        {
            WaitOptions.Skip => "Übersprungen — es geht ohne den Rest der Wartezeit weiter.",
            WaitOptions.Cancel => throw new OperationCanceledException("Per Esc abgebrochen."),
            _ => "Die Arbeit ist von selbst fertig geworden."
        }));
    }
    finally
    {
        offer.SetDefault();
    }

    // 5. Eigene Aufzählung: jede Enum mit [OptionDef] und genau einem IsDefault geht.
    await Heading(events, "5. Auswahl mit eigener Aufzählung");
    var color = await events.AskAsync(events.Choice<ColorOptions>("Lieblingsfarbe?"));
    await events.Emit(events.Std($"Gewählt: {color}."));

    // 6. Freitext: ohne Konsole oder bei EOF ist die Antwort null.
    await Heading(events, "6. Freitext");
    var name = await events.AskAsync(events.Text("Wie heißt du? "));
    await events.Emit(events.Std(string.IsNullOrWhiteSpace(name) ? "Kein Name — auch gut." : $"Hallo, {name.Trim()}!"));

    await events.Emit(events.Std(string.Empty));
    await events.Emit(events.Std($"Demo fertig nach {events.Elapsed.TotalSeconds:F1} s."));
}

static async Task Heading(EventFactory events, string title)
{
    await events.Emit(events.Std(string.Empty));
    await events.Emit(events.Std($"== {title} =="));
}

// Tickt alle 200 ms, bis die Zeit um ist oder das Angebot beantwortet wurde; liefert die Antwort
// (Wait, wenn niemand eine Taste gedrückt hat).
static async Task<WaitOptions> Ticks(EventFactory events, TimeSpan duration, ChoiceEvent<WaitOptions>? offer, CancellationToken cancellationToken)
{
    var until = events.Elapsed + duration;
    while (events.Elapsed < until)
    {
        if (offer is { IsOpen: false })
        {
            return offer.GetUserInputAsync().GetResult();
        }

        await events.Emit(offer is null ? events.Tick() : events.Tick(offer));
        await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
    }

    return WaitOptions.Wait;
}

/// <summary>Angebot während einer langen Arbeit; der Standard ist „weiter warten“.</summary>
internal enum WaitOptions
{
    [OptionDef("überspringen", "s,skip", [Keys.K])]
    Skip,

    [OptionDef("abbrechen", "c,cancel", [Keys.Esc])]
    Cancel,

    [OptionDef("weiter warten", "w,wait", IsDefault = true)]
    Wait
}

/// <summary>Eine eigene Auswahl: die Muster sind die Eingaben, die ReadLine akzeptiert.</summary>
internal enum ColorOptions
{
    [OptionDef("Rot", "r,rot")]
    Rot,

    [OptionDef("Grün", "g,gruen,grün")]
    Gruen,

    [OptionDef("Keine", "k,keine", IsDefault = true)]
    Keine
}
