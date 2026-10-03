using CoreBridge.Core.Multipass;
using CoreBridge.Interaction;

namespace CoreBridge.Cli.Tests;

/// <summary>
/// Prüft die Auswahl-Rückfragen über das typfreie <see cref="IChoiceInteraction"/>
/// — den Weg, auf dem Konsumenten (ConsoleAdapter, Lifecycle-Test) sie beantworten.
/// </summary>
// GetResult blockiert hier nie: die Antwort ist vorher schon gesetzt.
#pragma warning disable xUnit1031
public class ChoiceEventTests
{
    private enum DreiWege
    {
        [OptionDef("Behalten", "b")]
        Behalten,

        [OptionDef("Ersetzen", "e")]
        Ersetzen,

        [OptionDef("Abbrechen", "a", IsDefault = true)]
        Abbrechen
    }

    private enum OhneStandard
    {
        [OptionDef("Eins", "1")]
        Eins,

        [OptionDef("Zwei", "2")]
        Zwei
    }

    private enum ZweiStandards
    {
        [OptionDef("Eins", "1", IsDefault = true)]
        Eins,

        [OptionDef("Zwei", "2", IsDefault = true)]
        Zwei
    }

    [Fact]
    public void JedeAuswahlAufzaehlungDerCliHatGenauEinenStandard()
    {
        // Jede Aufzählung mit [OptionDef] in der CLI muss ohne Konsole beantwortbar sein.
        var choiceEnums = typeof(ChoiceEvent<>).Assembly.GetTypes()
            .Where(type => type.IsEnum)
            .Where(type => type.GetFields().Any(field => field.IsDefined(typeof(OptionDef), inherit: false)))
            .ToList();

        Assert.NotEmpty(choiceEnums);
        foreach (var type in choiceEnums)
        {
            var defaults = type.GetFields()
                .SelectMany(field => field.GetCustomAttributes(typeof(OptionDef), inherit: false).Cast<OptionDef>())
                .Count(def => def.IsDefault);
            Assert.True(defaults == 1, $"{type.Name} hat {defaults} Standardantworten.");
        }
    }

    [Fact]
    public void StandardantwortIstDerMarkierteWertNichtDerErste()
    {
        IChoiceInteraction choice = new ChoiceEvent<DreiWege>(TimeSpan.Zero, "Was tun?");

        Assert.Equal("Abbrechen", choice.DefaultOption.Display);
        Assert.True(choice.SetDefault());
        Assert.Equal(DreiWege.Abbrechen, ((ChoiceEvent<DreiWege>)choice).GetUserInputAsync().GetResult());
    }

    [Fact]
    public void NeueAufzaehlungWirdUeberDasInterfaceBeantwortet()
    {
        var typed = new ChoiceEvent<DreiWege>(TimeSpan.Zero, "Was tun?");
        IChoiceInteraction choice = typed;

        Assert.False(choice.TryParseChoice("x"));
        Assert.True(choice.IsOpen);

        Assert.True(choice.TryParseChoice("E"));
        Assert.False(choice.IsOpen);
        Assert.Equal(DreiWege.Ersetzen, typed.GetUserInputAsync().GetResult());
        Assert.Equal(3, choice.Options.Count);
    }

    [Fact]
    public void YesNoUndOkCancelLehnenOhneEingabeAb()
    {
        Assert.Equal(YesNoOptions.No, OptionDef.DefaultOf<YesNoOptions>().Value);
        Assert.Equal(OkCancelOptions.Cancel, OptionDef.DefaultOf<OkCancelOptions>().Value);
    }

    /// <summary>
    /// Bestätigen per Tastendruck (UAC-Abfrage des Neustarts): Enter = Ok,
    /// Strg+C = Cancel, andere Tasten antworten nicht.
    /// </summary>
    [Fact]
    public void OkCancelPerTastendruck()
    {
        var ok = new ChoiceEvent<OkCancelOptions>(TimeSpan.Zero, "Weiter?") { KeyPress = true };
        Assert.True(((IChoiceInteraction)ok).KeyPress);
        Assert.False(ok.TryChooseKey(Keys.Y));
        Assert.True(ok.TryChooseKey(Keys.Enter));
        Assert.Equal(OkCancelOptions.Ok, ok.GetUserInputAsync().GetResult());

        var cancel = new ChoiceEvent<OkCancelOptions>(TimeSpan.Zero, "Weiter?") { KeyPress = true };
        Assert.True(cancel.TryChooseKey(Keys.Ctrl | Keys.C));
        Assert.Equal(OkCancelOptions.Cancel, cancel.GetUserInputAsync().GetResult());

        Assert.False(new ChoiceEvent<YesNoOptions>(TimeSpan.Zero, "Ja?").KeyPress);
    }

    /// <summary>Angebote auf Ticks werden per Taste beantwortet — über die <c>Keys</c> der Optionen.</summary>
    [Fact]
    public void TasteBeantwortetUeberOptionDefKeys()
    {
        IChoiceInteraction offer = new ChoiceEvent<HungStopOptions>(TimeSpan.Zero, "Warten auf den Stop:");

        Assert.False(offer.TryChooseKey(Keys.X));
        Assert.False(offer.TryChooseKey(Keys.W));
        Assert.True(offer.IsOpen);

        Assert.False(offer.TryChooseKey(Keys.K));
        Assert.True(offer.TryChooseKey(Keys.Enter));
        Assert.Equal(HungStopOptions.Kill, ((ChoiceEvent<HungStopOptions>)offer).GetUserInputAsync().GetResult());
        Assert.False(offer.TryChooseKey(Keys.Esc));
    }

    /// <summary>
    /// Ohne Antwort schließt der Erzeuger das Angebot mit „weiter warten“; danach ist
    /// nichts mehr offen, und das Kommando endet nicht an einer vergessenen Rückfrage.
    /// </summary>
    [Fact]
    public void UnbeantwortetesAngebotAufTicksSchliesstMitWeiterWarten()
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<CliEvent>();
        var events = new EventFactory(channel.Writer);

        var offer = events.Choice<HungStopOptions>("Warten auf den Stop:");
        var tick = events.Tick(offer);

        Assert.Same(offer, tick.Offer);
        Assert.Equal(1, events.OpenInteractionCount);
        Assert.Throws<InvalidOperationException>(() => events.AssertNoOpenInteractions());

        Assert.True(offer.SetDefault());
        Assert.Equal(HungStopOptions.Wait, offer.GetUserInputAsync().GetResult());
        events.AssertNoOpenInteractions();
    }

    [Fact]
    public void FalschDeklarierteAufzaehlungFaelltBeimErzeugenAuf()
    {
        Assert.Throws<InvalidOperationException>(() => new ChoiceEvent<OhneStandard>(TimeSpan.Zero, "?"));
        Assert.Throws<InvalidOperationException>(() => new ChoiceEvent<ZweiStandards>(TimeSpan.Zero, "?"));
    }
}
