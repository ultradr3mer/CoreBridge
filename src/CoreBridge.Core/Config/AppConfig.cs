namespace CoreBridge.Core.Config;

/// <summary>
/// Wurzel der CoreBridge-Konfiguration (Leitsatz 6: CLI und TUI lesen dieselbe Quelle).
/// Keine Passwörter hier — Secrets laufen ausschließlich über die Env-Kette
/// `CORE_BRIDGE_[ALL,<NAME>,...]_PASSWORD`.
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
/// Eine Image-Komponente. Snap und Cloud-Init-Eigenschaften beschreiben die
/// Provisionierung im Gast. Ein optionales <see cref="CloudInitScript"/> wird
/// als Gast-Datei in die generierte cloud-init-YAML eingebettet; es wird nie
/// vom Host als Installationsschritt gestartet.
/// </summary>
public sealed record ComponentConfig
{
    /// <summary>Optionaler Store-Snap für die Custom-Image-Pipeline.</summary>
    public string? Snap { get; init; }

    /// <summary>
    /// Repo-relatives Shell-Skript, dessen Inhalt als ausführbare Datei in
    /// cloud-init geschrieben wird. Der Pfad selbst wird nicht ausgeführt.
    /// </summary>
    public string? CloudInitScript { get; init; }

    /// <summary>Zielpfad des eingebetteten Skripts im Gast.</summary>
    public string? CloudInitInstallPath { get; init; }

    /// <summary>Pakete, die cloud-init vor der Ausführung des Skripts installiert.</summary>
    public string[] CloudInitPackages { get; init; } = [];

    /// <summary>Snap mit classic confinement — <c>snap install --classic</c>.</summary>
    public bool Classic { get; init; }

    /// <summary>"quelle|ziel" — absolute Pfade, z. B. /snap/bin/x|/home/ubuntu/.local/bin/x.</summary>
    public string[] Symlinks { get; init; } = [];

    /// <summary>"KEY=value"-Zeilen, die an /home/ubuntu/.profile angehängt werden.</summary>
    public string[] ProfileExports { get; init; } = [];

    public VendoredLibsConfig? VendoredLibs { get; init; }

    public bool NeedsCloudInit =>
        !string.IsNullOrWhiteSpace(Snap) ||
        !string.IsNullOrWhiteSpace(CloudInitScript) ||
        CloudInitPackages.Length > 0 ||
        Symlinks.Length > 0 ||
        ProfileExports.Length > 0 ||
        VendoredLibs is not null;
}

public sealed record VendoredLibsConfig
{
    /// <summary>Binary, dessen ldd die Fehlenden Libs meldet (Guard: nur vendoren, wenn nötig).</summary>
    public required string LddTarget { get; init; }

    public required string[] Packages { get; init; } = [];

    /// <summary>Glob innerhalb des entpackten deb, z. B. "usr/lib/x86_64-linux-gnu/*.so.*".</summary>
    public string ExtractGlob { get; init; } = "usr/lib/x86_64-linux-gnu/*.so.*";

    public required string Destination { get; init; }
}
