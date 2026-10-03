using CoreBridge.Interaction;
using Microsoft.Win32;

namespace CoreBridge.Core.Multipass;

/// <summary>Woher der wirksame Multipass-Speicherort kommt.</summary>
public enum MultipassStorageSource
{
    /// <summary>Kein <c>MULTIPASS_STORAGE</c>: Multipass nimmt <c>%ProgramData%\Multipass</c>.</summary>
    Default,

    /// <summary>System-Umgebungsvariable (so beschreibt es die Multipass-Doku).</summary>
    System,

    /// <summary>Umgebung des Dienstes selbst (<c>Services\Multipass\Environment</c>); sie gewinnt.</summary>
    Service
}

/// <summary>Wirksamer Speicherort samt Herkunft.</summary>
public sealed record MultipassStorageLocation(string Path, MultipassStorageSource Source);

/// <summary>
/// Liest und setzt, wo der Multipass-Dienst seine Daten ablegt (Images, VM-Disks,
/// Client-Zertifikate): die Variable <c>MULTIPASS_STORAGE</c>. Geschrieben wird an
/// zwei Stellen — als System-Variable, wie die Multipass-Doku es beschreibt, und in
/// der Umgebung des Dienstes, weil der Dienststeuerungs-Manager eine geänderte
/// System-Variable erst nach einem Neustart des Rechners weitergibt, die
/// Dienst-Umgebung dagegen bei jedem Start des Dienstes liest. VMs und Images werden
/// weder kopiert noch verschoben oder gelöscht; mitgenommen werden nur die Einstellungen
/// (<see cref="CopySettings"/>). Schreiben braucht Administratorrechte.
/// </summary>
/// <param name="events">Optional: erhält jeden Schreibzugriff (System-Variable, Registry) als <see cref="DiagnosticEvent"/>.</param>
public class MultipassStorage(EventFactory? events = null)
{
    public const string VariableName = "MULTIPASS_STORAGE";

    private const string ServiceKey = @"SYSTEM\CurrentControlSet\Services\" + MultipassService.ServiceName;

    private const string EnvironmentValue = "Environment";

    /// <summary>Standardort ohne <c>MULTIPASS_STORAGE</c>.</summary>
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Multipass");

    /// <summary>Der Ort, den der Dienst beim nächsten Start verwendet.</summary>
    public MultipassStorageLocation Current()
    {
        if (ReadServiceEnvironment() is { } service && FromEnvironment(service) is { } servicePath)
        {
            return new MultipassStorageLocation(servicePath, MultipassStorageSource.Service);
        }

        return ReadSystemVariable() is { Length: > 0 } systemPath
            ? new MultipassStorageLocation(systemPath, MultipassStorageSource.System)
            : new MultipassStorageLocation(DefaultPath, MultipassStorageSource.Default);
    }

    /// <summary>
    /// Setzt den Speicherort (<c>null</c> = zurück zum Standard) an beiden Stellen.
    /// Wirksam erst mit dem nächsten Start des Dienstes.
    /// </summary>
    /// <exception cref="MultipassAccessDeniedException">Ohne Administratorrechte.</exception>
    public void Set(string? path)
    {
        try
        {
            WriteSystemVariable(path);
            WriteServiceEnvironment(WithStorage(ReadServiceEnvironment() ?? [], path));
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new MultipassAccessDeniedException(
                $"{VariableName} setzen braucht Administratorrechte (HKLM).", exception);
        }
    }

    /// <summary>
    /// Die Einstellungen des Dienstes (<c>local.*</c>, auch <c>local.passphrase</c>), relativ
    /// zum Speicherort — aufgenommen mit Multipass 1.16.3 unter Windows: ein frischer Ordner
    /// hat sie leer, deshalb meldet <c>multipass authenticate</c> dort „Passphrase is not set“.
    /// </summary>
    public const string SettingsFile = "multipassd.conf";

