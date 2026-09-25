using Xunit.Abstractions;

namespace CoreBridge.Cli.Tests;

[Collection(CliLifecycleCollection.Name)]
[TestCaseOrderer(
    "CoreBridge.Cli.Tests.LifecycleTestCaseOrderer",
    "CoreBridge.Cli.Tests")]
[Trait("Category", "Integration")]
public sealed class CliLifecycleTests
{
    private readonly CliLifecycleFixture fixture;
    private readonly ITestOutputHelper output;

    public CliLifecycleTests(CliLifecycleFixture fixture, ITestOutputHelper output)
    {
        this.fixture = fixture;
        this.output = output;
    }

    [Fact]
    public void Stage00_MultipassStatus()
    {
        var result = fixture.Cli.Run(["multipass-status"], TimeSpan.FromMinutes(2));
        fixture.RecordStatus(result);
        if (fixture.ShouldSkip())
        {
            output.WriteLine(fixture.SkipReason);
            return;
        }

        Assert.False(result.TimedOut, "multipass-status timed out.");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("CLI:", result.StdOut);
    }

    [Fact]
    public void Stage01_ImageConfigure()
    {
        if (SkipWhenUnavailable())
        {
            return;
        }

        fixture.ImageCleanupRequired = true;
        var result = fixture.Cli.Run(
            [
                "image-configure",
                "--name", fixture.ImageName,
                "--components", "claude,dotnet"
            ],
            TimeSpan.FromMinutes(5));
        AssertOk(result, "image-configure");

        fixture.ImageConfigured = true;
        Assert.True(File.Exists(fixture.ModelPath), $"Modellprofil fehlt: {fixture.ModelPath}");
        Assert.Contains(
            "ubuntu-classic-2610-amd64-dangerous",
            File.ReadAllText(fixture.ModelPath));
    }

    [Fact]
    public void Stage02_VmCreate()
    {
        if (SkipWhenUnavailable())
        {
            return;
        }

        if (!fixture.ImageConfigured)
        {
            output.WriteLine("Stage02_VmCreate übersprungen — Stage01 hat kein Profil erzeugt.");
            return;
        }

        // Vor dem Aufruf markieren: Auch ein Timeout kann eine VM bereits angelegt haben.
        fixture.VmCleanupRequired = true;
        var result = fixture.Cli.Run(
            [
                "vm-create",
                "--name", fixture.VmName,
                "--image", fixture.ImageName
            ],
            TimeSpan.FromMinutes(75));
        AssertOk(result, "vm-create");
        Assert.Contains("Gast-Provisionierung: cloud-init", result.StdOut);
        fixture.VmCreated = true;
    }

    [Fact]
    public void Stage03_VmStartIdempotent()
    {
        if (SkipWhenPrerequisite())
        {
            return;
        }

        var result = fixture.Cli.Run(
            ["vm-start", "--name", fixture.VmName],
            TimeSpan.FromMinutes(5));
        AssertOk(result, "vm-start");
        Assert.Contains("bereits Running", result.StdOut);
    }

    [Fact]
    public void Stage04_VmExecSmoke()
    {
        if (SkipWhenPrerequisite())
        {
            return;
        }

        var result = fixture.Cli.Run(
            ["vm-exec", "--name", fixture.VmName, "--command", "echo hallo"],
            TimeSpan.FromMinutes(5));
        AssertOk(result, "vm-exec smoke");
        Assert.Contains("hallo", result.StdOut);
    }

    [Fact]
    public void Stage05_VmExecClassicRelease()
    {
        if (SkipWhenPrerequisite())
        {
            return;
        }

        var result = fixture.Cli.Run(
            [
                "vm-exec",
                "--name", fixture.VmName,
                "--command",
                ". /etc/os-release && printf '%s' \"$VERSION_ID\""
            ],
            TimeSpan.FromMinutes(5));
        AssertOk(result, "vm-exec release");
        Assert.Contains("26.10", result.StdOut);
    }

    [Fact]
    public async Task Stage06_ClaudePostcondition()
    {
        if (SkipWhenPrerequisite())
        {
            return;
        }

        var available = await WaitForClaudeAsync();
        Assert.True(available, "Claude wurde nach der Cloud-init-Provisionierung nicht verfügbar.");
    }

    [Fact]
    public void Stage99_Cleanup()
    {
        var report = fixture.Cleanup();
        output.WriteLine(report.Diagnostics);
        Assert.True(report.Success, report.Diagnostics);
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

        output.WriteLine("Lifecycle-Stufe übersprungen — Stage02_VmCreate war nicht erfolgreich.");
        return true;
    }

    private async Task<bool> WaitForClaudeAsync()
    {
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            var result = fixture.Cli.Run(
                [
                    "vm-exec",
                    "--name", fixture.VmName,
                    "--command", "command -v claude && claude --version"
                ],
                TimeSpan.FromMinutes(5));
            output.WriteLine($"Claude probe {attempt}: exit={result.ExitCode} {result.StdOut}{result.StdErr}");
            if (result.ExitCode == 0 && result.StdOut.Contains("Claude Code", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromSeconds(10));
        }

        return false;
    }

    private static void AssertOk(CliProcessResult result, string operation)
    {
        Assert.False(result.TimedOut, $"{operation} timed out: {result.CommandLine}");
        Assert.True(
            result.ExitCode == 0,
            $"{operation} fehlgeschlagen (Exit {result.ExitCode}).\n{result.CommandLine}\nSTDOUT:\n{result.StdOut}\nSTDERR:\n{result.StdErr}");
    }
}
