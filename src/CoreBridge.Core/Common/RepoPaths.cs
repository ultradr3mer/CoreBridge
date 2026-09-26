namespace CoreBridge.Core.Common;

/// <summary>
/// Die eine Stelle, die aus der Repo-Wurzel Pfade auflöst. Die Repo-Wurzel ist
/// der Ordner, der <c>CoreBridge.slnx</c> enthält; gesucht wird von der DLL aus
/// nach oben, damit relative Pfade unabhängig vom Arbeitsverzeichnis des
/// aufrufenden Prozesses stimmen.
/// </summary>
/// <remarks>
/// Bewusst zentral: <see cref="Core.Inits.InitStore"/> und der Multipass-Mock
/// brauchen dieselbe Auflösung. Zwei eigene Kopien führten dazu, dass Profile
/// immer unter der Repo-Wurzel landeten, das Mock-Log aber relativ zum
/// aktuellen Verzeichnis — und die VM dadurch zwischen zwei <c>cb</c>-Aufrufen
/// verschwand.
/// </remarks>
public static class RepoPaths
{
    public const string SolutionFileName = "CoreBridge.slnx";

    public const string DefaultStoreFolderName = "artifacts";

    private static string? _root;

    /// <summary>Die Repo-Wurzel; beim ersten Aufruf ermittelt und danach gemerkt.</summary>
    public static string Root => _root ??= LocateRoot();

    /// <summary>
    /// Löst einen konfigurierten Pfad auf: absolute Pfade und Umgebungsvariablen
    /// bleiben, relative werden gegen die Repo-Wurzel verankert. Ohne Angabe
    /// greift <see cref="DefaultStoreFolderName"/> unterhalb der Repo-Wurzel.
    /// </summary>
    public static string Resolve(string? configured = null)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return Path.Combine(Root, DefaultStoreFolderName);
        }

        var expanded = Environment.ExpandEnvironmentVariables(configured.Trim());
        return Path.GetFullPath(
            Path.IsPathRooted(expanded)
                ? expanded
                : Path.Combine(Root, expanded));
    }

    /// <summary>Nur für Tests: die gemerkte Wurzel verwerfen.</summary>
    internal static void ResetCacheForTests() => _root = null;

    private static string LocateRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !directory.EnumerateFiles(SolutionFileName).Any())
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? AppContext.BaseDirectory;
    }
}
