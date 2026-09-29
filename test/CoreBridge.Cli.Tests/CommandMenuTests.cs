using System.Reflection;
using CommandLine;
using CoreBridge.Cli;

namespace CoreBridge.Cli.Tests;

/// <summary>
/// Sichert ab, dass die von Hand gepflegten <see cref="CommandMenu"/>-Deklarationen mit
/// den <c>[Verb]</c>-/<c>[Option]</c>-Attributen der Optionsklassen übereinstimmen.
/// Reflection ist hier ausdrücklich erlaubt und gewollt — nur im Test.
/// </summary>
public class CommandMenuTests
{
    [Fact]
    public void MenuesEnthaeltGenauDieRegistriertenVerbe()
    {
        var registered = Program.RegisteredOptionTypes;
        Assert.Equal(registered.Count, CommandMenus.All.Count);
        Assert.Equal(registered.Count, CommandMenus.ByName.Count);

        foreach (var type in registered)
        {
            var verb = type.GetCustomAttribute<VerbAttribute>();
            Assert.NotNull(verb);
            Assert.True(
                CommandMenus.ByName.ContainsKey(verb!.Name),
                $"Verb {verb.Name} ({type.Name}) fehlt in CommandMenus.");
        }
    }

    [Fact]
    public void MenueStimmtMitDenAttributenUeberein()
    {
        var nachTyp = Program.RegisteredOptionTypes
            .ToDictionary(t => t.Name, t => CommandMenus.ByName[VerbName(t)]);

        foreach (var (typName, menu) in nachTyp)
        {
            var type = Program.RegisteredOptionTypes.Single(t => t.Name == typName);
            var verb = type.GetCustomAttribute<VerbAttribute>()!;
            Assert.Equal(verb.Name, menu.Name);
            Assert.Equal(verb.HelpText, menu.Help);

            // Erwartete Parameter: alle nicht versteckten [Option]-Properties
            // (inklusive der Basisklasse) minus die [Option]-Properties mit Setter
            // ohne Attribut (die private Getter-Variante -t/--timeout zählt mit).
            var erwartet = type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.GetCustomAttribute<OptionAttribute>())
                .Where(o => o is not null && !o.Hidden)
                .Select(o => (o!.LongName, Abbrev(o.ShortName), o.Required, o.HelpText))
                .ToList();

            var ist = menu.Parameters
                .Select(p => (p.Name, p.Abbreviation?.TrimStart('-'), p.Required, p.Help))
                .ToList();

            Assert.Equal(erwartet.Count, ist.Count);
            for (var i = 0; i < erwartet.Count; i++)
            {
                Assert.Equal(erwartet[i].Item1, ist[i].Name);
                Assert.Equal(erwartet[i].Item2, ist[i].Item2);
                Assert.Equal(erwartet[i].Required, ist[i].Required);
                Assert.Equal(erwartet[i].HelpText, ist[i].Help);
            }
        }
    }

    private static string VerbName(Type type) => type.GetCustomAttribute<VerbAttribute>()!.Name;

    private static string? Abbrev(string? shortName) => string.IsNullOrEmpty(shortName) ? null : shortName.TrimStart('-');
}
