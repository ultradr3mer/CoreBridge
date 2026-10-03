using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using Org.BouncyCastle.Bcpg.OpenPgp;

namespace CoreBridge.Core.Authenticity;

/// <summary>
/// Dateiquelle für den <c>ConfigurationBuilder</c>, die Signaturen prüft
/// (<see cref="SettingsSignatures.Unwrap"/>) und dem JSON-Provider nur den zu
/// ladenden Inhalt gibt. Das Parsen bleibt beim normalen <c>AddJsonFile</c>.
/// <paramref name="policy"/> bestimmt je Dateiname, was eine fehlende Signatur
/// bedeutet; eine ungültige bricht ab (außer bei
/// <see cref="SignaturePolicy.Ignore"/>), mit
/// <see cref="SettingsSignatureException"/> (Exit 3). Unsignierte Dateien unter
/// <see cref="SignaturePolicy.Warn"/> stehen nach dem <c>Build()</c> in
/// <see cref="Warnings"/>.
/// </summary>
public sealed class SignedFileProvider(
    string root,
    Func<string, SignaturePolicy> policy,
    PgpPublicKeyRingBundle? trusted = null,
    TimeProvider? time = null) : IFileProvider
{
    private readonly PhysicalFileProvider files = new(root);
    private readonly List<SignatureCheck> warnings = [];
    private readonly PgpPublicKeyRingBundle trusted = trusted ?? TrustedKeys.CoreBridge;
    private readonly TimeProvider time = time ?? TimeProvider.System;

    /// <summary>Unsignierte Dateien, die laut Stufe eine Warnung wert sind.</summary>
    public IReadOnlyList<SignatureCheck> Warnings
    {
        get
        {
            lock (warnings)
            {
                return warnings.ToList();
            }
        }
    }

    public IFileInfo GetFileInfo(string subpath)
    {
        var file = files.GetFileInfo(subpath);
        return file.Exists && !file.IsDirectory ? new SignedFileInfo(file, this) : file;
    }

    public IDirectoryContents GetDirectoryContents(string subpath) => files.GetDirectoryContents(subpath);

    public IChangeToken Watch(string filter) => files.Watch(filter);

    private byte[] Read(IFileInfo file)
    {
        using var stream = file.CreateReadStream();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var found = new List<SignatureCheck>();
        var content = SettingsSignatures.Unwrap(
            file.PhysicalPath ?? file.Name, buffer.ToArray(), policy(file.Name), trusted, time.GetUtcNow().UtcDateTime, found);
        lock (warnings)
        {
            warnings.AddRange(found);
        }

        return content;
    }

    /// <summary>
    /// <see cref="PhysicalPath"/> ist absichtlich <c>null</c>: sonst öffnet der
    /// <c>FileConfigurationProvider</c> die Datei direkt und umgeht
    /// <see cref="CreateReadStream"/> samt Prüfung.
    /// </summary>
    private sealed class SignedFileInfo(IFileInfo file, SignedFileProvider provider) : IFileInfo
    {
        private readonly Lazy<byte[]> content = new(() => provider.Read(file));

        public bool Exists => true;

        public bool IsDirectory => false;

        public long Length => content.Value.Length;

        public string? PhysicalPath => null;

        public string Name => file.Name;

        public DateTimeOffset LastModified => file.LastModified;

        public Stream CreateReadStream() => new MemoryStream(content.Value, writable: false);
    }
}
