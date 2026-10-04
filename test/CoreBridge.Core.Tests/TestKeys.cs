using System.Text;
using CoreBridge.Core.Authenticity;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace CoreBridge.Core.Tests;

/// <summary>
/// Wegwerf-Schlüssel für Signaturtests: Ed25519 (v4 EdDSA, wie der echte CoreBridge-Schlüssel),
/// signiert mit <see cref="PgpFileSigner"/> aus tools/CoreBridge.Signer über den armored
/// Secret-Key-Export — derselbe Weg wie beim Signieren außerhalb der Tests.
/// </summary>
internal sealed record TestKey(byte[] ArmoredSecret, byte[] ArmoredPublic, PgpPublicKeyRingBundle Public, string Fingerprint)
{
    public const string Passphrase = "test-passphrase";

    public static TestKey Create(string userId = "Test <test@corebridge.invalid>", DateTime? created = null, long expiresAfterSeconds = 0)
    {
        var random = new SecureRandom();
        var generator = new Ed25519KeyPairGenerator();
        generator.Init(new Ed25519KeyGenerationParameters(random));
        var pair = new PgpKeyPair(PublicKeyAlgorithmTag.EdDsa_Legacy, generator.GenerateKeyPair(), created ?? DateTime.UtcNow.AddMinutes(-1));

        var hashed = new PgpSignatureSubpacketGenerator();
        hashed.SetKeyFlags(false, PgpKeyFlags.CanSign | PgpKeyFlags.CanCertify);
        if (expiresAfterSeconds > 0)
        {
            hashed.SetKeyExpirationTime(false, expiresAfterSeconds);
        }

        var ring = new PgpKeyRingGenerator(
            PgpSignature.PositiveCertification,
            pair,
            userId,
            SymmetricKeyAlgorithmTag.Aes256,
            HashAlgorithmTag.Sha256,
            Passphrase.ToCharArray(),
            false,
            hashed.Generate(),
            null,
            random);

        using var secret = new MemoryStream();
        using (var armored = new ArmoredOutputStream(secret))
        {
            ring.GenerateSecretKeyRing().Encode(armored);
        }

        var publicRing = ring.GeneratePublicKeyRing();
        using var armoredPublic = new MemoryStream();
        using (var armored = new ArmoredOutputStream(armoredPublic))
        {
            publicRing.Encode(armored);
        }

        using var pub = new MemoryStream();
        publicRing.Encode(pub);
        pub.Position = 0;
        return new TestKey(
            secret.ToArray(),
            armoredPublic.ToArray(),
            TrustedKeys.Read(pub),
            Convert.ToHexString(publicRing.GetPublicKey().GetFingerprint()));
    }

    /// <summary>Abgetrennte, armored Signatur (<c>&lt;datei&gt;.asc</c>).</summary>
    public byte[] Sign(string content) => Sign(Encoding.UTF8.GetBytes(content));

    public byte[] Sign(byte[] content)
    {
        using var output = new MemoryStream();
        PgpFileSigner.Sign(new MemoryStream(content), output, SigningKey());
        return output.ToArray();
    }

    /// <summary>Klartextsignatur wie <c>gpg --clearsign</c> (<c>&lt;datei&gt;.clearsigned.asc</c>).</summary>
    public byte[] ClearSign(byte[] content)
    {
        using var output = new MemoryStream();
        PgpFileSigner.ClearSign(content, output, SigningKey());
        return output.ToArray();
    }

    /// <summary>Signiert <paramref name="file"/> mit <c>&lt;file&gt;.clearsigned.asc</c> daneben.</summary>
    public void ClearSignFile(string file) =>
        File.WriteAllBytes(file + SettingsSignatures.ClearsignedExtension, ClearSign(File.ReadAllBytes(file)));

    private SigningKey SigningKey() =>
        PgpFileSigner.ReadSigningKey(new MemoryStream(ArmoredSecret), Passphrase.ToCharArray());
}
