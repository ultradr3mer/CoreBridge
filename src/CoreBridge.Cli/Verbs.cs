using CommandLine;

namespace CoreBridge.Cli;

[Verb("multipass-status", HelpText = "Prüft, ob Multipass auffindbar ist, und gibt CLI- und Daemon-Version aus.")]
public sealed class MultipassStatusOptions
{
}

[Verb("vm-list", HelpText = "Listet alle Multipass-VMs auf (inkl. Papierkorb, state Deleted).")]
public sealed class VmListOptions
{
}

[Verb("vm-create", HelpText = "Legt eine neue VM an (Standard-Image: core26).")]
public sealed class VmCreateOptions
{
    [Option('n', "name", Required = true, HelpText = "Name der neuen VM.")]
    public string Name { get; set; } = string.Empty;

    [Option('i', "image", Default = "core26", HelpText = "Katalog-Image (core26), URL oder Name eines per image-configure konfigurierten Images (dessen cloud-init wird automatisch mitgegeben).")]
    public string Image { get; set; } = "core26";

    [Option('c', "cloud-init", HelpText = "Pfad zu einer cloud-init-YAML (z. B. aus image-configure).")]
    public string? CloudInit { get; set; }

    [Option('d', "disk", HelpText = "Disk-Größe, z. B. 20G (multipass-Standard: 5G).")]
    public string? Disk { get; set; }
}

[Verb("image-configure", HelpText = "Erzeugt Build-Config, Basis-Modell (Canonical dangerous, signiert) und cloud-init für ein Custom-Image.")]
public sealed class ImageConfigureOptions
{
    [Option('n', "name", Required = true, HelpText = "Name des Images (Namespace für Modell/Config/cloud-init/Ausgabe-.img).")]
    public string Name { get; set; } = string.Empty;

    [Option('c', "components", Default = "claude,dotnet", HelpText = "Komma-Liste der Komponenten (verfügbar laut Config Images:Components, z. B. claude,dotnet,opencode,python).")]
    public string Components { get; set; } = "claude,dotnet";

    [Option("model-url", HelpText = "URL des Basis-Modells (Standard: Config Images:DefaultModelUrl).")]
    public string? ModelUrl { get; set; }

    [Option('b', "build-to", HelpText = "Zielpfad der .img für image-build (absolut oder relativ zur Repo-Wurzel; Standard: <StoreRoot>\\images\\<name>.img).")]
    public string? BuildTo { get; set; }

    [Option('f', "force", HelpText = "Existierende Konfiguration überschreiben (sonst Fehler, wenn das Image schon konfiguriert ist).")]
    public bool Force { get; set; }
}

[Verb("image-build", Hidden = true, HelpText = "(Verdeckt) Baut das Custom-Image in einer klassischen Ubuntu-Bau-VM (ubuntu-image + --snap) und holt die .img auf den Host.")]
public sealed class ImageBuildOptions
{
    [Option('n', "name", Required = true, HelpText = "Name des Images (muss mit image-configure konfiguriert sein).")]
    public string Name { get; set; } = string.Empty;

    [Option("build-vm", HelpText = "Name der Bau-VM (klassisches Ubuntu; wird bei Bedarf angelegt). Standard: Config Images:Build:VmName.")]
    public string? BuildVmName { get; set; }

    [Option("build-image", HelpText = "Katalog-Image der Bau-VM. Standard: Config Images:Build:Image.")]
    public string? BuildImage { get; set; }

    [Option('d', "disk", HelpText = "Disk-Größe der Bau-VM. Standard: Config Images:Build:Disk.")]
    public string? Disk { get; set; }

    [Option('t', "timeout", HelpText = "Timeout für den ubuntu-image-Lauf in Sekunden. Standard: Config Images:Build:TimeoutSeconds.")]
    public int? TimeoutSeconds { get; set; }

    [Option('o', "destination", HelpText = "Lokaler Zielpfad der .img (Standard: %LOCALAPPDATA%\\CoreBridge\\images\\<name>.img).")]
    public string? Destination { get; set; }
}

[Verb("image-list", HelpText = "Listet konfigurierte Custom-Images (Komponenten, Snaps, vorhandene .img).")]
public sealed class ImageListOptions
{
}

[Verb("image-delete", HelpText = "Löscht die Konfiguration (Modell/cloud-init/Build-Config) und die gebaute .img eines Custom-Images.")]
public sealed class ImageDeleteOptions
{
    [Option('n', "name", Required = true, HelpText = "Name des Images (aus image-list).")]
    public string Name { get; set; } = string.Empty;
}

[Verb("vm-start", HelpText = "Startet eine gestoppte VM.")]
public sealed class VmStartOptions
{
    [Option('n', "name", Required = true, HelpText = "Name der VM.")]
    public string Name { get; set; } = string.Empty;
}

[Verb("vm-stop", HelpText = "Stoppt eine laufende VM.")]
public sealed class VmStopOptions
{
    [Option('n', "name", Required = true, HelpText = "Name der VM.")]
    public string Name { get; set; } = string.Empty;
}

[Verb("vm-delete", HelpText = "Löscht eine VM in den Multipass-Papierkorb; --purge entfernt sie endgültig.")]
public sealed class VmDeleteOptions
{
    [Option('n', "name", Required = true, HelpText = "Name der VM.")]
    public string Name { get; set; } = string.Empty;

    [Option('p', "purge", HelpText = "Endgültig löschen (multipass purge) statt nur in den Papierkorb.")]
    public bool Purge { get; set; }
}

[Verb("vm-exec", HelpText = "Führt ein Kommando in der VM aus (via /bin/sh -c).")]
public sealed class VmExecOptions
{
    [Option('n', "name", Required = true, HelpText = "Name der VM.")]
    public string Name { get; set; } = string.Empty;

    [Option('c', "command", Required = true, HelpText = "Kommando, das in der VM ausgeführt wird.")]
    public string Command { get; set; } = string.Empty;

    [Option('t', "timeout", Default = 600, HelpText = "Timeout in Sekunden (Standard: 600).")]
    public int TimeoutSeconds { get; set; } = 600;
}

[Verb("vm-get", HelpText = "Kopiert eine Datei von der VM auf den Host (multipass transfer).")]
public sealed class VmGetOptions
{
    [Option('n', "name", Required = true, HelpText = "Name der VM.")]
    public string Name { get; set; } = string.Empty;

    [Option('r', "remote", Required = true, HelpText = "Pfad der Datei in der VM.")]
    public string Remote { get; set; } = string.Empty;

    [Option('d', "destination", Required = true, HelpText = "Lokaler Zielpfad auf dem Host.")]
    public string Destination { get; set; } = string.Empty;
}

[Verb("vm-connect", HelpText = "Öffnet eine interaktive Shell in der VM.")]
public sealed class VmConnectOptions
{
    [Option('n', "name", Required = true, HelpText = "Name der VM.")]
    public string Name { get; set; } = string.Empty;
}

[Verb("vm-put", HelpText = "Kopiert eine Datei vom Host in die VM (multipass transfer).")]
public sealed class VmPutOptions
{
    [Option('n', "name", Required = true, HelpText = "Name der VM.")]
    public string Name { get; set; } = string.Empty;

    [Option('s', "source", Required = true, HelpText = "Lokale Datei auf dem Host.")]
    public string Source { get; set; } = string.Empty;

    [Option('d', "destination", HelpText = "Zielpfad in der VM, relativ zu $HOME (Standard: Dateiname).")]
    public string? Destination { get; set; }
}
