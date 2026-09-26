using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using CommandLine;
using CoreBridge.Core.Common;
using CoreBridge.Core.Config;
using CoreBridge.Core.Inits;
using CoreBridge.Core.Multipass;
using CoreBridge.Core.Vm;
using Microsoft.Extensions.Configuration;

namespace CoreBridge.Cli;

public static class Program
{
    /// <summary>
    /// Einzige vom Parser verwendete Optionsliste. Tests können über dieses
    /// öffentliche Register sicherstellen, dass kein Verb die Dispatch-Grenze
    /// umgeht.
    /// </summary>
    public static IReadOnlyList<Type> RegisteredOptionTypes { get; } =
    [
        typeof(MultipassStatusOptions),
        typeof(MultipassRestartOptions),
        typeof(VmListOptions),
        typeof(VmCreateOptions),
        typeof(VmStartOptions),
        typeof(VmStopOptions),
        typeof(VmDeleteOptions),
        typeof(VmExecOptions),
        typeof(VmConnectOptions),
        typeof(VmPutOptions),
        typeof(VmGetOptions),
        typeof(InitConfigureOptions),
        typeof(InitListOptions),
        typeof(InitDeleteOptions)
    ];

    /// <summary>
    /// Leitsatz 6: CLI und TUI lesen dieselbe Config-Quelle. Reihenfolge:
    /// appsettings.json → appsettings.{Environment}.json (Env DOTNET_ENVIRONMENT,
    /// Default Development) → appsettings.local.json (gitignored) → Env-Variablen
    /// (Doppelter Unterstrich, z. B. Images__StoreRoot). Keine Passwörter in der
    /// Config — Secrets laufen über die CORE_BRIDGE_*_PASSWORD-Env-Kette.
    /// </summary>
    private static AppConfig LoadConfig()
    {
        var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Development";
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile($"appsettings.{environment}.json", optional: true)
            .AddJsonFile("appsettings.local.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        return configuration.Get<AppConfig>()
            ?? throw new InvalidOperationException("Konfiguration konnte nicht gebunden werden (appsettings.json).");
    }

    /// <summary>
    /// Parst nur die CLI und führt danach genau einen Dispatcher aus. Meldungen
    /// werden geschrieben, sobald das Kommando sie liefert; erwartete Fehler kommen
    /// ausschließlich als fertige <see cref="ProgramExecutionException"/> an und
    /// werden hier nur noch ausgegeben.
    /// </summary>
    public static async Task<int> Main(string[] args)
    {
        var config = LoadConfig();
        if (!TryParseOptions(args, out var options, out var helpRequested))
        {
            return helpRequested ? 0 : 1;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            // Konsument des Ereignisstroms: er schreibt alles auf die Konsole
            // und beantwortet die Rückfragen des Kommandos.
            return await new ConsoleAdapter().AcceptEventStream(Run(config, options, cancellation.Token));
        }
        catch (ProgramExecutionException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return exception.ExitCode;
        }
        finally
        {
            if (options is MultipassRestartOptions { PauseOnExit: true })
            {
                Console.Write("Any key to continue ...");
                Console.ReadKey(intercept: true);
            }
        }
    }

    /// <summary>
    /// Parst die Argumente gegen <see cref="RegisteredOptionTypes"/>; Hilfe- und
    /// Fehlermeldungen schreibt der Parser selbst.
    /// </summary>
    /// <param name="helpRequested"><c>true</c>, wenn nur Hilfe/Version angefragt wurde (kein Fehler).</param>
    private static bool TryParseOptions(string[] args, out IOptions options, out bool helpRequested)
    {
        var parsed = Parser.Default.ParseArguments(args, RegisteredOptionTypes.ToArray());
        if (parsed is NotParsed<object> notParsed)
        {
            options = null!;
            helpRequested = notParsed.Errors.Any(error => error.Tag is ErrorType.HelpRequestedError
                or ErrorType.VersionRequestedError
                or ErrorType.HelpVerbRequestedError);
            return false;
        }

        options = (IOptions)((Parsed<object>)parsed).Value;
        helpRequested = false;
        return true;
    }

    /// <summary>
    /// Einziger Kommando-Dispatcher nach dem Parsen. Services werden aus dem
    /// übergebenen Config erzeugt; es gibt keine statischen Vm-/Config-Zustände.
    /// Jede Meldung wird geliefert, sobald sie entsteht — nicht erst nach Ende
    /// des Kommandos. Jeder erwartete Fehler kommt ausschließlich als
    /// <see cref="ProgramExecutionException"/> mit fertiger Meldung und Exit-Code
    /// an (siehe <see cref="ToExecutionException"/>); alles andere ist ein Bug und
    /// wird unverändert weitergeworfen.
    /// </summary>
    public static IAsyncEnumerable<CliEvent> Run(
        AppConfig config,
        IOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(options);

        // Der Kanal ist die Grenze zwischen Erzeuger und Konsument: das Kommando
        // schiebt Ereignisse hinein, der Konsument zieht sie heraus und
        // beantwortet Rückfragen. Er ist unbeschränkt, damit ein wartendes
        // Kommando nie an einer vollen Queue hängen bleibt.
        var channel = Channel.CreateUnbounded<CliEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        var factory = new EventFactory(channel.Writer);
        _ = ProduceAsync(channel, ApplyTimeoutOverride(config, options), options, factory, cancellationToken);

        return channel.Reader.ReadAllAsync(cancellationToken);
    }

    /// <summary>
    /// Führt das Kommando aus und übersetzt dabei erwartete Fehler. Läuft als
    /// eigener Task, damit die Rückfragen der <c>Func&lt;bool&gt;</c>-Callbacks
    /// aus CoreBridge.Core den Kanal bedienen können, während hier blockiert
    /// wird.
    /// </summary>
    private static async Task ProduceAsync(
        Channel<CliEvent> channel,
        AppConfig config,
        IOptions options,
        EventFactory factory,
        CancellationToken cancellationToken)
    {
        try
        {
            await Execute(config, options, factory, cancellationToken);
            factory.AssertNoOpenInteractions();
        }
        catch (Exception exception) when (ToExecutionException(exception) is { } failure)
        {
            channel.Writer.TryComplete(failure);
            return;
        }
        catch (Exception exception)
        {
            // Unerwartet = Bug; die Meldung wandert trotzdem über den Kanal,
            // damit der Konsument sie sieht statt sie zu verschlucken.
            channel.Writer.TryComplete(exception);
            return;
        }
        finally
        {
            // Offene Rückfragen verwerfen, damit niemand auf eine Antwort wartet,
            // die nie kommen wird.
            factory.CancelOpen();
            channel.Writer.TryComplete();
        }
    }

    /// <summary>
    /// Einzige Stelle, die <c>--timeout</c> liest: ersetzt für diesen Aufruf die
    /// Config-Timeouts, danach sehen alle Kommandos nur noch die Config.
    /// </summary>
    private static AppConfig ApplyTimeoutOverride(AppConfig config, IOptions options)
    {
        return options is ITimeoutOverride ovr
            ? config with
            {
                DefaultTimeoutVmCreation = ovr.OverrideTimeout ?? config.DefaultTimeoutVmCreation,
                DefaultTimeout = ovr.OverrideTimeout ?? config.DefaultTimeout
            }
            : config;
    }

    /// <summary>
    /// Einzige Stelle, an der erwartete Fehler zu Meldung und Exit-Code werden:
    /// Multipass-Timeout (1, mit Hinweis auf multipass-restart), Multipass (1),
    /// unlesbare Multipass-Antwort (1), ungültige Zeitangabe in der Config (1), Datei-/Rechtefehler (1),
    /// Abbruch per Strg+C (130). <c>null</c> heißt: kein erwarteter Fehler.
    /// </summary>
    private static ProgramExecutionException? ToExecutionException(Exception exception)
    {
        return exception switch
        {
            ProgramExecutionException => null,
            MultipassTimeoutException timeout => new(
                1,
                $"`multipass {timeout.Command}` hat nach {timeout.TimeoutMs / 1000} s nicht geantwortet (Timeout). "
                    + "Hängt der Multipass-Dienst, hilft `cb multipass-restart` (als Administrator).",
                exception),
            MultipassException => new(1, exception.Message, exception),
            System.Text.Json.JsonException => new(1, $"Unerwartete Multipass-Antwort (kein JSON): {exception.Message}", exception),
            FormatException => new(1, $"Configuration error: {exception.Message}", exception),
            IOException or UnauthorizedAccessException => new(1, $"CLI-I/O-Fehler: {exception.Message}", exception),
            TaskCanceledException { InnerException: TimeoutException } => new(
                1,
                "Download timed out — raise it with `--timeout` (e.g. 300s) or `DefaultTimeout` in the config.",
                exception),
            OperationCanceledException => new(130, "Abgebrochen.", exception),
            _ => null
        };
    }

    private static async Task Execute(
        AppConfig config,
        IOptions options,
        EventFactory factory,
        CancellationToken cancellationToken)
    {
        InitStore.Initialize(config.Images.StoreRoot);
        var context = new CommandContext(config, new VmService(CreateMultipassCli(config.Images.StoreRoot)), factory, cancellationToken);
        await Dispatch(context, options);
    }

    /// <summary>
    /// Echter Multipass-Client — oder im Mock-Betrieb (<c>CORE_BRIDGE_MULTIPASS_MOCK=1</c>)
    /// ein <see cref="MockMultipassCli"/>, der den VM-Zustand im Speicher hält und
    /// jeden Aufruf als JSON Line protokolliert. So lässt sich die ganze Anwendung
    /// ohne installiertes Multipass und ohne Wartezeiten durchspielen.
    /// </summary>
    private static IMultipassCli CreateMultipassCli(string? storeRoot)
    {
        if (!MockMultipassCli.IsEnabled())
        {
            return new MultipassCli();
        }

        var mock = new MockMultipassCli(MockMultipassCli.ResolveLogPath(storeRoot));
        Console.Error.WriteLine(
            $"[MockMultipass] aktiv ({MockMultipassCli.EnableEnvironmentVariable}) — "
            + $"kein echtes Multipass. JSON-Log: {mock.LogPath}");
        return mock;
    }

    private static Task Dispatch(CommandContext context, IOptions options)
    {
        return options switch
        {
            MultipassStatusOptions => RunMultipassStatus(context),
            MultipassRestartOptions => RunMultipassRestart(context),
            VmListOptions => RunVmList(context),
            VmCreateOptions o => RunVmCreate(context, o),
            VmStartOptions o => RunVmStart(context, o),
            VmStopOptions o => RunVmStop(context, o),
            VmDeleteOptions o => RunVmDelete(context, o),
            VmExecOptions o => RunVmExec(context, o),
            VmConnectOptions o => RunVmConnect(context, o),
            VmPutOptions o => RunVmPut(context, o),
            VmGetOptions o => RunVmGet(context, o),
            InitConfigureOptions o => RunInitConfigure(context, o),
            InitListOptions => RunInitList(context),
            InitDeleteOptions o => RunInitDelete(context, o),
            _ => throw new MultipassException($"Unbekannter CLI-Options-Typ: {options.GetType().Name}")
        };
    }

    /// <summary>
    /// Zerlegt eine eingegebene Kommandozeile an Leerzeichen; Abschnitte in
    /// doppelten Anführungszeichen bleiben zusammen (ohne die Anführungszeichen).
    /// </summary>
    private static string[] SplitCommandLine(string commandLine)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var hasToken = false;
        foreach (var character in commandLine)
        {
            if (character == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;
            }
            else if (char.IsWhiteSpace(character) && !inQuotes)
            {
                if (hasToken)
                {
                    args.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }
            }
            else
            {
                current.Append(character);
                hasToken = true;
            }
        }

        if (hasToken)
        {
            args.Add(current.ToString());
        }

        return args.ToArray();
    }

