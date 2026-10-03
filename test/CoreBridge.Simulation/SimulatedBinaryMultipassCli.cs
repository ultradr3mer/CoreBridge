using System.Diagnostics;
using CoreBridge.Core.Common;
using CoreBridge.Core.Config;
using CoreBridge.Core.Multipass;

namespace CoreBridge.Simulation;

/// <summary>
/// Das echte <see cref="MultipassCli"/> — Prozessstart, Argumentliste, Ausgabe lesen,
/// Abschnitte und Abbruch —, aber mit <c>multipass-sim.exe</c> als Binary. Prüft also
/// denselben Weg wie gegen Multipass, nur ohne echte VMs.
/// </summary>
/// <remarks>
/// Zustand und Dauer wie beim In-Process-Simulator aus der Config
/// (<c>&lt;StoreRoot&gt;/multipass-sim/state.json</c>, <see cref="AppConfig.MultipassSimulationDelay"/>),
/// dem Kindprozess per Umgebung übergeben. Bleibt ein <see cref="IMultipassDouble"/>:
/// nicht protokolliert, und die CLI verweigert damit weiterhin alles, was den echten
/// Dienst oder git im Gast bräuchte.
/// </remarks>
public sealed class SimulatedBinaryMultipassCli : MultipassCli, IMultipassDouble
{
    private readonly string _statePath;
    private readonly string _delay;

    /// <summary>Einziger öffentlicher Konstruktor — den wählt der Container.</summary>
    public SimulatedBinaryMultipassCli(AppConfig config)
        : base(Locate)
    {
        _statePath = Path.Combine(RepoPaths.Resolve(config.Inits.StoreRoot), SimulatedMultipassCli.FolderName, SimulatedMultipassCli.StateFileName);
        _delay = config.MultipassSimulationDelay;
    }

    public string BinaryLabel => "simulation://multipass-sim";

    /// <summary>
    /// <c>multipass-sim(.exe)</c> aus <c>tools/CoreBridge.MultipassSim/bin/&lt;Konfiguration&gt;/&lt;TFM&gt;</c>,
    /// Konfiguration und TFM wie die dieses Assemblys; <c>null</c>, wenn noch nicht gebaut.
    /// </summary>
    public static string? Locate()
    {
        var segments = Path.GetDirectoryName(typeof(SimulatedBinaryMultipassCli).Assembly.Location)!
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var bin = Array.LastIndexOf(segments, "bin");
        var (configuration, framework) = bin >= 0 && bin + 2 < segments.Length
            ? (segments[bin + 1], segments[bin + 2])
            : ("Debug", "net10.0");
        // Unter Windows multipass-sim.exe, sonst das Apphost-Binary ohne Endung.
        var folder = RepoPaths.Resolve(Path.Combine("tools", "CoreBridge.MultipassSim", "bin", configuration, framework));
        return new[] { "multipass-sim.exe", "multipass-sim" }
            .Select(name => Path.Combine(folder, name))
            .FirstOrDefault(File.Exists);
    }

    // Das Binary steht fest: multipass-status reicht Doubles ihr BinaryLabel als exePath durch.
    public override MultipassRunResult Run(IReadOnlyList<string> args, int timeoutMs = 30_000, string? exePath = null) =>
        base.Run(args, timeoutMs);

    public override MultipassRunResult RunWithExtensions(IReadOnlyList<string> args, TimeSpan slice, Func<bool> keepWaiting, string? exePath = null) =>
        base.RunWithExtensions(args, slice, keepWaiting);

    public override int Attach(IReadOnlyList<string> args, string? exePath = null) =>
        base.Attach(args);

    protected override void PrepareProcess(ProcessStartInfo startInfo)
    {
        startInfo.Environment[MultipassSimCommandLine.StateVariable] = _statePath;
        startInfo.Environment[MultipassSimCommandLine.DelayVariable] = _delay;
    }
}
