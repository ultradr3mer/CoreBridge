using CommandLine;

namespace CoreBridge.Cli;

/// <summary>
/// Optionen, die für einen einzelnen Aufruf die Timeouts aus der Config
/// (<c>DefaultTimeout</c>, <c>DefaultTimeoutVmCreation</c>, <c>DefaultTimeoutVmStart</c>) ersetzen; wird zentral
/// in <c>Program.ApplyTimeoutOverride</c> angewendet.
/// </summary>
public interface ITimeoutOverride
{
    /// <summary>Zeitangabe wie <c>5m</c>, <c>300s</c> oder <c>1000ms</c>; <c>null</c> = Config-Wert.</summary>
    string? OverrideTimeout { get; }
}

/// <summary>Basis aller Verben, die Multipass aufrufen und daher auf Antworten warten.</summary>
public abstract class MultipassOptions : IOptions, ITimeoutOverride
{
    [Option('t', "timeout", HelpText = "Timeout for this call, e.g. 5m, 300s or 1000ms (overrides DefaultTimeout / DefaultTimeoutVmCreation / DefaultTimeoutVmStart from the config).")]
    public string? OverrideTimeout { set; private get; }

    string? ITimeoutOverride.OverrideTimeout => this.OverrideTimeout; 
}

[Verb("multipass-status", HelpText = "Prüft, ob Multipass auffindbar ist und der Daemon antwortet, und gibt CLI- und Daemon-Version aus (Exit 1, wenn nicht).")]
public sealed class MultipassStatusOptions : MultipassOptions
{
}

/// <summary>
/// Verben, die sich selbst erhöht in einem neuen Fenster starten: dort setzt die CLI
/// <c>--pause-on-exit</c>, damit das Admin-Fenster bis zu einer Taste offen bleibt.
/// </summary>
public interface IPauseOnExit
{
    bool PauseOnExit { get; }
}

[Verb("multipass-restart", HelpText = "Startet den Windows-Dienst Multipass neu (wie Restart-Service -Force); hängt der Stop, wird nach Rückfrage multipassd beendet. Fragt ohne Administratorrechte nach einem erhöhten Neustart.")]
public sealed class MultipassRestartOptions : MultipassOptions, IPauseOnExit
{
    /// <summary>Intern: vom erhöhten Neustart gesetzt, damit das Admin-Fenster offen bleibt.</summary>
    [Option("pause-on-exit", Hidden = true)]
    public bool PauseOnExit { get; set; }

    /// <summary>
    /// Intern: der Benutzer hat im nicht erhöhten Fenster schon „jetzt beenden“
    /// gewählt — das Admin-Fenster beendet den hängenden Daemon ohne erneutes Warten.
    /// </summary>
    [Option("kill-now", Hidden = true)]
    public bool KillNow { get; set; }
}

[Verb("multipass-store", HelpText = "Zeigt oder ändert, wo Multipass seine Daten ablegt (MULTIPASS_STORAGE: Images, VM-Disks, Client-Zertifikate). Ändern startet den Dienst neu und braucht Administratorrechte; kopiert wird nichts.")]
public sealed class MultipassStoreOptions : MultipassOptions, IPauseOnExit
{
    [Option('p', "path", HelpText = "Neuer Ordner für die Multipass-Daten, z. B. D:\\Multipass (wird angelegt). Ohne -p/--reset: nur anzeigen.")]
    public string? Path { get; set; }

    [Option("reset", HelpText = "Zurück zum Standardort %ProgramData%\\Multipass (MULTIPASS_STORAGE entfernen).")]
    public bool Reset { get; set; }

    [Option('y', "yes", HelpText = "Ohne Rückfrage umstellen.")]
    public bool Yes { get; set; }

    /// <summary>Intern: vom erhöhten Neustart gesetzt, damit das Admin-Fenster offen bleibt.</summary>
    [Option("pause-on-exit", Hidden = true)]
    public bool PauseOnExit { get; set; }
}

[Verb("vm-list", HelpText = "Listet alle Multipass-VMs auf (inkl. Papierkorb, state Deleted).")]
public sealed class VmListOptions : MultipassOptions
{
}

[Verb("vm-create", HelpText = "Legt eine neue VM an — aus einem Profil (--profile) oder einem Katalog-Image (Standard: Inits:DefaultRuntimeImage).")]
public sealed class VmCreateOptions : MultipassOptions
{
    [Option('n', "name", Required = true, HelpText = "Name der neuen VM.")]
    public string Name { get; set; } = string.Empty;

