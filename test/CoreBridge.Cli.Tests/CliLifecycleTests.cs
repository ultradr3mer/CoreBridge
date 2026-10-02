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
[Collection(CliLifecycleCollection.Name)]
[TestCaseOrderer(
    "CoreBridge.Cli.Tests.LifecycleTestCaseOrderer",
    "CoreBridge.Cli.Tests")]
[Trait("Category", "Integration")]
public sealed class CliLifecycleTests
{
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Snapshot, den vm-create nach der Provisionierung per <c>--snapshot</c> anlegt.</summary>
    private const string ConfiguredSnapshot = "configured";

    private readonly CliLifecycleFixture fixture;
    private readonly ITestOutputHelper output;

    public CliLifecycleTests(CliLifecycleFixture fixture, ITestOutputHelper output)
    {
        this.fixture = fixture;
        this.output = output;
    }

    /// <summary>
    /// Ebnet das Feld für die folgenden Stufen, je nach vorgefundenem Zustand:
    /// antwortet der Daemon nicht, wird er neu gestartet; steht die Test-VM noch
    /// da (früherer, abgebrochener Lauf), wird sie gestoppt und endgültig
    /// gelöscht und der Daemon danach neu gestartet — sonst hält er Aufgaben zur
    /// gelöschten VM (z. B. das „Waiting for SSH“ seines Starts) und trifft mit
    /// ihnen die neue VM gleichen Namens (beobachtet: <c>Save-VM</c> mitten im
    /// Launch). Zum Schluss muss <c>multipass-status</c> einen gesunden Daemon melden.
    /// </summary>
    [Fact]
    public async Task Stage00_LevelTheField()
    {
        var list = await fixture.RunAsync(new VmListOptions(), output.WriteLine);
        if (list.ExitCode != 0)
        {
            output.WriteLine($"Ausgangslage: vm-list scheitert ({list.Error}) — Daemon wird neu gestartet.");
            await RestartDaemonAsync();
            list = await fixture.RunAsync(new VmListOptions(), output.WriteLine);
            CliLifecycleFixture.AssertSucceeded(list);
        }

        var state = CliLifecycleFixture.FindVmState(list, fixture.VmName);
        if (state is not null)
        {
            output.WriteLine($"Ausgangslage: VM `{fixture.VmName}` ist noch da ({state}) — stoppen, löschen, Daemon neu starten.");
            if (state is not ("Stopped" or "Deleted"))
            {
                var stop = state == "Running"
                    ? await fixture.RunAsync(new VmStopOptions { Name = fixture.VmName }, output.WriteLine)
                    : await fixture.RunAsync(
                        new VmStopOptions { Name = fixture.VmName },
                        output.WriteLine,
                        Cli.Asks("Multipass can only power it off (--force). Power it off?").TestInputs("y"));
                CliLifecycleFixture.AssertCompleted(stop, $"stop{(state == "Running" ? string.Empty : " --force")} {fixture.VmName}", "Stopped");
            }

            var delete = await fixture.RunAsync(new VmDeleteOptions { Name = fixture.VmName, Purge = true }, output.WriteLine);
            CliLifecycleFixture.AssertCompleted(delete, $"delete {fixture.VmName} --purge", "nicht vorhanden");

            await RestartDaemonAsync();
        }

        var status = await fixture.RunAsync(new MultipassStatusOptions(), output.WriteLine);
        CliLifecycleFixture.AssertSucceeded(status);
    }

    /// <summary>
    /// <c>multipass-restart</c>: ohne Administratorrechte fragt die CLI nach dem
    /// erhöhten Neustart (UAC); Ausgabe und Rückfragen folgen dann im
    /// Administratorfenster, das am Ende auf eine Taste wartet.
    /// </summary>
    private async Task RestartDaemonAsync()
    {
        var restart = new MultipassService().IsElevated()
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
            Name = fixture.ImageName,
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
            Image = fixture.ImageName,
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
        var result = await fixture.RunAsync(new VmStartOptions { Name = fixture.VmName }, output.WriteLine);

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

        await AssertProvisionedAsync();
    }

    /// <summary>
    /// Prüft, dass die per cloud-init bestellten Komponenten im Gast wirklich
    /// laufen — nicht nur, dass die VM startet. Beides wird mehrmals versucht,
    /// weil die Paket-/Snap-Installation nach dem Boot noch läuft.
    /// </summary>
    [Fact]
    public async Task Stage05b_ProvisionierungIstVollstaendig()
    {
        await AssertProvisionedAsync();
    }

    private static readonly (string Component, string Probe)[] ProvisioningProbes =
    [
        ("claude", "command -v claude >/dev/null && claude --version"),
        ("dotnet", "command -v dotnet >/dev/null && dotnet --version"),
        ("claude-marker", "test -f /var/lib/corebridge/claude.ready"),
        ("dotnet-marker", "test -f /var/lib/corebridge/dotnet.ready")
    ];

    private async Task AssertProvisionedAsync()
    {
        var missing = ProvisioningProbes.Select(p => p.Component).ToList();
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var attempt = 1; attempt <= 12 && missing.Count > 0; attempt++)
        {
            foreach (var (component, probe) in ProvisioningProbes.Where(p => missing.Contains(p.Component)).ToArray())
            {
                var probe1 = await fixture.RunAsync(
                    new VmExecOptions { Name = fixture.VmName, Command = probe },
                    output.WriteLine);
                if (probe1.ExitCode == 0)
                {
                    missing.Remove(component);
                    seen[component] = string.Join(" | ", probe1.Lines);
                    output.WriteLine($"Provisionierung {component}: OK ({seen[component]})");
                }
            }

            if (missing.Count > 0)
            {
                output.WriteLine($"Provisionierung Versuch {attempt}: fehlt noch {string.Join(", ", missing)}.");
                await Task.Delay(TimeSpan.FromSeconds(15));
            }
        }

        Assert.True(
            missing.Count == 0,
            "Nach 12 Versuchen fehlen weiterhin: " + string.Join(", ", missing)
            + ". Vorhanden: " + string.Join("; ", seen.Select(e => $"{e.Key}={e.Value}")));
    }

    [Fact]
    public async Task Stage06_VmConnect()
    {
        using var session = new VmService().OpenConsole(fixture.VmName, output.WriteLine, (int)SessionTimeout.TotalMilliseconds);

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
            output.WriteLine($"Profil `{fixture.ImageName}` nicht vorhanden — nichts zu löschen.");
            return;
        }

        var delete = await fixture.RunAsync(new InitDeleteOptions { Name = fixture.ImageName }, output.WriteLine);

        CliLifecycleFixture.AssertSucceeded(delete);
        Assert.False(File.Exists(fixture.ProfileConfigPath), $"Profilkonfiguration noch vorhanden: {fixture.ProfileConfigPath}");
    }
}
