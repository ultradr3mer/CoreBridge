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
    /// Standard-Timeout für Multipass-Start/Stop/Restart, z. B. 5m, 10s, 1000ms.
    /// </summary>
    public string DefaultTimeout { get; init; } = "10s";

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
    /// Wurzel für Modell-/Profil-/cloud-init-Dateien. Absoluter Pfad oder
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

    [System.Text.Json.Serialization.JsonIgnore]
    public bool NeedsCloudInit =>
        !string.IsNullOrWhiteSpace(Snap) ||
        !string.IsNullOrWhiteSpace(Script) ||
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
