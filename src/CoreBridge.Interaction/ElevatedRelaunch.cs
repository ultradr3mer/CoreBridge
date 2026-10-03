using System.ComponentModel;
using System.Diagnostics;

namespace CoreBridge.Interaction;

/// <summary>
/// Übergibt ein Kommando an ein zweites, erhöhtes Konsolenfenster: dieselbe CLI per
/// UAC (<c>runas</c>) neu starten und auf ihr Ende warten. Ausgabe und Rückfragen
/// laufen dann in jenem Fenster — UAC erlaubt keine Umleitung von Ein- und Ausgabe,
/// und mitgeben lassen sich nur Argumente, keine Umgebung.
/// </summary>
public static class ElevatedRelaunch
{
    /// <summary><c>STATUS_CONTROL_C_EXIT</c> (0xC000013A): Konsolenfenster geschlossen oder Strg+C.</summary>
    public const int ControlCExit = unchecked((int)0xC000013A);

    /// <summary>
    /// Startet die CLI <paramref name="cliAssemblyPath"/> per UAC mit
    /// <paramref name="arguments"/> (Verb und Optionen, z. B. <c>multipass-restart
    /// --pause-on-exit --timeout 30s</c>) und liefert ihren Exit-Code. Läuft die CLI
    /// als DLL unter <c>dotnet</c>, wird die DLL als erstes Argument mitgegeben; läuft
    /// sie eingebettet in einem fremden Host (Testhost), wird die CLI neben der
    /// Assembly gestartet.
    /// </summary>
    /// <param name="events">Optional: erhält den Start und das Ende des erhöhten Fensters als <see cref="ToolCallEvent"/>.</param>
    /// <exception cref="ProgramExecutionException">UAC abgelehnt oder Start fehlgeschlagen.</exception>
    public static async Task<int> RunAsync(
        string cliAssemblyPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        EventFactory? events = null)
    {
        var processPath = Environment.ProcessPath
            ?? throw new ProgramExecutionException(1, "Pfad der laufenden CLI unbekannt — erhöhter Neustart nicht möglich.");
        var processName = Path.GetFileNameWithoutExtension(processPath);
        var (fileName, passAssembly) = processName switch
        {
            _ when string.Equals(processName, "dotnet", StringComparison.OrdinalIgnoreCase) => (processPath, true),
            _ when string.Equals(processName, Path.GetFileNameWithoutExtension(cliAssemblyPath), StringComparison.OrdinalIgnoreCase) => (processPath, false),
            // Eingebettet (z. B. Program.Run im Testhost): nicht den fremden Host
            // neu starten, sondern die CLI neben der Assembly.
            _ when File.Exists(Path.ChangeExtension(cliAssemblyPath, ".exe")) => (Path.ChangeExtension(cliAssemblyPath, ".exe"), false),
            _ => ("dotnet", true)
        };
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Environment.CurrentDirectory
        };
        if (passAssembly)
        {
            startInfo.ArgumentList.Add(cliAssemblyPath);
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var tool = $"{fileName} (UAC runas)";
        var stopwatch = Stopwatch.StartNew();
        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            events?.Post(events.ToolCall(tool, startInfo.ArgumentList, null, stopwatch.Elapsed, null, null, "UAC-Abfrage abgelehnt"));
            throw new ProgramExecutionException(1, "Abgebrochen — Administratorrechte in der UAC-Abfrage abgelehnt.");
        }

        using var elevated = process
            ?? throw new ProgramExecutionException(1, "Erhöhter Prozess konnte nicht gestartet werden.");
        events?.Post(events.Diagnostic($"Erhöhtes Fenster läuft (PID {elevated.Id}): {tool} {string.Join(' ', startInfo.ArgumentList)}"));
        await elevated.WaitForExitAsync(cancellationToken);
        events?.Post(events.ToolCall(tool, startInfo.ArgumentList, elevated.ExitCode, stopwatch.Elapsed, null, null,
            $"PID {elevated.Id}, Ausgabe im eigenen Fenster"));
        return elevated.ExitCode;
    }

    /// <summary>
    /// Hält das erhöhte Fenster am Ende offen, bis eine Taste kommt — sonst schlösse es
    /// sich, bevor jemand die Ausgabe gelesen hat. <paramref name="pause"/> ist das
    /// <c>--pause-on-exit</c>, das <see cref="RunAsync"/>-Aufrufer mitgeben.
    /// </summary>
    public static void PauseIfRequested(bool pause)
    {
        if (!pause)
        {
            return;
        }

        Console.Write("Any key to continue ...");
        Console.ReadKey(intercept: true);
    }
}
