using CoreBridge.Core.Common;
using CoreBridge.Core.Config;

namespace CoreBridge.Core.Multipass;

/// <summary>
/// Erzeugt den <see cref="IMultipassCli"/> eines Aufrufs: Implementierung aus
/// dem Container (<see cref="AppConfig.Bootstrap"/>), bei gesetztem
/// <see cref="AppConfig.MultipassLogPath"/> umhüllt von <see cref="LoggingMultipassCli"/>.
/// </summary>
public static class MultipassCliFactory
{
    public static IMultipassCli Create(IServiceProvider services)
    {
        var config = services.Resolve<AppConfig>();
        var cli = services.Resolve<IMultipassCli>();

        // Die Wiedergabe liest dieses Verzeichnis selbst; es zugleich zu
        // beschreiben, würde die Quelle mit ihren eigenen Antworten füllen.
        if (string.IsNullOrWhiteSpace(config.MultipassLogPath) || cli is ReplayMultipassCli)
        {
            return cli;
        }

        return new LoggingMultipassCli(cli, RepoPaths.Resolve(config.MultipassLogPath));
    }
}
