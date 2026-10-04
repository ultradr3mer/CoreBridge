using System.Text.RegularExpressions;
using CoreBridge.Core.Multipass;
using CoreBridge.Core.Vm;
using Xunit.Abstractions;

namespace CoreBridge.Cli.Tests;

/// <summary>
/// Kompletter VM-Zyklus: Status → Profil → Create (mit Snapshot nach der
/// Konfiguration) → List → Snapshots → Start → Exec → Connect → Stop → Delete, strikt in dieser Reihenfolge
/// (<see cref="LifecycleTestCaseOrderer"/>). Jede Stufe setzt den Zustand der
/// vorherigen voraus und prüft ihn nicht erneut. Die Stufen lassen sich auch
/// einzeln nacheinander ausführen, jede in einem eigenen Lauf: VM, Profil und Store
/// sind je Checkout fest (<see cref="CliLifecycleFixture"/>) und überleben den Lauf.
/// Aufgeräumt wird erst in Stage08/Stage09.
/// </summary>
/// <remarks>
/// Die Stufen laufen dreimal: gegen echtes Multipass (<see cref="CliLifecycleTests"/>),
/// gegen den Simulator im Prozess (<see cref="CliLifecycleSimulationTests"/>) und über
/// das echte <c>MultipassCli</c> gegen <c>multipass-sim.exe</c>
/// (<see cref="CliLifecycleSimulatedBinaryTests"/>). So bleibt der Simulator an dem
/// Ablauf gemessen, der auch in echt funktionieren muss.
/// </remarks>
public abstract class CliLifecycleStages
{
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Snapshot, den vm-create nach der Provisionierung per <c>--snapshot</c> anlegt.</summary>
    private const string ConfiguredSnapshot = "configured";

    private readonly CliLifecycleFixture fixture;
    private readonly ITestOutputHelper output;

    protected CliLifecycleStages(CliLifecycleFixture fixture, ITestOutputHelper output)
    {
        this.fixture = fixture;
        this.output = output;
    }

    /// <summary>
    /// Ebnet das Feld für die folgenden Stufen, je nach vorgefundenem Zustand:
    /// <list type="bullet">
    /// <item>Antwortet der Daemon nicht (vm-list scheitert), wird er neu gestartet.</item>
    /// <item>Steht die Test-VM in einem Übergangszustand (<c>Unknown</c>, <c>Starting</c> …),
    /// hängt der Daemon meist an ihr — dann antwortet schon das nächste vm-list nicht
    /// mehr. Deshalb zuerst neu starten und den Zustand neu einlesen.</item>
    /// <item>Steht die Test-VM noch da (früherer, abgebrochener Lauf), wird sie gestoppt,
    /// endgültig gelöscht und der Daemon danach neu gestartet — sonst kann er Aufgaben
    /// zur gelöschten VM halten (z. B. das „Waiting for SSH“ seines Starts) und mit ihnen
    /// die neue VM gleichen Namens treffen (beobachtet: <c>Save-VM</c> mitten im Launch,
    /// obwohl die alte VM beim Löschen Stopped war). Ob der Daemon so eine Aufgabe hält,
    /// verrät der Zustand der VM nicht.</item>
    /// </list>
    /// Zum Schluss muss <c>multipass-status</c> einen gesunden Daemon melden.
    /// </summary>
    [Fact]
    public async Task Stage00_LevelTheField()
    {
        var list = await fixture.RunAsync(new VmListOptions(), output.WriteLine);
        if (list.ExitCode != 0)
        {
            output.WriteLine($"Ausgangslage: vm-list scheitert ({list.Error}) — Daemon wird neu gestartet.");
            list = await RestartAndListAsync();
        }

        var state = CliLifecycleFixture.FindVmState(list, fixture.VmName);
        if (IsTransitional(state))
        {
            output.WriteLine($"Ausgangslage: VM `{fixture.VmName}` ist {state} — der Daemon hängt vermutlich an ihr, zuerst neu starten.");
            list = await RestartAndListAsync();
            state = CliLifecycleFixture.FindVmState(list, fixture.VmName);
        }

        if (state is not null)
        {
            output.WriteLine($"Ausgangslage: VM `{fixture.VmName}` ist noch da ({state}) — stoppen, löschen, Daemon neu starten.");
            if (state is not ("Stopped" or "Deleted"))
            {
                var stop = IsTransitional(state)
                    ? await fixture.RunAsync(
                        new VmStopOptions { Name = fixture.VmName },
                        output.WriteLine,
                        Cli.Asks("Multipass can only power it off (--force). Power it off?").TestInputs("y"))
                    : await fixture.RunAsync(new VmStopOptions { Name = fixture.VmName }, output.WriteLine);
                CliLifecycleFixture.AssertCompleted(stop, $"stop{(IsTransitional(state) ? " --force" : string.Empty)} {fixture.VmName}", "Stopped");
            }

            var delete = await fixture.RunAsync(new VmDeleteOptions { Name = fixture.VmName, Purge = true }, output.WriteLine);
            CliLifecycleFixture.AssertCompleted(delete, $"delete {fixture.VmName} --purge", "nicht vorhanden");

            await RestartDaemonAsync();
        }

        var status = await fixture.RunAsync(new MultipassStatusOptions(), output.WriteLine);
        CliLifecycleFixture.AssertSucceeded(status);
    }

