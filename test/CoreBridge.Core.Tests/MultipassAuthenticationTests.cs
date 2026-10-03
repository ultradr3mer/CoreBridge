using CoreBridge.Core.Multipass;

namespace CoreBridge.Core.Tests;

public class MultipassAuthenticationTests
{
    [Fact]
    public void SetztPassphraseUndAuthentifiziertMitDenRichtigenArgumenten()
    {
        var cli = new RecordingCli();
        var authentication = new MultipassAuthentication(cli);

        authentication.SetPassphrase("geheim", 1_234);
        authentication.Authenticate("geheim", 1_234);

        Assert.Equal(["set local.passphrase=geheim", "authenticate geheim"], cli.Calls);
        Assert.All(cli.Timeouts, timeout => Assert.Equal(1_234, timeout));
    }

    /// <summary>Jede Passphrase ist neu und enthält nichts, was eine Shell oder ein Argument zerlegt.</summary>
    [Fact]
    public void NeuePassphraseIstZufaelligUndUnbedenklich()
    {
        var passphrases = Enumerable.Range(0, 20).Select(_ => MultipassAuthentication.NewPassphrase()).ToList();

        Assert.Equal(passphrases.Count, passphrases.Distinct().Count());
        Assert.All(passphrases, passphrase =>
        {
            Assert.True(passphrase.Length >= 30);
            Assert.Matches("^[A-Za-z0-9_-]+$", passphrase);
        });
    }

    private sealed class RecordingCli() : MultipassCli(() => null)
    {
        public List<string> Calls { get; } = [];

        public List<int> Timeouts { get; } = [];

        public override MultipassRunResult Run(IReadOnlyList<string> args, int timeoutMs = 30_000, string? exePath = null)
        {
            Calls.Add(string.Join(' ', args));
            Timeouts.Add(timeoutMs);
            return new MultipassRunResult(0, string.Empty, string.Empty, 1);
        }
    }
}