    [Option('i', "image", HelpText = "Multipass-Katalog-Image (z. B. daily:26.10) oder URL/Pfad. Ohne Angabe: Inits:DefaultRuntimeImage bzw. das Image des Profils. Ein Profilname wird noch angenommen, gehört aber zu --profile.")]
    public string? Image { get; set; }

    [Option('p', "profile", HelpText = "Profil aus init-configure (cloud-init, Image, Komponenten); Übersicht: init-list.")]
    public string? Profile { get; set; }

    [Option('c', "cloud-init", HelpText = "Pfad zu einer cloud-init-YAML; überschreibt die cloud-init eines Profils (z. B. aus init-configure).")]
    public string? CloudInit { get; set; }

    [Option('d', "disk", HelpText = "Disk-Größe, z. B. 20G (multipass-Standard: 5G; schlägt die Vorgabe des Profils).")]
    public string? Disk { get; set; }

    [Option("cpus", HelpText = "Anzahl CPUs (multipass-Standard: 1; schlägt die Vorgabe des Profils).")]
    public int? Cpus { get; set; }

    [Option('m', "memory", HelpText = "Arbeitsspeicher, z. B. 4G (multipass-Standard: 1G; schlägt die Vorgabe des Profils).")]
    public string? Memory { get; set; }

    [Option("snapshot", HelpText = "Nach erfolgreicher Provisionierung (Profil/cloud-init) ohne Rückfrage stoppen und diesen Snapshot anlegen.")]
    public string? Snapshot { get; set; }
}

[Verb("init-configure", HelpText = "Erzeugt ein cloud-init-Profil für den Gast (Pakete, Benutzer, Snaps, Komponenten-Skripte).")]
public sealed class InitConfigureOptions : MultipassOptions
{
    [Option('n', "name", Required = true, HelpText = "Name des Profils (Namespace für Profilkonfiguration und cloud-init).")]
    public string Name { get; set; } = string.Empty;

    [Option('c', "components", Default = "claude,dotnet", HelpText = "Komma-Liste der Komponenten (Übersicht: component-list, z. B. claude,dotnet,opencode,python,cline).")]
    public string Components { get; set; } = "claude,dotnet";

    [Option("runtime-image", HelpText = "Multipass-Katalogname, den vm-create für dieses Profil startet (Standard: Config Inits:DefaultRuntimeImage).")]
    public string? RuntimeImage { get; set; }

    [Option("cpus", HelpText = "Vorgabe für vm-create: Anzahl CPUs.")]
    public int? Cpus { get; set; }

    [Option("memory", HelpText = "Vorgabe für vm-create: Arbeitsspeicher, z. B. 4G.")]
    public string? Memory { get; set; }

    [Option("disk", HelpText = "Vorgabe für vm-create: Disk-Größe, z. B. 20G.")]
    public string? Disk { get; set; }

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

[Verb("component-list", HelpText = "Listet die Komponenten (mitgeliefert und benutzereigen); mit -n die vollständige Definition als JSON.")]
public sealed class ComponentListOptions : IOptions
{
    [Option('n', "name", HelpText = "Nur diese Komponente als JSON ausgeben (Vorlage für component-add).")]
    public string? Name { get; set; }
}

[Verb("component-add", HelpText = "Legt eine benutzereigene Komponente an (<StoreRoot>/appsettings.user.json) oder übernimmt ein Bundle unverändert samt Signatur (<StoreRoot>/<name>.component.json); mitgelieferte sind nicht änderbar.")]
public sealed class ComponentAddOptions : IOptions
{
    [Option('n', "name", HelpText = "Name der Komponente (Kleinbuchstaben, Ziffern, Bindestriche); bei --bundle steht er im Bundle.")]
    public string? Name { get; set; }

    [Option("bundle", SetName = "bundle", HelpText = "Bundle-Datei <name>.component.json (aus component-bundle); wird unverändert kopiert, eine Signatur daneben (.clearsigned.asc/.asc) mit.")]
    public string? Bundle { get; set; }

    [Option("json", SetName = "json", HelpText = "Definition als JSON — Dateipfad oder direkt der Text, Schlüssel wie in appsettings.json (Snap, Classic, Script, InstallPath, Packages, …).")]
    public string? Json { get; set; }

    [Option("yaml", SetName = "yaml", HelpText = "Definition als YAML — Dateipfad oder direkt der Text, Schlüssel wie bei --json.")]
    public string? Yaml { get; set; }

