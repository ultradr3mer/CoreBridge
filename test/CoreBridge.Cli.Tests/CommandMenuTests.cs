using System.Reflection;
using CommandLine;
using CoreBridge.Cli;

namespace CoreBridge.Cli.Tests;

/// <summary>
/// Prüft das Menü, das aus den <c>[Verb]</c>-/<c>[Option]</c>-Attributen gelesen wird:
/// Vollständigkeit, Vererbung (das geerbte <c>-t/--timeout</c>) und vor allem, dass die
/// Angaben vom echten Parser akzeptiert werden. Reflection ist hier (nur im Test) erlaubt,
/// um den Parser generisch über den jeweiligen Options-Typ aufzurufen.
/// </summary>
public class CommandMenuTests
{
    [Fact]
    public void MenueEnthaeltGenauDieRegistriertenVerbe()
    {
        Assert.Equal(Program.RegisteredOptionTypes.Count, CommandMenus.All.Count);
        Assert.Equal(CommandMenus.All.Count, CommandMenus.ByName.Count);

        foreach (var type in Program.RegisteredOptionTypes)
        {
            var menu = CommandMenus.For(type);
            Assert.Equal(menu, CommandMenus.Find(menu.Name));
        }
    }

    [Fact]
    public void JedesVerbLaesstSichMitDenPflichtparameternParsen()
    {
        // Beweis, dass das Menü nicht nur abgelesene, sondern gültige Angaben enthält:
        // ein Aufruf mit genau den im Menü genannten Pflichtoptionen muss fehlerfrei sein.
        foreach (var (menu, type) in Paare())
        {
            var args = new List<string> { menu.Name };
            foreach (var parameter in menu.Parameters.Where(p => p.Required))
            {
                args.Add(parameter.Abbreviation ?? $"--{parameter.Name}");
                if (IstSchalter(type, parameter))
                {
                    continue;
                }

                args.Add("x");
            }

            var fehler = Parse(args, type);
            Assert.Empty(fehler);
        }
    }

    [Fact]
    public void FehlendePflichtoptionenMeldetDerParserGenausoWieDasMenu()
    {
        // Umgekehrte Richtung: ohne Angaben meldet der Parser genau die Optionen, die
        // das Menü als Required ausweist.
        foreach (var (menu, type) in Paare().Where(p => p.Item1.Parameters.Any(x => x.Required)))
        {
            var fehlend = Parse([menu.Name], type).OfType<MissingRequiredOptionError>().Count();
            Assert.Equal(menu.Parameters.Count(p => p.Required), fehlend);
        }
    }

    [Fact]
    public void GeerbterTimeoutErscheintNurBeiMultipassVerben()
    {
        foreach (var (menu, type) in Paare())
        {
            var erwartet = typeof(MultipassOptions).IsAssignableFrom(type);
            Assert.Equal(erwartet, menu.Parameters.Any(p => p.Name == "timeout"));
        }
    }

    [Fact]
    public void VersteckteOptionenErscheinenNichtImMenu()
    {
        Assert.DoesNotContain(CommandMenus.For<MultipassRestartOptions>().Parameters, p => p.Name == "pause-on-exit");
    }

    [Fact]
    public void UnbekanntesVerbLiefertNull()
    {
        Assert.Null(CommandMenus.Find("gibtsnicht"));
    }

    private static IEnumerable<(CommandMenu Menu, Type Type)> Paare()
        => Program.RegisteredOptionTypes.Select(type => (CommandMenus.For(type), type));

    private static bool IstSchalter(Type optionsType, CommandParameter parameter)
        => optionsType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .First(p => p.GetCustomAttribute<OptionAttribute>()?.LongName == parameter.Name)
            .PropertyType == typeof(bool);
    /// <summary>
    /// Ruft den echten Parser generisch über <paramref name="optionsType"/> auf und
    /// liefert seine Fehler. Das Ergebnis ist <c>ParserResult&lt;TOptions&gt;</c>, und
    /// <c>Errors</c> ist unabhängig von <c>T</c> immer <c>IEnumerable&lt;Error&gt;</c> —
    /// deshalb wird die Property statt des Ergebnisses geholt (kein Cast nötig).
    /// </summary>
    private static IReadOnlyList<Error> Parse(IReadOnlyList<string> args, Type optionsType)
    {
        // ParseArguments ist mehrfach überladen (IEnumerable<string> und string[]);
        // hier ist die Variante mit genau einem Parameter gemeint.
        var methode = typeof(Parser)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Single(m => m.Name == nameof(Parser.ParseArguments)
                && m.IsGenericMethodDefinition
                && m.GetGenericArguments().Length == 1
                && m.GetParameters().Length == 1);

        var ergebnis = methode.MakeGenericMethod(optionsType).Invoke(Parser.Default, [args.ToArray()]);
        var errors = (IEnumerable<Error>)ergebnis!.GetType().GetProperty("Errors")!.GetValue(ergebnis)!;
        return errors.ToList();
    }
}
