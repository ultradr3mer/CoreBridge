using CoreBridge.Core.Inits;
using CoreBridge.Core.Multipass;

namespace CoreBridge.Core.Tests;

public class HostSshKeyTests
{
    [Fact]
    public void ErzeugtDasSchluesselpaarEinmalUndBehaeltEs()
    {
        using var temp = new TempDirectory();

        var path = HostSshKey.EnsurePublicKeyPath(temp.Path);
        var first = HostSshKey.ReadPublicKey(path);
        var again = HostSshKey.ReadPublicKey(HostSshKey.EnsurePublicKeyPath(temp.Path));

        Assert.StartsWith("ssh-ed25519 ", first);
        Assert.True(File.Exists(HostSshKey.PrivateKeyPath(temp.Path)));
        Assert.Equal(first, again);
    }

    [Fact]
    public void LehntMehrzeiligeOderFremdeDateienAb()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "bad.pub");
        File.WriteAllText(path, "-----BEGIN OPENSSH PRIVATE KEY-----");

        Assert.Throws<MultipassException>(() => HostSshKey.ReadPublicKey(path));
    }
}
