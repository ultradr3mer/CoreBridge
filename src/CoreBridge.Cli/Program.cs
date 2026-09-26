using System.Runtime.CompilerServices;
using System.Text;
using CommandLine;
using CoreBridge.Core.Common;
using CoreBridge.Core.Config;
using CoreBridge.Core.Images;
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
        typeof(ImageConfigureOptions),
        typeof(ImageListOptions),
        typeof(ImageDeleteOptions)
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
            await foreach (var line in Run(config, options, cancellation.Token, ConfirmOnConsole, PromptOnConsole))
            {
                WriteOutput(line);
            }

            EndProgressLine();
            return 0;
        }
        catch (ProgramExecutionException exception)
        {
            EndProgressLine();
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

    private static bool _midProgressLine;

    /// <summary>
    /// Einziger Kommando-Dispatcher nach dem Parsen. Services werden aus dem
    /// übergebenen Config erzeugt; es gibt keine statischen Vm-/Config-Zustände.
    /// Jede Meldung wird geliefert, sobald sie entsteht — nicht erst nach Ende
    /// des Kommandos. Jeder erwartete Fehler kommt ausschließlich als
    /// <see cref="ProgramExecutionException"/> mit fertiger Meldung und Exit-Code
    /// an (siehe <see cref="ToExecutionException"/>); alles andere ist ein Bug und
    /// wird unverändert weitergeworfen.
    /// </summary>
    /// <param name="confirm">
    /// Ja/Nein-Rückfrage für Kommandos, die vor riskanten Schritten fragen
    /// (multipass-restart, vm-create). Ohne Angabe wird jede Rückfrage mit Nein beantwortet.
    /// </param>
    /// <param name="prompt">
    /// Freitext-Eingabe, z. B. das Folgekommando nach abgebrochenem Warten in
    /// vm-create. Ohne Angabe gibt es keine Eingabe (<c>null</c>).
    /// </param>
    public static IAsyncEnumerable<CliEvent> Run(
        AppConfig config,
        IOptions options,
        CancellationToken cancellationToken = default,
        Func<string, bool>? confirm = null,
        Func<string, string?>? prompt = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(options);

        return RunCore(config: ApplyTimeoutOverride(config, options),
                       options: options,
                       confirm: confirm ?? (_ => false),
                       prompt: prompt ?? (_ => null),
                       cancellationToken: cancellationToken);
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
    /// Reicht den Ausgabestrom durch und übersetzt dabei erwartete Fehler. Weil
    /// <c>yield</c> nicht in einem <c>try</c> mit <c>catch</c> stehen darf, wird
    /// nur <c>MoveNextAsync</c> überwacht.
    /// </summary>
    private static async IAsyncEnumerable<string> RunCore(
        AppConfig config,
        IOptions options,
        Func<string, bool> confirm,
        Func<string, string?> prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var lines = Execute(config, options, confirm, prompt, cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            try
            {
                if (!await lines.MoveNextAsync())
                {
                    yield break;
                }
            }
            catch (Exception exception) when (ToExecutionException(exception) is { } failure)
            {
                throw failure;
            }

            yield return lines.Current;
        }
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

    private static async IAsyncEnumerable<string> Execute(
        AppConfig config,
        IOptions options,
        Func<string, bool> confirm,
        Func<string, string?> prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ImageStore.Initialize(config.Images.StoreRoot);
        var context = new CommandContext(config, new VmService(), confirm, prompt, cancellationToken);
        await foreach (var line in Dispatch(context, options))
        {
            yield return line;
        }
    }

    private static IAsyncEnumerable<string> Dispatch(CommandContext context, IOptions options)
    {
        var (config, vms, cancellationToken) = (context.Config, context.Vms, context.CancellationToken);
        return options switch
        {
            MultipassStatusOptions => RunMultipassStatus(cancellationToken),
            MultipassRestartOptions => RunMultipassRestart(context),
            VmListOptions => RunVmList(vms, context.DefaultTimeoutMs, cancellationToken),
            VmCreateOptions o => RunVmCreate(context, o),
            VmStartOptions o => RunVmStart(context, o),
            VmStopOptions o => RunVmStop(context, o),
            VmDeleteOptions o => RunVmDelete(context, o),
            VmExecOptions o => RunVmExec(context, o),
            VmConnectOptions o => RunVmConnect(context, o),
            VmPutOptions o => RunVmPut(context, o),
            VmGetOptions o => RunVmGet(context, o),
            ImageConfigureOptions o => RunImageConfigure(config, o, cancellationToken),
            ImageListOptions => RunImageList(config, cancellationToken),
            ImageDeleteOptions o => RunImageDelete(config, o, cancellationToken),
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
    private static IEnumerable<string> Report(MultipassRunResult result)
    {
        foreach (var line in Lines(result.StdOut))
        {
            yield return line;
        }

        if (result.ExitCode != 0)
        {
            throw Failure(result);
        }

        foreach (var line in Lines(result.StdErr))
        {
            yield return line;
        }
    }

    private static async IAsyncEnumerable<string> RunMultipassStatus(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var report = await Task.Run(() => Doctor.Check(), cancellationToken);
        if (!report.Found)
        {
            var message = new StringBuilder("Multipass nicht gefunden.\n");
            foreach (var hint in report.Hints)
            {
                message.Append($"\n  - {hint}");
            }

            throw new ProgramExecutionException(1, message.ToString());
        }

        yield return $"Binary:  {report.BinaryPath}";
        yield return $"CLI:     {report.ClientVersion ?? "unbekannt"}";
        yield return $"Daemon:  {report.DaemonVersion ?? "keine Antwort"}";
        if (report.Error is not null)
        {
            yield return string.Empty;
            yield return $"Hinweis: {report.Error}";
            foreach (var line in Lines(report.RawVersionOutput ?? string.Empty))
            {
                yield return line;
            }
        }
    }

    private static async IAsyncEnumerable<string> RunMultipassRestart(CommandContext context)
    {
        var cancellationToken = context.CancellationToken;
        var timeout = context.Config.DefaultTimeout.ReadTime();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var service = new MultipassService(commandTimeout: timeout);
        if (!service.IsElevated())
        {
            yield return $"Warnung: Der Neustart des Dienstes {MultipassService.ServiceName} benötigt Administratorrechte.";
            if (!context.Confirm("Mit Administratorrechten in einem neuen Fenster fortfahren?"))
            {
                throw new ProgramExecutionException(1, "Abgebrochen — ohne Administratorrechte kein Neustart.");
            }

            yield return "Starte erhöht (UAC-Abfrage) — Ausgabe und Rückfragen folgen im Administratorfenster ...";
            var exitCode = await RunElevatedAsync(context.Config.DefaultTimeout, cancellationToken);
            if (exitCode != 0)
            {
                throw new ProgramExecutionException(exitCode, $"multipass-restart im Administratorfenster fehlgeschlagen (Exit {exitCode}).");
            }

            yield return $"multipass-restart: OK ({stopwatch.Elapsed.TotalSeconds:F1} s, erhöht) — State: {service.QueryState()}";
            yield break;
        }

        await foreach (var line in service.Restart(timeout, context.Confirm, cancellationToken))
        {
            yield return line;
        }

        yield return $"multipass-restart: OK ({stopwatch.Elapsed.TotalSeconds:F1} s) — State: {service.QueryState()}";
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

    private static async IAsyncEnumerable<string> RunVmList(
        VmService vms,
        int timeoutMs,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var list = await Task.Run(() => vms.List(timeoutMs), cancellationToken);
        if (list.Count == 0)
        {
            yield return "No VMs — create one with `cb vm-create -n <name>`.";
            yield break;
        }

        yield return $"{"Name",-30} {"State",-10} IPv4";
        foreach (var vm in list)
        {
            yield return $"{vm.Name,-30} {vm.State ?? "?",-10} {string.Join(", ", vm.Ipv4)}";
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
    private static async IAsyncEnumerable<string> RunVmCreate(CommandContext context, VmCreateOptions options)
    {
        var (config, vms, cancellationToken) = (context.Config, context.Vms, context.CancellationToken);
        var waitSlice = config.DefaultTimeoutVmCreation.ReadTime();
        var requestedImage = string.IsNullOrWhiteSpace(options.Image)
            ? config.Images.DefaultRuntimeImage
            : options.Image.Trim();
        var profile = ImageProfileResolver.Resolve(config.Images, requestedImage, options.CloudInit);
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

        await foreach (var line in ReplaceExistingVm(context, options.Name))
        {
            yield return line;
        }

        yield return $"Lege VM `{options.Name}` an (Image: {requestedImage}"
            + (profile is null ? string.Empty : $" → Runtime: {launchImage}")
            + $"{(options.Disk is null ? string.Empty : $", Disk: {options.Disk}")}"
            + $"{(cloudInit is null ? string.Empty : ", cloud-init")}) ...";

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
                    () => context.Confirm(
                        $"VM `{options.Name}` is not ready after {stopwatch.Elapsed.TotalSeconds:F0} s — keep waiting?")),
                cancellationToken);
        }
        catch (MultipassTimeoutException)
        {
            result = null;
        }

        if (result is null)
        {
            yield return "Stopped waiting — Multipass keeps creating the VM in the background (check with `cb vm-list`).";
            var followUp = context.Prompt("Follow-up command (empty = quit): ");
            if (string.IsNullOrWhiteSpace(followUp))
            {
                yield break;
            }

            if (!TryParseOptions(SplitCommandLine(followUp), out var followUpOptions, out _))
            {
                throw new ProgramExecutionException(1, $"Invalid follow-up command: {followUp}");
            }

            var followUpContext = context with { Config = ApplyTimeoutOverride(context.Config, followUpOptions) };
            await foreach (var line in Dispatch(followUpContext, followUpOptions))
            {
                yield return line;
            }

            yield break;
        }

        if (result.ExitCode != 0)
        {
            foreach (var line in Lines(result.StdOut))
            {
                yield return line;
            }

            throw Failure(result);
        }

        if (profile is not null)
        {
            yield return $"Image-Profil: {profile.Name} (Runtime: {profile.RuntimeImage})";
            yield return "Gast-Provisionierung: cloud-init";
        }

        yield return await Completed(vms, options.Name, $"create {options.Name}", result, cancellationToken);
    }

    private static async IAsyncEnumerable<string> RunVmExec(CommandContext context, VmExecOptions options)
    {
        await foreach (var line in EnsureRunning(context, options.Name))
        {
            yield return line;
        }

        var result = await Task.Run(
            () => context.Vms.Exec(options.Name, options.Command, context.DefaultTimeoutMs),
            context.CancellationToken);
        foreach (var line in Report(result))
        {
            yield return line;
        }
    }

    private static async IAsyncEnumerable<string> RunVmConnect(CommandContext context, VmConnectOptions options)
    {
        await foreach (var line in EnsureRunning(context, options.Name))
        {
            yield return line;
        }

        var exitCode = await Task.Run(() => context.Vms.Connect(options.Name), context.CancellationToken);
        if (exitCode != 0)
        {
            throw new ProgramExecutionException(exitCode, $"vm-connect fehlgeschlagen (Exit {exitCode}).");
        }
    }

    private static async IAsyncEnumerable<string> RunVmStart(CommandContext context, VmStartOptions options)
    {
        var name = options.Name;
        var vm = await RequireVm(context, name);
        if (vm.IsDeleted)
        {
            throw InTrash(name);
        }

        if (vm.IsRunning)
        {
            yield return $"start {name}: OK (lief bereits) — State: Running";
            yield break;
        }

        await foreach (var line in RunVmStateCommand(
            context.Vms,
            name,
            $"start {name}",
            () => context.Vms.Start(name, context.DefaultTimeoutMs, KeepWaiting(context, $"start {name}")),
            context.CancellationToken))
        {
            yield return line;
        }
    }

    private static async IAsyncEnumerable<string> RunVmStop(CommandContext context, VmStopOptions options)
    {
        var name = options.Name;
        var vm = await RequireVm(context, name);
        if (vm.IsDeleted)
        {
            throw InTrash(name);
        }

        if (vm.IsStopped)
        {
            yield return $"stop {name}: OK (was already stopped) — State: {vm.State}";
            yield break;
        }

        if (vm.IsTransitional
            && !context.Confirm($"VM `{name}` is {vm.State} — Multipass can only power it off (--force). Power it off?"))
        {
            throw new ProgramExecutionException(1, $"Cancelled — VM `{name}` was not stopped (State: {vm.State}).");
        }

        await foreach (var line in StopVm(context, vm))
        {
            yield return line;
        }
    }

    /// <summary>
    /// Stoppt die VM; in Übergangszuständen (<see cref="VmInfo.IsTransitional"/>)
    /// mit <c>--force</c>, weil Multipass den normalen Stop dort ablehnt. Die
    /// Rückfrage dazu stellt der Aufrufer.
    /// </summary>
    private static IAsyncEnumerable<string> StopVm(CommandContext context, VmInfo vm)
    {
        var force = vm.IsTransitional;
        var label = force ? $"stop --force {vm.Name}" : $"stop {vm.Name}";
        return RunVmStateCommand(
            context.Vms,
            vm.Name,
            label,
            () => context.Vms.Stop(vm.Name, context.DefaultTimeoutMs, KeepWaiting(context, label), force),
            context.CancellationToken);
    }

    /// <summary>
    /// Löscht die VM (in den Papierkorb oder mit <c>--purge</c> endgültig). Vor dem
    /// Löschen einer laufenden VM wird gefragt; eine VM im Papierkorb ohne
    /// <c>--purge</c> ist bereits gelöscht.
    /// </summary>
    private static async IAsyncEnumerable<string> RunVmDelete(CommandContext context, VmDeleteOptions options)
    {
        var name = options.Name;
        var vm = await RequireVm(context, name);
        if (vm.IsDeleted && !options.Purge)
        {
            yield return $"delete {name}: OK (already in the trash; --purge removes it for good) — State: Deleted";
            yield break;
        }

        if (!vm.IsDeleted
            && !vm.IsStopped
            && !context.Confirm($"VM `{name}` is {vm.State} — stop and delete it?"))
        {
            throw new ProgramExecutionException(1, $"Cancelled — VM `{name}` was not deleted (State: {vm.State}).");
        }

        if (!vm.IsDeleted && !vm.IsStopped)
        {
            await foreach (var line in StopVm(context, vm))
            {
                yield return line;
            }
        }

        var label = $"delete {name}{(options.Purge ? " --purge" : string.Empty)}";
        await foreach (var line in RunVmStateCommand(
            context.Vms,
            name,
            label,
            () => context.Vms.Delete(name, options.Purge, context.DefaultTimeoutMs, KeepWaiting(context, label)),
            context.CancellationToken))
        {
            yield return line;
        }
    }

    /// <summary>
    /// Gibt es die VM schon (auch im Papierkorb), wird gefragt, ob sie gestoppt,
    /// endgültig gelöscht und neu angelegt werden darf; bei Nein bricht vm-create ab.
    /// </summary>
    private static async IAsyncEnumerable<string> ReplaceExistingVm(CommandContext context, string name)
    {
        var existing = await Task.Run(() => context.Vms.Find(name), context.CancellationToken);
        if (existing is null)
        {
            yield break;
        }

        var question = existing.IsDeleted
            ? $"VM `{name}` is still in the trash (Deleted) — purge it and create it again?"
            : $"VM `{name}` already exists ({existing.State}) — {(existing.IsStopped ? "delete" : "stop and delete")} it and create it again?";
        if (!context.Confirm(question))
        {
            throw new ProgramExecutionException(
                1,
                $"Cancelled — VM `{name}` already exists ({existing.State}). Pick another name or remove it with `cb vm-delete -n {name} --purge`.");
        }

        if (!existing.IsDeleted && !existing.IsStopped)
        {
            await foreach (var line in StopVm(context, existing))
            {
                yield return line;
            }
        }

        await foreach (var line in RunVmStateCommand(
            context.Vms,
            name,
            $"delete {name} --purge",
            () => context.Vms.Delete(name, purge: true, context.DefaultTimeoutMs, KeepWaiting(context, $"delete {name}")),
            context.CancellationToken))
        {
            yield return line;
        }
    }

    /// <summary>
    /// Stellt sicher, dass die VM läuft: fehlt sie oder liegt sie im Papierkorb, gibt
    /// es eine fertige Meldung; ist sie gestoppt, wird nach Rückfrage gestartet.
    /// Übergangszustände (Starting, Restarting …) wartet Multipass selbst ab.
    /// </summary>
    private static async IAsyncEnumerable<string> EnsureRunning(CommandContext context, string name)
    {
        var vm = await RequireVm(context, name);
        if (vm.IsDeleted)
        {
            throw InTrash(name);
        }

        if (vm.IsTransitional)
        {
            yield return $"VM `{name}` is {vm.State} — waiting for it to come up (if it hangs: `cb vm-stop -n {name}` powers it off) ...";
        }

        if (!vm.IsStopped)
        {
            yield break;
        }

        if (!context.Confirm($"VM `{name}` is {vm.State} — start it now?"))
        {
            throw new ProgramExecutionException(1, $"Cancelled — VM `{name}` is {vm.State}; start it with `cb vm-start -n {name}`.");
        }

        await foreach (var line in RunVmStateCommand(
            context.Vms,
            name,
            $"start {name}",
            () => context.Vms.Start(name, context.DefaultTimeoutMs, KeepWaiting(context, $"start {name}")),
            context.CancellationToken))
        {
            yield return line;
        }
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
    /// <c>DefaultTimeout</c> brauchen; die Zeit läuft ab Erzeugung.
    /// </summary>
    private static Func<bool> KeepWaiting(CommandContext context, string label)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        return () => context.Confirm($"`{label}` is still running after {stopwatch.Elapsed.TotalSeconds:F0} s — keep waiting?");
    }

    /// <summary>
    /// Führt ein zustandsänderndes VM-Kommando aus und meldet zum Schluss
    /// Erfolg, Dauer und den neuen State laut vm-list.
    /// </summary>
    private static async IAsyncEnumerable<string> RunVmStateCommand(
        VmService vms,
        string name,
        string label,
        Func<MultipassRunResult> action,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return $"{label} ...";
        var result = await Task.Run(action, cancellationToken);
        if (result.ExitCode != 0)
        {
            foreach (var line in Report(result))
            {
                yield return line;
            }
        }

        yield return await Completed(vms, name, label, result, cancellationToken);
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

    private static async IAsyncEnumerable<string> RunVmPut(CommandContext context, VmPutOptions options)
    {
        if (!File.Exists(options.Source))
        {
            throw new ProgramExecutionException(1, $"Quelldatei fehlt: {options.Source}");
        }

        await foreach (var line in EnsureRunning(context, options.Name))
        {
            yield return line;
        }

        yield return $"transfer {options.Source} → {options.Name} ...";
        var result = await Task.Run(
            () => context.Vms.Put(options.Source, options.Name, options.Destination, context.DefaultTimeoutMs),
            context.CancellationToken);
        if (result.ExitCode != 0)
        {
            foreach (var line in Report(result))
            {
                yield return line;
            }
        }

        yield return $"transfer: OK ({result.DurationMs / 1000.0:F1} s)";
    }

    private static async IAsyncEnumerable<string> RunVmGet(CommandContext context, VmGetOptions options)
    {
        await foreach (var line in EnsureRunning(context, options.Name))
        {
            yield return line;
        }

        var targetDirectory = Path.GetDirectoryName(Path.GetFullPath(options.Destination));
        if (!string.IsNullOrEmpty(targetDirectory) && !Directory.Exists(targetDirectory))
        {
            Directory.CreateDirectory(targetDirectory);
        }

        yield return $"transfer {options.Name}:{options.Remote} → {options.Destination} ...";
        var result = await Task.Run(
            () => context.Vms.Get(options.Name, options.Remote, options.Destination, context.DefaultTimeoutMs),
            context.CancellationToken);
        if (!File.Exists(options.Destination))
        {
            foreach (var line in Report(result))
            {
                yield return line;
            }

            yield break;
        }

        var megaBytes = new FileInfo(options.Destination).Length / (1024.0 * 1024.0);
        yield return $"transfer: OK — {megaBytes:N0} MB ({result.DurationMs / 1000.0:F1} s)";
        if (result.ExitCode != 0)
        {
            yield return $"Hinweis: Exit-Code {result.ExitCode} — {result.StdErr.Trim()}";
        }
    }

    private static async IAsyncEnumerable<string> RunImageConfigure(
        AppConfig config,
        ImageConfigureOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var components = options.Components.Split(',', StringSplitOptions.RemoveEmptyEntries);
        yield return $"Konfiguriere Image '{options.Name}' ({string.Join(", ", components)}) ...";
        using var http = new HttpClient { Timeout = config.DefaultTimeout.ReadTime() };
        var images = new ImageService(config.Images, http);
        var result = await Task.Run(
            () => images.Configure(
                options.Name,
                components,
                options.ModelUrl,
                force: options.Force,
                runtimeImage: options.RuntimeImage),
            cancellationToken);

        yield return $"Image '{result.Name}' konfiguriert.";
        yield return $"  Modell:       {result.ModelPath} (Canonical dangerous, unverändert signiert)";
        yield return $"  Config:       {result.ConfigPath}";
        yield return $"  Runtime-Image: {result.RuntimeImage}";
        yield return $"  Snaps:        {(result.Snaps.Length == 0 ? "keine" : string.Join(", ", result.Snaps))}";
        yield return result.CloudInitPath is null
            ? "  cloud-init: nicht nötig (keine Gast-Provisionierung)."
            : $"  cloud-init: {result.CloudInitPath}";
        yield return string.Empty;
        yield return $"VM starten: cb vm-create --name <vm> --image {result.Name}";
    }

    private static async IAsyncEnumerable<string> RunImageList(
        AppConfig config,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var entries = await Task.Run(() => new ImageService(config.Images).List(), cancellationToken);
        if (entries.Count == 0)
        {
            yield return "Keine konfigurierten Classic-Profile.";
            yield break;
        }

        yield return $"{"Name",-20} {"Komponenten",-28} Snaps";
        foreach (var entry in entries)
        {
            yield return $"{entry.Name,-20} {string.Join(",", entry.Components),-28} {string.Join(",", entry.Snaps)}";
        }
    }

    private static async IAsyncEnumerable<string> RunImageDelete(
        AppConfig config,
        ImageDeleteOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var result = await Task.Run(() => new ImageService(config.Images).Delete(options.Name), cancellationToken);
        yield return $"Konfiguration gelöscht: {result.ModelDir}";
    }
}
