namespace CoreBridge.Core.Images;

/// <summary>
/// Wo Modell-, Config-, cloud-init- und Image-Dateien liegen. Standard:
/// <Repo-Wurzel>/artifacts (Dev-Tool, keine App-Daten) — die Repo-Wurzel ist
/// der Ordner, der CoreBridge.slnx enthält, von der DLL aus nach oben gesucht.
/// Über Images:StoreRoot (AppConfig) konfigurierbar.
/// </summary>
public static class ImageStore
{
    private static string? _root;

    /// <summary>Wurzel setzen ( einmalig beim Start aus der Config); null/leer = Default.</summary>
    public static void Initialize(string? storeRoot)
    {
        _root = ResolveRoot(storeRoot);
    }

    private static string Root
    {
        get
        {
            if (_root is null)
            {
                _root = ResolveRoot(null);
            }

            return _root;
        }
    }

    private static string ResolveRoot(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var expanded = Environment.ExpandEnvironmentVariables(configured);
            return Path.GetFullPath(Path.IsPathRooted(expanded) ? expanded : Path.Combine(RepoRoot(), expanded));
        }

        return Path.Combine(RepoRoot(), "artifacts");
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !directory.EnumerateFiles("CoreBridge.slnx").Any())
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? AppContext.BaseDirectory;
    }

    public static string ModelsRoot => Path.Combine(Root, "models");

    public static string ImagesRoot => Path.Combine(Root, "images");

    public static string ModelDir(string name) => Path.Combine(ModelsRoot, name);

    public static string ModelFile(string name) => Path.Combine(ModelDir(name), $"{name}.model");

    public static string ConfigFile(string name) => Path.Combine(ModelDir(name), $"{name}.build.json");

    public static string CloudInitFile(string name) => Path.Combine(ModelDir(name), "cloud-init.yaml");

    public static string ImageFile(string name) => Path.Combine(ImagesRoot, $"{name}.img");

    /// <summary>
    /// Löst einen konfigurierten repo-relativen Pfad auf. Eingebettete Gast-Skripte
    /// und optionale Testressourcen werden dadurch unabhängig vom Arbeitsverzeichnis
    /// des aufrufenden Prozesses gefunden.
    /// </summary>
    public static string ResolvePath(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path);
        return Path.GetFullPath(Path.IsPathRooted(expanded)
            ? expanded
            : Path.Combine(RepoRoot(), expanded));
    }
}
