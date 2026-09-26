using CoreBridge.Core.Common;

namespace CoreBridge.Core.Inits;

/// <summary>
/// Wo die generierten cloud-init-Profile liegen. Standard:
/// <Repo-Wurzel>/artifacts (Dev-Tool, keine App-Daten). Die Auflösung macht
/// <see cref="RepoPaths"/>, damit sie unabhängig vom Arbeitsverzeichnis stimmt.
/// Über Images:StoreRoot (AppConfig) konfigurierbar.
/// </summary>
public static class InitStore
{
    private static string? _root;

    /// <summary>Wurzel setzen ( einmalig beim Start aus der Config); null/leer = Default.</summary>
    public static void Initialize(string? storeRoot)
    {
        _root = RepoPaths.Resolve(storeRoot);
    }

    private static string Root => _root ??= RepoPaths.Resolve(null);

    public static string ProfilesRoot => Path.Combine(Root, "profiles");

    public static string ProfileDir(string name) => Path.Combine(ProfilesRoot, name);

    public static string ConfigFile(string name) => Path.Combine(ProfileDir(name), $"{name}.profile.json");

    public static string CloudInitFile(string name) => Path.Combine(ProfileDir(name), "cloud-init.yaml");

    /// <summary>
    /// Löst einen konfigurierten relativen oder absoluten Pfad auf. Relative
    /// Eingebettete Gast-Skripte und optionale Testressourcen werden dadurch
    /// unabhängig vom Arbeitsverzeichnis des aufrufenden Prozesses gefunden.
    /// </summary>
    public static string ResolvePath(string path) => RepoPaths.Resolve(path);
}