    private static IEnumerable<string> Lines(string text)
    {
        return string.IsNullOrEmpty(text)
            ? []
            : MultipassOutput.CleanLines(text);
    }

    private static ProgramExecutionException Failure(MultipassRunResult result)
    {
        var stdErr = MultipassStdErr.Clean(result.StdErr);
        var message = string.IsNullOrEmpty(stdErr)
            ? $"multipass beendet mit Exit {result.ExitCode}."
            : stdErr;
        return new ProgramExecutionException(result.ExitCode, message);
    }

    /// <summary>
    /// Liefert stdout des Multipass-Aufrufs zeilenweise. Bei Exit ≠ 0 folgt die
    /// Exception mit stderr; sonst wird stderr als reguläre Meldung geliefert.
    /// </summary>
    private static async Task Report(EventFactory events, MultipassRunResult result)
    {
        foreach (var line in Lines(result.StdOut))
        {
            await events.Emit(events.Std(line));
        }

        if (result.ExitCode != 0)
        {
            throw Failure(result);
        }

        foreach (var line in Lines(result.StdErr))
        {
            await events.Emit(events.Std(line));
        }
    }

    private static async Task RunMultipassStatus(CommandContext context)
    {
        var events = context.Events;

        // Im Mock-Betrieb gibt es weder Binary noch Daemon: einen erfundenen
        // Pfad melden, damit der Erfolgspfad (Parsing der Version) durchläuft.
        var mocking = MockMultipassCli.IsEnabled();
        var report = await Task.Run(
            () => mocking
                ? Doctor.Check(
                    () => "mock://multipass",
                    CreateMultipassCli(context.Config.Images.StoreRoot))
                : Doctor.Check(),
            context.CancellationToken);
        if (!report.Found)
        {
            var message = new StringBuilder("Multipass nicht gefunden.\n");
            foreach (var hint in report.Hints)
            {
                message.Append($"\n  - {hint}");
            }

            throw new ProgramExecutionException(1, message.ToString());
        }

        await events.Emit(events.Std($"Binary:  {report.BinaryPath}"));
        await events.Emit(events.Std($"CLI:     {report.ClientVersion ?? "unbekannt"}"));
        await events.Emit(events.Std($"Daemon:  {report.DaemonVersion ?? "keine Antwort"}"));
        if (report.Error is not null)
        {
            await events.Emit(events.Std(string.Empty));
            await events.Emit(events.Std($"Hinweis: {report.Error}"));
            foreach (var line in Lines(report.RawVersionOutput ?? string.Empty))
            {
                await events.Emit(events.Std(line));
            }
        }
    }

