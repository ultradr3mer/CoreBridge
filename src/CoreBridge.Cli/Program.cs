using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using CommandLine;
using CoreBridge.Cli.CliAbstrction;
using CoreBridge.Core.Authenticity;
using CoreBridge.Core.Common;
using CoreBridge.Core.Config;
using CoreBridge.Core.Git;
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
        typeof(VmCloneOptions),
        typeof(VmSnapshotOptions),
        typeof(VmSnapshotsOptions),
        typeof(VmRestoreOptions),
        typeof(VmInstallOptions),
        typeof(VmResizeOptions),
        typeof(RepoSendOptions),
        typeof(RepoReceiveOptions),
        typeof(InitConfigureOptions),
        typeof(InitListOptions),
        typeof(InitDeleteOptions),
        typeof(ComponentListOptions),
        typeof(ComponentAddOptions),
        typeof(ComponentRemoveOptions),
        typeof(SettingsVerifyOptions)
  ];

  /// <summary>Wert von DOTNET_ENVIRONMENT, Default Development.</summary>
  private static string CurrentEnvironment =>
      Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Development";

  /// <summary>
  /// Leitsatz 6: CLI und TUI lesen dieselbe Config-Quelle. Reihenfolge:
  /// appsettings.json → appsettings.{Environment}.json (Env DOTNET_ENVIRONMENT,
  /// Default Development) → appsettings.local.json (gitignored) → Env-Variablen
  /// (Doppelter Unterstrich, z. B. Images__StoreRoot). Keine Passwörter in der
  /// Config — Secrets laufen über die CORE_BRIDGE_*_PASSWORD-Env-Kette.
  /// Vorher müssen appsettings.json und appsettings.{Environment}.json gültig
  /// signiert sein (<see cref="SettingsSignatures"/>), sonst
  /// <see cref="SettingsSignatureException"/>; <paramref name="requireSignatures"/> ist nur
  /// für <c>settings-verify</c> aus, das die Prüfung selbst meldet.
  /// </summary>
  private static AppConfig LoadSystemConfig()
  {
    var environment = CurrentEnvironment;

    var configuration = new ConfigurationBuilder()
        .SetFileProvider(new SignedFileProvider(AppContext.BaseDirectory, requireSignature: false))
        .AddJsonFile("appsettings.json", optional: false)
        .AddJsonFile($"appsettings.{environment}.json", optional: true)
        .AddJsonFile("appsettings.local.json", optional: true)
        .AddEnvironmentVariables()
        .Build();

    return configuration.Get<AppConfig>()
      ?? throw new InvalidOperationException("Konfiguration konnte nicht gebunden werden (appsettings.json).");
  }

  public static AppConfig LoadUserConfig()
  { 
    var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    var configuration = new ConfigurationBuilder()
        .SetFileProvider(new SignedFileProvider(localAppData, requireSignature: false))
        .AddJsonFile($"appsettings.{userProfile}.json", optional: true)
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
    if (!TryParseOptions(args, out var options, out var helpRequested))
    {
      return helpRequested ? 0 : 1;
    }

    AppConfig config;
    try
    {
      config = LoadSystemConfig();
    }
    catch (Exception exception) when (ToExecutionException(exception) is { } failure)
    {
      Console.Error.WriteLine(failure.Message);
      return failure.ExitCode;
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
  /// ungültig signierte Konfiguration (3),
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
      SettingsSignatureException => new(
          3,
          exception.Message + Environment.NewLine
              + "Nach einer Änderung neu signieren: .\\scripts\\sign-settings.ps1 (prüfen: cb settings-verify).",
          exception),
      MultipassException => new(1, exception.Message, exception),
      GitException => new(1, exception.Message, exception),
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
    config = InitComponentStore.Merge(config, InitStore.UserSettingsFile);
    using var services = CoreBridgeContainer.Create(config);
    var cli = MultipassCliFactory.Create(services);
    var context = new CommandContext(config, new VmService(cli), factory, cancellationToken, new GitCli());
    if (cli is IMultipassDouble && options is RepoSendOptions or RepoReceiveOptions)
    {
      // git auf dem Host erreicht keine simulierte VM.
      throw new ProgramExecutionException(1, "repo-send/repo-receive are not supported in simulation — git has to run inside a real VM.");
    }

    if (cli is IMultipassDouble multipassDouble && options is not (InitConfigureOptions or InitListOptions or InitDeleteOptions or ComponentListOptions or ComponentAddOptions or ComponentRemoveOptions or SettingsVerifyOptions or MultipassRestartOptions))
    {
      // Unübersehbar, damit niemand Ergebnisse einer Attrappe für echte VMs hält.
      await factory.Emit(factory.Err($"[{multipassDouble.BinaryLabel}] kein echtes Multipass ({cli.GetType().Name})."));
    }

    await Dispatch(context, options);
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
      VmCloneOptions o => RunVmClone(context, o),
      VmSnapshotOptions o => RunVmSnapshot(context, o),
      VmSnapshotsOptions o => RunVmSnapshots(context, o),
      VmRestoreOptions o => RunVmRestore(context, o),
      VmInstallOptions o => RunVmInstall(context, o),
      VmResizeOptions o => RunVmResize(context, o),
      RepoSendOptions o => RunRepoSend(context, o),
      RepoReceiveOptions o => RunRepoReceive(context, o),
      InitConfigureOptions o => RunInitConfigure(context, o),
      InitListOptions => RunInitList(context),
      InitDeleteOptions o => RunInitDelete(context, o),
      ComponentListOptions o => RunComponentList(context, o),
      ComponentAddOptions o => RunComponentAdd(context, o),
      ComponentRemoveOptions o => RunComponentRemove(context, o),
      SettingsVerifyOptions o => RunSettingsVerify(context, o),
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

    // Bei Wiedergabe/Simulation gibt es weder Binary noch Daemon: den
    // Pseudo-Pfad melden, damit der Erfolgspfad (Parsing der Version) durchläuft.
    var cli = context.Vms.Cli;
    var report = await Task.Run(
        () => cli is IMultipassDouble multipassDouble
            ? Doctor.Check(() => multipassDouble.BinaryLabel, cli, context.DefaultTimeoutMs)
            : Doctor.Check(cli: cli, timeoutMs: context.DefaultTimeoutMs),
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
      // Ein Daemon, der nicht antwortet, ist kein gesundes Multipass: Exit 1,
      // damit Skripte und die Lifecycle-Stage00 nicht auf einem toten Daemon weiterbauen.
      foreach (var line in Lines(report.RawVersionOutput ?? string.Empty))
      {
        await events.Emit(events.Std(line));
      }

      throw new ProgramExecutionException(
          1,
          $"{report.Error} — Dienst neu starten mit `cb multipass-restart`.");
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
  /// wird die DLL als erstes Argument mitgegeben; läuft sie eingebettet in einem
  /// fremden Host (Testhost), wird die CLI neben der Assembly gestartet.
  /// </summary>
  private static async Task<int> RunElevatedAsync(string timeout, CancellationToken cancellationToken)
  {
    var processPath = Environment.ProcessPath
        ?? throw new ProgramExecutionException(1, "Pfad der laufenden CLI unbekannt — erhöhter Neustart nicht möglich.");
    var cliAssembly = typeof(Program).Assembly.Location;
    var processName = Path.GetFileNameWithoutExtension(processPath);
    var (fileName, passAssembly) = processName switch
    {
      _ when string.Equals(processName, "dotnet", StringComparison.OrdinalIgnoreCase) => (processPath, true),
      _ when string.Equals(processName, Path.GetFileNameWithoutExtension(cliAssembly), StringComparison.OrdinalIgnoreCase) => (processPath, false),
      // Eingebettet (z. B. Program.Run im Testhost): nicht den fremden Host
      // neu starten, sondern die CLI neben der Assembly.
      _ when File.Exists(Path.ChangeExtension(cliAssembly, ".exe")) => (Path.ChangeExtension(cliAssembly, ".exe"), false),
      _ => ("dotnet", true)
    };
    var startInfo = new System.Diagnostics.ProcessStartInfo
    {
      FileName = fileName,
      UseShellExecute = true,
      Verb = "runas",
      WorkingDirectory = Environment.CurrentDirectory
    };
    if (passAssembly)
    {
      startInfo.ArgumentList.Add(cliAssembly);
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
      result = await WithProgress(
          context,
          () => vms.Create(
              options.Name,
              launchImage,
              options.Disk,
              cloudInit,
              waitSlice,
              () => context.Events.Ask(context.Events.Choice<YesNoOptions>(
                  $"VM `{options.Name}` is not ready after {stopwatch.Elapsed.TotalSeconds:F0} s — keep waiting?")) == YesNoOptions.Yes));
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

    if (cloudInit is not null)
    {
      // Erst wenn die Provisionierung nachweislich fertig ist, gibt es den
      // Snapshot — sonst hielte er eine halbe Installation fest, und weil
      // runcmd nur einmal je Instanz läuft, holt kein späterer Start sie nach.
      await VerifyProvisioning(context, options.Name, profile);
      await OfferSnapshotAfterConfiguration(context, options.Name, profile?.Name ?? "provisioned", options.Snapshot);
    }
  }

  /// <summary>
  /// Wartet nach dem Launch, bis cloud-init im Gast fertig ist, und verlangt
  /// dann die <c>.ready</c>-Marker aller Profil-Komponenten mit Skript. Ein
  /// fehlender Marker nach beendetem cloud-init heißt: diese Komponente ist
  /// gescheitert — Exit 1, die VM läuft zur Diagnose weiter, kein Snapshot.
  /// Ohne Profil (nur <c>--cloud-init</c>) sind keine Komponenten bekannt; dann
  /// wird nur auf cloud-init gewartet.
  /// </summary>
  private static async Task VerifyProvisioning(CommandContext context, string name, InitProfile? profile)
  {
    var events = context.Events;
    var waitMs = (int)context.Config.DefaultTimeoutVmCreation.ReadTime().TotalMilliseconds;
    const string waitLabel = "cloud-init: warten, bis die Gast-Provisionierung fertig ist";
    MultipassRunResult waited;
    try
    {
      waited = await RunStep(
          context,
          waitLabel,
          () => context.Vms.Exec(name, InitInstall.WaitForCloudInitCommand, waitMs, KeepWaiting(context, waitLabel)));
    }
    catch (MultipassTimeoutException)
    {
      throw new ProgramExecutionException(
          1,
          $"Stopped waiting — cloud-init keeps provisioning `{name}` in the background; no snapshot was taken. Check later with `cb vm-exec -n {name} -c \"cloud-init status\"`.");
    }

    if (waited.ExitCode != 0)
    {
      throw new ProgramExecutionException(
          waited.ExitCode,
          $"vm-create {name}: waiting for cloud-init failed (exit {waited.ExitCode}). The VM stays running for diagnosis; no snapshot was taken.");
    }

    if (profile is null)
    {
      await events.Emit(events.Std("Provisionierung: cloud-init fertig (ohne Profil keine Komponenten zu prüfen)."));
      return;
    }

    var markers = InitInstall.Markers(context.Config.Images, profile.Components);
    var missing = new List<(string Component, string Marker)>();
    foreach (var (component, marker) in markers)
    {
      var probe = await Task.Run(
          () => context.Vms.Exec(name, InitInstall.MarkerProbe(marker), context.DefaultTimeoutMs),
          context.CancellationToken);
      if (probe.ExitCode != 0)
      {
        missing.Add((component, marker));
      }
    }

    if (missing.Count > 0)
    {
      throw new ProgramExecutionException(
          1,
          $"vm-create {name}: cloud-init finished, but {string.Join(", ", missing.Select(m => $"`{m.Component}`"))} did not set "
          + $"{(missing.Count == 1 ? "its marker" : "their markers")} ({string.Join(", ", missing.Select(m => m.Marker))}) — the provisioning is incomplete. "
          + $"The VM stays running for diagnosis (`cb vm-exec -n {name} -c \"sudo tail -n 50 /var/log/cloud-init-output.log\"`); no snapshot was taken.");
    }

    await events.Emit(events.Std(markers.Count == 0
        ? "Provisionierung: cloud-init fertig (keine Komponente mit Marker)."
        : $"Provisionierung: OK ({string.Join(", ", markers.Select(m => m.Component))})"));
  }

  private static async Task RunVmExec(CommandContext context, VmExecOptions options)
  {
    await EnsureRunning(context, options.Name);

    var result = await WithProgress(
        context,
        () => context.Vms.Exec(options.Name, options.Command, context.DefaultTimeoutMs));
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
  /// <param name="ask">
  /// <c>false</c>: ohne Rückfrage starten — für Befehle, die eine laufende VM
  /// ausdrücklich voraussetzen (<c>vm-install</c>).
  /// </param>
  private static async Task EnsureRunning(CommandContext context, string name, bool ask = true)
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

    if (ask && !await context.ConfirmAsync($"VM `{name}` is {vm.State} — start it now?"))
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

  /// <summary>Abstand der Fortschritts-Ticks während einer lang laufenden Multipass-Aktion.</summary>
  private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(200);

  /// <summary>
  /// Führt eine blockierende Multipass-Aktion auf dem Threadpool aus und meldet
  /// währenddessen alle <see cref="ProgressInterval"/> einen Tick, den der
  /// <see cref="ConsoleAdapter"/> als Spinner zeichnet. Solange eine Rückfrage
  /// offen ist (etwa „weiter warten?“ aus <c>keepWaiting</c>), pausiert er —
  /// sonst schriebe er in die Eingabezeile.
  /// </summary>
  private static async Task<T> WithProgress<T>(CommandContext context, Func<T> action)
  {
    var events = context.Events;
    var work = Task.Run(action, context.CancellationToken);
    while (await Task.WhenAny(work, Task.Delay(ProgressInterval, context.CancellationToken)) != work)
    {
      if (events.OpenInteractionCount == 0)
      {
        await events.Emit(events.Tick());
      }
    }

    return await work;
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
    MultipassRunResult result;
    try
    {
      result = await WithProgress(context, action);
    }
    catch (MultipassTimeoutException)
    {
      // Hier endet ein Timeout nur, wenn der Benutzer "weiter warten?" verneint
      // hat — der Daemon arbeitet weiter, der Dienst hängt nicht. Exit 1, weil
      // ein folgender Schritt (Löschen nach Stopp, exec nach Start) nicht auf
      // einem unfertigen Zustand aufsetzen darf.
      throw new ProgramExecutionException(
          1,
          $"Stopped waiting — Multipass keeps running `{label}` in the background (check with `cb vm-list`).");
    }

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
    var result = await WithProgress(
        context,
        () => context.Vms.Put(options.Source, options.Name, options.Destination, context.DefaultTimeoutMs));
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
    var result = await WithProgress(
        context,
        () => context.Vms.Get(options.Name, options.Remote, options.Destination, context.DefaultTimeoutMs));
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

  /// <summary>
  /// Klont eine gestoppte VM (<c>multipass clone</c>); der Klon bringt alles mit,
  /// was cloud-init in der Quelle installiert hat. Ein vorhandener Klon-Name wird
  /// wie bei vm-create nur nach Rückfrage ersetzt.
  /// </summary>
  private static async Task RunVmClone(CommandContext context, VmCloneOptions options)
  {
    var source = options.Source;
    var destination = string.IsNullOrWhiteSpace(options.Name) ? null : options.Name.Trim();
    if (destination is not null)
    {
      RequireValidName(destination);
      if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
      {
        throw new ProgramExecutionException(1, $"The clone needs a name other than its source `{source}`.");
      }
    }

    var vm = await RequireVm(context, source);
    await EnsureStopped(context, vm, "cloning");
    if (destination is not null)
    {
      await ReplaceExistingVm(context, destination);
    }

    // Ein Klon kopiert die ganze Disk — dafür ist DefaultTimeout zu knapp; wie
    // vm-create wird in Abschnitten von DefaultTimeoutVmCreation gewartet.
    var label = destination is null ? $"clone {source}" : $"clone {source} → {destination}";
    var waitMs = (int)context.Config.DefaultTimeoutVmCreation.ReadTime().TotalMilliseconds;
    MultipassRunResult result;
    try
    {
      result = await RunReported(
          context,
          label,
          () => context.Vms.Clone(source, destination, waitMs, KeepWaiting(context, label)));
    }
    catch (MultipassTimeoutException)
    {
      await context.Events.Emit(context.Events.Std(
          "Stopped waiting — Multipass keeps cloning in the background (check with `cb vm-list`)."));
      return;
    }

    var cloned = destination ?? ParseCloneName(result.StdOut);
    await context.Events.Emit(context.Events.Std(cloned is null
        ? $"{label}: OK ({result.DurationMs / 1000.0:F1} s)"
        : await Completed(context.Vms, cloned, destination is null ? $"{label} → {cloned}" : label, result, context.CancellationToken)));
    await context.Events.Emit(context.Events.Std($"Source `{source}` stays stopped — start it with `cb vm-start -n {source}`."));
  }

  /// <summary>Klon-Name aus „Cloned from &lt;source&gt; to &lt;name&gt;.“; <c>null</c>, wenn die Zeile fehlt.</summary>
  private static string? ParseCloneName(string stdOut)
  {
    var match = System.Text.RegularExpressions.Regex.Match(stdOut, @"\bto\s+""?([A-Za-z][A-Za-z0-9-]*)");
    return match.Success ? match.Groups[1].Value : null;
  }

  /// <summary>Sichert den Stand einer gestoppten VM (<c>multipass snapshot</c>).</summary>
  private static async Task RunVmSnapshot(CommandContext context, VmSnapshotOptions options)
  {
    var name = options.Name;
    var snapshot = string.IsNullOrWhiteSpace(options.Snapshot) ? null : options.Snapshot.Trim();
    if (snapshot is not null)
    {
      RequireValidName(snapshot);
    }

    var vm = await RequireVm(context, name);
    if (snapshot is not null)
    {
      var existing = await Task.Run(() => context.Vms.ListSnapshots(name, context.DefaultTimeoutMs), context.CancellationToken);
      if (existing.Any(s => string.Equals(s.Name, snapshot, StringComparison.OrdinalIgnoreCase)))
      {
        throw new ProgramExecutionException(
            1,
            $"VM `{name}` already has a snapshot `{snapshot}` — pick another name (existing: {string.Join(", ", existing.Select(s => s.Name))}).");
      }
    }

    await EnsureStopped(context, vm, "the snapshot");

    var label = snapshot is null ? $"snapshot {name}" : $"snapshot {name}.{snapshot}";
    var result = await RunReported(
        context,
        label,
        () => context.Vms.Snapshot(name, snapshot, options.Comment, context.DefaultTimeoutMs, KeepWaiting(context, label)));
    await context.Events.Emit(context.Events.Std(await Completed(context.Vms, name, label, result, context.CancellationToken)));
  }

  private static async Task RunVmSnapshots(CommandContext context, VmSnapshotsOptions options)
  {
    var events = context.Events;
    var name = string.IsNullOrWhiteSpace(options.Name) ? null : options.Name.Trim();
    if (name is not null)
    {
      await RequireVm(context, name);
    }

    var snapshots = await Task.Run(() => context.Vms.ListSnapshots(name, context.DefaultTimeoutMs), context.CancellationToken);
    if (snapshots.Count == 0)
    {
      await events.Emit(events.Std(name is null
          ? "No snapshots — take one with `cb vm-snapshot -n <vm>`."
          : $"VM `{name}` has no snapshots — take one with `cb vm-snapshot -n {name}`."));
      return;
    }

    await events.Emit(events.Std($"{"VM",-30} {"Snapshot",-20} {"Parent",-20} Comment"));
    foreach (var snapshot in snapshots)
    {
      await events.Emit(events.Std($"{snapshot.Instance,-30} {snapshot.Name,-20} {(string.IsNullOrEmpty(snapshot.Parent) ? "--" : snapshot.Parent),-20} {snapshot.Comment}"));
    }
  }

  /// <summary>
  /// Setzt eine gestoppte VM auf einen Snapshot zurück. Der aktuelle Stand wird
  /// vorher als Snapshot gesichert, außer der Benutzer verwirft ihn ausdrücklich —
  /// ohne interaktive Eingabe (Antwort Nein) wird also immer gesichert.
  /// </summary>
  private static async Task RunVmRestore(CommandContext context, VmRestoreOptions options)
  {
    var events = context.Events;
    var (name, snapshot) = (options.Name, options.Snapshot);
    var vm = await RequireVm(context, name);
    var snapshots = await Task.Run(() => context.Vms.ListSnapshots(name, context.DefaultTimeoutMs), context.CancellationToken);
    if (!snapshots.Any(s => string.Equals(s.Name, snapshot, StringComparison.OrdinalIgnoreCase)))
    {
      throw new ProgramExecutionException(
          1,
          $"VM `{name}` has no snapshot `{snapshot}`. "
          + (snapshots.Count == 0
              ? $"Take one with `cb vm-snapshot -n {name}`."
              : $"Existing snapshots: {string.Join(", ", snapshots.Select(s => s.Name))}."));
    }

    await EnsureStopped(context, vm, "the restore");

    if (!await context.ConfirmAsync($"Discard the current state of `{name}` without a backup snapshot?"))
    {
      var backupLabel = $"snapshot {name} (backup before restore)";
      var backup = await RunReported(
          context,
          backupLabel,
          () => context.Vms.Snapshot(
              name,
              comment: $"Backup before restore to {snapshot}",
              timeoutMs: context.DefaultTimeoutMs,
              keepWaiting: KeepWaiting(context, backupLabel)));
      await events.Emit(events.Std($"{backupLabel}: OK ({backup.DurationMs / 1000.0:F1} s)"));
    }

    var label = $"restore {name}.{snapshot}";
    var result = await RunReported(
        context,
        label,
        () => context.Vms.Restore(name, snapshot, context.DefaultTimeoutMs, KeepWaiting(context, label)));
    await events.Emit(events.Std(await Completed(context.Vms, name, label, result, context.CancellationToken)));
  }

  /// <summary>
  /// Vergrößert die Disk einer VM (<c>multipass set local.&lt;vm&gt;.disk</c>). Multipass
  /// ändert nur gestoppte VMs und verkleinert nie — beides wird geprüft, bevor eine
  /// laufende VM (nach Rückfrage) gestoppt wird; sie startet danach wieder, und der
  /// Gast zieht seine Root-Partition beim Boot per cloud-init <c>growpart</c> nach.
  /// </summary>
  private static async Task RunVmResize(CommandContext context, VmResizeOptions options)
  {
    var events = context.Events;
    var name = options.Name;
    var size = options.Disk.Trim();
    if (!MultipassSize.TryParse(size, out var requested))
    {
      throw new ProgramExecutionException(
          1,
          $"Invalid disk size `{size}` — use a number followed by K, M or G, e.g. 20G or 0.5G.");
    }

    var vm = await RequireVm(context, name);
    if (vm.IsDeleted)
    {
      throw InTrash(name);
    }

    var current = await Task.Run(() => context.Vms.GetDisk(name, context.DefaultTimeoutMs), context.CancellationToken);
    if (MultipassSize.TryParse(current, out var currentBytes))
    {
      if (requested < currentBytes)
      {
        throw new ProgramExecutionException(
            1,
            $"VM `{name}` has a {current} disk — Multipass can only grow it, not shrink it to {size}.");
      }

      if (requested == currentBytes)
      {
        await events.Emit(events.Std($"resize {name}: OK (disk is already {current}) — State: {vm.State}"));
        return;
      }
    }

    var wasRunning = !vm.IsStopped;
    await EnsureStopped(context, vm, "the resize");

    var label = $"resize {name} disk {current} -> {size}";
    var result = await RunReported(
        context,
        label,
        () => context.Vms.SetDisk(name, size, context.DefaultTimeoutMs, KeepWaiting(context, label)));
    var resized = await Task.Run(() => context.Vms.GetDisk(name, context.DefaultTimeoutMs), context.CancellationToken);
    await events.Emit(events.Std($"{label}: OK ({result.DurationMs / 1000.0:F1} s) — disk now {resized}"));

    if (!wasRunning)
    {
      await events.Emit(events.Std($"VM `{name}` stays stopped — the guest grows its root partition to the new size on the next start (`cb vm-start -n {name}`)."));
      return;
    }

    await RunVmStateCommand(
        context,
        name,
        $"start {name}",
        () => context.Vms.Start(name, context.DefaultTimeoutMs, KeepWaiting(context, $"start {name}")));
    await events.Emit(events.Std($"cloud-init grows the root partition while booting — check with `cb vm-exec -n {name} -c \"df -h /\"`."));
  }

  /// <summary>
  /// Schickt <c>main</c> eines Host-Repositorys in eine VM (wiederholbar): prüft die git-Komponente,
  /// legt das Bare-Repo an, bringt <c>main</c> samt Historie als Bundle hinein (beim
  /// nächsten Mal nur, was neu ist) und klont bzw. fetcht die Arbeitskopie. Fehlt die
  /// git-Komponente, wird nichts angelegt (fail closed).
  /// </summary>
  private static async Task RunRepoSend(CommandContext context, RepoSendOptions options)
  {
    var events = context.Events;
    var name = options.Name;
    var (root, repo) = await ResolveRepo(context, options.Repo);
    var hostMain = await RunGit(context, root, "rev-parse", "--verify", "--quiet", SandboxRepo.MainRef);
    if (hostMain.ExitCode != 0)
    {
      throw new ProgramExecutionException(1, $"Repository `{root}` has no branch `main` — repo-send transfers `main`.");
    }

    await EnsureRunning(context, name);
    await RequireGitComponent(context, name);

    var bare = SandboxRepo.BareDir(repo);
    await RunVmStep(context, name, $"bare repo {bare}", SandboxRepo.InitBareCommand(repo));

    var vmRefs = RequireVmSuccess($"refs of {name}", await VmQuery(context, name, SandboxRepo.ListRefsCommand(repo, SandboxRepo.MainRef)));
    var vmMain = SandboxRepo.ParseRefs(vmRefs.StdOut).GetValueOrDefault(SandboxRepo.MainRef);
    if (vmMain == hostMain.StdOut.Trim())
    {
      await events.Emit(events.Std($"main in {name}: up to date ({vmMain[..7]})"));
    }
    else
    {
      // Nur das Neue bündeln, wenn der Host den Stand der VM kennt; sonst alles (git
      // lehnt beim Einlesen ab, was kein Vorspulen ist — wie ein push ohne --force).
      var incremental = vmMain is not null && await HostHasCommit(context, root, vmMain);
      string[] range = incremental ? ["main", $"^{vmMain}"] : ["main"];
      await TransferBundleToVm(context, root, name, repo, range, incremental ? "new commits of main" : "main with full history");
    }

    await RunVmStep(context, name, $"workspace {SandboxRepo.WorkDir(repo)}", SandboxRepo.WorkspaceCommand(name, repo));
    var work = SandboxRepo.WorkDir(repo);
    await events.Emit(events.Std($"main of `{repo}` is in `{name}`: {work}."));
    await events.Emit(events.Std(
        $"Work there on a branch claude/<id> and push it to origin; bring it back with `cb repo-receive -n {name} -i <id>` (from a checkout with main checked out)."));
  }

  /// <summary>Bundle auf dem Host packen, per transfer in die VM legen und dort ins Bare-Repo einlesen; temporäre Dateien werden auf beiden Seiten entfernt.</summary>
  private static async Task TransferBundleToVm(CommandContext context, string root, string name, string repo, string[] range, string what)
  {
    var local = Path.Combine(Path.GetTempPath(), $"corebridge-{Guid.NewGuid():N}.bundle");
    var remote = SandboxRepo.VmBundlePath(repo, "main");
    try
    {
      await RunGitReported(context, root, $"git bundle ({what})", ["bundle", "create", "-q", local, .. range]);
      await RunTransfer(context, $"transfer bundle ({new FileInfo(local).Length / 1024.0:F1} KiB) → {name}", () => context.Vms.Put(local, name, remote, TransferTimeoutMs(context)));
      await RunVmStep(context, name, $"main → {SandboxRepo.BareDir(repo)}", SandboxRepo.FetchMainBundleCommand(repo, remote));
    }
    finally
    {
      File.Delete(local);
    }
  }

  /// <summary>
  /// Holt <c>claude/&lt;id&gt;</c> als Bundle und spult <c>main</c> darauf vor (<c>merge
  /// --ff-only</c>) — nur nach Rückfrage (ohne Konsole: nein) oder mit <c>--yes</c>. Geht
  /// das nicht, weil main eigene Commits hat, bleibt alles unverändert. Ohne Id: Liste
  /// der Task-Branches.
  /// </summary>
  private static async Task RunRepoReceive(CommandContext context, RepoReceiveOptions options)
  {
    var events = context.Events;
    var name = options.Name;
    var (root, repo) = await ResolveRepo(context, options.Repo);
    var id = string.IsNullOrWhiteSpace(options.Id) ? null : options.Id.Trim();
    if (id is not null)
    {
      if (!SandboxRepo.IsValidId(id))
      {
        throw new ProgramExecutionException(1, $"Invalid id `{id}` — use letters, digits, `.`, `_` and `-` (e.g. 20260927-1432).");
      }

      await RequireCleanMain(context, root);
    }

    await EnsureRunning(context, name);
    if ((await VmQuery(context, name, SandboxRepo.BareExistsCommand(repo))).ExitCode != 0)
    {
      throw new ProgramExecutionException(1, $"VM `{name}` has no repository `{repo}` yet — send it first with `cb repo-send -n {name}`.");
    }

    if (id is null)
    {
      await ListTaskBranches(context, name, repo);
      return;
    }

    var branch = SandboxRepo.TaskBranch(id);
    var vmRefs = RequireVmSuccess(
        $"refs of {name}",
        await VmQuery(context, name, SandboxRepo.ListRefsCommand(repo, SandboxRepo.MainRef, SandboxRepo.TaskRef(id))));
    var refs = SandboxRepo.ParseRefs(vmRefs.StdOut);
    if (!refs.TryGetValue(SandboxRepo.TaskRef(id), out var taskCommit))
    {
      throw new ProgramExecutionException(1, $"VM `{name}` has no branch {branch} — list the ids with `cb repo-receive -n {name}`.");
    }

    var tracking = SandboxRepo.TrackingRef(name, id);
    if (await HostHasCommit(context, root, taskCommit))
    {
      // Schon einmal geholt (oder nichts Neues): kein Transfer nötig.
      RequireGitSuccess($"update-ref {tracking}", await RunGit(context, root, "update-ref", tracking, taskCommit));
      await events.Emit(events.Std($"{branch}: already on the host ({taskCommit[..7]})"));
    }
    else
    {
      // Nur bündeln, was über den main der VM hinausgeht — sofern der Host ihn kennt.
      var vmMain = refs.GetValueOrDefault(SandboxRepo.MainRef);
      var exclude = vmMain is not null && await HostHasCommit(context, root, vmMain) ? vmMain : null;
      await TransferBundleFromVm(context, root, name, repo, id, exclude, tracking);
    }

    if (await IsAncestor(context, root, tracking, SandboxRepo.MainRef))
    {
      await events.Emit(events.Std($"Nothing new — {branch} is already contained in main."));
      return;
    }

    // Nur Vorspulen: main muss Vorfahr des Task-Branches sein. Hat main seit dem Task-Start
    // eigene Commits bekommen, wird nichts verändert — kein Merge-Commit, kein Konflikt.
    if (!await IsAncestor(context, root, SandboxRepo.MainRef, tracking))
    {
      throw new ProgramExecutionException(
          1,
          $"main has commits that {branch} doesn't have — a fast-forward isn't possible, nothing was changed. "
              + $"Rebase the task onto the current main in the VM (send it first with `cb repo-send -n {name}`), or merge by hand: `git merge {tracking}`.");
    }

    var count = RequireGitSuccess("rev-list --count", await RunGit(context, root, "rev-list", "--count", $"main..{tracking}")).StdOut.Trim();
    await EmitGit(context, await RunGit(context, root, "log", "--oneline", "--no-decorate", "--reverse", $"main..{tracking}"));
    await EmitGit(context, await RunGit(context, root, "diff", "--stat", "main", tracking));
    if (options.Diff)
    {
      await EmitGit(context, await RunGit(context, root, "diff", "main", tracking));
    }

    var commits = count == "1" ? "1 commit" : $"{count} commits";
    if (!options.Yes && !await context.ConfirmAsync($"Fast-forward main by {commits} from {branch}?"))
    {
      await events.Emit(events.Std($"Not merged — run again with `--yes` or answer yes: `cb repo-receive -n {name} -i {id}`."));
      return;
    }

    var merge = await RunGit(context, root, "merge", "--ff-only", "-q", tracking);
    if (merge.ExitCode != 0)
    {
      throw new ProgramExecutionException(1, $"`git merge --ff-only {tracking}` failed: {FirstLine(merge.StdErr, merge.StdOut)} — main is unchanged.");
    }

    await events.Emit(events.Std($"main fast-forwarded to {taskCommit[..7]} ({commits} from {branch})."));
  }

  /// <summary>Bundle von <c>claude/&lt;id&gt;</c> in der VM packen, per transfer holen und nach <paramref name="tracking"/> einlesen; temporäre Dateien werden auf beiden Seiten entfernt.</summary>
  private static async Task TransferBundleFromVm(CommandContext context, string root, string name, string repo, string id, string? exclude, string tracking)
  {
    var branch = SandboxRepo.TaskBranch(id);
    var local = Path.Combine(Path.GetTempPath(), $"corebridge-{Guid.NewGuid():N}.bundle");
    var remote = SandboxRepo.VmBundlePath(repo, id);
    try
    {
      await RunVmStep(context, name, $"git bundle {branch} in {name}", SandboxRepo.CreateTaskBundleCommand(repo, id, remote, exclude));
      await RunTransfer(context, $"transfer bundle {name} → host", () => IgnoreLocalPermissionError(context.Vms.Get(name, remote, local, TransferTimeoutMs(context)), local));
      await RunGitReported(context, root, $"git fetch bundle {branch} → {tracking}", ["fetch", "-q", local, $"+{SandboxRepo.TaskRef(id)}:{tracking}"]);
    }
    finally
    {
      File.Delete(local);
      await VmQuery(context, name, $"rm -f {Shell.Quote(remote)}");
    }
  }

  private static async Task ListTaskBranches(CommandContext context, string name, string repo)
  {
    var events = context.Events;
    var refs = RequireVmSuccess($"refs of {name}", await VmQuery(context, name, SandboxRepo.ListRefsCommand(repo, "refs/heads/claude/")));
    var ids = SandboxRepo.ParseRefs(refs.StdOut).Keys
        .Where(reference => reference.StartsWith("refs/heads/claude/", StringComparison.Ordinal))
        .Select(reference => reference["refs/heads/claude/".Length..])
        .ToArray();
    if (ids.Length == 0)
    {
      await events.Emit(events.Std($"No claude/* branches in `{name}` yet."));
      return;
    }

    await events.Emit(events.Std($"Task branches in `{name}` (receive one with `cb repo-receive -n {name} -i <id>`):"));
    foreach (var taskId in ids)
    {
      await events.Emit(events.Std($"  {taskId}"));
    }
  }

  /// <summary>Vorspulen nur auf ausgechecktem <c>main</c> ohne Änderungen an getrackten Dateien.</summary>
  private static async Task RequireCleanMain(CommandContext context, string root)
  {
    var head = await RunGit(context, root, "symbolic-ref", "--short", "-q", "HEAD");
    if (head.ExitCode != 0 || head.StdOut.Trim() != "main")
    {
      throw new ProgramExecutionException(1, $"`{root}` is not on main (HEAD: {(head.ExitCode == 0 ? head.StdOut.Trim() : "detached")}) — repo-receive fast-forwards main; switch with `git switch main`.");
    }

    var status = await RunGit(context, root, "status", "--porcelain", "--untracked-files=no");
    RequireGitSuccess("status", status);
    if (GitLines(status.StdOut).Any())
    {
      throw new ProgramExecutionException(1, $"`{root}` has uncommitted changes — commit or stash them before repo-receive.");
    }
  }

  /// <summary>git-Komponente in der VM (Marker und git im PATH); sonst der Hinweis auf vm-install.</summary>
  private static async Task RequireGitComponent(CommandContext context, string name)
  {
    if ((await VmQuery(context, name, SandboxRepo.ReadyCommand())).ExitCode != 0)
    {
      throw new ProgramExecutionException(1, $"VM `{name}` has no git component yet — install it with `cb vm-install -n {name} -c git`.");
    }

    await context.Events.Emit(context.Events.Std($"git component in {name}: OK"));
  }

  /// <summary><c>git merge-base --is-ancestor</c>: ist <paramref name="ancestor"/> in <paramref name="descendant"/> enthalten?</summary>
  private static async Task<bool> IsAncestor(CommandContext context, string root, string ancestor, string descendant)
  {
    var result = await RunGit(context, root, "merge-base", "--is-ancestor", ancestor, descendant);
    return result.ExitCode switch
    {
      0 => true,
      1 => false,
      _ => throw new ProgramExecutionException(1, $"`git merge-base --is-ancestor {ancestor} {descendant}` failed: {FirstLine(result.StdErr, result.StdOut)}")
    };
  }

  private static async Task<bool> HostHasCommit(CommandContext context, string root, string commit)
  {
    return (await RunGit(context, root, "cat-file", "-e", $"{commit}^{{commit}}")).ExitCode == 0;
  }

  /// <summary>Host-Repository zu <paramref name="path"/> (Standard: aktuelles Verzeichnis); Name aus dem gemeinsamen .git — in einem Worktree also der des Haupt-Repos.</summary>
  private static async Task<(string Root, string Name)> ResolveRepo(CommandContext context, string? path)
  {
    var start = Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? Environment.CurrentDirectory : path);
    if (!Directory.Exists(start))
    {
      throw new ProgramExecutionException(1, $"Folder `{start}` does not exist.");
    }

    var top = await RunGit(context, start, "rev-parse", "--show-toplevel");
    if (top.ExitCode != 0)
    {
      throw new ProgramExecutionException(1, $"`{start}` is not inside a git repository.");
    }

    var common = await RunGit(context, start, "rev-parse", "--path-format=absolute", "--git-common-dir");
    RequireGitSuccess("rev-parse --git-common-dir", common);
    var commonDir = Path.GetFullPath(common.StdOut.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    var name = Path.GetFileName(commonDir) == ".git"
        ? Path.GetFileName(Path.GetDirectoryName(commonDir))!
        : Path.GetFileNameWithoutExtension(commonDir);
    if (!SandboxRepo.IsValidRepoName(name))
    {
      throw new ProgramExecutionException(1, $"Repository name `{name}` can't be used in the VM — use letters, digits, `.`, `_` and `-`.");
    }

    return (Path.GetFullPath(top.StdOut.Trim()), name);
  }

  private static Task<GitRunResult> RunGit(CommandContext context, string root, params string[] args)
  {
    return Task.Run(() => context.Git.Run(args, root, context.DefaultTimeoutMs), context.CancellationToken);
  }

  /// <summary>Lange git-Aufrufe (bundle/fetch): mit Fortschritt und <c>DefaultTimeoutVmCreation</c>; bei Exit ≠ 0 die fertige Meldung.</summary>
  private static async Task<GitRunResult> RunGitReported(CommandContext context, string root, string label, params string[] args)
  {
    await context.Events.Emit(context.Events.Std($"{label} ..."));
    var result = await WithProgress(context, () => context.Git.Run(args, root, TransferTimeoutMs(context)));
    RequireGitSuccess(label, result);
    await context.Events.Emit(context.Events.Std($"{label}: OK ({result.DurationMs / 1000.0:F1} s)"));
    return result;
  }

  /// <summary>Shell-Zeile in der VM (multipass exec); bei Exit ≠ 0 die fertige Meldung.</summary>
  private static async Task RunVmStep(CommandContext context, string name, string label, string command)
  {
    var result = await RunReported(context, label, () => context.Vms.Exec(name, command, TransferTimeoutMs(context)));
    await context.Events.Emit(context.Events.Std($"{label}: OK ({result.DurationMs / 1000.0:F1} s)"));
  }

  /// <summary>Kurze Abfrage in der VM ohne Ausgabe; der Aufrufer wertet Exit-Code und stdout aus.</summary>
  private static Task<MultipassRunResult> VmQuery(CommandContext context, string name, string command)
  {
    return Task.Run(() => context.Vms.Exec(name, command, context.DefaultTimeoutMs), context.CancellationToken);
  }

  private static MultipassRunResult RequireVmSuccess(string label, MultipassRunResult result)
  {
    if (result.ExitCode != 0)
    {
      throw new ProgramExecutionException(1, $"`{label}` failed (exit {result.ExitCode}): {FirstLine(result.StdErr, result.StdOut)}");
    }

    return result;
  }

  /// <summary>multipass transfer eines Bundles, mit Fortschritt; bei Exit ≠ 0 die fertige Meldung.</summary>
  private static async Task RunTransfer(CommandContext context, string label, Func<MultipassRunResult> transfer)
  {
    var result = await RunReported(context, label, transfer);
    await context.Events.Emit(context.Events.Std($"{label}: OK ({result.DurationMs / 1000.0:F1} s)"));
  }

  /// <summary>
  /// Multipass holt Dateien unter Windows vollständig, meldet dann aber Exit 2, weil es
  /// die Unix-Rechte nicht setzen kann („cannot set permissions for local file“). Nur
  /// genau das gilt als Erfolg; ob die Datei heil ist, prüft danach git selbst.
  /// </summary>
  private static MultipassRunResult IgnoreLocalPermissionError(MultipassRunResult result, string localPath)
  {
    var onlyPermissions = result.ExitCode != 0
        && File.Exists(localPath)
        && Lines(result.StdErr).Any()
        && Lines(result.StdErr).All(line => line.Contains("cannot set permissions for local file", StringComparison.Ordinal));
    return onlyPermissions ? result with { ExitCode = 0, StdErr = string.Empty } : result;
  }

  /// <summary>Bundles mit ganzer Historie können dauern: <c>DefaultTimeoutVmCreation</c> statt <c>DefaultTimeout</c>.</summary>
  private static int TransferTimeoutMs(CommandContext context)
  {
    return (int)context.Config.DefaultTimeoutVmCreation.ReadTime().TotalMilliseconds;
  }

  private static GitRunResult RequireGitSuccess(string label, GitRunResult result)
  {
    if (result.ExitCode != 0)
    {
      throw new ProgramExecutionException(1, $"`{label}` failed (exit {result.ExitCode}): {FirstLine(result.StdErr, result.StdOut)}");
    }

    return result;
  }

  private static async Task EmitGit(CommandContext context, GitRunResult result)
  {
    foreach (var line in GitLines(result.StdOut).Concat(GitLines(result.StdErr)))
    {
      await context.Events.Emit(context.Events.Std("  " + line));
    }
  }

  private static IEnumerable<string> GitLines(string text)
  {
    return text.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0);
  }

  private static string FirstLine(params string[] texts)
  {
    return texts.SelectMany(GitLines).FirstOrDefault() ?? "no output";
  }

  /// <summary>
  /// Installiert Komponenten in eine vorhandene VM — dieselben Schritte wie
  /// cloud-init beim Anlegen (<see cref="InitInstall.Plan"/>). Der erste
  /// fehlgeschlagene Schritt beendet den Befehl; die VM läuft dann zur Diagnose
  /// weiter. Nach Erfolg wird ein Snapshot empfohlen.
  /// </summary>
  private static async Task RunVmInstall(CommandContext context, VmInstallOptions options)
  {
    var events = context.Events;
    var name = options.Name;
    var components = options.Components.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (components.Length == 0)
    {
      throw new ProgramExecutionException(1, "No components given — e.g. `-c dotnet,python`.");
    }

    if (options.Snapshot is not null)
    {
      RequireValidName(options.Snapshot);
    }

    var steps = InitInstall.Plan(context.Config.Images, components);
    await EnsureRunning(context, name, ask: false);

    // apt und snap brauchen Minuten: gewartet wird in Abschnitten von
    // DefaultTimeoutVmCreation, danach fragt die CLI „weiter warten?“.
    var waitMs = (int)context.Config.DefaultTimeoutVmCreation.ReadTime().TotalMilliseconds;
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    for (var i = 0; i < steps.Count; i++)
    {
      var step = steps[i];
      var label = $"[{i + 1}/{steps.Count}] {step.Title}";
      MultipassRunResult result;
      string? upload = null;
      try
      {
        if (step.IsUpload)
        {
          upload = Path.Combine(Path.GetTempPath(), $"corebridge-{Guid.NewGuid():N}.sh");
          await File.WriteAllTextAsync(upload, InitInstall.ReadScript(step.ScriptSource!), context.CancellationToken);
          result = await RunStep(context, label, () => context.Vms.Put(upload, name, step.RemotePath, waitMs));
        }
        else
        {
          result = await RunStep(context, label, () => context.Vms.Exec(name, step.Command!, waitMs, KeepWaiting(context, label)));
        }
      }
      catch (MultipassTimeoutException)
      {
        throw new ProgramExecutionException(
            1,
            $"Stopped waiting — `{step.Title}` keeps running inside `{name}`. The VM stays running; check with `cb vm-exec -n {name} -c ...`.");
      }
      finally
      {
        if (upload is not null)
        {
          File.Delete(upload);
        }
      }

      if (result.ExitCode != 0)
      {
        var detail = step.Marker is not null
            ? $"the script ran but did not set its marker `{step.Marker}` — the installation counts as incomplete"
            : $"exit {result.ExitCode}";
        throw new ProgramExecutionException(
            result.ExitCode,
            $"vm-install {name}: step `{step.Title}` failed ({detail}). The VM stays running for diagnosis (`cb vm-exec -n {name} -c ...`); no snapshot was taken.");
      }
    }

    await events.Emit(events.Std($"install {string.Join(", ", components)} → {name}: OK ({stopwatch.Elapsed.TotalSeconds:F1} s)"));
    await OfferSnapshotAfterConfiguration(context, name, string.Join("-", components), options.Snapshot);
  }

  /// <summary>
  /// Ein Installationsschritt: ankündigen, mit Spinner ausführen, stdout und
  /// stderr zeigen. Anders als <see cref="RunReported"/> wirft er bei Exit ≠ 0
  /// nicht, damit <see cref="RunVmInstall"/> den Schritt in der Meldung nennen kann.
  /// </summary>
  private static async Task<MultipassRunResult> RunStep(CommandContext context, string label, Func<MultipassRunResult> action)
  {
    var events = context.Events;
    await events.Emit(events.Std($"{label} ..."));
    var result = await WithProgress(context, action);
    foreach (var line in Lines(result.StdOut).Concat(Lines(MultipassStdErr.Clean(result.StdErr))))
    {
      await events.Emit(events.Std($"    {line}"));
    }

    return result;
  }

  /// <summary>
  /// Empfehlung nach jeder Konfiguration: einen Snapshot anlegen, solange der
  /// Stand frisch ist und noch niemand verbunden hat. Mit
  /// <paramref name="explicitName"/> (<c>--snapshot</c>) ohne Rückfrage; sonst wird
  /// gefragt — ohne Konsole gilt Nein. Die VM wird dafür gestoppt und bleibt gestoppt.
  /// </summary>
  private static async Task OfferSnapshotAfterConfiguration(
      CommandContext context,
      string name,
      string suggestedName,
      string? explicitName)
  {
    var events = context.Events;
    if (explicitName is null
        && !await context.ConfirmAsync(
            $"Recommended: take a snapshot of `{name}` now that it is configured — a stable state before anyone connects (the VM gets stopped). Take one now?"))
    {
      await events.Emit(events.Std($"No snapshot — take one later with `cb vm-snapshot -n {name}`."));
      return;
    }

    var existing = await Task.Run(() => context.Vms.ListSnapshots(name, context.DefaultTimeoutMs), context.CancellationToken);
    string? snapshot = explicitName ?? (VmNamePattern.IsMatch(suggestedName) ? suggestedName : null);
    if (snapshot is not null && existing.Any(s => string.Equals(s.Name, snapshot, StringComparison.OrdinalIgnoreCase)))
    {
      if (explicitName is not null)
      {
        throw new ProgramExecutionException(
            1,
            $"VM `{name}` already has a snapshot `{snapshot}` — the installation is done, take a snapshot with another name via `cb vm-snapshot -n {name} -s <name>`.");
      }

      // Vorschlag vergeben: Multipass wählt dann snapshotN.
      snapshot = null;
    }

    var vm = await RequireVm(context, name);
    if (!vm.IsStopped)
    {
      await StopVm(context, vm);
    }

    var label = snapshot is null ? $"snapshot {name}" : $"snapshot {name}.{snapshot}";
    var result = await RunReported(
        context,
        label,
        () => context.Vms.Snapshot(name, snapshot, $"after configuration ({suggestedName})", context.DefaultTimeoutMs, KeepWaiting(context, label)));
    await events.Emit(events.Std(await Completed(context.Vms, name, label, result, context.CancellationToken)));
    await events.Emit(events.Std($"VM `{name}` stays stopped — start it with `cb vm-start -n {name}`."));
  }

  /// <summary>
  /// Clone, Snapshot und Restore gehen nur auf gestoppten VMs: eine laufende
  /// wird nach Rückfrage gestoppt, eine im Papierkorb abgewiesen.
  /// </summary>
  private static async Task EnsureStopped(CommandContext context, VmInfo vm, string purpose)
  {
    if (vm.IsDeleted)
    {
      throw InTrash(vm.Name);
    }

    if (vm.IsStopped)
    {
      return;
    }

    if (!await context.ConfirmAsync($"VM `{vm.Name}` is {vm.State} — stop it for {purpose}?"))
    {
      throw new ProgramExecutionException(1, $"Cancelled — {purpose} needs a stopped VM; `{vm.Name}` is {vm.State}.");
    }

    await StopVm(context, vm);
  }

  /// <summary>Kündigt das Kommando an, führt es aus und meldet stdout; bei Exit ≠ 0 folgt die fertige Fehlermeldung.</summary>
  private static async Task<MultipassRunResult> RunReported(
      CommandContext context,
      string label,
      Func<MultipassRunResult> action)
  {
    await context.Events.Emit(context.Events.Std($"{label} ..."));
    var result = await WithProgress(context, action);
    await Report(context.Events, result);
    return result;
  }

  private static void RequireValidName(string name)
  {
    if (!VmNamePattern.IsMatch(name))
    {
      throw new ProgramExecutionException(
          1,
          $"Invalid name `{name}` — use letters, digits and hyphens, starting with a letter and not ending with a hyphen (e.g. dev-01).");
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

  private static async Task RunComponentList(CommandContext context, ComponentListOptions options)
  {
    var events = context.Events;
    var entries = InitComponentStore.List(context.Config.Images, InitStore.UserSettingsFile);
    if (!string.IsNullOrWhiteSpace(options.Name))
    {
      var entry = entries.FirstOrDefault(e => string.Equals(e.Name, options.Name, StringComparison.OrdinalIgnoreCase))
          ?? throw new ProgramExecutionException(1, $"Unbekannte Komponente '{options.Name}'. Übersicht: `cb component-list`.");
      await events.Emit(events.Std(InitComponentStore.ToJson(entry.Component)));
      return;
    }

    if (entries.Count == 0)
    {
      await events.Emit(events.Std("Keine Komponenten konfiguriert."));
      return;
    }

    await events.Emit(events.Std($"{"Name",-16} {"Herkunft",-12} {"Snap",-18} {"Script",-6} Pakete"));
    foreach (var entry in entries)
    {
      var component = entry.Component;
      await events.Emit(events.Std(
          $"{entry.Name,-16} {(entry.Source == ComponentSource.User ? "benutzer" : "mitgeliefert"),-12} "
          + $"{component.Snap ?? "-",-18} {(string.IsNullOrWhiteSpace(component.Script) ? "-" : "ja"),-6} "
          + (component.Packages.Length == 0 ? "-" : string.Join(",", component.Packages))));
    }

    await events.Emit(events.Std(string.Empty));
    await events.Emit(events.Std($"Benutzerkomponenten: {InitStore.UserSettingsFile}"));
  }

  private static async Task RunComponentAdd(CommandContext context, ComponentAddOptions options)
  {
    var events = context.Events;
    var (value, format) = (options.Json, options.Yaml) switch
    {
      ({ } json, null) => (json, ComponentFormat.Json),
      (null, { } yaml) => (yaml, ComponentFormat.Yaml),
      _ => throw new ProgramExecutionException(1, "Genau eine Definition angeben: --json oder --yaml (Dateipfad oder Text).")
    };

    var component = InitComponentStore.ParseArgument(value, format);
    var userFile = InitStore.UserSettingsFile;
    var replace = options.Force;
    if (!replace && InitComponentStore.ReadUser(userFile).ContainsKey(options.Name))
    {
      if (!await context.ConfirmAsync($"Komponente '{options.Name}' existiert bereits — ersetzen?"))
      {
        throw new ProgramExecutionException(1, $"Abgebrochen — Komponente '{options.Name}' bleibt unverändert (--force ersetzt ohne Rückfrage).");
      }

      replace = true;
    }

    var saved = InitComponentStore.Add(context.Config.Images, userFile, options.Name, component, replace);
    await events.Emit(events.Std($"Komponente '{options.Name}' gespeichert: {userFile}"));
    await events.Emit(events.Std(InitComponentStore.ToJson(saved)));
    await events.Emit(events.Std(string.Empty));
    await events.Emit(events.Std($"Verwenden: cb init-configure -n <profil> -c {options.Name} oder cb vm-install -n <vm> -c {options.Name}"));
    if (replace)
    {
      await events.Emit(events.Std("Bestehende Profile behalten ihre gerenderte cloud-init — bei Bedarf `cb init-configure -f` erneut ausführen."));
    }
  }

  private static async Task RunComponentRemove(CommandContext context, ComponentRemoveOptions options)
  {
    InitComponentStore.Remove(context.Config.Images, InitStore.UserSettingsFile, options.Name);
    await context.Events.Emit(context.Events.Std($"Komponente '{options.Name}' entfernt: {InitStore.UserSettingsFile}"));
    await context.Events.Emit(context.Events.Std("Bestehende Profile behalten ihre gerenderte cloud-init; neue init-configure/vm-install kennen die Komponente nicht mehr."));
  }

  /// <summary>
  /// Meldet je Datei OK oder den Grund der Ablehnung; ohne <c>-f</c> die
  /// signaturpflichtigen appsettings neben der CLI. Exit 3, wenn eine fehlschlägt.
  /// </summary>
  private static async Task RunSettingsVerify(CommandContext context, SettingsVerifyOptions options)
  {
    var events = context.Events;
    var now = DateTime.UtcNow;
    var files = options.Files.ToList();
    var checks = files.Count > 0
        ? files.Select(file => PgpFileVerifier.VerifyFile(Path.GetFullPath(file), TrustedKeys.CoreBridge, now)).ToList()
        : SettingsSignatures.Check(AppContext.BaseDirectory, CurrentEnvironment, TrustedKeys.CoreBridge, now);

    foreach (var check in checks)
    {
      await events.Emit(check.Valid
          ? events.Std($"OK       {check.File} — {check.Reason}")
          : events.Err($"UNGÜLTIG {check.File} — {check.Reason}"));
    }

    SettingsSignatures.RequireValid(checks);
  }
}
