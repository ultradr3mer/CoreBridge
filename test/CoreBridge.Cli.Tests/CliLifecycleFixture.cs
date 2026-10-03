using System.Text.Json;
using System.Text.RegularExpressions;
using CommandLine;
using CoreBridge.Interaction;
using CoreBridge.Core.Common;
using CoreBridge.Core.Config;
using CoreBridge.Core.Inits;
using CoreBridge.Core.Multipass;
using CoreBridge.Simulation;
using Microsoft.Extensions.DependencyInjection;

namespace CoreBridge.Cli.Tests;

/// <summary>
/// Ergebnis eines CLI-Kommandos; <paramref name="Error"/> ist die fertige Fehlermeldung bei Exit ≠ 0,
/// <paramref name="DialogErrors"/> sind Abweichungen vom vorgegebenen Dialog.
/// </summary>
internal sealed record CliRunResult(
    int ExitCode,
    IReadOnlyList<string> Lines,
    IReadOnlyList<string> DialogErrors,
    string? Error = null);

/// <summary>
/// Ein Schritt des Dialogs: die CLI fragt etwas, das <paramref name="Prompt"/>
/// enthält, und der Test tippt <paramref name="Input"/> — so, wie ein Benutzer es
/// an der Konsole eingäbe (<c>y</c>, <c>no</c>, ein Folgekommando, <c>null</c> = leere Eingabe).
/// </summary>
internal sealed record DialogStep(string Prompt, string? Input);

/// <summary>Liest sich wie der Dialog: <c>Cli.Asks("already exists").TestInputs("y")</c>.</summary>
internal static class Cli
{
    public static DialogPrompt Asks(string prompt) => new(prompt);
}

internal sealed record DialogPrompt(string Prompt)
{
    public DialogStep TestInputs(string? input) => new(Prompt, input);
}

/// <summary>
/// Gemeinsame Namen, Config und Store der Lifecycle-Stufen. Namen und Store sind
/// je Checkout fest (<c>cbx-cli-vm-&lt;checkout&gt;</c>,
/// <c>&lt;Repo&gt;/artifacts/cli-lifecycle/store</c>), damit jede Stufe auch in einem
/// eigenen Testlauf den Zustand findet, den die vorherige Stufe hinterlassen hat. Aufgeräumt wird deshalb nicht beim Dispose, sondern von den
/// letzten Stufen (vm-delete, init-delete). CLI-Kommandos laufen ausschließlich über den öffentlichen
/// <see cref="Program.Run"/>-Dispatcher (kein CLI-Kindprozess). Nur vm-connect
/// geht direkt über <see cref="Core.Vm.VmService.OpenConsole"/> (mit <see cref="Cli"/>),
/// weil `multipass shell` ein TTY braucht.
/// </summary>
public class CliLifecycleFixture : IDisposable
{
    private readonly string storePath;
    private readonly AppConfig config;
    private readonly Lazy<ServiceProvider> services;

    /// <summary>Echtes Multipass (Bootstrap-Standard), jeder Aufruf protokolliert.</summary>
    public CliLifecycleFixture()
        : this(simulator: null, "artifacts/cli-lifecycle/store")
    {
    }

