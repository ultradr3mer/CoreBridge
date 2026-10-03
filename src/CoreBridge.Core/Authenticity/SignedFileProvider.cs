using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using Org.BouncyCastle.Bcpg.OpenPgp;

namespace CoreBridge.Core.Authenticity;

/// <summary>
/// Dateiquelle für den <c>ConfigurationBuilder</c>, die Clearsign-Dateien
/// (<c>gpg --clearsign</c>) prüft und dem JSON-Provider nur den Klartext gibt.
/// Das Parsen bleibt beim normalen <c>AddJsonFile</c>; hier wird nur entschieden,
/// welche Bytes er bekommt (<see cref="ClearsignedContent.Unwrap"/>).
/// <paramref name="requireSignature"/> gibt an, ob auch unsignierter Klartext
/// geladen oder direkt abgebrochen wird. Eine ungültige Signatur bricht immer ab,
/// mit <see cref="SettingsSignatureException"/> (Exit 3).
/// </summary>
public sealed class SignedFileProvider(
    string root,
    bool requireSignature,
    PgpPublicKeyRingBundle? trusted = null,
    TimeProvider? time = null) : IFileProvider
{
    private readonly PhysicalFileProvider files = new(root);
    private readonly PgpPublicKeyRingBundle trusted = trusted ?? TrustedKeys.CoreBridge;
    private readonly TimeProvider time = time ?? TimeProvider.System;

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
        return ClearsignedContent.Unwrap(
                file.PhysicalPath ?? file.Name, buffer.ToArray(), requireSignature, trusted, time.GetUtcNow().UtcDateTime);
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