    [Option('f', "force", HelpText = "Existierende Benutzerkomponente ohne Rückfrage ersetzen.")]
    public bool Force { get; set; }
}

[Verb("settings-verify", HelpText = "Prüft die OpenPGP-Signaturen (<datei>.clearsigned.asc oder <datei>.asc) der mitgelieferten appsettings bzw. der angegebenen Dateien gegen den eingebetteten CoreBridge-Schlüssel; unsigniert ist eine Warnung, ungültig Exit 3. Startet auch, wenn die Config ungültig signiert ist.")]
public sealed class SettingsVerifyOptions : IOptions
{
    [Option('f', "file", Separator = ',', HelpText = "Stattdessen diese Dateien prüfen (kommagetrennt); erwartet je <datei>.clearsigned.asc oder <datei>.asc daneben, oder die Datei ist selbst clearsigned.")]
    public IEnumerable<string> Files { get; set; } = [];
}

[Verb("component-bundle", HelpText = "Schreibt eine Komponente als Bundle: eine JSON-Datei mit Definition und Skript inline, bereit zum Signieren (CoreBridge.Signer --clearsign) und für component-add --bundle.")]
public sealed class ComponentBundleOptions : IOptions
{
    [Option('n', "name", Required = true, HelpText = "Name der Komponente (aus component-list).")]
    public string Name { get; set; } = string.Empty;

    [Option('o', "output", HelpText = "Zieldatei (Standard: <name>.component.json im aktuellen Verzeichnis).")]
    public string? Output { get; set; }
}

[Verb("key-trust", HelpText = "Vertraut einem öffentlichen OpenPGP-Schlüssel für Komponenten und Bundles (<StoreRoot>/trusted-keys); gilt nie für die mitgelieferten appsettings.")]
public sealed class KeyTrustOptions : IOptions
{
    [Option('f', "file", Required = true, HelpText = "Öffentlicher Schlüssel, z. B. aus gpg --export --armor <fingerprint>.")]
    public string File { get; set; } = string.Empty;
}

[Verb("key-list", HelpText = "Listet die Schlüssel, denen CoreBridge bei Komponenten vertraut: eingebettet (CoreBridge) und Benutzer-vertraut.")]
public sealed class KeyListOptions : IOptions
{
}

[Verb("key-untrust", HelpText = "Entzieht einem Benutzer-vertrauten Schlüssel das Vertrauen.")]
public sealed class KeyUntrustOptions : IOptions
{
    [Option('k', "key", Required = true, HelpText = "Fingerprint (voll oder die letzten 16 Stellen; Übersicht: key-list).")]
    public string Key { get; set; } = string.Empty;
}

[Verb("component-remove",HelpText = "Entfernt eine benutzereigene Komponente; mitgelieferte sind nicht entfernbar.")]
public sealed class ComponentRemoveOptions : IOptions
{
    [Option('n', "name", Required = true, HelpText = "Name der Komponente (aus component-list).")]
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

[Verb("vm-resize", HelpText = "Ändert CPUs, Arbeitsspeicher und/oder Disk einer VM (multipass set local.<vm>.cpus|memory|disk) — z. B. klein aufsetzen, zum Arbeiten wachsen lassen; die Disk wächst nur. Eine laufende VM wird nach Rückfrage gestoppt und danach wieder gestartet.")]
public sealed class VmResizeOptions : MultipassOptions
{
    [Option('n', "name", Required = true, HelpText = "Name der VM.")]
    public string Name { get; set; } = string.Empty;

    [Option('d', "disk", HelpText = "Neue Disk-Größe, z. B. 20G (nur größer als die aktuelle; Multipass verkleinert nicht).")]
    public string? Disk { get; set; }

    [Option("cpus", HelpText = "Neue Anzahl CPUs.")]
    public int? Cpus { get; set; }

    [Option('m', "memory", HelpText = "Neuer Arbeitsspeicher, z. B. 4G.")]
    public string? Memory { get; set; }
}

[Verb("vm-inventory", HelpText = "Zeigt VMs und Snapshots mit Profil, Komponenten und Ressourcen aus dem Inventar (<StoreRoot>/inventory.json); --refresh gleicht mit Multipass und den .ready-Markern im Gast ab.")]
public sealed class VmInventoryOptions : MultipassOptions
{
    [Option('n', "name", HelpText = "Nur diese VM.")]
    public string? Name { get; set; }

