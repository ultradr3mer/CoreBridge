using CommandLine;

namespace CoreBridge.Cli;

[Verb("multipass-status", HelpText = "Prüft, ob Multipass auffindbar ist, und gibt CLI- und Daemon-Version aus.")]
public sealed class MultipassStatusOptions : IOptions
{
}

[Verb("vm-list", HelpText = "Listet alle Multipass-VMs auf (inkl. Papierkorb, state Deleted).")]
public sealed class VmListOptions : IOptions
{
}

[Verb("vm-create", HelpText = "Legt eine neue VM an (Standard-Image: Images:DefaultRuntimeImage).")]
public sealed class VmCreateOptions : IOptions
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
public sealed class ImageConfigureOptions : IOptions
{
    [Option('n', "name", Required = true, HelpText = "Name des Images (Namespace für Modell/Config/cloud-init/Ausgabe-.img).")]
    public string Name { get; set; } = string.Empty;

    [Option('c', "components", Default = "claude,dotnet", HelpText = "Komma-Liste der Komponenten (verfügbar laut Config Images:Components, z. B. claude,dotnet,opencode,python).")]
    public string Components { get; set; } = "claude,dotnet";

    [Option("model-url", HelpText = "URL des Basis-Modells (Standard: Config Images:DefaultModelUrl).")]
    public string? ModelUrl { get; set; }

    [Option("runtime-image", HelpText = "Runtime-Image für das erzeugte Profil (Standard: Config Images:DefaultRuntimeImage).")]
    public string? RuntimeImage { get; set; }

    [Option('b', "build-to", HelpText = "DEPRECATED: Zielpfad der .img für den nicht unterstützten Kompatibilitätspfad image-build (Standard: <StoreRoot>\\images\\<name>.img).")]
    public string? BuildTo { get; set; }

    [Option('f', "force", HelpText = "Existierende Konfiguration überschreiben (sonst Fehler, wenn das Image schon konfiguriert ist).")]
    public bool Force { get; set; }
}

[Verb(
    "image-build",
    Hidden = true,
    HelpText = "DEPRECATED, UNSUPPORTED: verborgener Kompatibilitätspfad zum Bau eines Custom-Images; der Windows-Import ist nicht verifiziert.")]
public sealed class ImageBuildOptions : IOptions
{
    [Option('n', "name", Required = true, HelpText = "DEPRECATED, UNSUPPORTED: Name des Images (muss mit image-configure konfiguriert sein).")]
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

[Verb("image-list", HelpText = "Listet konfigurierte Classic-Profile (Komponenten, Snaps, optionale Legacy-.img).")]
public sealed class ImageListOptions : IOptions
{
}

[Verb("image-delete", HelpText = "Löscht ein Classic-Profil (Modell/cloud-init/Config) und eine optionale Legacy-.img.")]
public sealed class ImageDeleteOptions : IOptions
{
    [Option('n', "name", Required = true, HelpText = "Name des Images (aus image-list).")]
    public string Name { get; set; } = string.Empty;
}

[Verb("vm-start", HelpText = "Startet eine gestoppte VM.")]
public sealed class VmStartOptions : IOptions
{
    [Option('n', "name", Required = true, HelpText = "Name der VM.")]
    public string Name { get; set; } = string.Empty;
}

[Verb("vm-stop", HelpText = "Stoppt eine laufende VM.")]
public sealed class VmStopOptions : IOptions
{
    [Option('n', "name", Required = true, HelpText = "Name der VM.")]
    public string Name { get; set; } = string.Empty;
}

[Verb("vm-delete", HelpText = "Löscht eine VM in den Multipass-Papierkorb; --purge entfernt sie endgültig.")]
public sealed class VmDeleteOptions : IOptions
{
    [Option('n', "name", Required = true, HelpText = "Name der VM.")]
    public string Name { get; set; } = string.Empty;

    [Option('p', "purge", HelpText = "Endgültig löschen (multipass purge) statt nur in den Papierkorb.")]
    public bool Purge { get; set; }
}

[Verb("vm-exec", HelpText = "Führt ein Kommando in der VM aus (via /bin/sh -c).")]
public sealed class VmExecOptions : IOptions
{
    [Option('n', "name", Required = true, HelpText = "Name der VM.")]
    public string Name { get; set; } = string.Empty;

    [Option('c', "command", Required = true, HelpText = "Kommando, das in der VM ausgeführt wird.")]
    public string Command { get; set; } = string.Empty;

    [Option('t', "timeout", Default = 600, HelpText = "Timeout in Sekunden (Standard: 600).")]
    public int TimeoutSeconds { get; set; } = 600;
}

[Verb("vm-get", HelpText = "Kopiert eine Datei von der VM auf den Host (multipass transfer).")]
public sealed class VmGetOptions : IOptions
{
    [Option('n', "name", Required = true, HelpText = "Name der VM.")]
    public string Name { get; set; } = string.Empty;

    [Option('r', "remote", Required = true, HelpText = "Pfad der Datei in der VM.")]
    public string Remote { get; set; } = string.Empty;

    [Option('d', "destination", Required = true, HelpText = "Lokaler Zielpfad auf dem Host.")]
    public string Destination { get; set; } = string.Empty;
}

[Verb("vm-connect", HelpText = "Öffnet eine interaktive Shell in der VM.")]
public sealed class VmConnectOptions : IOptions
{
    [Option('n', "name", Required = true, HelpText = "Name der VM.")]
    public string Name { get; set; } = string.Empty;
}

[Verb("vm-put", HelpText = "Kopiert eine Datei vom Host in die VM (multipass transfer).")]
public sealed class VmPutOptions : IOptions
{
    [Option('n', "name", Required = true, HelpText = "Name der VM.")]
    public string Name { get; set; } = string.Empty;

    [Option('s', "source", Required = true, HelpText = "Lokale Datei auf dem Host.")]
    public string Source { get; set; } = string.Empty;

    [Option('d', "destination", HelpText = "Zielpfad in der VM, relativ zu $HOME (Standard: Dateiname).")]
    public string? Destination { get; set; }
}
