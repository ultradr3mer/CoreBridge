using CoreBridge.Core.Vm;
using Xunit.Abstractions;

namespace CoreBridge.Cli.Tests;

/// <summary>
/// Kompletter VM-Zyklus in fester Reihenfolge (<see cref="LifecycleTestCaseOrderer"/>
/// sortiert nach Methodenname): Status → Image → Create → List → Start → Exec →
/// Connect → SSH → Stop → Delete. Jede Stufe setzt die vorherigen voraus.
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
    public async Task _0_multipassStatus()
    {
        var result = await fixture.RunAsync(new MultipassStatusOptions(), output.WriteLine);
        fixture.RecordStatus(result.ExitCode);
        if (fixture.ShouldSkip())
        {
            output.WriteLine(fixture.SkipReason);
            return;
        }

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task _1_imageConfigure()
    {
        if (SkipWhenUnavailable())
        {
            return;
        }

        fixture.ImageCleanupRequired = true;
        var result = await fixture.RunAsync(new ImageConfigureOptions
        {
            Name = fixture.ImageName,
            Components = "claude,dotnet"
        }, output.WriteLine);

        Assert.Equal(0, result.ExitCode);
        fixture.ImageConfigured = true;
        Assert.True(File.Exists(fixture.ModelPath), $"Modellprofil fehlt: {fixture.ModelPath}");
        Assert.True(File.Exists(fixture.ProfileConfigPath), $"Profilkonfiguration fehlt: {fixture.ProfileConfigPath}");
    }

    [Fact]
    public async Task _2_vmCreate()
    {
        if (SkipWhenUnavailable())
        {
            return;
        }

        if (!fixture.ImageConfigured)
        {
            output.WriteLine("_2_vmCreate übersprungen — _1_imageConfigure hat kein Profil erzeugt.");
            return;
        }

        fixture.VmCleanupRequired = true;
        var result = await fixture.RunAsync(new VmCreateOptions
        {
            Name = fixture.VmName,
            Image = fixture.ImageName
        }, output.WriteLine);

        Assert.Equal(0, result.ExitCode);
        fixture.VmCreated = true;
    }

    [Fact]
    public async Task _3_vmList()
    {
        if (SkipWhenPrerequisite())
        {
            return;
        }

        var result = await fixture.RunAsync(new VmListOptions(), output.WriteLine);

        Assert.Equal(0, result.ExitCode);
        AssertVmState(result, "Running");
    }

    [Fact]
    public async Task _4_vmStart()
    {
        if (SkipWhenPrerequisite())
        {
            return;
        }

        var result = await fixture.RunAsync(new VmStartOptions { Name = fixture.VmName }, output.WriteLine);

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task _5_vmExec()
    {
        if (SkipWhenPrerequisite())
        {
            return;
        }

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
    public void _6_vmConnect()
    {
        if (SkipWhenPrerequisite())
        {
            return;
        }

        using var session = new VmService().OpenConsole(fixture.VmName, output.WriteLine);

        session.SendCommand("echo cbx-connect-$((20+22))", "cbx-connect-42", SessionTimeout);
        session.SendCommand("echo user=$(id -un)", "user=ubuntu", SessionTimeout);
    }

    [Fact]
    public async Task _7_vmConnectSsh()
    {
        if (SkipWhenPrerequisite())
        {
            return;
        }

        var publicKey = fixture.EnsureSshKey().Trim();
        var install = await fixture.RunAsync(new VmExecOptions
        {
            Name = fixture.VmName,
            Command = "mkdir -p ~/.ssh && chmod 700 ~/.ssh"
                + $" && echo '{publicKey}' >> ~/.ssh/authorized_keys"
                + " && chmod 600 ~/.ssh/authorized_keys"
        }, output.WriteLine);
        Assert.Equal(0, install.ExitCode);

        using var session = await OpenSshSessionAsync();

        // Gemeinsamer Shell-Status: cd wirkt auf den folgenden Befehl.
        session.SendLine("cd /tmp");
        session.SendCommand("echo cwd=$(pwd)", "cwd=/tmp", SessionTimeout);
        session.SendCommand("echo cbx-ssh-$((20+22))", "cbx-ssh-42", SessionTimeout);
        session.Exit();
    }

    [Fact]
    public async Task _8_vmStop()
    {
        if (SkipWhenPrerequisite())
        {
            return;
        }

        var stop = await fixture.RunAsync(new VmStopOptions { Name = fixture.VmName }, output.WriteLine);
        Assert.Equal(0, stop.ExitCode);

        var list = await fixture.RunAsync(new VmListOptions(), output.WriteLine);
        Assert.Equal(0, list.ExitCode);
        AssertVmState(list, "Stopped");
    }

    [Fact]
    public async Task _9_vmDelete()
    {
        if (SkipWhenPrerequisite())
        {
            return;
        }

        var delete = await fixture.RunAsync(
            new VmDeleteOptions { Name = fixture.VmName, Purge = true },
            output.WriteLine);
        Assert.Equal(0, delete.ExitCode);
        fixture.VmCleanupRequired = false;

        var list = await fixture.RunAsync(new VmListOptions(), output.WriteLine);
        Assert.Equal(0, list.ExitCode);
        Assert.DoesNotContain(list.Lines, line => VmColumns(line)?.Name == fixture.VmName);
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

    private void AssertVmState(CliRunResult list, string expectedState)
    {
        var row = list.Lines.Select(VmColumns).FirstOrDefault(columns => columns?.Name == fixture.VmName);
        Assert.True(row is not null, $"VM '{fixture.VmName}' fehlt in vm-list.");
        Assert.Equal(expectedState, row.Value.State);
    }

    private static (string Name, string State)? VmColumns(string line)
    {
        var columns = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return columns.Length >= 2 ? (columns[0], columns[1]) : null;
    }

    private bool SkipWhenUnavailable()
    {
        if (!fixture.ShouldSkip())
        {
            return false;
        }

        output.WriteLine(fixture.SkipReason);
        return true;
    }

    private bool SkipWhenPrerequisite()
    {
        if (SkipWhenUnavailable())
        {
            return true;
        }

        if (fixture.VmCreated)
        {
            return false;
        }

        output.WriteLine("Lifecycle-Stufe übersprungen — _2_vmCreate war nicht erfolgreich.");
        return true;
    }
}
