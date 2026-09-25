namespace CoreBridge.Core.Config;

/// <summary>
/// Wurzel der CoreBridge-Konfiguration (Leitsatz 6: CLI und TUI lesen dieselbe Quelle).
/// Keine Passwörter hier — Secrets laufen ausschließlich über die Env-Kette
/// `CORE_BRIDGE_[ALL,&lt;NAME&gt;,...]_PASSWORD`.
/// </summary>
public sealed record AppConfig
{
    public ImagesConfig Images { get; init; } = new();
}

public sealed record ImagesConfig
{
    /// <summary>
    /// Kanonisches Modell für neu konfigurierte Classic-Ubuntu-Images.
    /// 26.10 ist aktuell der Edge-Stand; der Runtime-Image-Katalog muss dazu
    /// passend konfiguriert werden (<see cref="DefaultRuntimeImage"/>).
    /// </summary>
    public string DefaultModelUrl { get; init; } =
        "https://raw.githubusercontent.com/canonical/models/main/ubuntu-classic-2610-amd64-dangerous.model";

    /// <summary>
    /// Multipass-Katalogname, der für VMs aus einem konfigurierten Image-Profil
    /// verwendet wird. Der Classic-26.10-Stand ist im T02-Katalog als
    /// <c>daily:26.10</c> verfügbar.
    /// </summary>
    public string DefaultRuntimeImage { get; init; } = "daily:26.10";

    /// <summary>
    /// Wurzel für Modell-/Config-/cloud-init-/Image-Dateien. Absoluter Pfad oder
    /// relativ zur Repo-Wurzel (Ordner, der CoreBridge.slnx enthält). Leer =
    /// Repo-Wurzel/artifacts.
    /// </summary>
    public string? StoreRoot { get; init; }

    public ImageBuildDefaultsConfig Build { get; init; } = new();

    public Dictionary<string, ComponentConfig> Components { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record ImageBuildDefaultsConfig
{
    public string VmName { get; init; } = "cbx-t11-build2";

    public string Image { get; init; } = "26.04";

    public string Disk { get; init; } = "20G";

    public int TimeoutSeconds { get; init; } = 1800;
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
