using CoreBridge.Core.Multipass;

namespace CoreBridge.Core.Tests;

public class MultipassStorageTests
{
    [Fact]
    public void OhneVariableGiltDerStandardort()
    {
        var storage = new FakeStorage();

        var current = storage.Current();

        Assert.Equal(MultipassStorageSource.Default, current.Source);
        Assert.Equal(MultipassStorage.DefaultPath, current.Path);
        Assert.EndsWith("Multipass", current.Path);
    }

    [Fact]
    public void SystemVariableWirdGelesen()
    {
        var storage = new FakeStorage { System = @"D:\VirtalHardDrives" };

        Assert.Equal(new MultipassStorageLocation(@"D:\VirtalHardDrives", MultipassStorageSource.System), storage.Current());
    }

    /// <summary>Die Dienst-Umgebung überlagert die geerbte System-Variable — sie gewinnt.</summary>
    [Fact]
    public void DienstUmgebungGewinntVorSystemVariable()
    {
        var storage = new FakeStorage
        {
            System = @"D:\Alt",
            Service = ["OTHER=1", @"MULTIPASS_STORAGE=E:\Neu"]
        };

        Assert.Equal(new MultipassStorageLocation(@"E:\Neu", MultipassStorageSource.Service), storage.Current());
    }

    [Fact]
    public void SetzenSchreibtBeideStellenUndBehaeltAndereEintraege()
    {
        var storage = new FakeStorage { Service = ["OTHER=1", @"MULTIPASS_STORAGE=D:\Alt"] };

        storage.Set(@"E:\Multipass");

        Assert.Equal(@"E:\Multipass", storage.System);
        Assert.Equal(["OTHER=1", @"MULTIPASS_STORAGE=E:\Multipass"], storage.Service);
        Assert.Equal(MultipassStorageSource.Service, storage.Current().Source);
    }

    [Fact]
    public void ZuruecksetzenEntferntBeideStellen()
    {
        var storage = new FakeStorage { System = @"D:\Alt", Service = [@"MULTIPASS_STORAGE=D:\Alt"] };

        storage.Set(null);

        Assert.Null(storage.System);
        Assert.Empty(storage.Service!);
        Assert.Equal(MultipassStorageSource.Default, storage.Current().Source);
    }

    [Fact]
    public void OhneAdminrechteZugriffVerweigert()
    {
        var storage = new FakeStorage { Denied = true };

        Assert.Throws<MultipassAccessDeniedException>(() => storage.Set(@"E:\Multipass"));
    }

    [Theory]
    [InlineData(@"D:\Multipass", @"d:\multipass\", true)]
    [InlineData(@"D:\Multipass", @"D:\Multipass2", false)]
    public void SamePathIgnoriertSchreibweiseUndTrenner(string left, string right, bool expected)
    {
        Assert.Equal(expected, MultipassStorage.SamePath(left, right));
    }

    private sealed class FakeStorage : MultipassStorage
    {
        public string? System { get; set; }

        public string[]? Service { get; set; }

        public bool Denied { get; init; }

        protected override string? ReadSystemVariable() => System;

        protected override void WriteSystemVariable(string? path)
        {
            if (Denied)
            {
                throw new UnauthorizedAccessException("HKLM (Test)");
            }

            System = path;
        }

        protected override string[]? ReadServiceEnvironment() => Service;

        protected override void WriteServiceEnvironment(string[] environment) => Service = environment;
    }
}
