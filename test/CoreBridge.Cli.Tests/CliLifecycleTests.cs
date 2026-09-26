using CoreBridge.Core.Multipass;
using CoreBridge.Core.Vm;
using Xunit.Abstractions;

namespace CoreBridge.Cli.Tests;

/// <summary>
/// Kompletter VM-Zyklus: Status → Profil → Create → List → Start → Exec →
/// Connect (SSH) → Stop → Delete, strikt in dieser Reihenfolge
/// (<see cref="LifecycleTestCaseOrderer"/>). Jede Stufe setzt den Zustand der
/// vorherigen voraus und stellt ihn nicht selbst her — schlägt eine Stufe fehl,
/// schlagen die folgenden ebenfalls fehl. Nur die ganze Klasse ausführen.
/// </summary>
[Collection(CliLifecycleCollection.Name)]
[TestCaseOrderer(
    "CoreBridge.Cli.Tests.LifecycleTestCaseOrderer",
    "CoreBridge.Cli.Tests")]
[Trait("Category", "Integration")]
public sealed class CliLifecycleTests
{
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromSeconds(60);

    private readonly CliLifecycleFixture fixture;
    private readonly ITestOutputHelper output;

    public CliLifecycleTests(CliLifecycleFixture fixture, ITestOutputHelper output)
    {
        this.fixture = fixture;
        this.output = output;
    }

    [Fact]
    public async Task Stage00_MultipassStatus()
    {
        var result = await fixture.RunAsync(new MultipassStatusOptions(), output.WriteLine);

        CliLifecycleFixture.AssertSucceeded(result);
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

    [Fact]
    public async Task Stage02_VmCreate()
    {
        var result = await fixture.RunAsync(new VmCreateOptions
        {
            Name = fixture.VmName,
            Image = fixture.ImageName,

        }, output.WriteLine);

        CliLifecycleFixture.AssertCompleted(result, $"create {fixture.VmName}", "Running");
    }

    [Fact]
    public async Task Stage03_VmList()
    {
        var result = await fixture.RunAsync(new VmListOptions(), output.WriteLine);

        CliLifecycleFixture.AssertSucceeded(result);
        Assert.Equal("Running", CliLifecycleFixture.FindVmState(result, fixture.VmName));
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
        var publicKey = fixture.EnsureSshKey().Trim();
        var install = await fixture.RunAsync(new VmExecOptions
        {
            Name = fixture.VmName,
            Command = "mkdir -p ~/.ssh && chmod 700 ~/.ssh"
                + $" && (grep -qxF '{publicKey}' ~/.ssh/authorized_keys 2>/dev/null"
                + $" || echo '{publicKey}' >> ~/.ssh/authorized_keys)"
                + " && chmod 600 ~/.ssh/authorized_keys"
        }, output.WriteLine);
        CliLifecycleFixture.AssertSucceeded(install);

        using var session = await OpenSshSessionAsync();

        // Gemeinsamer Shell-Status: cd wirkt auf den folgenden Befehl.
        session.SendLine("cd /tmp");
        session.SendCommand("echo cwd=$(pwd)", "cwd=/tmp", SessionTimeout);
        session.SendCommand("echo cbx-connect-$((20+22))", "cbx-connect-42", SessionTimeout);
        session.SendCommand("echo user=$(id -un)", "user=ubuntu", SessionTimeout);
        session.Exit();
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

    private async Task<VmSshSession> OpenSshSessionAsync()
    {
        // sshd kommt nach dem Boot evtl. erst verzögert hoch.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return new VmService().OpenSshSession(fixture.VmName, output.WriteLine, fixture.SshPrivateKeyPath);
            }
            catch (MultipassException exception) when (attempt < 6)
            {
                output.WriteLine($"SSH-Versuch {attempt} fehlgeschlagen: {exception.Message}");
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
        }
    }
}
