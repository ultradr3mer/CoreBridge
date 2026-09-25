using CoreBridge.Core.Vm;
using Xunit.Abstractions;

namespace CoreBridge.Cli.Tests;

/// <summary>
/// Kompletter VM-Zyklus: Status → Image → Create → List → Start → Exec →
/// Connect (SSH) → Stop → Delete. Im Gesamtlauf in dieser Reihenfolge
/// (<see cref="LifecycleTestCaseOrderer"/>), aber jede Stufe ist einzeln
/// lauffähig: sie stellt ihre Voraussetzungen anhand des echten Zustands selbst
/// her (Image, VM, Laufzustand) und hängt nicht vom Ergebnis anderer Stufen ab.
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

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Stage01_ImageConfigure()
    {
        var result = await fixture.RunAsync(new ImageConfigureOptions
        {
            Name = fixture.ImageName,
            Components = "claude,dotnet",
            Force = true
        }, output.WriteLine);

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(fixture.ModelPath), $"Modellprofil fehlt: {fixture.ModelPath}");
        Assert.True(File.Exists(fixture.ProfileConfigPath), $"Profilkonfiguration fehlt: {fixture.ProfileConfigPath}");
    }

    [Fact]
    public async Task Stage02_VmCreate()
    {
        await fixture.EnsureImageAsync(output.WriteLine);
        await fixture.EnsureVmAbsentAsync(output.WriteLine);

        var result = await fixture.RunAsync(new VmCreateOptions
        {
            Name = fixture.VmName,
            Image = fixture.ImageName
        }, output.WriteLine);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("Running", await fixture.QueryVmStateAsync(output.WriteLine));
    }

    [Fact]
    public async Task Stage03_VmList()
    {
        await fixture.EnsureVmAsync(output.WriteLine);

        var result = await fixture.RunAsync(new VmListOptions(), output.WriteLine);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("Running", CliLifecycleFixture.FindVmState(result, fixture.VmName));
    }

    [Fact]
    public async Task Stage04_VmStart()
    {
        await fixture.EnsureVmAsync(output.WriteLine, running: false);

        var result = await fixture.RunAsync(new VmStartOptions { Name = fixture.VmName }, output.WriteLine);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("Running", await fixture.QueryVmStateAsync(output.WriteLine));
    }

    [Fact]
    public async Task Stage05_VmExec()
    {
        await fixture.EnsureVmAsync(output.WriteLine);

        var echo = await fixture.RunAsync(new VmExecOptions
        {
            Name = fixture.VmName,
            Command = "echo cbx-exec-$((20+22))"
        }, output.WriteLine);
        Assert.Equal(0, echo.ExitCode);
        Assert.Contains("cbx-exec-42", echo.Lines);

        var release = await fixture.RunAsync(new VmExecOptions
        {
            Name = fixture.VmName,
            Command = ". /etc/os-release && test \"$VERSION_ID\" = \"26.10\""
        }, output.WriteLine);
        Assert.Equal(0, release.ExitCode);

        // Claude wird per cloud-init nachinstalliert und kann kurz nach dem Boot noch fehlen.
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            var claude = await fixture.RunAsync(new VmExecOptions
            {
                Name = fixture.VmName,
                Command = "command -v claude >/dev/null && claude --version"
            }, output.WriteLine);
            if (claude.ExitCode == 0)
            {
                return;
            }

            output.WriteLine($"Claude probe {attempt} fehlgeschlagen (Exit {claude.ExitCode}).");
            await Task.Delay(TimeSpan.FromSeconds(10));
        }

        Assert.Fail("Claude wurde nach der Cloud-init-Provisionierung nicht verfügbar.");
    }

    [Fact]
    public async Task Stage06_VmConnect()
    {
        await fixture.EnsureVmAsync(output.WriteLine);

        var publicKey = fixture.EnsureSshKey().Trim();
        var install = await fixture.RunAsync(new VmExecOptions
        {
            Name = fixture.VmName,
            Command = "mkdir -p ~/.ssh && chmod 700 ~/.ssh"
                + $" && (grep -qxF '{publicKey}' ~/.ssh/authorized_keys 2>/dev/null"
                + $" || echo '{publicKey}' >> ~/.ssh/authorized_keys)"
                + " && chmod 600 ~/.ssh/authorized_keys"
        }, output.WriteLine);
        Assert.Equal(0, install.ExitCode);

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
        await fixture.EnsureVmAsync(output.WriteLine);

        var stop = await fixture.RunAsync(new VmStopOptions { Name = fixture.VmName }, output.WriteLine);

        Assert.Equal(0, stop.ExitCode);
        Assert.Equal("Stopped", await fixture.QueryVmStateAsync(output.WriteLine));
    }

    [Fact]
    public async Task Stage08_VmDelete()
    {
        await fixture.EnsureVmAsync(output.WriteLine, running: false);

        var delete = await fixture.RunAsync(
            new VmDeleteOptions { Name = fixture.VmName, Purge = true },
            output.WriteLine);

        Assert.Equal(0, delete.ExitCode);
        Assert.Null(await fixture.QueryVmStateAsync(output.WriteLine));
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