    [Option("refresh", HelpText = "Abgleichen: Ressourcen per multipass get, Komponenten laufender VMs aus ihren Markern, Snapshots aus ihren Kommentaren.")]
    public bool Refresh { get; set; }
}

[Verb("repo-match", HelpText = "Erkennt, was ein Repository braucht (dotnet, node, python, rust, go, java, git; dazu Detect der Komponenten), ordnet VMs und Snapshots danach und nennt den nächsten Befehl oder den Migrationspfad.")]
public sealed class RepoMatchOptions : MultipassOptions
{
    [Option('r', "repo", HelpText = "Pfad im Host-Repository (Standard: aktuelles Verzeichnis).")]
    public string? Repo { get; set; }
}

[Verb("vm-install", HelpText = "Installiert Komponenten in eine vorhandene VM (dieselben Schritte wie cloud-init: Pakete, Snaps, Komponenten-Skript, Marker-Prüfung) und empfiehlt danach einen Snapshot.")]
public sealed class VmInstallOptions : MultipassOptions
{
    [Option('n', "name", Required = true, HelpText = "Name der VM.")]
    public string Name { get; set; } = string.Empty;

    [Option('c', "components", Required = true, HelpText = "Komma-Liste der Komponenten (Übersicht: component-list, z. B. dotnet,python).")]
    public string Components { get; set; } = string.Empty;

    [Option("snapshot", HelpText = "Nach erfolgreicher Installation ohne Rückfrage stoppen und diesen Snapshot anlegen.")]
    public string? Snapshot { get; set; }
}

[Verb("work", HelpText = "Arbeiten wie mit einem lokalen Agenten: passende VM finden (sonst nach Rückfrage anlegen/ergänzen), starten, Code schicken, Task-Branch claude/<yyyyMMdd-HHmm> anlegen, den Agenten in der VM starten und danach die Commits zurückholen. `cb` ohne Argumente in einem Repo tut genau das.")]
public sealed class WorkOptions : MultipassOptions
{
    [Option('r', "repo", HelpText = "Pfad im Host-Repository (Standard: aktuelles Verzeichnis).")]
    public string? Repo { get; set; }

    [Option("agent", HelpText = "claude oder opencode (Standard: aus dem Repo — CLAUDE.md → claude, AGENTS.md/opencode.json → opencode — sonst Work:DefaultAgent).")]
    public string? Agent { get; set; }

    [Option("vm", HelpText = "Diese VM verwenden (sonst die gemerkte bzw. die von repo-match).")]
    public string? Vm { get; set; }

    [Option('y', "yes", HelpText = "Migration und Vorspulen von main ohne Rückfrage.")]
    public bool Yes { get; set; }
}

[Verb("repo-send", HelpText = "Schickt main eines Host-Repositorys in eine VM: Bare-Repo ~/repos/<repo>.git, main samt Historie als git bundle per transfer (danach nur Neues, nur Vorspulen), Arbeitskopie ~/work/<repo>. Wiederholbar; braucht die Komponente git (cb vm-install -n <vm> -c git).")]
public sealed class RepoSendOptions : MultipassOptions
{
    [Option('n', "name", Required = true, HelpText = "Name der VM.")]
    public string Name { get; set; } = string.Empty;

    [Option('r', "repo", HelpText = "Pfad im Host-Repository (Standard: aktuelles Verzeichnis).")]
    public string? Repo { get; set; }

    /// <summary>Aufruf aus <c>work</c>: der Hinweis zum Weiterarbeiten von Hand entfällt (keine CLI-Option).</summary>
    internal bool FromWork { get; init; }
}

[Verb("repo-receive", HelpText = "Holt claude/<id> der VM als git bundle und spult main darauf vor (merge --ff-only) — nur nach Rückfrage oder mit --yes; hat main eigene Commits, bleibt alles unverändert. Ohne --id: listet die claude/*-Branches der VM.")]
public sealed class RepoReceiveOptions : MultipassOptions
{
    [Option('n', "name", Required = true, HelpText = "Name der VM.")]
    public string Name { get; set; } = string.Empty;

    [Option('i', "id", HelpText = "Task-Id; geholt wird claude/<id>. Ohne Angabe: vorhandene Ids auflisten.")]
    public string? Id { get; set; }

    [Option('r', "repo", HelpText = "Pfad im Host-Repository (Standard: aktuelles Verzeichnis).")]
    public string? Repo { get; set; }

    [Option("diff", HelpText = "Vor der Rückfrage den vollständigen Diff zeigen (sonst nur die Statistik).")]
    public bool Diff { get; set; }

    [Option('y', "yes", HelpText = "Ohne Rückfrage vorspulen (ausdrückliche Zustimmung).")]
    public bool Yes { get; set; }

    [Option("integrate", HelpText = "Hat main neue Commits, ohne Rückfrage in der VM integrieren: main schicken, dort mergen, Konflikte löst der Agent, dann holen.")]
    public bool Integrate { get; set; }
}