    private static async Task RunMultipassRestart(CommandContext context)
    {
        var events = context.Events;
        var cancellationToken = context.CancellationToken;
        var timeout = context.Config.DefaultTimeout.ReadTime();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var service = new MultipassService(commandTimeout: timeout);
        if (!service.IsElevated())
        {
            await events.Emit(events.Std($"Warnung: Der Neustart des Dienstes {MultipassService.ServiceName} benötigt Administratorrechte."));
            if (!await context.ConfirmAsync("Mit Administratorrechten in einem neuen Fenster fortfahren?"))
            {
                throw new ProgramExecutionException(1, "Abgebrochen — ohne Administratorrechte kein Neustart.");
            }

            await events.Emit(events.Std("Starte erhöht (UAC-Abfrage) — Ausgabe und Rückfragen folgen im Administratorfenster ..."));
            var exitCode = await RunElevatedAsync(context.Config.DefaultTimeout, cancellationToken);
            if (exitCode != 0)
            {
                throw new ProgramExecutionException(exitCode, $"multipass-restart im Administratorfenster fehlgeschlagen (Exit {exitCode}).");
            }

            await events.Emit(events.Std($"multipass-restart: OK ({stopwatch.Elapsed.TotalSeconds:F1} s, erhöht) — State: {service.QueryState()}"));
            return;
        }

        // MultipassService erwartet Func<string,bool>; die blockiert auf einem
        // Threadpool-Thread, während der Konsument den Kanal bedient.
        bool ConfirmInCore(string question)
            => events.Ask(events.Choice<YesNoOptions>(question)) == YesNoOptions.Yes;

        await foreach (var line in service.Restart(timeout, ConfirmInCore, cancellationToken))
        {
            await events.Emit(line == Progress.ProgressTick ? events.Tick() : events.Std(line));
        }

        await events.Emit(events.Std($"multipass-restart: OK ({stopwatch.Elapsed.TotalSeconds:F1} s) — State: {service.QueryState()}"));
    }

