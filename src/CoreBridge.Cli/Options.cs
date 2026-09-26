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

    [Option('i', "image", HelpText = "Katalog-Image (z. B. daily:26.10), URL/Pfad oder Name eines per init-configure erzeugten Profils. Ohne Angabe: Images:DefaultRuntimeImage.")]
    public string? Image { get; set; }

    [Option('c', "cloud-init", HelpText = "Pfad zu einer cloud-init-YAML; überschreibt die cloud-init eines Profils (z. B. aus init-configure).")]
    public string? CloudInit { get; set; }

    [Option('d', "disk", HelpText = "Disk-Größe, z. B. 20G (multipass-Standard: 5G).")]
    public string? Disk { get; set; }

    [Option("snapshot", HelpText = "Nach erfolgreicher Provisionierung (Profil/cloud-init) ohne Rückfrage stoppen und diesen Snapshot anlegen.")]
    public string? Snapshot { get; set; }
}

[Verb("init-configure", HelpText = "Erzeugt ein cloud-init-Profil für den Gast (Pakete, Benutzer, Snaps, Komponenten-Skripte).")]
public sealed class InitConfigureOptions : MultipassOptions
{
    [Option('n', "name", Required = true, HelpText = "Name des Profils (Namespace für Profilkonfiguration und cloud-init).")]
    public string Name { get; set; } = string.Empty;

    [Option('c', "components", Default = "claude,dotnet", HelpText = "Komma-Liste der Komponenten (verfügbar laut Config Images:Components, z. B. claude,dotnet,opencode,python,cline).")]
    public string Components { get; set; } = "claude,dotnet";

    [Option("runtime-image", HelpText = "Multipass-Katalogname, den vm-create für dieses Profil startet (Standard: Config Images:DefaultRuntimeImage).")]
    public string? RuntimeImage { get; set; }

    [Option('f', "force", HelpText = "Existierende Konfiguration überschreiben (sonst Fehler, wenn das Profil schon existiert).")]
    public bool Force { get; set; }
}

[Verb("init-list", HelpText = "Listet konfigurierte cloud-init-Profile (Komponenten und Snaps).")]
public sealed class InitListOptions : IOptions
{
}

[Verb("init-delete", HelpText = "Löscht ein cloud-init-Profil (Profilkonfiguration und cloud-init).")]
public sealed class InitDeleteOptions : IOptions
{
    [Option('n', "name", Required = true, HelpText = "Name des Profils (aus init-list).")]
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

[Verb("vm-clone", HelpText = "Klont eine VM (multipass clone) — samt allem, was cloud-init darin installiert hat; eine laufende Quelle wird nach Rückfrage gestoppt.")]
public sealed class VmCloneOptions : MultipassOptions
{
    [Option('s', "source", Required = true, HelpText = "Name der Quell-VM.")]
    public string Source { get; set; } = string.Empty;

    [Option('n', "name", HelpText = "Name des Klons (Standard von Multipass: <source>-cloneN).")]
    public string? Name { get; set; }
}

[Verb("vm-snapshot", HelpText = "Sichert den Stand einer VM als Snapshot (multipass snapshot); eine laufende VM wird nach Rückfrage gestoppt.")]
public sealed class VmSnapshotOptions : MultipassOptions
{
    [Option('n', "name", Required = true, HelpText = "Name der VM.")]
    public string Name { get; set; } = string.Empty;

    [Option('s', "snapshot", HelpText = "Name des Snapshots (Standard von Multipass: snapshotN).")]
    public string? Snapshot { get; set; }

    [Option('m', "comment", HelpText = "Freitext zum Snapshot.")]
    public string? Comment { get; set; }
}

[Verb("vm-snapshots", HelpText = "Listet die Snapshots aller VMs oder einer VM (multipass list --snapshots).")]
public sealed class VmSnapshotsOptions : MultipassOptions
{
    [Option('n', "name", HelpText = "Nur die Snapshots dieser VM.")]
    public string? Name { get; set; }
}

[Verb("vm-restore", HelpText = "Setzt eine VM auf einen Snapshot zurück (multipass restore); fragt, ob der aktuelle Stand vorher gesichert werden soll.")]
public sealed class VmRestoreOptions : MultipassOptions
{
    [Option('n', "name", Required = true, HelpText = "Name der VM.")]
    public string Name { get; set; } = string.Empty;

    [Option('s', "snapshot", Required = true, HelpText = "Name des Snapshots (aus vm-snapshots).")]
    public string Snapshot { get; set; } = string.Empty;
}

[Verb("vm-resize", HelpText = "Vergrößert die Disk einer VM (multipass set local.<vm>.disk=<size>) — z. B. klein aufsetzen, zum Arbeiten wachsen lassen; eine laufende VM wird nach Rückfrage gestoppt und danach wieder gestartet.")]
public sealed class VmResizeOptions : MultipassOptions
{
    [Option('n', "name", Required = true, HelpText = "Name der VM.")]
    public string Name { get; set; } = string.Empty;

    [Option('d', "disk", Required = true, HelpText = "Neue Disk-Größe, z. B. 20G (nur größer als die aktuelle; Multipass verkleinert nicht).")]
    public string Disk { get; set; } = string.Empty;
}

[Verb("vm-install", HelpText = "Installiert Komponenten in eine vorhandene VM (dieselben Schritte wie cloud-init: Pakete, Snaps, Komponenten-Skript, Marker-Prüfung) und empfiehlt danach einen Snapshot.")]
public sealed class VmInstallOptions : MultipassOptions
{
    [Option('n', "name", Required = true, HelpText = "Name der VM.")]
    public string Name { get; set; } = string.Empty;

    [Option('c', "components", Required = true, HelpText = "Komma-Liste der Komponenten (Config Images:Components, z. B. dotnet,python).")]
    public string Components { get; set; } = string.Empty;

    [Option("snapshot", HelpText = "Nach erfolgreicher Installation ohne Rückfrage stoppen und diesen Snapshot anlegen.")]
    public string? Snapshot { get; set; }
}
