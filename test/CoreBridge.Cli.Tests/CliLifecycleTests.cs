using CoreBridge.Core.Vm;
using Xunit.Abstractions;

namespace CoreBridge.Cli.Tests;

/// <summary>
/// Kompletter VM-Zyklus: Status → Image → Create → List → Start → Exec →
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
    public async Task Stage01_ImageConfigure()
    {
        var result = await fixture.RunAsync(new ImageConfigureOptions
        {
            Name = fixture.ImageName,
            Components = "claude,dotnet",
            Force = true
        }, output.WriteLine);

        CliLifecycleFixture.AssertSucceeded(result);
        Assert.True(File.Exists(fixture.ModelPath), $"Modellprofil fehlt: {fixture.ModelPath}");
        Assert.True(File.Exists(fixture.ProfileConfigPath), $"Profilkonfiguration fehlt: {fixture.ProfileConfigPath}");
    }

    [Fact]
    public async Task Stage02_VmCreate()
    {
        var result = await fixture.RunAsync(new VmCreateOptions
        {
            Name = fixture.VmName,
            Image = fixture.ImageName
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
