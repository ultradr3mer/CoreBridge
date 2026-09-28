using CoreBridge.Core.Git;

namespace CoreBridge.Core.Tests;

public class SandboxRepoTests
{
    [Fact]
    public void Pfade()
    {
        Assert.Equal("/home/ubuntu/repos/CoreBridge.git", SandboxRepo.BareDir("CoreBridge"));
        Assert.Equal("/home/ubuntu/work/CoreBridge", SandboxRepo.WorkDir("CoreBridge"));
        Assert.Equal("/tmp/corebridge-CoreBridge-main.bundle", SandboxRepo.VmBundlePath("CoreBridge", "main"));
        Assert.Equal("refs/remotes/ai-ubuntu-dotnet/claude/20260927-1432", SandboxRepo.TrackingRef("ai-ubuntu-dotnet", "20260927-1432"));
    }

    [Theory]
    [InlineData("20260927-1432", true)]
    [InlineData("t1", true)]
    [InlineData("fix_login.v2", true)]
    [InlineData("", false)]
    [InlineData("-x", false)]
    [InlineData("a/b", false)]
    [InlineData("a..b", false)]
    [InlineData("x.lock", false)]
    [InlineData("mit leer", false)]
    public void IdWirdGeprueft(string id, bool valid)
    {
        Assert.Equal(valid, SandboxRepo.IsValidId(id));
    }

    [Fact]
    public void VmKommandosSindIdempotentUndGequotet()
    {
        Assert.Equal(
            "test -d /home/ubuntu/repos/CoreBridge.git || git init --bare -q --initial-branch=main /home/ubuntu/repos/CoreBridge.git",
            SandboxRepo.InitBareCommand("CoreBridge"));

        var workspace = SandboxRepo.WorkspaceCommand("ai-ubuntu-dotnet", "CoreBridge");
        Assert.StartsWith("if [ -d /home/ubuntu/work/CoreBridge/.git ]; then git -C /home/ubuntu/work/CoreBridge fetch -q origin; else git clone -q /home/ubuntu/repos/CoreBridge.git /home/ubuntu/work/CoreBridge; fi", workspace);
        Assert.Contains("config user.name 'Claude (ai-ubuntu-dotnet)'", workspace);
        Assert.Contains("config user.email 'claude@ai-ubuntu-dotnet.corebridge.local'", workspace);
    }

    [Fact]
    public void BundleKommandos()
    {
        Assert.Equal(
            "git -C /home/ubuntu/repos/CoreBridge.git fetch -q /tmp/b.bundle refs/heads/main:refs/heads/main; rc=$?; rm -f /tmp/b.bundle; exit $rc",
            SandboxRepo.FetchMainBundleCommand("CoreBridge", "/tmp/b.bundle"));
        Assert.Equal(
            "git -C /home/ubuntu/repos/CoreBridge.git bundle create -q /tmp/t.bundle refs/heads/claude/t1 ^abc123",
            SandboxRepo.CreateTaskBundleCommand("CoreBridge", "t1", "/tmp/t.bundle", "abc123"));
        Assert.EndsWith("refs/heads/claude/t1", SandboxRepo.CreateTaskBundleCommand("CoreBridge", "t1", "/tmp/t.bundle", null));
        Assert.Equal(
            "git -C /home/ubuntu/repos/CoreBridge.git for-each-ref --format='%(objectname) %(refname)' refs/heads/main refs/heads/claude/",
            SandboxRepo.ListRefsCommand("CoreBridge", SandboxRepo.MainRef, "refs/heads/claude/"));
    }

    [Fact]
    public void RefsWerdenGeparst()
    {
        var refs = SandboxRepo.ParseRefs("aaa refs/heads/main\r\nbbb refs/heads/claude/t1\n\n");

        Assert.Equal("aaa", refs["refs/heads/main"]);
        Assert.Equal("bbb", refs["refs/heads/claude/t1"]);
        Assert.Equal(2, refs.Count);
    }
}
