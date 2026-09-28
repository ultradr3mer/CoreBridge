using Org.BouncyCastle.Bcpg.OpenPgp;

namespace CoreBridge.Core.Authenticity;

/// <summary>
/// Die öffentlichen Schlüssel, denen CoreBridge beim Laden signierter Dateien
/// vertraut. Sie stecken als Ressource in der Core-DLL und nicht in einer
/// Config-Datei, sonst könnte eine manipulierte Config ihren eigenen Schlüssel
/// mitbringen.
/// </summary>
public static class TrustedKeys
{
    /// <summary>Fingerprint des CoreBridge-Signierschlüssels (<c>CoreBridge &lt;core@brid.ge&gt;</c>, Ed25519).</summary>
    public const string CoreBridgeFingerprint = "7D67FF405BAA2BB53027F57280E7816D2A7F227B";

    private const string ResourceName = "CoreBridge.Core.Authenticity.corebridge-signing.pub.asc";

    private static readonly Lazy<PgpPublicKeyRingBundle> Bundle = new(LoadEmbedded);

    /// <summary>
    /// Das Bundle mit dem eingebetteten CoreBridge-Schlüssel. Passt die Ressource
    /// nicht zum festen <see cref="CoreBridgeFingerprint"/>, ist das ein Bug im
    /// Build, kein Laufzeitfall.
    /// </summary>
    public static PgpPublicKeyRingBundle CoreBridge => Bundle.Value;

    /// <summary>Liest ein armored oder binäres Public-Key-Bundle (z. B. <c>gpg --export --armor</c>).</summary>
    public static PgpPublicKeyRingBundle Read(Stream keys)
    {
        return new PgpPublicKeyRingBundle(PgpUtilities.GetDecoderStream(keys));
    }

    private static PgpPublicKeyRingBundle LoadEmbedded()
    {
        using var stream = typeof(TrustedKeys).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Eingebettete Ressource {ResourceName} fehlt.");
        var bundle = Read(stream);

        var fingerprints = bundle.GetKeyRings()
            .Select(ring => Convert.ToHexString(ring.GetPublicKey().GetFingerprint()))
            .ToList();
        if (fingerprints is not [CoreBridgeFingerprint])
        {
            throw new InvalidOperationException(
                $"{ResourceName} enthält [{string.Join(", ", fingerprints)}], erwartet genau {CoreBridgeFingerprint}.");
        }

        return bundle;
    }
}
