using CoreBridge.Interaction;
using CoreBridge.Core.Common;
using CoreBridge.Core.Config;
using CoreBridge.Core.Git;
using CoreBridge.Core.Inits;
using CoreBridge.Core.Vm;

namespace CoreBridge.Cli;

/// <summary>
/// Alles, was ein Kommando neben seinen Optionen braucht; wird einmal in
/// <c>Program.Execute</c> erzeugt und an <c>Dispatch</c> sowie an Folgekommandos
/// weitergereicht.
/// </summary>
/// <param name="Config">Gemeinsame CLI-/TUI-Konfiguration.</param>
/// <param name="Vms">Multipass-VM-Verben.</param>
/// <param name="Events">
/// Erzeugt die Ereignisse dieses Kommandos und reicht sie an den Konsumenten
/// weiter; beantwortet werden sie über <c>Ask</c>/<c>AskAsync</c>.
/// </param>
/// <param name="CancellationToken">Abbruch per Strg+C.</param>
/// <param name="Git">git auf dem Host (repo-send/-receive).</param>
/// <param name="Catalog">Alle Komponenten samt Herkunft und Signatur.</param>
/// <param name="Sources">Wo die Komponenten liegen (für settings-verify und component-add).</param>
public sealed record CommandContext(
    AppConfig Config,
    VmService Vms,
    EventFactory Events,
    CancellationToken CancellationToken,
    GitCli Git,
    ComponentCatalog Catalog,
    ComponentSources Sources)
{
    /// <summary><c>DefaultTimeout</c> (ggf. per <c>--timeout</c> ersetzt) in Millisekunden.</summary>
    public int DefaultTimeoutMs => (int)Config.DefaultTimeout.ReadTime().TotalMilliseconds;

    /// <summary><c>DefaultTimeoutVmStart</c> (ggf. per <c>--timeout</c> ersetzt) in Millisekunden.</summary>
    public int VmStartTimeoutMs => (int)Config.DefaultTimeoutVmStart.ReadTime().TotalMilliseconds;

    /// <summary>
    /// Ja/Nein-Rückfrage, die <c>await</c>en kann — für Kommandos, die nicht
    /// in einem Core-Callback festhängen.
    /// </summary>
    public async ValueTask<bool> ConfirmAsync(string question, TimeSpan? timeout = null)
    {
        return await this.Events.AskAsync(this.Events.Choice<YesNoOptions>(question, timeout))
            == YesNoOptions.Yes;
    }

    /// <summary>
    /// Freitext-Eingabe, die <c>await</c>en kann; <c>null</c> = keine Eingabe.
    /// </summary>
    public ValueTask<string?> PromptAsync(string question, TimeSpan? timeout = null)
    {
        return this.Events.AskAsync(this.Events.Text(question, timeout));
    }
}
