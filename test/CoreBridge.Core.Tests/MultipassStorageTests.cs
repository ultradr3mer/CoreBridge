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

    /// <summary>
    /// Wie aufgenommen: der frische Ordner hat eine leere multipassd.conf — die alte kommt mit
    /// (samt Passphrase), sonst nichts: weder vertraute Zertifikate noch VMs.
    /// </summary>
    [Fact]
    public void CopySettingsNimmtEinstellungenInFrischenOrdnerMit()
    {
        using var folders = new TempFolders();
        folders.Write(folders.From, "multipassd.conf", "[local] passphrase=hash");
        folders.Write(folders.From, @"data\authenticated-certs\multipass_client_certs.pem", "CERT");
        folders.Write(folders.From, @"data\vault\instances\vm\cloud-init-config.iso", "VM");
        folders.Write(folders.To, "multipassd.conf", string.Empty);

        Assert.True(MultipassStorage.CopySettings(folders.From, folders.To));

        Assert.Equal("[local] passphrase=hash", folders.Read(folders.To, "multipassd.conf"));
        Assert.False(File.Exists(Path.Combine(folders.To, @"data\authenticated-certs\multipass_client_certs.pem")));
        Assert.False(File.Exists(Path.Combine(folders.To, @"data\vault\instances\vm\cloud-init-config.iso")));
    }

    /// <summary>Ein Ordner mit eigenen Einstellungen (Zurückwechseln) bleibt unberührt.</summary>
    [Fact]
    public void CopySettingsLaesstEigeneEinstellungenDesZielsStehen()
    {
        using var folders = new TempFolders();
        folders.Write(folders.From, "multipassd.conf", "neu");
        folders.Write(folders.To, "multipassd.conf", "alt");

        Assert.False(MultipassStorage.CopySettings(folders.From, folders.To));
        Assert.Equal("alt", folders.Read(folders.To, "multipassd.conf"));
    }

    [Fact]
    public void CopySettingsOhneQuelleOderAufDenselbenOrtKopiertNichts()
    {
        using var folders = new TempFolders();

        Assert.False(MultipassStorage.CopySettings(folders.From, folders.To));
        Assert.False(MultipassStorage.CopySettings(folders.From, folders.From + Path.DirectorySeparatorChar));
    }

    private sealed class TempFolders : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), $"cbx-storage-{Guid.NewGuid():N}");

        public string From => Path.Combine(this.root, "from");

        public string To => Path.Combine(this.root, "to");

        public void Write(string folder, string relative, string content)
        {
            var path = Path.Combine(folder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public string Read(string folder, string relative) => File.ReadAllText(Path.Combine(folder, relative));

        public void Dispose()
        {
            if (Directory.Exists(this.root))
            {
                Directory.Delete(this.root, recursive: true);
            }
        }
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
