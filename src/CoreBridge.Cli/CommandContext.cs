using CoreBridge.Core.Common;
using CoreBridge.Core.Config;
using CoreBridge.Core.Vm;

namespace CoreBridge.Cli;

/// <summary>
/// Alles, was ein Kommando neben seinen Optionen braucht; wird einmal in
/// <c>Program.Execute</c> erzeugt und an <c>Dispatch</c> sowie an Folgekommandos
/// weitergereicht.
/// </summary>
/// <param name="Config">Gemeinsame CLI-/TUI-Konfiguration.</param>
/// <param name="Vms">Multipass-VM-Verben.</param>
/// <param name="Confirm">Ja/Nein-Rückfrage an den Benutzer; <c>true</c> = Ja.</param>
/// <param name="Prompt">Freitext-Eingabe des Benutzers; <c>null</c> = keine Eingabe.</param>
/// <param name="CancellationToken">Abbruch per Strg+C.</param>
public sealed record CommandContext(
    AppConfig Config,
    VmService Vms,
    Func<string, bool> Confirm,
    Func<string, string?> Prompt,
    CancellationToken CancellationToken)
{
    /// <summary><c>DefaultTimeout</c> (ggf. per <c>--timeout</c> ersetzt) in Millisekunden.</summary>
    public int DefaultTimeoutMs => (int)Config.DefaultTimeout.ReadTime().TotalMilliseconds;
}