    /// <summary>Weder Running noch Stopped noch im Papierkorb — Multipass stoppt so eine VM nur mit <c>--force</c>.</summary>
    private static bool IsTransitional(string? state) =>
        state is not (null or "Running" or "Stopped" or "Deleted");

    private async Task<CliRunResult> RestartAndListAsync()
    {
        await RestartDaemonAsync();
        var list = await fixture.RunAsync(new VmListOptions(), output.WriteLine);
        CliLifecycleFixture.AssertSucceeded(list);
        return list;
    }

    /// <summary>
    /// <c>multipass-restart</c>: darf der Benutzer den Dienst steuern (erhöht oder
    /// per Dienst-ACL), läuft er ohne Rückfrage hier; sonst fragt die CLI nach dem
    /// erhöhten Neustart (UAC), und Ausgabe und Rückfragen folgen im
    /// Administratorfenster, das am Ende auf eine Taste wartet.
    /// </summary>
    private async Task RestartDaemonAsync()
    {
        if (fixture.IsSimulation)
        {
            // Der Simulator hat keinen Daemon, der Aufgaben gelöschter VMs halten könnte;
            // multipass-restart bliebe ohnehin echt.
            output.WriteLine("Simulation: kein Daemon-Neustart.");
            return;
        }

        var service = new MultipassService();
        var restart = service.IsElevated() || service.CanControlService()
            ? await fixture.RunAsync(new MultipassRestartOptions(), output.WriteLine)
            : await fixture.RunAsync(
                new MultipassRestartOptions(),
                output.WriteLine,
                Cli.Asks("Mit Administratorrechten in einem neuen Fenster fortfahren?").TestInputs("y"));
        CliLifecycleFixture.AssertSucceeded(restart);
    }

    [Fact]
    public async Task Stage01_InitConfigure()
    {
        var result = await fixture.RunAsync(new InitConfigureOptions
        {
            Name = fixture.InitName,
            Components = "claude,dotnet",
            OverrideTimeout = "300s",
            Force = true
        }, output.WriteLine);

        CliLifecycleFixture.AssertSucceeded(result);
        Assert.True(File.Exists(fixture.ProfileConfigPath), $"Profilkonfiguration fehlt: {fixture.ProfileConfigPath}");
        Assert.True(File.Exists(fixture.CloudInitPath), $"cloud-init fehlt: {fixture.CloudInitPath}");
    }