    /// <param name="simulator">
    /// <c>null</c>: echtes Multipass. Sonst die Simulator-Implementierung von
    /// <see cref="IMultipassCli"/> (<see cref="SimulatedMultipassCli"/> im Prozess oder
    /// <see cref="SimulatedBinaryMultipassCli"/> über <c>multipass-sim.exe</c>) mit kurzer
    /// simulierter Dauer der langen Verben — dieselben Stufen laufen in Sekunden.
    /// </param>
    /// <param name="store">Store je Variante (darin auch der Simulatorzustand), relativ zur Repo-Wurzel.</param>
    protected CliLifecycleFixture(Type? simulator, string store)
    {
        var simulated = simulator is not null;
        IsSimulation = simulated;
        storePath = RepoPaths.Resolve(store);
        Directory.CreateDirectory(storePath);
        config = new AppConfig
        {
            // Ohne interaktive Rückfrage (Antwort Nein) darf ein langer cloud-init-Launch
            // nicht nach dem ersten Abschnitt abgebrochen werden.
            DefaultTimeoutVmCreation = "5m",
            // Start/Stop/Exec/Transfer warten DefaultTimeout (Standard 10 s) — für echte VMs zu kurz.
            DefaultTimeout = "30s",
            // Jeder echte Multipass-Aufruf wird protokolliert — außerhalb des
            // Temp-Stores, damit die Aufzeichnung den Lauf überlebt und per
            // ReplayMultipassCli wiedergegeben werden kann. Ohne Simulation bleibt
            // Bootstrap Standard: dann läuft garantiert das echte Multipass.
            // Den Simulator protokolliert die Fabrik ohnehin nicht.
            MultipassLogPath = simulated ? null : "artifacts/mp-log/cli-lifecycle",
            MultipassSimulationDelay = "100ms",
            Bootstrap = simulated
                ? new() { [typeof(IMultipassCli).FullName!] = simulator!.AssemblyQualifiedName! }
                : new AppConfig().Bootstrap,
            // CLI-Protokoll wie bei `cb` in einem Checkout: Schalter und Pfad aus
            // appsettings.Development.json, damit Testläufe in derselben Datei landen.
            CommandLog = DevelopmentCommandLog(),
            Inits = new InitsConfig
            {
                StoreRoot = storePath,
                Components = new Dictionary<string, ComponentConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["claude"] = new()
                    {
                        Script = "scripts/components/install-claude.sh",
                        InstallPath = "/usr/local/sbin/corebridge-install-claude",
                        Packages = ["bash", "ca-certificates", "curl", "sudo"],
                        PackageUpdate = true,
                        InstallUser = "ubuntu"
                    },
                    ["dotnet"] = new()
                    {
                        Snap = "dotnet-sdk-100",
                        Classic = true,
                        Script = "scripts/components/install-dotnet.sh",
                        InstallPath = "/usr/local/sbin/corebridge-install-dotnet",
                        InstallUser = "ubuntu"
                    }
                }
            }
        };

