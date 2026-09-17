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
    public string DefaultModelUrl { get; init; } =
        "https://raw.githubusercontent.com/canonical/models/main/ubuntu-core-26-amd64-dangerous.model";

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
/// Eine Bootstrap-Komponente: Snap (wird per `ubuntu-image --snap` ins Image gebacken)
/// plus optionaler Nicht-Snap-Rest (Symlinks, .profile-Exports, vendored Libs),
/// der über die generierte cloud-init beim ersten Boot nachgezogen wird.
/// </summary>
public sealed record ComponentConfig
{
    public required string Snap { get; init; }

    /// <summary>
    /// Snap mit classic confinement — `snap install --classic` (z. B. opencode,
    /// python3-alt). Ohne das Flag schlägt die Installation auf Core still fehl-
    /// schlagend fehl („no matching snaps installed").
    /// </summary>
    public bool Classic { get; init; }

    /// <summary>"quelle|ziel" — absolute Pfade, z. B. /snap/bin/x|/home/ubuntu/.local/bin/x.</summary>
    public string[] Symlinks { get; init; } = [];

    /// <summary>"KEY=value"-Zeilen, die an /home/ubuntu/.profile angehängt werden.</summary>
    public string[] ProfileExports { get; init; } = [];

    public VendoredLibsConfig? VendoredLibs { get; init; }

    public bool NeedsCloudInit => Symlinks.Length > 0 || ProfileExports.Length > 0 || VendoredLibs is not null;
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
