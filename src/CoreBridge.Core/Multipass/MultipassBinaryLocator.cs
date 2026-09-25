namespace CoreBridge.Core.Multipass;

public static class MultipassBinaryLocator
{
    public const string ExeEnvironmentVariable = "CORE_BRIDGE_MULTIPASS_EXE";

    public const string DefaultRelativePath = "Multipass\\bin\\multipass.exe";

    public static string? LocateOrNull(
        string? environmentValue = null,
        string? pathValue = null,
        string? programFilesPath = null)
    {
        environmentValue ??= Environment.GetEnvironmentVariable(ExeEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(environmentValue))
        {
            return environmentValue;
        }

        pathValue ??= Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathValue))
        {
            foreach (var directory in pathValue.Split(Path.PathSeparator))
            {
                var trimmed = directory.Trim().Trim('"');
                if (trimmed.Length == 0)
                {
                    continue;
                }

                var candidate = Path.Combine(trimmed, "multipass.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        programFilesPath ??= Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var defaultPath = Path.Combine(programFilesPath, DefaultRelativePath);
        return File.Exists(defaultPath) ? defaultPath : null;
    }

    public static bool IsInstalled(
        string? environmentValue = null,
        string? pathValue = null,
        string? programFilesPath = null)
    {
        environmentValue ??= Environment.GetEnvironmentVariable(ExeEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(environmentValue))
        {
            return File.Exists(environmentValue);
        }

        return LocateOrNull(environmentValue, pathValue, programFilesPath) is not null;
    }
}
