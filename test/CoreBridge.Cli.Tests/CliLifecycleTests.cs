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
    public async Task Stage00_MultipassStatus()
    {
        var exitCode = await fixture.RunAsync(new MultipassStatusOptions());
        fixture.RecordStatus(exitCode);
        if (fixture.ShouldSkip())
        {
            output.WriteLine(fixture.SkipReason);
            return;
        }

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task Stage01_ImageConfigure()
    {
        if (SkipWhenUnavailable())
        {
            return;
        }

        fixture.ImageCleanupRequired = true;
        var exitCode = await fixture.RunAsync(new ImageConfigureOptions
        {
            Name = fixture.ImageName,
            Components = "claude,dotnet"
        });

        Assert.Equal(0, exitCode);
        fixture.ImageConfigured = true;
        Assert.True(File.Exists(fixture.ModelPath), $"Modellprofil fehlt: {fixture.ModelPath}");
        Assert.True(File.Exists(fixture.ProfileConfigPath), $"Profilkonfiguration fehlt: {fixture.ProfileConfigPath}");
    }

    [Fact]
    public async Task Stage02_VmCreate()
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

        fixture.VmCleanupRequired = true;
        var exitCode = await fixture.RunAsync(new VmCreateOptions
        {
            Name = fixture.VmName,
            Image = fixture.ImageName
        });

        Assert.Equal(0, exitCode);
        fixture.VmCreated = true;
    }

    [Fact]
    public async Task Stage03_VmStartIdempotent()
    {
        if (SkipWhenPrerequisite())
        {
            return;
        }

        var exitCode = await fixture.RunAsync(new VmStartOptions { Name = fixture.VmName });

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task Stage04_VmExecSmoke()
    {
        if (SkipWhenPrerequisite())
        {
            return;
        }

        var exitCode = await fixture.RunAsync(new VmExecOptions
        {
            Name = fixture.VmName,
            Command = "printf 'hallo\\n' | grep -qx hallo"
        });

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task Stage05_VmExecClassicRelease()
    {
        if (SkipWhenPrerequisite())
        {
            return;
        }

        var exitCode = await fixture.RunAsync(new VmExecOptions
        {
            Name = fixture.VmName,
            Command = ". /etc/os-release && test \"$VERSION_ID\" = \"26.10\""
        });

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task Stage06_ClaudePostcondition()
    {
        if (SkipWhenPrerequisite())
        {
            return;
        }

        for (var attempt = 1; attempt <= 6; attempt++)
        {
            var exitCode = await fixture.RunAsync(new VmExecOptions
            {
                Name = fixture.VmName,
                Command = "command -v claude >/dev/null && claude --version >/dev/null"
            });
            if (exitCode == 0)
            {
                return;
            }

            output.WriteLine($"Claude probe {attempt} fehlgeschlagen (Exit {exitCode}).");
            await Task.Delay(TimeSpan.FromSeconds(10));
        }

        Assert.Fail("Claude wurde nach der Cloud-init-Provisionierung nicht verfügbar.");
    }

    [Fact]
    public async Task Stage99_Cleanup()
    {
        var report = await fixture.CleanupAsync();
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
}