    /// <summary>
    /// Nimmt die <see cref="SettingsFile"/> von <paramref name="from"/> nach <paramref name="to"/>
    /// mit — damit die vor dem Umstellen gesetzte Passphrase im neuen Ordner gilt und
    /// <c>multipass authenticate</c> den Client dort aufnehmen kann. Nur, wo das Ziel keine
    /// oder eine leere hat: ein Ordner mit eigenen Einstellungen (etwa beim Zurückwechseln)
    /// bleibt unberührt. Vertraute Zertifikate, VMs und Images werden nicht kopiert.
    /// </summary>
    /// <remarks>Die Daten des Dienstes darf nur ein Administrator lesen; vor dem Neustart aufrufen.</remarks>
    /// <returns><c>true</c>, wenn kopiert wurde.</returns>
    /// <exception cref="MultipassAccessDeniedException">Ohne Administratorrechte.</exception>
    public static bool CopySettings(string from, string to)
    {
        if (SamePath(from, to))
        {
            return false;
        }

        var source = new FileInfo(Path.Combine(from, SettingsFile));
        var target = new FileInfo(Path.Combine(to, SettingsFile));
        try
        {
            if (!source.Exists || source.Length == 0 || (target.Exists && target.Length > 0))
            {
                return false;
            }

            target.Directory!.Create();
            source.CopyTo(target.FullName, overwrite: true);
            return true;
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new MultipassAccessDeniedException(
                $"Die Daten des Multipass-Dienstes in {from} darf nur ein Administrator lesen.", exception);
        }
    }

    /// <summary>Gleicher Ort? Ohne Rücksicht auf Groß-/Kleinschreibung und abschließende Trenner.</summary>
    public static bool SamePath(string left, string right)
    {
        static string Normalize(string path) =>
            Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Wert von <c>MULTIPASS_STORAGE=</c> in einer Dienst-Umgebung, <c>null</c> wenn nicht gesetzt.</summary>
    public static string? FromEnvironment(IEnumerable<string> environment)
    {
        var prefix = VariableName + "=";
        return environment
            .Where(entry => entry.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry[prefix.Length..])
            .LastOrDefault(value => value.Length > 0);
    }

    /// <summary>
    /// Die Dienst-Umgebung mit <paramref name="path"/> als einzigem
    /// <c>MULTIPASS_STORAGE</c> (<c>null</c> = Eintrag entfernt); andere Einträge bleiben.
    /// </summary>
    public static string[] WithStorage(IEnumerable<string> environment, string? path)
    {
        var prefix = VariableName + "=";
        var others = environment.Where(entry => !entry.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return path is null ? others.ToArray() : [.. others, prefix + path];
    }

    protected virtual string? ReadSystemVariable() =>
        Environment.GetEnvironmentVariable(VariableName, EnvironmentVariableTarget.Machine);

    /// <summary>Schreibt die System-Variable; <see cref="Environment"/> benachrichtigt dabei offene Fenster (WM_SETTINGCHANGE).</summary>
    protected virtual void WriteSystemVariable(string? path)
    {
        events?.Post(events.Diagnostic($"System-Variable {VariableName} := {path ?? "(entfernt)"}"));
        Environment.SetEnvironmentVariable(VariableName, path, EnvironmentVariableTarget.Machine);
    }

    protected virtual string[]? ReadServiceEnvironment()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        using var key = Registry.LocalMachine.OpenSubKey(ServiceKey);
        return key?.GetValue(EnvironmentValue) as string[];
    }

    protected virtual void WriteServiceEnvironment(string[] environment)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var key = Registry.LocalMachine.OpenSubKey(ServiceKey, writable: true)
            ?? throw new MultipassException($"Dienst {MultipassService.ServiceName} nicht gefunden (HKLM\\{ServiceKey}).");
        events?.Post(events.Diagnostic($"Registry HKLM\\{ServiceKey}\\{EnvironmentValue} := "
            + (environment.Length == 0 ? "(entfernt)" : string.Join(" | ", environment))));
        if (environment.Length == 0)
        {
            key.DeleteValue(EnvironmentValue, throwOnMissingValue: false);
        }
        else
        {
            key.SetValue(EnvironmentValue, environment, RegistryValueKind.MultiString);
        }
    }
}
