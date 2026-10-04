using CoreBridge.Core.Multipass;

namespace CoreBridge.Core.Config;

/// <summary>
/// Wurzel der CoreBridge-Konfiguration (Leitsatz 6: CLI und TUI lesen dieselbe Quelle).
/// Keine Passwörter hier — Secrets laufen ausschließlich über die Env-Kette
/// `CORE_BRIDGE_[ALL,&lt;NAME&gt;,...]_PASSWORD`.
/// </summary>
public sealed record AppConfig
{
    public InitsConfig Inits { get; init; } = new();

    /// <summary>
    /// Standard-Timeout für Multipass-VM-Erstellung, z. B. 5m, 10s, 1000ms, dauert etwas.
    /// </summary>
    /// <remarks>
    /// Nach jedem Abschnitt fragt die CLI, ob weiter gewartet werden soll; bei Nein
    /// legt Multipass die VM im Hintergrund weiter an und ein Folgekommando kann
    /// eingegeben werden.
    /// </remarks>
    public string DefaultTimeoutVmCreation { get; init; } = "120s";

    /// <summary>
    /// Standard-Timeout für Multipass-Stop/Delete/List/Exec/Transfer und den Dienst-Neustart,
    /// z. B. 5m, 30s, 1000ms; danach fragt die CLI „weiter warten?“.
    /// </summary>
    public string DefaultTimeout { get; init; } = "30s";

    /// <summary>
    /// Wartezeit auf den Start einer VM (vm-start, Start vor exec/put/get/install, Neustart
    /// nach vm-resize), z. B. 3m. Eigener Wert, weil ein Start unter Hyper-V oft länger als
    /// <see cref="DefaultTimeout"/> braucht (Issue #5); <c>-t</c> ersetzt auch ihn.
    /// </summary>
    public string DefaultTimeoutVmStart { get; init; } = "3m";

    /// <summary>
    /// Verzeichnis für das Multipass-Aufrufprotokoll (JSON Lines, eine Datei je
    /// Prozess). Gesetzt = Protokoll an; leer = aus. Absolut oder relativ zur
    /// Repo-Wurzel (<see cref="Common.RepoPaths.Resolve"/>).
    /// </summary>
    /// <remarks>
    /// Ist <see cref="IMultipassCli"/> in <see cref="Bootstrap"/> auf
    /// <see cref="ReplayMultipassCli"/> gemappt, ist dies die Quelle der
    /// Wiedergabe und es wird nicht zusätzlich protokolliert.
    /// </remarks>
    public string? MultipassLogPath { get; init; }

    /// <summary>Protokoll jedes CLI-Aufrufs (Ausgabe, Rückfragen samt Antwort, Exit-Code).</summary>
    public CommandLogConfig CommandLog { get; init; } = new();

    /// <summary><c>cb work</c>: Agent und wie er in der VM gestartet wird.</summary>
    public WorkConfig Work { get; init; } = new();

    /// <summary>
    /// Dauer, die der Multipass-Simulator (<c>CoreBridge.Simulation</c>, nur Debug/Tests) lang laufende Verben
    /// (launch, start, stop, restart, clone) vorgibt, z. B. <c>4s</c> oder <c>0ms</c>.
    /// Länger als ein Warteabschnitt (<see cref="DefaultTimeout"/> bzw.
    /// <see cref="DefaultTimeoutVmCreation"/>) löst die Rückfrage „weiter warten?“ aus.
    /// </summary>
    public string MultipassSimulationDelay { get; init; } = "4s";

    /// <summary>
    /// Typ-Zuordnungen für den <see cref="Common.CoreBridgeContainer"/>:
    /// voller Name der Schnittstelle → voller Name der Implementierung.
    /// Zum Mocken z. B. <see cref="IMultipassCli"/> → <see cref="ReplayMultipassCli"/>.
    /// </summary>
    public Dictionary<string, string> Bootstrap { get; init; } = new()
    {
        [typeof(IMultipassCli).FullName!] = typeof(MultipassCli).FullName!
    };
}

public sealed record InitsConfig
{
    /// <summary>
    /// Multipass-Katalogname, der beim Starten einer VM zugrunde liegt.
    /// Der Classic-26.10-Stand ist im T02-Katalog als <c>daily:26.10</c>
    /// verfügbar. Ein Profil (<c>init-configure</c>) überschreibt diesen Wert.
    /// </summary>
    public string DefaultRuntimeImage { get; init; } = "daily:26.10";

    /// <summary>
    /// Wurzel für Profil-/cloud-init-Dateien. Absoluter Pfad oder
    /// relativ zur Repo-Wurzel (Ordner, der CoreBridge.slnx enthält). Leer =
    /// Repo-Wurzel/artifacts.
    /// </summary>
    public string? StoreRoot { get; init; }

