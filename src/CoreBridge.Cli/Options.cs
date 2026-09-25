using CommandLine;

namespace CoreBridge.Cli;

/// <summary>
/// Optionen, die für einen einzelnen Aufruf die Timeouts aus der Config
/// (<c>DefaultTimeout</c>, <c>DefaultTimeoutVmCreation</c>) ersetzen; wird zentral
/// in <c>Program.ApplyTimeoutOverride</c> angewendet.
/// </summary>
public interface ITimeoutOverride
{
    /// <summary>Zeitangabe wie <c>300s</c> oder <c>1000ms</c>; <c>null</c> = Config-Wert.</summary>
    string? OverrideTimeout { get; }
}

/// <summary>Basis aller Verben, die Multipass aufrufen und daher auf Antworten warten.</summary>
public abstract class MultipassOptions : IOptions, ITimeoutOverride
{
    [Option('t', "timeout", HelpText = "Timeout for this call, e.g. 300s or 1000ms (overrides DefaultTimeout / DefaultTimeoutVmCreation from the config).")]
    public string? OverrideTimeout { set; private get; }

    string? ITimeoutOverride.OverrideTimeout => this.OverrideTimeout; 

}

[Verb("multipass-status", HelpText = "Prüft, ob Multipass auffindbar ist, und gibt CLI- und Daemon-Version aus.")]
public sealed class MultipassStatusOptions : IOptions
{
}

[Verb("multipass-restart", HelpText = "Startet den Windows-Dienst Multipass neu (wie Restart-Service -Force); hängt der Stop, wird nach Rückfrage multipassd beendet. Fragt ohne Administratorrechte nach einem erhöhten Neustart.")]
public sealed class MultipassRestartOptions : MultipassOptions
{
    /// <summary>Intern: vom erhöhten Neustart gesetzt, damit das Admin-Fenster offen bleibt.</summary>
    [Option("pause-on-exit", Hidden = true)]
    public bool PauseOnExit { get; set; }
}

[Verb("vm-list", HelpText = "Listet alle Multipass-VMs auf (inkl. Papierkorb, state Deleted).")]
public sealed class VmListOptions : MultipassOptions
{
}

[Verb("vm-create", HelpText = "Legt eine neue VM an (Standard-Image: Images:DefaultRuntimeImage).")]
public sealed class VmCreateOptions : MultipassOptions
{
    [Option('n', "name", Required = true, HelpText = "Name der neuen VM.")]
    public string Name { get; set; } = string.Empty;

    [Option('i', "image", HelpText = "Katalog-Image, URL/Pfad oder Name eines per image-configure konfigurierten Profils. Ohne Angabe: Images:DefaultRuntimeImage.")]
    public string? Image { get; set; }

    [Option('c', "cloud-init", HelpText = "Pfad zu einer cloud-init-YAML (z. B. aus image-configure).")]
    public string? CloudInit { get; set; }

    [Option('d', "disk", HelpText = "Disk-Größe, z. B. 20G (multipass-Standard: 5G).")]
    public string? Disk { get; set; }
}

[Verb("image-configure", HelpText = "Erzeugt ein Classic-Profil mit Basis-Modell (Canonical dangerous, signiert) und cloud-init für den Gast.")]
public sealed class ImageConfigureOptions : MultipassOptions
{
    [Option('n', "name", Required = true, HelpText = "Name des Images (Namespace für Modell/Config/cloud-init/Ausgabe-.img).")]
    public string Name { get; set; } = string.Empty;

    [Option('c', "components", Default = "claude,dotnet", HelpText = "Komma-Liste der Komponenten (verfügbar laut Config Images:Components, z. B. claude,dotnet,opencode,python,cline).")]
    public string Components { get; set; } = "claude,dotnet";

    [Option("model-url", HelpText = "URL des Basis-Modells (Standard: Config Images:DefaultModelUrl).")]
    public string? ModelUrl { get; set; }

