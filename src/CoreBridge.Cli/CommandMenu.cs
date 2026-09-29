namespace CoreBridge.Cli;

/// <summary>
/// Ein Parameter eines Verbs — ohne Reflection aus den <c>[Option]</c>-Attributen
/// gelesen, sondern von Hand in der Menu-Deklaration der Optionsklasse gepflegt
/// (die Übereinstimmung sichert <c>CommandMenuTests</c>).
/// </summary>
/// <param name="Name">Langer Name, z. B. <c>name</c>.</param>
/// <param name="Abbreviation">Kurzform wie <c>-n</c> (mit Bindestrich) oder <c>null</c>.</param>
/// <param name="Required">Pflichtparameter wie in <c>[Option(Required = true)]</c>.</param>
/// <param name="Help">Hilfetext, wie er in der CLI-Hilfe erscheint.</param>
public sealed record CommandParameter(string Name, string? Abbreviation, bool Required, string Help)
{
    /// <summary>Parameter mit Kurzform.</summary>
    public static CommandParameter Short(string abbreviation, string name, bool required, string help)
        => new(name, abbreviation.StartsWith('-') ? abbreviation : "-" + abbreviation, required, help);

    /// <summary>Parameter ohne Kurzform.</summary>
    public static CommandParameter Long(string name, bool required, string help) => new(name, null, required, help);
}

/// <summary>
/// Beschreibung eines Verbs für Menü-/Hilfeanzeigen (TUI, Shell-Auswahl, Doku).
/// </summary>
/// <param name="Name">Verbname, z. B. <c>vm-create</c>.</param>
/// <param name="Help">Hilfetext des Verbs.</param>
/// <param name="Parameters">Parameter in Eingabereihenfolge.</param>
public sealed record CommandMenu(string Name, string Help, IReadOnlyList<CommandParameter> Parameters)
{
    /// <summary>Verb ohne Parameter.</summary>
    public static CommandMenu Simple(string name, string help) => new(name, help, Array.Empty<CommandParameter>());

    /// <summary>
    /// Das von allen Verben aus <c>MultipassOptions</c> angebotene
    /// <c>-t/--timeout</c> (siehe <see cref="MultipassOptions"/>).
    /// </summary>
    public static CommandParameter Timeout { get; } =
        CommandParameter.Short("t", "timeout", false,
            "Timeout for this call, e.g. 300s or 1000ms (overrides DefaultTimeout / DefaultTimeoutVmCreation from the config).");

    /// <summary>Parameterliste eines Verbs, das von <c>MultipassOptions</c> erbt (hinten <c>-t/--timeout</c>).</summary>
    public static IReadOnlyList<CommandParameter> WithTimeout(params CommandParameter[] parameters)
    {
        var all = new CommandParameter[parameters.Length + 1];
        parameters.CopyTo(all, 0);
        all[^1] = Timeout;
        return all;
    }
}

/// <summary>
/// Das Menü aller registrierten Verben: <see cref="All"/> in deklarierter Reihenfolge
/// (dieselbe Reihenfolge wie <c>Program.RegisteredOptionTypes</c>), <see cref="ByName"/>
/// zum Nachschlagen. Die Zuordnung entsteht beim Start aus den
/// <c>public static CommandMenu Menu</c>-Membern der Optionsklassen — ohne Reflection.
/// Das Member steht bewusst nicht als <c>static abstract</c> in <see cref="IOptions"/>:
/// statische Member werden nicht vererbt, die abstrakte <c>MultipassOptions</c> müsste
/// das Member also ohnehin nur erneut als abstrakt deklarieren. Die Liste unten erzwingt
/// die Vollständigkeit genauso, und <c>CommandMenuTests</c> prüft sie gegen die Attribute.
/// </summary>
public static class CommandMenus
{
    /// <summary>Alle Verben mit ihren Parametern, in Ausführungs-/Menüreihenfolge.</summary>
    public static IReadOnlyList<CommandMenu> All { get; } =
    [
        MultipassStatusOptions.Menu,
        MultipassRestartOptions.Menu,
        VmListOptions.Menu,
        VmCreateOptions.Menu,
        VmStartOptions.Menu,
        VmStopOptions.Menu,
        VmDeleteOptions.Menu,
        VmExecOptions.Menu,
        VmConnectOptions.Menu,
        VmPutOptions.Menu,
        VmGetOptions.Menu,
        VmCloneOptions.Menu,
        VmSnapshotOptions.Menu,
        VmSnapshotsOptions.Menu,
        VmRestoreOptions.Menu,
        VmInstallOptions.Menu,
        VmResizeOptions.Menu,
        RepoSendOptions.Menu,
        RepoReceiveOptions.Menu,
        InitConfigureOptions.Menu,
        InitListOptions.Menu,
        InitDeleteOptions.Menu
    ];

    /// <summary>Wie <see cref="All"/>, aber nach Verbnamen (Groß-/Kleinschreibung egal).</summary>
    public static IReadOnlyDictionary<string, CommandMenu> ByName { get; } =
        All.ToDictionary(menu => menu.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Menü eines Verbs oder <c>null</c>, wenn es das Verb nicht gibt.</summary>
    public static CommandMenu? Find(string name)
        => ByName.TryGetValue(name, out var menu) ? menu : null;
}