    /// <summary>
    /// Snapshot nach der Konfiguration ist Standard: mit <c>--snapshot</c> legt
    /// vm-create ihn ohne Rückfrage an, stoppt die VM dafür und lässt sie gestoppt.
    /// </summary>
    [Fact]
    public async Task Stage02_VmCreate()
    {
        var result = await fixture.RunAsync(new VmCreateOptions
        {
            Name = fixture.VmName,
            Profile = fixture.InitName,
            Snapshot = ConfiguredSnapshot
        }, output.WriteLine);

        CliLifecycleFixture.AssertSucceeded(result);
        Assert.Contains(result.Lines, line => Regex.IsMatch(
            line,
            $@"^{Regex.Escape($"create {fixture.VmName}")}: OK \([^)]*\) — State: Running$"));
        Assert.Contains(result.Lines, line => Regex.IsMatch(
            line,
            $@"^{Regex.Escape($"snapshot {fixture.VmName}.{ConfiguredSnapshot}")}: OK \([^)]*\) — State: Stopped$"));
        Assert.Equal(
            $"VM `{fixture.VmName}` stays stopped — start it with `cb vm-start -n {fixture.VmName}`.",
            result.Lines[^1]);
    }

    [Fact]
    public async Task Stage03_VmList()
    {
        var result = await fixture.RunAsync(new VmListOptions(), output.WriteLine);

        CliLifecycleFixture.AssertSucceeded(result);
        Assert.Equal("Stopped", CliLifecycleFixture.FindVmState(result, fixture.VmName));
    }

    [Fact]
    public async Task Stage03b_VmSnapshots()
    {
        var result = await fixture.RunAsync(new VmSnapshotsOptions { Name = fixture.VmName }, output.WriteLine);

        CliLifecycleFixture.AssertSucceeded(result);
        Assert.Contains(result.Lines, line =>
            line.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var vm, var snapshot, ..]
            && vm == fixture.VmName
            && snapshot == ConfiguredSnapshot);
    }

    [Fact]
    public async Task Stage04_VmStart()
    {
        var result = await fixture.RunAsync(new VmStartOptions { Name = fixture.VmName, OverrideTimeout = "1m" }, output.WriteLine);

        CliLifecycleFixture.AssertCompleted(result, $"start {fixture.VmName}", "Running");
    }

    [Fact]
    public async Task Stage05_VmExec()
    {
        var echo = await fixture.RunAsync(new VmExecOptions
        {
            Name = fixture.VmName,
            Command = "echo cbx-exec-$((20+22))"
        }, output.WriteLine);
        CliLifecycleFixture.AssertSucceeded(echo);
        Assert.Contains("cbx-exec-42", echo.Lines);

        var release = await fixture.RunAsync(new VmExecOptions
        {
            Name = fixture.VmName,
            Command = ". /etc/os-release && test \"$VERSION_ID\" = \"26.10\""
        }, output.WriteLine);
        CliLifecycleFixture.AssertSucceeded(release);
    }

    /// <summary>
    /// Prüft, dass die per cloud-init bestellten Komponenten im Gast wirklich
    /// laufen — nicht nur, dass die VM startet. Ein Durchgang genügt: vm-create
    /// meldet erst Erfolg (und legt den Snapshot an), wenn cloud-init fertig ist und
    /// alle Marker stehen, danach installiert nichts mehr nach. Ein Wiederholen
    /// würde nur einen echten Fehler verdecken.
    /// </summary>
    [Fact]
    public async Task Stage05b_ProvisionierungIstVollstaendig()
    {
        var failed = new List<string>();
        foreach (var (component, probe) in ProvisioningProbes)
        {
            var result = await fixture.RunAsync(new VmExecOptions { Name = fixture.VmName, Command = probe }, output.WriteLine);
            if (result.ExitCode == 0 && result.DialogErrors.Count == 0)
            {
                output.WriteLine($"Provisionierung {component}: OK ({string.Join(" | ", result.Lines)})");
            }
            else
            {
                failed.Add($"{component} (Exit {result.ExitCode}{(result.DialogErrors.Count > 0 ? ", " + string.Join(" | ", result.DialogErrors) : string.Empty)})");
            }
        }

        Assert.True(failed.Count == 0, "Provisionierung unvollständig: " + string.Join("; ", failed));
    }

    private static readonly (string Component, string Probe)[] ProvisioningProbes =
    [
        ("claude", "command -v claude >/dev/null && claude --version"),
        ("dotnet", "command -v dotnet >/dev/null && dotnet --version"),
        ("claude-marker", "test -f /var/lib/corebridge/claude.ready"),
        ("dotnet-marker", "test -f /var/lib/corebridge/dotnet.ready")
    ];

    [Fact]
    public async Task Stage06_VmConnect()
    {
        using var session = new VmService(fixture.Cli).OpenConsole(fixture.VmName, output.WriteLine, (int)SessionTimeout.TotalMilliseconds);

        // Das Arbeitsverzeichnis wandert mit: cd wirkt auf den folgenden Befehl.
        session.SendLine("cd /tmp");
        session.SendCommand("echo cwd=$(pwd)", "cwd=/tmp", SessionTimeout);
        session.SendCommand("echo cbx-connect-$((20+22))", "cbx-connect-42", SessionTimeout);
        session.SendCommand("echo user=$(id -un)", "user=ubuntu", SessionTimeout);
        Assert.Equal("/tmp", session.WorkingDirectory);
    }

    [Fact]
    public async Task Stage07_VmStop()
    {
        var stop = await fixture.RunAsync(new VmStopOptions { Name = fixture.VmName }, output.WriteLine);

        CliLifecycleFixture.AssertCompleted(stop, $"stop {fixture.VmName}", "Stopped");
    }

    [Fact]
    public async Task Stage08_VmDelete()
    {
        var delete = await fixture.RunAsync(
            new VmDeleteOptions { Name = fixture.VmName, Purge = true },
            output.WriteLine);

        CliLifecycleFixture.AssertCompleted(delete, $"delete {fixture.VmName} --purge", "nicht vorhanden");
    }

    /// <summary>Räumt das Profil ab; ohne Profil (nie angelegt oder schon gelöscht) ist nichts zu tun.</summary>
    [Fact]
    public async Task Stage09_InitDelete()
    {
        if (!File.Exists(fixture.ProfileConfigPath))
        {
            output.WriteLine($"Profil `{fixture.InitName}` nicht vorhanden — nichts zu löschen.");
            return;
        }

        var delete = await fixture.RunAsync(new InitDeleteOptions { Name = fixture.InitName }, output.WriteLine);

        CliLifecycleFixture.AssertSucceeded(delete);
        Assert.False(File.Exists(fixture.ProfileConfigPath), $"Profilkonfiguration noch vorhanden: {fixture.ProfileConfigPath}");
    }
}

