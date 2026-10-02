using System.Reflection;
using CommandLine;

namespace CoreBridge.Cli;

/// <summary>
/// Ein Parameter eines Verbs, gelesen aus dem <c>[Option]</c>-Attribut der
/// Optionsklasse. <c>Abbreviation</c> ist die Kurzform mit Bindestrich
/// (z. B. <c>-n</c>) oder <c>null</c> ohne.
/// </summary>
public sealed record CommandParameter(string Name, string? Abbreviation, bool Required, string Help);

/// <summary>
/// Ein Verb mit Hilfe und Parametern, gelesen aus <c>[Verb]</c> und <c>[Option]</c>.
/// </summary>
public sealed record CommandMenu(string Name, string Help, IReadOnlyList<CommandParameter> Parameters);

/// <summary>
/// Das Menü aller registrierten Verben: <see cref="All"/> in der Reihenfolge von
/// <c>Program.RegisteredOptionTypes</c>, <see cref="ByName"/> zum Nachschlagen.
/// <para>
/// Die Attribute in <c>Options.cs</c> sind die einzige Quelle — Name, Hilfetext,
/// Kurzform und Pflichtangabe stehen genau einmal, am <c>[Verb]</c>-/<c>[Option]</c>-Attribut.
/// Gelesen wird per Reflection, einmalig beim ersten Zugriff und danach gecacht.
/// </para>
/// </summary>
public static class CommandMenus
{
    private static readonly Lazy<IReadOnlyList<CommandMenu>> Cache =
        new(BaueAlle, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Alle Verben mit ihren Parametern, in der Reihenfolge von <c>Program.RegisteredOptionTypes</c>.</summary>
    public static IReadOnlyList<CommandMenu> All => Cache.Value;

    /// <summary>Wie <see cref="All"/>, aber nach Verbnamen (Groß-/Kleinschreibung egal).</summary>
    public static IReadOnlyDictionary<string, CommandMenu> ByName { get; } =
        Cache.Value.ToDictionary(menu => menu.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Menü eines Verbs oder <c>null</c>, wenn es das Verb nicht gibt.</summary>
    public static CommandMenu? Find(string name)
        => ByName.TryGetValue(name, out var menu) ? menu : null;

    /// <summary>Menü einer Optionsklasse, z. B. <c>CommandMenus.For&lt;VmCreateOptions&gt;()</c>.</summary>
    public static CommandMenu For<TOptions>() where TOptions : IOptions
        => For(typeof(TOptions));

    /// <summary>Menü einer Optionsklasse; <paramref name="optionsType"/> muss eine <c>[Verb]</c>-Klasse sein.</summary>
    public static CommandMenu For(Type optionsType)
    {
        var name = optionsType.GetCustomAttribute<VerbAttribute>()?.Name
            ?? throw new ArgumentException($"{optionsType.Name} ist keine [Verb]-Klasse.", nameof(optionsType));
        return ByName[name];
    }

    private static IReadOnlyList<CommandMenu> BaueAlle()
        => Program.RegisteredOptionTypes
            .Where(type => type.GetCustomAttribute<VerbAttribute>() is not null)
            .Select(Baue)
            .ToList();

    private static CommandMenu Baue(Type optionsType)
    {
        var verb = optionsType.GetCustomAttribute<VerbAttribute>()!;
        var parameter = optionsType
            // GetProperties liefert die eigenen und die geerbten Properties mit, so
            // kommt das -t/--timeout aus MultipassOptions automatisch mit. Versteckte
            // Optionen (z. B. --pause-on-exit) gehören nicht ins Menü.
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => (Property: property, Option: property.GetCustomAttribute<OptionAttribute>()))
            .Where(x => x.Option is { Hidden: false })
            .Select(x => new CommandParameter(
                Name: x.Option!.LongName ?? x.Property.Name.ToLowerInvariant(),
                Abbreviation: string.IsNullOrEmpty(x.Option.ShortName) ? null : "-" + x.Option.ShortName.TrimStart('-'),
                Required: x.Option.Required,
                Help: x.Option.HelpText ?? string.Empty))
            .ToList();

        return new CommandMenu(verb.Name, verb.HelpText ?? string.Empty, parameter);
    }
}
