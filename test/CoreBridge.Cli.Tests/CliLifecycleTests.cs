using Xunit.Abstractions;

namespace CoreBridge.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class CliLifecycleTests
{
    private readonly ITestOutputHelper output;

    public CliLifecycleTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    [Fact]
    public async Task KonfiguriertesClassicProfilLaeuftVollstaendigUeberCli()
    {
        using var cli = new CliRunner();
        var status = cli.Run(["multipass-status"], TimeSpan.FromMinutes(2));
        if (status.ExitCode != 0)
        {
            output.WriteLine("CLI-Integration übersprungen — multipass-status: " + status.StdErr);
            return;
        }

        var suffix = $"{Environment.ProcessId}-{Guid.NewGuid():N}"[..20];
        var imageName = $"cbx-cli-img-{suffix}";
        var vmName = $"cbx-cli-vm-{suffix}";
        var imageConfigured = false;

        try
        {
            var configure = cli.Run(
                [
                    "image-configure",
                    "--name", imageName,
                    "--components", "claude,dotnet"
                ],
                TimeSpan.FromMinutes(5));
            AssertOk(configure, "image-configure");
            imageConfigured = true;
            Assert.Contains("ubuntu-classic-2610-amd64-dangerous", File.ReadAllText(
                Path.Combine(cli.StoreRoot, "models", imageName, imageName + ".model")));

            var create = cli.Run(
                [
                    "vm-create",
                    "--name", vmName,
                    "--image", imageName
                ],
                TimeSpan.FromMinutes(75));
            AssertOk(create, "vm-create");
            Assert.Contains("Gast-Provisionierung: cloud-init", create.StdOut);

            // vm-connect bleibt wegen TTY-Bedararf im separaten
            // CliProcessContractTests; der redirected Lifecycle ruft es nicht auf.
            var start = cli.Run(["vm-start", "--name", vmName], TimeSpan.FromMinutes(5));
            AssertOk(start, "vm-start");
            Assert.Contains("bereits Running", start.StdOut);

            var echo = cli.Run(
                ["vm-exec", "--name", vmName, "--command", "echo hallo"],
                TimeSpan.FromMinutes(5));
            AssertOk(echo, "vm-exec echo");
            Assert.Contains("hallo", echo.StdOut);

            var release = cli.Run(
                [
                    "vm-exec",
                    "--name", vmName,
                    "--command",
                    ". /etc/os-release && printf '%s' \"$VERSION_ID\""
                ],
                TimeSpan.FromMinutes(5));
            AssertOk(release, "vm-exec release");
            Assert.Contains("26.10", release.StdOut);

            var claude = await WaitForClaudeAsync(cli, vmName);
            Assert.True(claude, "Claude wurde nach der Cloud-init-Provisionierung nicht verfügbar.");
        }
        finally
        {
            // ausschließlich der exakt erzeugte Name, niemals --purge/all
            var delete = cli.Run(["vm-delete", "--name", vmName], TimeSpan.FromMinutes(5));
            output.WriteLine($"cleanup vm-delete exit={delete.ExitCode}: {delete.StdOut}{delete.StdErr}");

            if (imageConfigured)
            {
                var imageDelete = cli.Run(["image-delete", "--name", imageName], TimeSpan.FromMinutes(2));
                output.WriteLine($"cleanup image-delete exit={imageDelete.ExitCode}: {imageDelete.StdOut}{imageDelete.StdErr}");
            }
        }
    }

    private async Task<bool> WaitForClaudeAsync(CliRunner cli, string vmName)
    {
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            var result = cli.Run(
                [
                    "vm-exec",
                    "--name", vmName,
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

    private void AssertOk(CliProcessResult result, string operation)
    {
        Assert.False(result.TimedOut, $"{operation} timed out: {result.CommandLine}");
        Assert.True(
            result.ExitCode == 0,
            $"{operation} fehlgeschlagen (Exit {result.ExitCode}).\n{result.CommandLine}\nSTDOUT:\n{result.StdOut}\nSTDERR:\n{result.StdErr}");
    }
}