/// <summary>Der Lifecycle gegen echtes Multipass; jeder Aufruf landet in <c>artifacts/mp-log/cli-lifecycle</c>.</summary>
[Collection(CliLifecycleCollection.Name)]
[TestCaseOrderer(
    "CoreBridge.Cli.Tests.LifecycleTestCaseOrderer",
    "CoreBridge.Cli.Tests")]
[Trait("Category", "Integration")]
public sealed class CliLifecycleTests(CliLifecycleFixture fixture, ITestOutputHelper output)
    : CliLifecycleStages(fixture, output);

/// <summary>
/// Derselbe Lifecycle gegen <see cref="Simulation.SimulatedMultipassCli"/> — kein Multipass
/// nötig, läuft in Sekunden und deshalb auch beim schnellen Lauf ohne Integration.
/// </summary>
[Collection(CliLifecycleCollection.Name)]
[TestCaseOrderer(
    "CoreBridge.Cli.Tests.LifecycleTestCaseOrderer",
    "CoreBridge.Cli.Tests")]
public sealed class CliLifecycleSimulationTests(SimulatedCliLifecycleFixture fixture, ITestOutputHelper output)
    : CliLifecycleStages(fixture, output);

/// <summary>
/// Derselbe Lifecycle über das echte <see cref="Core.Multipass.MultipassCli"/> — Prozessstart,
/// Argumente, Ausgabe — gegen <c>multipass-sim.exe</c> statt <c>multipass.exe</c>.
/// </summary>
[Collection(CliLifecycleCollection.Name)]
[TestCaseOrderer(
    "CoreBridge.Cli.Tests.LifecycleTestCaseOrderer",
    "CoreBridge.Cli.Tests")]
public sealed class CliLifecycleSimulatedBinaryTests(SimulatedBinaryCliLifecycleFixture fixture, ITestOutputHelper output)
    : CliLifecycleStages(fixture, output);
