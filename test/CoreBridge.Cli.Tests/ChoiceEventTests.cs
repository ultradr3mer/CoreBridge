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

    [Fact]
    public void FalschDeklarierteAufzaehlungFaelltBeimErzeugenAuf()
    {
        Assert.Throws<InvalidOperationException>(() => new ChoiceEvent<OhneStandard>(TimeSpan.Zero, "?"));
        Assert.Throws<InvalidOperationException>(() => new ChoiceEvent<ZweiStandards>(TimeSpan.Zero, "?"));
    }
}
