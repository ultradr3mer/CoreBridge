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
    /// Schreibt eine Meldung; <see cref="Progress.ProgressTick"/>-Zeilen werden
    /// hintereinander in dieselbe Zeile geschrieben.
    /// </summary>
    private static void WriteOutput(string line)
    {
        if (line == Progress.ProgressTick)
        {
            Console.Write(line);
            _midProgressLine = true;
            return;
        }

        if (_midProgressLine && line == "OK")
        {
            Console.WriteLine(line);
            _midProgressLine = false;
            return;
        }

        EndProgressLine();
        Console.WriteLine(line);
    }

    /// <summary>Schließt eine offene Punktezeile ab, bevor anderer Text folgt.</summary>
    private static void EndProgressLine()
    {
        if (_midProgressLine)
        {
            Console.WriteLine();
            _midProgressLine = false;
        }
    }

    /// <summary>Ja/Nein-Rückfrage auf der Konsole; ohne interaktive Eingabe immer Nein.</summary>
    private static bool ConfirmOnConsole(string question)
    {
        EndProgressLine();
        if (Console.IsInputRedirected)
        {
            Console.WriteLine($"{question} [y/n] n (keine interaktive Eingabe)");
            return false;
        }

        Console.Write($"{question} [y/n] ");
        return Console.ReadLine()?.Trim().ToLowerInvariant() is "y" or "yes" or "j" or "ja";
    }

    /// <summary>Freitext-Eingabe auf der Konsole; ohne interaktive Eingabe <c>null</c>.</summary>
    private static string? PromptOnConsole(string question)
    {
        EndProgressLine();
        if (Console.IsInputRedirected)
        {
            Console.WriteLine($"{question}(no interactive input)");
            return null;
        }

        Console.Write(question);
        return Console.ReadLine();
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
    /// <param name="confirm">
    /// Ja/Nein-Rückfrage für Kommandos, die vor riskanten Schritten fragen
    /// (multipass-restart, vm-create). Ohne Angabe wird jede Rückfrage mit Nein beantwortet.
    /// </param>
    /// <param name="prompt">
    /// Freitext-Eingabe, z. B. das Folgekommando nach abgebrochenem Warten in
    /// vm-create. Ohne Angabe gibt es keine Eingabe (<c>null</c>).
    /// </param>
    public static IAsyncEnumerable<string> Run(
        AppConfig config,
        IOptions options,
        CancellationToken cancellationToken = default,
        Func<string, bool>? confirm = null,
        Func<string, string?>? prompt = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(options);

        return RunCore(ApplyTimeoutOverride(config, options), options, confirm ?? (_ => false), prompt ?? (_ => null), cancellationToken);
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
            VmStartOptions o => RunVmStart(vms, o, context.DefaultTimeoutMs, cancellationToken),
            VmStopOptions o => RunVmStateCommand(
                vms,
                o.Name,
                $"stop {o.Name}",
                () => vms.Stop(o.Name, context.DefaultTimeoutMs),
                cancellationToken),
            VmDeleteOptions o => RunVmStateCommand(
                vms,
                o.Name,
                $"delete {o.Name}{(o.Purge ? " --purge" : string.Empty)}",
                () => vms.Delete(o.Name, o.Purge, context.DefaultTimeoutMs),
                cancellationToken),
            VmExecOptions o => RunVmExec(vms, o, context.DefaultTimeoutMs, cancellationToken),
            VmConnectOptions o => RunVmConnect(vms, o, cancellationToken),
            VmPutOptions o => RunVmPut(vms, o, context.DefaultTimeoutMs, cancellationToken),
            VmGetOptions o => RunVmGet(vms, o, context.DefaultTimeoutMs, cancellationToken),
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
            : text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
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
        yield return $"{"Name",-30} {"State",-10} IPv4";
        foreach (var vm in list)
        {
            yield return $"{vm.Name,-30} {vm.State ?? "?",-10} {string.Join(", ", vm.Ipv4)}";
        }
    }

    /// <summary>
    /// Legt die VM an und wartet in Abschnitten von <c>DefaultTimeoutVmCreation</c>;
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

    private static async IAsyncEnumerable<string> RunVmExec(
        VmService vms,
        VmExecOptions options,
        int timeoutMs,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var result = await Task.Run(
            () => vms.Exec(options.Name, options.Command, timeoutMs),
            cancellationToken);
        foreach (var line in Report(result))
        {
            yield return line;
        }
    }

    private static async IAsyncEnumerable<string> RunVmConnect(
        VmService vms,
        VmConnectOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var exitCode = await Task.Run(() => vms.Connect(options.Name), cancellationToken);
        if (exitCode != 0)
        {
            throw new ProgramExecutionException(exitCode, $"vm-connect fehlgeschlagen (Exit {exitCode}).");
        }

        yield break;
    }

    private static async IAsyncEnumerable<string> RunVmStart(
        VmService vms,
        VmStartOptions options,
        int timeoutMs,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (string.Equals(await QueryVmState(vms, options.Name, cancellationToken), "Running", StringComparison.OrdinalIgnoreCase))
        {
            yield return $"start {options.Name}: OK (lief bereits) — State: Running";
            yield break;
        }

        await foreach (var line in RunVmStateCommand(
            vms,
            options.Name,
            $"start {options.Name}",
            () => vms.Start(options.Name, timeoutMs),
            cancellationToken))
        {
            yield return line;
        }
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

    private static async IAsyncEnumerable<string> RunVmPut(
        VmService vms,
        VmPutOptions options,
        int timeoutMs,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!File.Exists(options.Source))
        {
            throw new ProgramExecutionException(1, $"Quelldatei fehlt: {options.Source}");
        }

        yield return $"transfer {options.Source} → {options.Name} ...";
        var result = await Task.Run(
            () => vms.Put(options.Source, options.Name, options.Destination, timeoutMs),
            cancellationToken);
        if (result.ExitCode != 0)
        {
            foreach (var line in Report(result))
            {
                yield return line;
            }
        }

        yield return $"transfer: OK ({result.DurationMs / 1000.0:F1} s)";
    }

    private static async IAsyncEnumerable<string> RunVmGet(
        VmService vms,
        VmGetOptions options,
        int timeoutMs,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var targetDirectory = Path.GetDirectoryName(Path.GetFullPath(options.Destination));
        if (!string.IsNullOrEmpty(targetDirectory) && !Directory.Exists(targetDirectory))
        {
            Directory.CreateDirectory(targetDirectory);
        }

        yield return $"transfer {options.Name}:{options.Remote} → {options.Destination} ...";
        var result = await Task.Run(
            () => vms.Get(options.Name, options.Remote, options.Destination, timeoutMs),
            cancellationToken);
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