    public Dictionary<string, ComponentConfig> Components { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Eine Classic-Gastkomponente. Die Komponente beschreibt Pakete, Benutzer und
/// ein optionales Gast-Skript. Das Skript wird als Datei in cloud-init eingebettet
/// und erst im Gast ausgeführt; der Host führt es nicht separat aus.
/// </summary>
public sealed record ComponentConfig
{
    /// <summary>Optionales Store-Snap für Komponenten, die weiterhin einen Snap benötigen.</summary>
    public string? Snap { get; init; }

    /// <summary>Snap mit classic confinement — <c>snap install --classic</c>.</summary>
    public bool Classic { get; init; }

    /// <summary>Relatives oder absolutes Shell-Skript, dessen Inhalt in die Gast-cloud-init eingebettet wird.</summary>
    public string? Script { get; init; }

    /// <summary>
    /// Das Skript selbst statt eines Pfads — so steht es in einem Bundle
    /// (<c>&lt;name&gt;.component.json</c>), und eine Signatur deckt Definition und Skript
    /// zugleich. Hat Vorrang vor <see cref="Script"/>.
    /// </summary>
    public string? ScriptContent { get; init; }

    /// <summary>Absoluter Unix-Pfad des eingebetteten Skripts im Gast.</summary>
    public string? InstallPath { get; init; }

    /// <summary>Pakete, die cloud-init für diese Komponente installieren soll.</summary>
    public string[] Packages { get; init; } = [];

    /// <summary>Fordert ein Update der Paketquellen an.</summary>
    public bool PackageUpdate { get; init; }

    /// <summary>Fordert ein VM-weites Paket-Upgrade an; standardmäßig deaktiviert.</summary>
    public bool PackageUpgrade { get; init; }

    /// <summaryBenutzer, die diese Komponente im Gast benötigt.</summary>
    public CloudInitUserConfig[] Users { get; init; } = [];

    /// <summary>Benutzer, als der das Skript installiert bzw. seine Postconditions ausführt.</summary>
    public string InstallUser { get; init; } = "ubuntu";

    /// <summary>
    /// Dateimuster, an denen <c>repo-match</c> erkennt, dass ein Repository diese Komponente
    /// braucht, z. B. <c>*.csproj</c> oder <c>package.json</c> (Dateiname, irgendwo im Repo).
    /// </summary>
    public string[] Detect { get; init; } = [];

    /// <summary>Hat die Komponente ein Skript (inline oder als Pfad)?</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasScript => !string.IsNullOrWhiteSpace(ScriptContent) || !string.IsNullOrWhiteSpace(Script);

    [System.Text.Json.Serialization.JsonIgnore]
    public bool NeedsCloudInit =>
        !string.IsNullOrWhiteSpace(Snap) ||
        HasScript ||
        Packages.Length > 0 ||
        PackageUpdate ||
        PackageUpgrade ||
        Users.Length > 0;
}

/// <summary>Ein Benutzer-Datensatz für die globale cloud-init-Sektion <c>users</c>.</summary>
public sealed record CloudInitUserConfig
{
    public required string Name { get; init; }

    public string? Sudo { get; init; }

    public string Shell { get; init; } = "/bin/bash";

    public string[] Groups { get; init; } = [];

    public bool LockPassword { get; init; } = true;
}

/// <summary>Einstellungen von <c>cb work</c> (Abschnitt <c>Work</c>).</summary>
public sealed record WorkConfig
{
    /// <summary>Agent, wenn weder <c>--agent</c> noch das Repo (CLAUDE.md, AGENTS.md, opencode.json) einen nennt.</summary>
    public string DefaultAgent { get; init; } = "claude";

    /// <summary>
    /// <c>exec</c> (Standard): <c>multipass exec &lt;vm&gt; --working-directory … -- &lt;agent&gt;</c> mit
    /// geerbter Konsole. <c>shell</c>: Ausweg, falls <c>exec</c> unter Windows keine Tastatur
    /// durchreicht — <c>multipass shell</c> plus einmalige Autostart-Datei
    /// <c>~/.corebridge/autostart</c>, die ein <c>.bashrc</c>-Haken ausführt.
    /// </summary>
    public string AgentLaunch { get; init; } = "exec";
}

/// <summary>
/// Einstellungen des CLI-Protokolls (<c>cli-&lt;Datum&gt;.log</c>). Ein erhöhtes Fenster liest
/// dieselben appsettings-Dateien, aber keine Env-Variablen des aufrufenden Fensters — UAC
/// reicht nur Argumente weiter.
/// </summary>
public sealed record CommandLogConfig
{
    /// <summary>Protokoll an? Standard aus; <c>appsettings.Development.json</c> schaltet es ein.</summary>
    public bool Enabled { get; init; }

    /// <summary>Ordner einer Installation; Entwicklungspfade setzt <c>appsettings.Development.json</c> selbst.</summary>
    public const string DefaultPath = @"%LOCALAPPDATA%\CoreBridge\logs";

    /// <summary>
    /// Ordner der Protokolle; Umgebungsvariablen werden aufgelöst. Absolut oder relativ zur
    /// Repo-Wurzel (<see cref="Common.RepoPaths.Resolve"/>), wie <see cref="AppConfig.MultipassLogPath"/>.
    /// Leer = <see cref="DefaultPath"/>.
    /// </summary>
    public string Path { get; init; } = DefaultPath;

    /// <summary>Der wirksame Ordner, <c>null</c> wenn das Protokoll aus ist.</summary>
    public string? ResolveDirectory() =>
        !this.Enabled ? null
        // Der Standard über den Systemordner statt %LOCALAPPDATA%: die Variable gibt es nur unter Windows.
        : string.IsNullOrWhiteSpace(this.Path) || this.Path == DefaultPath
            ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CoreBridge", "logs")
            : Common.RepoPaths.Resolve(this.Path);
}