        var suffix = CheckoutSuffix();
        InitName = $"cbx-cli-init-{suffix}";
        VmName = $"cbx-cli-vm-{suffix}";
        services = new Lazy<ServiceProvider>(() => CoreBridgeContainer.Create(config));
    }

    /// <summary>Läuft gegen <see cref="SimulatedMultipassCli"/> statt gegen echtes Multipass.</summary>
    public bool IsSimulation { get; }

    /// <summary>
    /// Der Multipass-Client, den auch <see cref="Program.Run"/> mit dieser Config bekäme —
    /// für das, was nicht über den Dispatcher geht (vm-connect).
    /// </summary>
    public IMultipassCli Cli => MultipassCliFactory.Create(services.Value);

    public void Dispose()
    {
        if (services.IsValueCreated)
        {
            services.Value.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    public string InitName { get; }

    public string VmName { get; }

    internal string ProfileConfigPath => Path.Combine(storePath, "profiles", InitName, InitName + ".profile.json");

    internal string CloudInitPath => Path.Combine(storePath, "profiles", InitName, "cloud-init.yaml");

    /// <summary>
    /// Konsumiert den Ausgabestrom vollständig, schreibt jede Meldung sofort ins
    /// Log und bildet <see cref="ProgramExecutionException"/> auf ihren Exit-Code
    /// ab (0 bei Erfolg); die Fehlermeldung landet ebenfalls im Log. Andere
    /// Exceptions werden nicht abgefangen.
    /// </summary>
    /// <param name="dialog">
    /// Alle Rückfragen des Aufrufs, in ihrer Reihenfolge, mit der Eingabe des
    /// Tests. Es gibt keine Standardantwort: eine Rückfrage, die nicht als
    /// nächster Schritt vorgesehen ist, eine ungültige Eingabe oder ein
    /// vorgesehener Schritt, nach dem die CLI nie fragt, landet in
    /// <see cref="CliRunResult.DialogErrors"/>; die abweichende Rückfrage wird
    /// verworfen, damit das Kommando sofort endet statt zu hängen.
    /// </param>
    internal async Task<CliRunResult> RunAsync(IOptions options, Action<string> log, params DialogStep[] dialog)
    {
        var lines = new List<string>();
        var dialogErrors = new List<string>();
        var pending = new Queue<DialogStep>(dialog);
        using var commandLog = CommandLog.Open(config.CommandLog.ResolveDirectory(), CommandLineOf(options));
        var exitCode = 0;
        try
        {
            // Der Test ist der Konsument des Ereignisstroms — dieselbe Rolle wie
            // die ConsoleAdapter in der echten CLI: Ausgabe sammeln und
            // Rückfragen beantworten.
            await foreach (var cliEvent in commandLog.Tee(Program.Run(config, options)))
            {
                switch (cliEvent)
                {
                    // Reihenfolge zählt: InteractionEvent erbt von StdEvent.
                    case IOpenInteraction interaction:
                        var error = Answer(cliEvent, pending, log);
                        if (error is not null)
                        {
                            dialogErrors.Add(error);
                            log($"{cliEvent.Printable}(Test: {error} — Rückfrage verworfen)");
                            interaction.Cancel();
                        }

                        break;
                    case ProgressEvent:
                        break;
                    case DiagnosticEvent:
                        // Innenleben: nur fürs Protokoll (commandLog.Tee), nicht für die Prüfungen.
                        break;
                    case StdEvent std:
                        log(std.Printable);
                        lines.Add(std.Printable);
                        break;
                    case ErrEvent err:
                        log(err.Printable);
                        break;
                    default:
                        log(cliEvent.Printable);
                        lines.Add(cliEvent.Printable);
                        break;
                }
            }

            dialogErrors.AddRange(NotAsked(pending));
            return new CliRunResult(0, lines, dialogErrors);
        }
        catch (ProgramExecutionException exception)
        {
            commandLog.Write("fehler", exception.Message);
            commandLog.WriteCause(exception);
            exitCode = exception.ExitCode;
            log($"Fehler (Exit {exception.ExitCode}): {exception.Message}");
            dialogErrors.AddRange(NotAsked(pending));
            return new CliRunResult(exception.ExitCode, lines, dialogErrors, exception.Message);
        }
        finally
        {
            commandLog.Write("ende", $"Exit {exitCode} (Test)");
        }
    }

    /// <summary>
    /// Der CommandLog-Abschnitt der mitgelieferten Development-Konfiguration — dieselbe Quelle
    /// wie bei <c>cb</c>, damit Schalter und Pfad nur an einer Stelle stehen.
    /// </summary>
    private static CommandLogConfig DevelopmentCommandLog()
    {
        var file = RepoPaths.Resolve("src/CoreBridge.Cli/appsettings.Development.json");
        using var document = JsonDocument.Parse(File.ReadAllText(file));
        return document.RootElement.TryGetProperty("CommandLog", out var section)
            ? section.Deserialize<CommandLogConfig>() ?? new CommandLogConfig()
            : new CommandLogConfig();
    }

    /// <summary>
    /// Die Optionen als Kommandozeile fürs Protokoll (Verb + gesetzte Optionen), wie sie
    /// <c>cb</c> bekommen hätte. Nicht über <c>Parser.FormatCommandLineArgs</c>: der braucht
    /// öffentliche Getter, <c>--timeout</c> hat aber einen privaten.
    /// </summary>
    private static IEnumerable<string> CommandLineOf(IOptions options)
    {
        var type = options.GetType();
        yield return type.GetCustomAttributes(typeof(VerbAttribute), false).OfType<VerbAttribute>().FirstOrDefault()?.Name
            ?? type.Name;
        foreach (var property in type.GetProperties())
        {
            if (property.GetCustomAttributes(typeof(OptionAttribute), true).FirstOrDefault() is not OptionAttribute option)
            {
                continue;
            }

            // Privater Getter (--timeout, geerbt): nur über den deklarierenden Typ lesbar.
            var getter = property.DeclaringType?.GetProperty(property.Name)?.GetGetMethod(nonPublic: true);
            switch (getter?.Invoke(options, null))
            {
                case null or false:
                case string text when text.Length == 0:
                    break;
                case true:
                    yield return "--" + option.LongName;
                    break;
                case var value:
                    yield return "--" + option.LongName;
                    yield return value.ToString() ?? string.Empty;
                    break;
            }
        }
    }

    /// <summary>
    /// Beantwortet die Rückfrage mit dem nächsten Dialogschritt, wenn sie zu ihm
    /// passt; liefert sonst die Abweichung (die Rückfrage bleibt dann offen).
    /// </summary>
    private static string? Answer(CliEvent prompt, Queue<DialogStep> pending, Action<string> log)
    {
        if (!pending.TryPeek(out var step))
        {
            return "unerwartete Rückfrage";
        }

        if (!prompt.Printable.Contains(step.Prompt, StringComparison.Ordinal))
        {
            return $"erwartet „{step.Prompt}“";
        }

        pending.Dequeue();
        var accepted = prompt switch
        {
            IChoiceInteraction choice => step.Input is not null && choice.TryParseChoice(step.Input),
            TextEvent text => text.SetResult(step.Input),
            _ => false
        };
        if (!accepted)
        {
            return $"Eingabe „{step.Input}“ passt nicht ({prompt.GetType().Name})";
        }

        log($"{prompt.Printable}{step.Input} (Test)");
        return null;
    }

    private static IEnumerable<string> NotAsked(Queue<DialogStep> pending) =>
        pending.Select(step => $"die CLI hat nie „{step.Prompt}“ gefragt");

    /// <summary>
    /// Scheitert bei einer Abweichung vom Dialog (sie ist meist die Ursache des
    /// Folgefehlers) oder bei Exit ≠ 0 mit der Fehlermeldung des Kommandos statt
    /// „Expected 0, Actual 1“.
    /// </summary>
    internal static void AssertSucceeded(CliRunResult result)
    {
        if (result.DialogErrors.Count > 0)
        {
            Assert.Fail(
                "Dialog weicht ab: " + string.Join(" | ", result.DialogErrors)
                + (result.ExitCode != 0 ? $" — danach Exit {result.ExitCode}: {result.Error}" : string.Empty));
        }

        if (result.ExitCode != 0)
        {
            Assert.Fail($"Exit {result.ExitCode}: {result.Error}");
        }
    }

    /// <summary>
    /// Prüft Exit 0 und die Abschlusszeile zustandsändernder Kommandos:
    /// <c>&lt;label&gt;: OK (&lt;Dauer&gt;) — State: &lt;state&gt;</c>.
    /// </summary>
    internal static void AssertCompleted(CliRunResult result, string label, string state)
    {
        AssertSucceeded(result);
        Assert.NotEmpty(result.Lines);
        Assert.Matches(
            $@"^{Regex.Escape(label)}: OK \([^)]*\) — State: {Regex.Escape(state)}$",
            result.Lines[^1]);
    }

    internal static string? FindVmState(CliRunResult list, string vmName)
    {
        return list.Lines
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(columns => columns.Length >= 2 && columns[0] == vmName)
            .Select(columns => columns[1])
            .FirstOrDefault();
    }

    /// <summary>
    /// Fester Namensteil je Checkout (Ordnername der Worktree), damit zwei
    /// Worktrees nicht auf dieselbe VM losgehen, wiederholte Läufe derselben
    /// Worktree aber dieselbe VM wiederfinden.
    /// </summary>
    private static string CheckoutSuffix()
    {
        var name = Regex.Replace(
            Path.GetFileName(RepoPaths.Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).ToLowerInvariant(),
            "[^a-z0-9]+",
            "-").Trim('-');
        name = name.Length > 30 ? name[..30].TrimEnd('-') : name;
        return name.Length == 0 ? "default" : name;
    }
}

/// <summary>Dieselben Stufen gegen <see cref="SimulatedMultipassCli"/> im Prozess (siehe <see cref="CliLifecycleSimulationTests"/>).</summary>
public sealed class SimulatedCliLifecycleFixture : CliLifecycleFixture
{
    public SimulatedCliLifecycleFixture()
        : base(typeof(SimulatedMultipassCli), "artifacts/cli-lifecycle-sim/store")
    {
    }
}

/// <summary>
/// Dieselben Stufen über das echte <see cref="MultipassCli"/> gegen <c>multipass-sim.exe</c>
/// (siehe <see cref="CliLifecycleSimulatedBinaryTests"/>).
/// </summary>
public sealed class SimulatedBinaryCliLifecycleFixture : CliLifecycleFixture
{
    public SimulatedBinaryCliLifecycleFixture()
        : base(typeof(SimulatedBinaryMultipassCli), "artifacts/cli-lifecycle-simexe/store")
    {
    }
}
