using CommandLine;

namespace CoreBridge.Cli;

[Verb("doctor", HelpText = "Prüft, ob Multipass auffindbar ist, und gibt CLI- und Daemon-Version aus.")]
public sealed class DoctorOptions
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

    [Option('i', "image", Default = "core26", HelpText = "Image aus dem multipass-Katalog oder eine file://-URL.")]
    public string Image { get; set; } = "core26";

    [Option('d', "disk", HelpText = "Disk-Größe, z. B. 20G (multipass-Standard: 5G).")]
    public string? Disk { get; set; }
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

[Verb("vm-delete", HelpText = "Löscht eine VM in den Multipass-Papierkorb (kein purge).")]
public sealed class VmDeleteOptions
{
    [Option('n', "name", Required = true, HelpText = "Name der VM.")]
    public string Name { get; set; } = string.Empty;
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