    [Option("runtime-image", HelpText = "Runtime-Image für das erzeugte Profil (Standard: Config Images:DefaultRuntimeImage).")]
    public string? RuntimeImage { get; set; }

    [Option('f', "force", HelpText = "Existierende Konfiguration überschreiben (sonst Fehler, wenn das Image schon konfiguriert ist).")]
    public bool Force { get; set; }
}

[Verb("image-list", HelpText = "Listet konfigurierte Classic-Profile (Komponenten und Snaps).")]
public sealed class ImageListOptions : IOptions
{
}

[Verb("image-delete", HelpText = "Löscht ein Classic-Profil (Modell, Profilkonfiguration und cloud-init).")]
public sealed class ImageDeleteOptions : IOptions
{
    [Option('n', "name", Required = true, HelpText = "Name des Images (aus image-list).")]
    public string Name { get; set; } = string.Empty;
}

[Verb("vm-start", HelpText = "Startet eine gestoppte VM.")]
public sealed class VmStartOptions : MultipassOptions
{
    [Option('n', "name", Required = true, HelpText = "Name der VM.")]
    public string Name { get; set; } = string.Empty;
}

[Verb("vm-stop", HelpText = "Stoppt eine laufende VM.")]
public sealed class VmStopOptions : MultipassOptions
{
    [Option('n', "name", Required = true, HelpText = "Name der VM.")]
    public string Name { get; set; } = string.Empty;
}

[Verb("vm-delete", HelpText = "Löscht eine VM in den Multipass-Papierkorb; --purge entfernt sie endgültig.")]
public sealed class VmDeleteOptions : MultipassOptions
{
    [Option('n', "name", Required = true, HelpText = "Name der VM.")]
    public string Name { get; set; } = string.Empty;

    [Option('p', "purge", HelpText = "Endgültig löschen (multipass purge) statt nur in den Papierkorb.")]
    public bool Purge { get; set; }
}

[Verb("vm-exec", HelpText = "Führt ein Kommando in der VM aus (via /bin/sh -c).")]
public sealed class VmExecOptions : MultipassOptions
{
    [Option('n', "name", Required = true, HelpText = "Name der VM.")]
    public string Name { get; set; } = string.Empty;

    [Option('c', "command", Required = true, HelpText = "Kommando, das in der VM ausgeführt wird.")]
    public string Command { get; set; } = string.Empty;
}

[Verb("vm-get", HelpText = "Kopiert eine Datei von der VM auf den Host (multipass transfer).")]
public sealed class VmGetOptions : MultipassOptions
{
    [Option('n', "name", Required = true, HelpText = "Name der VM.")]
    public string Name { get; set; } = string.Empty;

    [Option('r', "remote", Required = true, HelpText = "Pfad der Datei in der VM.")]
    public string Remote { get; set; } = string.Empty;

    [Option('d', "destination", Required = true, HelpText = "Lokaler Zielpfad auf dem Host.")]
    public string Destination { get; set; } = string.Empty;
}

[Verb("vm-connect", HelpText = "Öffnet eine interaktive Shell in der VM.")]
public sealed class VmConnectOptions : MultipassOptions
{
    [Option('n', "name", Required = true, HelpText = "Name der VM.")]
    public string Name { get; set; } = string.Empty;
}

[Verb("vm-put", HelpText = "Kopiert eine Datei vom Host in die VM (multipass transfer).")]
public sealed class VmPutOptions : MultipassOptions
{
    [Option('n', "name", Required = true, HelpText = "Name der VM.")]
    public string Name { get; set; } = string.Empty;

    [Option('s', "source", Required = true, HelpText = "Lokale Datei auf dem Host.")]
    public string Source { get; set; } = string.Empty;

    [Option('d', "destination", HelpText = "Zielpfad in der VM, relativ zu $HOME (Standard: Dateiname).")]
    public string? Destination { get; set; }
}
