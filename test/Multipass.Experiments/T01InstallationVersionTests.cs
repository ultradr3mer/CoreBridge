using CoreBridge.Core.Multipass;
using CoreBridge.Core.Vm;

namespace Multipass.Experiments;

[Collection("MultipassExperiments")]
[Trait("Category", "Integration")]
public class T01InstallationVersionTests : MultipassExperiment
{
    [MultipassFact]
    public void VersionLiefertJsonMitCliVersion()
    {
        var result = RunExperiment(
            "T01",
            "Installation & `version`",
            new[] { "version", "--format", "json" },
            60_000,
            r =>
            {
                var (client, daemon) = MultipassVersion.Parse(r.StdOut);
                return $"client={client ?? "unbekannt"}, daemon={daemon ?? "keine Antwort"}";
            });

        Assert.Equal(0, result.ExitCode);
        var (clientVersion, daemonVersion) = MultipassVersion.Parse(result.StdOut);
        Assert.False(string.IsNullOrWhiteSpace(clientVersion), $"multipass.version fehlt in: {result.StdOut}");
    }
}
