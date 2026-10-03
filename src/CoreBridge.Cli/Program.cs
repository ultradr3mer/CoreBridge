using System.Threading.Channels;
using CommandLine;
using CoreBridge.Interaction;
using CoreBridge.Core.Authenticity;
using CoreBridge.Core.Common;
using CoreBridge.Core.Config;
using CoreBridge.Core.Git;
using CoreBridge.Core.Inits;
using CoreBridge.Core.Multipass;
using CoreBridge.Core.Vm;
using Microsoft.Extensions.Configuration;

namespace CoreBridge.Cli;

public static partial class Program
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
        typeof(MultipassStoreOptions),
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
        typeof(ComponentBundleOptions),
        typeof(KeyTrustOptions),
        typeof(KeyListOptions),
        typeof(KeyUntrustOptions),
        typeof(SettingsVerifyOptions)
  ];

  /// <summary>Wert von DOTNET_ENVIRONMENT, Default Development.</summary>
  private static string CurrentEnvironment =>
      Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Development";

  /// <summary>
  /// Leitsatz 6: CLI und TUI lesen dieselbe Config-Quelle. Reihenfolge:
  /// appsettings.json → appsettings.{Environment}.json (Env DOTNET_ENVIRONMENT,
  /// Default Development) → appsettings.local.json (gitignored) → Env-Variablen
  /// (Doppelter Unterstrich, z. B. Inits__StoreRoot). Keine Passwörter in der
  /// Config — Secrets laufen über die CORE_BRIDGE_*_PASSWORD-Env-Kette.
  /// Signaturen (<see cref="SettingsSignatures"/>): fehlt sie bei appsettings.json
  /// oder appsettings.{Environment}.json, landet die Datei in
  /// <paramref name="warnings"/>; appsettings.local.json darf unsigniert sein.
  /// Eine vorhandene, aber ungültige Signatur wirft
  /// <see cref="SettingsSignatureException"/>. <paramref name="checkSignatures"/>
  /// ist nur für <c>settings-verify</c> aus, das die Prüfung selbst meldet.
  /// </summary>
  private static AppConfig LoadSystemConfig(bool checkSignatures, out IReadOnlyList<SignatureCheck> warnings)
  {
    var environment = CurrentEnvironment;
    var files = new SignedFileProvider(
        AppContext.BaseDirectory,
        name => checkSignatures ? SettingsSignatures.PolicyFor(name, environment) : SignaturePolicy.Ignore);

    var configuration = new ConfigurationBuilder()
        .SetFileProvider(files)
        .AddJsonFile("appsettings.json", optional: false)
        .AddJsonFile($"appsettings.{environment}.json", optional: true)
        .AddJsonFile("appsettings.local.json", optional: true)
        .AddEnvironmentVariables()
        .Build();

    warnings = files.Warnings;
    return configuration.Get<AppConfig>()
      ?? throw new InvalidOperationException("Konfiguration konnte nicht gebunden werden (appsettings.json).");
  }

  /// <summary>
  /// Schlüssel für Komponenten und Bundles: der eingebettete CoreBridge-Schlüssel plus
  /// der Vertrauensordner des Benutzers — nie für die mitgelieferten appsettings.
  /// </summary>
  private static Org.BouncyCastle.Bcpg.OpenPgp.PgpPublicKeyRingBundle ComponentTrustBundle() =>
      new TrustedKeyStore(InitStore.TrustedKeysFolder).Combined(TrustedKeys.CoreBridge);

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
    IReadOnlyList<SignatureCheck> unsigned;
    try
    {
      config = LoadSystemConfig(checkSignatures: options is not SettingsVerifyOptions, out unsigned);
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

    // Protokoll laut CommandLog-Config (Standard aus; Development: <Repo>/artifacts/logs).
    using var log = CommandLog.Open(config.CommandLog.ResolveDirectory(), args);
    log.Write("info", $"Umgebung {CurrentEnvironment}, appsettings aus {AppContext.BaseDirectory}");
    foreach (var check in unsigned)
    {
      var warning = SettingsSignatures.WarningMessage(check);
      log.Write("warnung", warning);
      Console.Error.WriteLine(warning);
    }

    var exitCode = 0;
    try
    {
      // Konsument des Ereignisstroms: er schreibt alles auf die Konsole
      // und beantwortet die Rückfragen des Kommandos.
      return exitCode = await new ConsoleAdapter().AcceptEventStream(log.Tee(Run(config, options, cancellation.Token)));
    }
    catch (ProgramExecutionException exception)
    {
      log.Write("fehler", exception.Message);
      log.WriteCause(exception);
      Console.Error.WriteLine(exception.Message);
      return exitCode = exception.ExitCode;
    }
    catch (Exception exception)
    {
      // Unerwartet = Bug: ins Protokoll, dann wie bisher weiterwerfen.
      log.Write("bug", exception.ToString());
      exitCode = -1;
      throw;
    }
    finally
    {
      log.Write("ende", $"Exit {exitCode}");
      ElevatedRelaunch.PauseIfRequested(options is IPauseOnExit { PauseOnExit: true });
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
          DefaultTimeout = ovr.OverrideTimeout ?? config.DefaultTimeout,
          DefaultTimeoutVmStart = ovr.OverrideTimeout ?? config.DefaultTimeoutVmStart
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
              + "Die mitgelieferten appsettings bleiben unverändert; eigene Werte gehören nach appsettings.local.json "
              + "(Komponenten: component-add). Wer den Signierschlüssel hat, signiert nach einer Änderung neu: "
              + ".\\scripts\\sign-settings.ps1 -ClearSign (prüfen: cb settings-verify).",
          exception),
      MultipassException => new(1, exception.Message, exception),
      GitException => new(1, exception.Message, exception),
      System.Text.Json.JsonException => new(1, $"Unerwartete Multipass-Antwort (kein JSON): {exception.Message}", exception),
      FormatException => new(1, $"Configuration error: {exception.Message}", exception),
      InvalidDataException => new(1, exception.Message, exception),
      IOException or UnauthorizedAccessException => new(1, $"CLI-I/O-Fehler: {exception.Message}", exception),
      TaskCanceledException { InnerException: TimeoutException } => new(
          1,
          "Download timed out — raise it with `--timeout` (e.g. 300s) or `DefaultTimeout` in the config.",
          exception),
      OperationCanceledException => new(130, "Abgebrochen.", exception),
      _ => null
    };
  }

  /// <summary>Womit das Kommando arbeitet (Diagnose): Multipass-Client, Aufnahme, Profile, Timeouts.</summary>
  private static async Task EmitSetup(EventFactory factory, AppConfig config, IMultipassCli cli)
  {
    var client = cli switch
    {
      IMultipassDouble multipassDouble => $"{cli.GetType().Name} ({multipassDouble.BinaryLabel}, kein echtes Multipass)",
      LoggingMultipassCli logging => $"{logging.Inner.GetType().Name} ({MultipassBinaryLocator.LocateOrNull() ?? "Binary nicht gefunden"}), Aufnahme {logging.LogFile}",
      _ => $"{cli.GetType().Name} ({MultipassBinaryLocator.LocateOrNull() ?? "Binary nicht gefunden"})"
    };
    await factory.Emit(factory.Diagnostic($"Multipass-Client: {client}"));
    await factory.Emit(factory.Diagnostic(
        $"Profile in {InitStore.ProfilesRoot}, Timeouts {config.DefaultTimeout} / VM-Erzeugung {config.DefaultTimeoutVmCreation}"));
  }

  private static async Task Execute(
      AppConfig config,
      IOptions options,
      EventFactory factory,
      CancellationToken cancellationToken)
  {
    InitStore.Initialize(config.Inits.StoreRoot);
    var sources = new ComponentSources(AppContext.BaseDirectory, CurrentEnvironment, InitStore.Root);
    var catalog = ComponentCatalog.Load(config, sources, ComponentTrustBundle(), DateTime.UtcNow);
    config = config with { Inits = catalog.Inits };
    using var services = CoreBridgeContainer.Create(config);
    var cli = MultipassCliFactory.Create(services);
    await EmitSetup(factory, config, cli);
    // Jeder Multipass- und git-Aufruf wird zum ToolCallEvent im Ereignisstrom.
    var context = new CommandContext(
        config,
        new VmService(new TracingMultipassCli(cli, factory)),
        factory,
        cancellationToken,
        new GitCli(factory),
        catalog,
        sources);
    if (cli is IMultipassDouble && options is RepoSendOptions or RepoReceiveOptions)
    {
      // git auf dem Host erreicht keine simulierte VM.
      throw new ProgramExecutionException(1, "repo-send/repo-receive are not supported in simulation — git has to run inside a real VM.");
    }

    if (cli is IMultipassDouble && options is MultipassStoreOptions)
    {
      // Der Speicherort ist eine echte Einstellung des Rechners; neben simulierten VMs führte sie in die Irre.
      throw new ProgramExecutionException(1, "multipass-store ändert den echten Multipass-Dienst und läuft deshalb nicht in der Simulation (cb-sim).");
    }

    if (cli is IMultipassDouble multipassDouble && options is not (InitConfigureOptions or InitListOptions or InitDeleteOptions or ComponentListOptions or ComponentAddOptions or ComponentRemoveOptions or ComponentBundleOptions or KeyTrustOptions or KeyListOptions or KeyUntrustOptions or SettingsVerifyOptions or MultipassRestartOptions))
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
      MultipassRestartOptions o => RunMultipassRestart(context, o),
      MultipassStoreOptions o => RunMultipassStore(context, o),
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
      ComponentBundleOptions o => RunComponentBundle(context, o),
      KeyTrustOptions o => RunKeyTrust(context, o),
      KeyListOptions => RunKeyList(context),
      KeyUntrustOptions o => RunKeyUntrust(context, o),
      SettingsVerifyOptions o => RunSettingsVerify(context, o),
      _ => throw new MultipassException($"Unbekannter CLI-Options-Typ: {options.GetType().Name}")
    };
  }

  /// <summary>
  /// Meldet je Datei OK, UNSIGNIERT (Warnung) oder den Grund der Ablehnung;
  /// ohne <c>-f</c> die mitgelieferten appsettings neben der CLI und alle
  /// Komponenten-Dateien (Bundles, Benutzerordner, Skripte). Exit 3, wenn eine
  /// Signatur ungültig ist.
  /// </summary>
  private static async Task RunSettingsVerify(CommandContext context, SettingsVerifyOptions options)
  {
    var events = context.Events;
    var now = DateTime.UtcNow;
    var files = options.Files.ToList();
    var checks = files.Count > 0
        // appsettings*.json prüft nur der CoreBridge-Schlüssel, alles andere (Bundles, Skripte) auch der Vertrauensordner.
        ? files.Select(file => SettingsSignatures.VerifyFile(
            Path.GetFullPath(file),
            Path.GetFileName(file).StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) && !file.EndsWith(ComponentCatalog.BundleExtension, StringComparison.OrdinalIgnoreCase)
                ? TrustedKeys.CoreBridge
                : ComponentTrustBundle(),
            now)).ToList()
        : SettingsSignatures.Check(AppContext.BaseDirectory, CurrentEnvironment, TrustedKeys.CoreBridge, now);

    foreach (var check in checks)
    {
      await events.Emit(check switch
      {
        { Valid: true } => events.Std($"OK         {check.File} — {check.Reason}"),
        { Unsigned: true } => events.Err($"UNSIGNIERT {check.File} — {check.Reason}"),
        _ => events.Err($"UNGÜLTIG   {check.File} — {check.Reason}")
      });
    }

    // Ohne -f auch die Komponenten: Bundles, Benutzerdateien und Skripte, gegen CoreBridge
    // plus Vertrauensordner. Unbekannter Schlüssel ist dort eine Warnung wie unsigniert.
    var invalidComponents = new List<SignatureCheck>();
    if (files.Count == 0)
    {
      foreach (var (check, signature) in ComponentChecks(context))
      {
        await events.Emit(signature.Trust switch
        {
          ComponentTrust.CoreBridge or ComponentTrust.UserTrusted => events.Std($"OK         {check.File} — {signature.Describe()}"),
          ComponentTrust.Unsigned => events.Err($"UNSIGNIERT {check.File} — {check.Reason}"),
          ComponentTrust.UnknownKey => events.Err($"UNBEKANNT  {check.File} — {check.Reason}"),
          _ => events.Err($"UNGÜLTIG   {check.File} — {check.Reason}")
        });
        if (signature.Trust == ComponentTrust.Invalid)
        {
          invalidComponents.Add(check);
        }
      }
    }

    SettingsSignatures.RequireValid([.. checks, .. invalidComponents]);
  }
}