    /// <summary>
    /// Startet dieselbe CLI per UAC (<c>runas</c>) mit <c>multipass-restart
    /// --pause-on-exit --timeout &lt;timeout&gt;</c> (der wirksame <c>DefaultTimeout</c>)
    /// und wartet auf ihr Ende. Läuft die CLI als DLL unter <c>dotnet</c>,
    /// wird die DLL als erstes Argument mitgegeben.
    /// </summary>
    private static async Task<int> RunElevatedAsync(string timeout, CancellationToken cancellationToken)
    {
        var processPath = Environment.ProcessPath
            ?? throw new ProgramExecutionException(1, "Pfad der laufenden CLI unbekannt — erhöhter Neustart nicht möglich.");
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Environment.CurrentDirectory
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add(typeof(Program).Assembly.Location);
        }

        foreach (var argument in new[] { "multipass-restart", "--pause-on-exit" })
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add("--timeout");
        startInfo.ArgumentList.Add(timeout);

        System.Diagnostics.Process? process;
        try
        {
            process = System.Diagnostics.Process.Start(startInfo);
        }
        catch (System.ComponentModel.Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            throw new ProgramExecutionException(1, "Abgebrochen — Administratorrechte in der UAC-Abfrage abgelehnt.");
        }

        using var elevated = process
            ?? throw new ProgramExecutionException(1, "Erhöhter Prozess konnte nicht gestartet werden.");
        await elevated.WaitForExitAsync(cancellationToken);
        return elevated.ExitCode;
    }

    private static async Task RunVmList(CommandContext context)
    {
        var events = context.Events;
        var list = await Task.Run(() => context.Vms.List(context.DefaultTimeoutMs), context.CancellationToken);
        if (list.Count == 0)
        {
            await events.Emit(events.Std("No VMs — create one with `cb vm-create -n <name>`."));
            return;
        }

        await events.Emit(events.Std($"{"Name",-30} {"State",-10} IPv4"));
        foreach (var vm in list)
        {
            await events.Emit(events.Std($"{vm.Name,-30} {vm.State ?? "?",-10} {string.Join(", ", vm.Ipv4)}"));
        }
    }

    /// <summary>Multipass-Namensregel: Buchstaben, Ziffern, Bindestriche; Beginn mit Buchstabe, Ende ohne Bindestrich.</summary>
    private static readonly System.Text.RegularExpressions.Regex VmNamePattern =
        new("^[A-Za-z](?:[A-Za-z0-9-]*[A-Za-z0-9])?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Legt die VM an (eine vorhandene gleichnamige VM nur nach Rückfrage ersetzt)
    /// und wartet in Abschnitten von <c>DefaultTimeoutVmCreation</c>;
    /// nach jedem Abschnitt wird gefragt, ob weiter gewartet werden soll. Bei Nein
    /// läuft der Launch im Multipass-Daemon weiter, und der Benutzer kann ein
    /// Folgekommando eingeben, das wie ein normaler CLI-Aufruf ausgeführt wird.
    /// </summary>
    private static async Task RunVmCreate(CommandContext context, VmCreateOptions options)
    {
        var events = context.Events;
        var (config, vms, cancellationToken) = (context.Config, context.Vms, context.CancellationToken);
        var waitSlice = config.DefaultTimeoutVmCreation.ReadTime();
        var requestedImage = string.IsNullOrWhiteSpace(options.Image)
            ? config.Images.DefaultRuntimeImage
            : options.Image.Trim();
        var profile = InitProfileResolver.Resolve(config.Images, requestedImage, options.CloudInit);
        var launchImage = profile?.RuntimeImage ?? requestedImage;
        var cloudInit = options.CloudInit ?? profile?.CloudInitPath;
        if (!VmNamePattern.IsMatch(options.Name))
        {
            throw new ProgramExecutionException(
                1,
                $"Invalid VM name `{options.Name}` — use letters, digits and hyphens, starting with a letter and not ending with a hyphen (e.g. dev-01).");
        }

        if (cloudInit is not null && !File.Exists(cloudInit))
        {
            throw new ProgramExecutionException(1, $"cloud-init file not found: {cloudInit}");
        }

        await ReplaceExistingVm(context, options.Name);

        await events.Emit(events.Std($"Lege VM `{options.Name}` an (Image: {requestedImage}"
            + (profile is null ? string.Empty : $" → Runtime: {launchImage}")
            + $"{(options.Disk is null ? string.Empty : $", Disk: {options.Disk}")}"
            + $"{(cloudInit is null ? string.Empty : ", cloud-init")}) ..."));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        MultipassRunResult? result;
        try
        {
            result = await Task.Run(
                () => vms.Create(
                    options.Name,
                    launchImage,
                    options.Disk,
                    cloudInit,
                    waitSlice,
                    () => context.Events.Ask(context.Events.Choice<YesNoOptions>(
                        $"VM `{options.Name}` is not ready after {stopwatch.Elapsed.TotalSeconds:F0} s — keep waiting?")) == YesNoOptions.Yes),
                cancellationToken);
        }
        catch (MultipassTimeoutException)
        {
            result = null;
        }

        if (result is null)
        {
            await events.Emit(events.Std("Stopped waiting — Multipass keeps creating the VM in the background (check with `cb vm-list`)."));
            var followUp = await context.PromptAsync("Follow-up command (empty = quit): ");
            if (string.IsNullOrWhiteSpace(followUp))
            {
                return;
            }

            if (!TryParseOptions(SplitCommandLine(followUp), out var followUpOptions, out _))
            {
                throw new ProgramExecutionException(1, $"Invalid follow-up command: {followUp}");
            }

            var followUpContext = context with { Config = ApplyTimeoutOverride(context.Config, followUpOptions) };
            await Dispatch(followUpContext, followUpOptions);
            return;
        }

        if (result.ExitCode != 0)
        {
            foreach (var line in Lines(result.StdOut))
            {
                await events.Emit(events.Std(line));
            }

            throw Failure(result);
        }

        if (profile is not null)
        {
            await events.Emit(events.Std($"Profil: {profile.Name} (Runtime: {profile.RuntimeImage})"));
            await events.Emit(events.Std("Gast-Provisionierung: cloud-init"));
        }

        await events.Emit(events.Std(await Completed(vms, options.Name, $"create {options.Name}", result, cancellationToken)));
    }

    private static async Task RunVmExec(CommandContext context, VmExecOptions options)
    {
        await EnsureRunning(context, options.Name);

        var result = await Task.Run(
            () => context.Vms.Exec(options.Name, options.Command, context.DefaultTimeoutMs),
            context.CancellationToken);
        await Report(context.Events, result);
    }

    private static async Task RunVmConnect(CommandContext context, VmConnectOptions options)
    {
        await EnsureRunning(context, options.Name);

        var exitCode = await Task.Run(() => context.Vms.Connect(options.Name), context.CancellationToken);
        if (exitCode != 0)
        {
            throw new ProgramExecutionException(exitCode, $"vm-connect fehlgeschlagen (Exit {exitCode}).");
        }
    }

    private static async Task RunVmStart(CommandContext context, VmStartOptions options)
    {
        var name = options.Name;
        var vm = await RequireVm(context, name);
        if (vm.IsDeleted)
        {
            throw InTrash(name);
        }

        if (vm.IsRunning)
        {
            await context.Events.Emit(context.Events.Std($"start {name}: OK (lief bereits) — State: Running"));
            return;
        }

        await RunVmStateCommand(
            context,
            name,
            $"start {name}",
            () => context.Vms.Start(name, context.DefaultTimeoutMs, KeepWaiting(context, $"start {name}")));
    }

    private static async Task RunVmStop(CommandContext context, VmStopOptions options)
    {
        var name = options.Name;
        var vm = await RequireVm(context, name);
        if (vm.IsDeleted)
        {
            throw InTrash(name);
        }

        if (vm.IsStopped)
        {
            await context.Events.Emit(context.Events.Std($"stop {name}: OK (was already stopped) — State: {vm.State}"));
            return;
        }

        if (vm.IsTransitional
            && !await context.ConfirmAsync($"VM `{name}` is {vm.State} — Multipass can only power it off (--force). Power it off?"))
        {
            throw new ProgramExecutionException(1, $"Cancelled — VM `{name}` was not stopped (State: {vm.State}).");
        }

        await StopVm(context, vm);
    }

    /// <summary>
    /// Stoppt die VM; in Übergangszuständen (<see cref="VmInfo.IsTransitional"/>)
    /// mit <c>--force</c>, weil Multipass den normalen Stop dort ablehnt. Die
    /// Rückfrage dazu stellt der Aufrufer.
    /// </summary>
    private static Task StopVm(CommandContext context, VmInfo vm)
    {
        var force = vm.IsTransitional;
        var label = force ? $"stop --force {vm.Name}" : $"stop {vm.Name}";
        return RunVmStateCommand(
            context,
            vm.Name,
            label,
            () => context.Vms.Stop(vm.Name, context.DefaultTimeoutMs, KeepWaiting(context, label), force));
    }

    /// <summary>
    /// Löscht die VM (in den Papierkorb oder mit <c>--purge</c> endgültig). Vor dem
    /// Löschen einer laufenden VM wird gefragt; eine VM im Papierkorb ohne
    /// <c>--purge</c> ist bereits gelöscht.
    /// </summary>
    private static async Task RunVmDelete(CommandContext context, VmDeleteOptions options)
    {
        var name = options.Name;
        var vm = await RequireVm(context, name);
        if (vm.IsDeleted && !options.Purge)
        {
            await context.Events.Emit(context.Events.Std($"delete {name}: OK (already in the trash; --purge removes it for good) — State: Deleted"));
            return;
        }

        if (!vm.IsDeleted
            && !vm.IsStopped
            && !await context.ConfirmAsync($"VM `{name}` is {vm.State} — stop and delete it?"))
        {
            throw new ProgramExecutionException(1, $"Cancelled — VM `{name}` was not deleted (State: {vm.State}).");
        }

        if (!vm.IsDeleted && !vm.IsStopped)
        {
            await StopVm(context, vm);
        }

        var label = $"delete {name}{(options.Purge ? " --purge" : string.Empty)}";
        await RunVmStateCommand(
            context,
            name,
            label,
            () => context.Vms.Delete(name, options.Purge, context.DefaultTimeoutMs, KeepWaiting(context, label)));
    }

    /// <summary>
    /// Gibt es die VM schon (auch im Papierkorb), wird gefragt, ob sie gestoppt,
    /// endgültig gelöscht und neu angelegt werden darf; bei Nein bricht vm-create ab.
    /// </summary>
    private static async Task ReplaceExistingVm(CommandContext context, string name)
    {
        var existing = await Task.Run(() => context.Vms.Find(name), context.CancellationToken);
        if (existing is null)
        {
            return;
        }

        var question = existing.IsDeleted
            ? $"VM `{name}` is still in the trash (Deleted) — purge it and create it again?"
            : $"VM `{name}` already exists ({existing.State}) — {(existing.IsStopped ? "delete" : "stop and delete")} it and create it again?";
        if (!await context.ConfirmAsync(question))
        {
            throw new ProgramExecutionException(
                1,
                $"Cancelled — VM `{name}` already exists ({existing.State}). Pick another name or remove it with `cb vm-delete -n {name} --purge`.");
        }

        if (!existing.IsDeleted && !existing.IsStopped)
        {
            await StopVm(context, existing);
        }

        await RunVmStateCommand(
            context,
            name,
            $"delete {name} --purge",
            () => context.Vms.Delete(name, purge: true, context.DefaultTimeoutMs, KeepWaiting(context, $"delete {name}")));
    }

    /// <summary>
    /// Stellt sicher, dass die VM läuft: fehlt sie oder liegt sie im Papierkorb, gibt
    /// es eine fertige Meldung; ist sie gestoppt, wird nach Rückfrage gestartet.
    /// Übergangszustände (Starting, Restarting …) wartet Multipass selbst ab.
    /// </summary>
    private static async Task EnsureRunning(CommandContext context, string name)
    {
        var events = context.Events;
        var vm = await RequireVm(context, name);
        if (vm.IsDeleted)
        {
            throw InTrash(name);
        }

        if (vm.IsTransitional)
        {
            await events.Emit(events.Std($"VM `{name}` is {vm.State} — waiting for it to come up (if it hangs: `cb vm-stop -n {name}` powers it off) ..."));
        }

        if (!vm.IsStopped)
        {
            return;
        }

        if (!await context.ConfirmAsync($"VM `{name}` is {vm.State} — start it now?"))
        {
            throw new ProgramExecutionException(1, $"Cancelled — VM `{name}` is {vm.State}; start it with `cb vm-start -n {name}`.");
        }

        await RunVmStateCommand(
            context,
            name,
            $"start {name}",
            () => context.Vms.Start(name, context.DefaultTimeoutMs, KeepWaiting(context, $"start {name}")));
    }

    /// <summary>VM laut vm-list; fehlt sie, gibt es eine fertige Meldung mit den vorhandenen Namen.</summary>
    private static async Task<VmInfo> RequireVm(CommandContext context, string name)
    {
        var list = await Task.Run(() => context.Vms.List(context.DefaultTimeoutMs), context.CancellationToken);
        return list.FirstOrDefault(vm => string.Equals(vm.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ProgramExecutionException(
                1,
                $"VM `{name}` does not exist. "
                + (list.Count == 0
                    ? "There are no VMs yet — create one with `cb vm-create -n <name>`."
                    : $"Existing VMs: {string.Join(", ", list.Select(vm => vm.Name))}."));
    }

    private static ProgramExecutionException InTrash(string name)
    {
        return new ProgramExecutionException(
            1,
            $"VM `{name}` is in the trash (Deleted) — restore it with `multipass recover {name}` or remove it with `cb vm-delete -n {name} --purge`.");
    }

    /// <summary>
    /// Rückfrage „weiter warten?“ für VM-Kommandos, die länger als
    /// <c>DefaultTimeout</c> brauchen; die Zeit läuft ab Erzeugung. Wird von
    /// CoreBridge.Core auf einem Threadpool-Thread aufgerufen, kann also nicht
    /// <c>await</c>en — der <see cref="EventFactory.Ask{T}"/>-Weg blockiert
    /// stattdessen, während der Konsument die Frage über den Kanal beantwortet.
    /// </summary>
    private static Func<bool> KeepWaiting(CommandContext context, string label)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        return () => context.Events.Ask(context.Events.Choice<YesNoOptions>(
            $"`{label}` is still running after {stopwatch.Elapsed.TotalSeconds:F0} s — keep waiting?")) == YesNoOptions.Yes;
    }

    /// <summary>
    /// Führt ein zustandsänderndes VM-Kommando aus und meldet zum Schluss
    /// Erfolg, Dauer und den neuen State laut vm-list.
    /// </summary>
    private static async Task RunVmStateCommand(
        CommandContext context,
        string name,
        string label,
        Func<MultipassRunResult> action)
    {
        var events = context.Events;
        await events.Emit(events.Std($"{label} ..."));
        var result = await Task.Run(action, context.CancellationToken);
        if (result.ExitCode != 0)
        {
            await Report(events, result);
        }

        await events.Emit(events.Std(await Completed(context.Vms, name, label, result, context.CancellationToken)));
    }

    /// <summary>Abschlusszeile: <c>&lt;label&gt;: OK (&lt;Dauer&gt;) — State: &lt;State&gt;</c>.</summary>
    private static async Task<string> Completed(
        VmService vms,
        string name,
        string label,
        MultipassRunResult result,
        CancellationToken cancellationToken)
    {
        var state = await QueryVmState(vms, name, cancellationToken) ?? "nicht vorhanden";
        return $"{label}: OK ({result.DurationMs / 1000.0:F1} s) — State: {state}";
    }

    /// <summary>State der VM laut vm-list, <c>null</c> wenn sie nicht existiert.</summary>
    private static async Task<string?> QueryVmState(VmService vms, string name, CancellationToken cancellationToken)
    {
        var list = await Task.Run(() => vms.List(), cancellationToken);
        return list
            .FirstOrDefault(vm => string.Equals(vm.Name, name, StringComparison.OrdinalIgnoreCase))
            ?.State;
    }

    private static async Task RunVmPut(CommandContext context, VmPutOptions options)
    {
        var events = context.Events;
        if (!File.Exists(options.Source))
        {
            throw new ProgramExecutionException(1, $"Quelldatei fehlt: {options.Source}");
        }

        await EnsureRunning(context, options.Name);

        await events.Emit(events.Std($"transfer {options.Source} → {options.Name} ..."));
        var result = await Task.Run(
            () => context.Vms.Put(options.Source, options.Name, options.Destination, context.DefaultTimeoutMs),
            context.CancellationToken);
        if (result.ExitCode != 0)
        {
            await Report(events, result);
        }

        await events.Emit(events.Std($"transfer: OK ({result.DurationMs / 1000.0:F1} s)"));
    }

    private static async Task RunVmGet(CommandContext context, VmGetOptions options)
    {
        var events = context.Events;
        await EnsureRunning(context, options.Name);

        var targetDirectory = Path.GetDirectoryName(Path.GetFullPath(options.Destination));
        if (!string.IsNullOrEmpty(targetDirectory) && !Directory.Exists(targetDirectory))
        {
            Directory.CreateDirectory(targetDirectory);
        }

        await events.Emit(events.Std($"transfer {options.Name}:{options.Remote} → {options.Destination} ..."));
        var result = await Task.Run(
            () => context.Vms.Get(options.Name, options.Remote, options.Destination, context.DefaultTimeoutMs),
            context.CancellationToken);
        if (!File.Exists(options.Destination))
        {
            await Report(events, result);
            return;
        }

        var megaBytes = new FileInfo(options.Destination).Length / (1024.0 * 1024.0);
        await events.Emit(events.Std($"transfer: OK — {megaBytes:N0} MB ({result.DurationMs / 1000.0:F1} s)"));
        if (result.ExitCode != 0)
        {
            await events.Emit(events.Std($"Hinweis: Exit-Code {result.ExitCode} — {result.StdErr.Trim()}"));
        }
    }

    private static async Task RunInitConfigure(CommandContext context, InitConfigureOptions options)
    {
        var events = context.Events;
        var config = context.Config;
        var components = options.Components.Split(',', StringSplitOptions.RemoveEmptyEntries);
        await events.Emit(events.Std($"Erzeuge Profil '{options.Name}' ({string.Join(", ", components)}) ..."));
        var inits = new InitService(config.Images);
        var result = await Task.Run(
            () => inits.Configure(
                options.Name,
                components,
                force: options.Force,
                runtimeImage: options.RuntimeImage),
            context.CancellationToken);

        await events.Emit(events.Std($"Profil '{result.Name}' erzeugt."));
        await events.Emit(events.Std($"  Config:       {result.ConfigPath}"));
        await events.Emit(events.Std($"  Runtime-Image: {result.RuntimeImage}"));
        await events.Emit(events.Std($"  Snaps:        {(result.Snaps.Length == 0 ? "keine" : string.Join(", ", result.Snaps))}"));
        await events.Emit(events.Std(result.CloudInitPath is null
            ? "  cloud-init: nicht nötig (keine Gast-Provisionierung)."
            : $"  cloud-init: {result.CloudInitPath}"));
        await events.Emit(events.Std(string.Empty));
        await events.Emit(events.Std($"VM starten: cb vm-create --name <vm> --image {result.Name}"));
    }

    private static async Task RunInitList(CommandContext context)
    {
        var events = context.Events;
        var entries = await Task.Run(() => new InitService(context.Config.Images).List(), context.CancellationToken);
        if (entries.Count == 0)
        {
            await events.Emit(events.Std("Keine konfigurierten Profile."));
            return;
        }

        await events.Emit(events.Std($"{"Name",-20} {"Komponenten",-28} Snaps"));
        foreach (var entry in entries)
        {
            await events.Emit(events.Std($"{entry.Name,-20} {string.Join(",", entry.Components),-28} {string.Join(",", entry.Snaps)}"));
        }
    }

    private static async Task RunInitDelete(CommandContext context, InitDeleteOptions options)
    {
        var result = await Task.Run(
            () => new InitService(context.Config.Images).Delete(options.Name),
            context.CancellationToken);
        await context.Events.Emit(context.Events.Std($"Konfiguration gelöscht: {result.ProfileDir}"));
    }
}
