using System.Diagnostics;

namespace CoreBridge.Cli.Tests;

/// <summary>
/// Startet die bereits gebaute CLI als Child-Prozess. Kein `dotnet run`, kein
/// Shell-Profil und kein direkter Multipass-Aufruf.
/// </summary>
internal sealed class CliRunner : IDisposable
{
    private readonly string _storeRoot;
    private readonly string _tempRoot;
    private readonly string _repositoryRoot;
    private readonly string _cliDll;

    public CliRunner()
    {
        _repositoryRoot = FindRepositoryRoot();
        // Bewusst die Development-Konfiguration verwenden: StoreRoot=artifacts
        // wird relativ zur Repo-Wurzel aufgelöst, nicht in einen isolierten
        // Test-Root umgebogen.
        _storeRoot = Path.Combine(_repositoryRoot, "artifacts");
        _tempRoot = Path.Combine(Path.GetTempPath(), "corebridge-cli-tests", "temp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_storeRoot);
        Directory.CreateDirectory(_tempRoot);
        _cliDll = FindCliDll();
    }

    public string StoreRoot => _storeRoot;

    public CliProcessResult Run(IReadOnlyList<string> arguments, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromMinutes(5);
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = _repositoryRoot,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(_cliDll);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["DOTNET_ENVIRONMENT"] = "Development";
        startInfo.Environment["TEMP"] = _tempRoot;
        startInfo.Environment["TMP"] = _tempRoot;

        var stopwatch = Stopwatch.StartNew();
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("CLI-Prozess konnte nicht gestartet werden.");
        }

        process.StandardInput.Close();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        var exited = process.WaitForExit((int)effectiveTimeout.TotalMilliseconds);
        if (!exited)
        {
            TryKill(process);
            process.WaitForExit(5_000);
        }

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        var exitCode = exited ? process.ExitCode : -1;
        return new CliProcessResult(
            exitCode,
            stdout,
            stderr,
            stopwatch.ElapsedMilliseconds,
            !exited,
            $"dotnet {_cliDll} {string.Join(' ', arguments)}");
    }

    private string FindCliDll()
    {
        var besideTest = Path.Combine(AppContext.BaseDirectory, "CoreBridge.Cli.dll");
        if (File.Exists(besideTest))
        {
            return besideTest;
        }

        var configuration = AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";
        var built = Path.Combine(
            _repositoryRoot,
            "src", "CoreBridge.Cli", "bin", configuration, "net10.0", "CoreBridge.Cli.dll");
        if (File.Exists(built))
        {
            return built;
        }

        throw new FileNotFoundException(
            "CoreBridge.Cli.dll wurde weder neben dem Testoutput noch im erwarteten Buildoutput gefunden.");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CoreBridge.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("CoreBridge.slnx nicht von Testoutput aus erreichbar.");
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    public void Dispose()
    {
        // Der Development-StoreRoot ist repositoryweit und wird nicht vom
        // Testrunner gelöscht; image-delete entfernt nur das Testprofil.
        TryDelete(_tempRoot);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
