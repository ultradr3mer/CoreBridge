using Org.BouncyCastle.Bcpg.OpenPgp;

namespace CoreBridge.Core.Authenticity;

/// <summary>Ein öffentlicher Schlüssel, dem der Benutzer vertraut (<c>key-trust</c>).</summary>
public sealed record TrustedKeyEntry(string File, string Fingerprint, string? UserId, DateTime Created, DateTime? Expires);

/// <summary>
/// Schlüssel, denen der <em>Benutzer</em> vertraut: <c>&lt;StoreRoot&gt;/trusted-keys/*.asc</c>,
/// nur öffentliche Schlüssel. Sie gelten ausschließlich für Komponenten und Bundles —
/// nie für die mitgelieferten <c>appsettings*.json</c>, die allein der eingebettete
/// <see cref="TrustedKeys.CoreBridge"/> deckt. Eine Signatur damit heißt
/// „Benutzer-vertraut“, sichtbar anders als „CoreBridge“.
/// </summary>
public sealed class TrustedKeyStore(string folder)
{
    /// <summary>Name des Ordners unter dem StoreRoot.</summary>
    public const string FolderName = "trusted-keys";

    public string Folder { get; } = folder;

    /// <summary>Alle gültig lesbaren Schlüssel des Ordners, nach Datei sortiert; ein fehlender Ordner ist leer.</summary>
    public IReadOnlyList<TrustedKeyEntry> List()
    {
        return Files()
            .SelectMany(file => Rings(file).Select(ring => Entry(file, ring)))
            .ToList();
    }

    /// <summary>Die Benutzer-Schlüssel als Bundle (ohne den CoreBridge-Schlüssel).</summary>
    public PgpPublicKeyRingBundle Bundle()
    {
        var bundle = new PgpPublicKeyRingBundle(Array.Empty<PgpPublicKeyRing>());
        foreach (var ring in Files().SelectMany(Rings))
        {
            if (bundle.GetPublicKeyRing(ring.GetPublicKey().KeyId) is null)
            {
                bundle = PgpPublicKeyRingBundle.AddPublicKeyRing(bundle, ring);
            }
        }

        return bundle;
    }

    /// <summary>
    /// <paramref name="builtIn"/> (der CoreBridge-Schlüssel) plus die Benutzer-Schlüssel —
    /// das Bundle für Komponenten und Bundles.
    /// </summary>
    public PgpPublicKeyRingBundle Combined(PgpPublicKeyRingBundle builtIn)
    {
        var bundle = builtIn;
        foreach (PgpPublicKeyRing ring in Bundle().GetKeyRings())
        {
            if (bundle.GetPublicKeyRing(ring.GetPublicKey().KeyId) is null)
            {
                bundle = PgpPublicKeyRingBundle.AddPublicKeyRing(bundle, ring);
            }
        }

        return bundle;
    }

    /// <summary>
    /// Nimmt die öffentlichen Schlüssel aus <paramref name="publicKeyFile"/> auf
    /// (armored oder binär, z. B. <c>gpg --export --armor</c>) und legt je Schlüssel
    /// <c>&lt;fingerprint&gt;.asc</c> ab. Geheime Schlüssel werden abgelehnt.
    /// </summary>
    public IReadOnlyList<TrustedKeyEntry> Trust(string publicKeyFile)
    {
        if (!File.Exists(publicKeyFile))
        {
            throw new InvalidDataException($"Schlüsseldatei fehlt: {Path.GetFullPath(publicKeyFile)}");
        }

        var bytes = File.ReadAllBytes(publicKeyFile);
        if (System.Text.Encoding.ASCII.GetString(bytes).Contains("PRIVATE KEY BLOCK", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Das ist ein geheimer Schlüssel — vertraut wird nur dem öffentlichen Teil (gpg --export --armor <fingerprint>).");
        }

        PgpPublicKeyRingBundle bundle;
        try
        {
            bundle = TrustedKeys.Read(new MemoryStream(bytes));
        }
        catch (Exception exception) when (exception is PgpException or IOException)
        {
            throw new InvalidDataException($"{Path.GetFileName(publicKeyFile)} enthält keinen lesbaren öffentlichen OpenPGP-Schlüssel: {exception.Message}", exception);
        }

        var rings = bundle.GetKeyRings().Cast<PgpPublicKeyRing>().ToList();
        if (rings.Count == 0)
        {
            throw new InvalidDataException($"{Path.GetFileName(publicKeyFile)} enthält keinen öffentlichen Schlüssel.");
        }

        Directory.CreateDirectory(Folder);
        var added = new List<TrustedKeyEntry>();
        foreach (var ring in rings)
        {
            var fingerprint = Convert.ToHexString(ring.GetPublicKey().GetFingerprint());
            if (fingerprint == TrustedKeys.CoreBridgeFingerprint)
            {
                throw new InvalidDataException("Der CoreBridge-Schlüssel ist eingebettet und braucht keinen Eintrag.");
            }

            var target = Path.Combine(Folder, fingerprint + ".asc");
            using (var output = File.Create(target))
            using (var armored = new Org.BouncyCastle.Bcpg.ArmoredOutputStream(output))
            {
                ring.Encode(armored);
            }

            added.Add(Entry(target, ring));
        }

        return added;
    }

    /// <summary>
    /// Entfernt den Schlüssel, dessen Fingerprint mit <paramref name="fingerprint"/> endet
    /// (voll oder mindestens die 16 Stellen der Key-ID, Leerzeichen egal).
    /// </summary>
    public TrustedKeyEntry Untrust(string fingerprint)
    {
        var wanted = fingerprint.Replace(" ", string.Empty).ToUpperInvariant();
        if (wanted.Length < 16)
        {
            throw new InvalidDataException("Mindestens die 16 letzten Stellen des Fingerprints angeben (Übersicht: cb key-list).");
        }

        var matches = List().Where(entry => entry.Fingerprint.EndsWith(wanted, StringComparison.Ordinal)).ToList();
        if (matches.Count != 1)
        {
            throw new InvalidDataException(matches.Count == 0
                ? $"Kein vertrauter Schlüssel {wanted} (Übersicht: cb key-list)."
                : $"{wanted} passt auf mehrere Schlüssel — vollen Fingerprint angeben.");
        }

        File.Delete(matches[0].File);
        return matches[0];
    }

    private IEnumerable<string> Files() =>
        Directory.Exists(Folder)
            ? Directory.EnumerateFiles(Folder, "*.asc").Order(StringComparer.OrdinalIgnoreCase)
            : [];

    /// <summary>Unlesbare Dateien zählen nicht — ein kaputter Eintrag darf nichts freischalten.</summary>
    private static IEnumerable<PgpPublicKeyRing> Rings(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            return TrustedKeys.Read(stream).GetKeyRings().Cast<PgpPublicKeyRing>().ToList();
        }
        catch (Exception exception) when (exception is PgpException or IOException)
        {
            return [];
        }
    }

    private static TrustedKeyEntry Entry(string file, PgpPublicKeyRing ring)
    {
        var key = ring.GetPublicKey();
        var valid = key.GetValidSeconds();
        return new TrustedKeyEntry(
            file,
            Convert.ToHexString(key.GetFingerprint()),
            key.GetUserIds().FirstOrDefault(),
            key.CreationTime,
            valid > 0 ? key.CreationTime.AddSeconds(valid) : null);
    }
}
