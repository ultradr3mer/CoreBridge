using System.Text;
using CoreBridge.Core.Authenticity;
using CoreBridge.Signer;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace CoreBridge.Core.Tests;

/// <summary>
/// Signaturprüfung mit im Test erzeugten Ed25519-Schlüsseln (wie der echte
/// CoreBridge-Schlüssel, v4 EdDSA); signiert wird mit <see cref="PgpFileSigner"/>
/// aus tools/CoreBridge.Signer über den armored Secret-Key-Export.
/// </summary>
public class PgpFileVerifierTests
{
    private const string Passphrase = "test-passphrase";
    private const string Json = "{\r\n  \"Images\": {\r\n    \"StoreRoot\": \"artifacts\"\r\n  }\r\n}\r\n";

    private sealed record TestKey(byte[] ArmoredSecret, PgpPublicKeyRingBundle Public);

    private static TestKey CreateKey(DateTime? created = null, long expiresAfterSeconds = 0)
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
            "Test <test@corebridge.invalid>",
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

        using var pub = new MemoryStream();
        ring.GeneratePublicKeyRing().Encode(pub);
        pub.Position = 0;
        return new TestKey(secret.ToArray(), TrustedKeys.Read(pub));
    }

    private static byte[] Sign(TestKey key, string content)
    {
        var signingKey = PgpFileSigner.ReadSigningKey(new MemoryStream(key.ArmoredSecret), Passphrase.ToCharArray());
        using var output = new MemoryStream();
        PgpFileSigner.Sign(new MemoryStream(Encoding.UTF8.GetBytes(content)), output, signingKey);
        return output.ToArray();
    }

    private static SignatureCheck Verify(string content, byte[] signature, PgpPublicKeyRingBundle trusted, DateTime? now = null)
    {
        return PgpFileVerifier.Verify(
            "appsettings.json",
            new MemoryStream(Encoding.UTF8.GetBytes(content)),
            new MemoryStream(signature),
            trusted,
            now ?? DateTime.UtcNow);
    }

    [Fact]
    public void Verify_UnveraenderteDateiIstGueltig()
    {
        var key = CreateKey();
        var check = Verify(Json, Sign(key, Json), key.Public);

        Assert.True(check.Valid, check.Reason);
        Assert.Equal(Convert.ToHexString(key.Public.GetKeyRings().Single().GetPublicKey().GetFingerprint()), check.Fingerprint);
    }

    [Fact]
    public void Verify_EinGeaendertesZeichenIstUngueltig()
    {
        var key = CreateKey();
        var check = Verify(Json.Replace("artifacts", "artifactz"), Sign(key, Json), key.Public);

        Assert.False(check.Valid);
        Assert.Contains("Inhalt passt nicht", check.Reason);
    }

    [Fact]
    public void Verify_NurAndereZeilenendenBleibtGueltig()
    {
        // core.autocrlf kann CRLF und LF tauschen; CanonicalTextDocument gleicht das aus.
        var key = CreateKey();
        var signature = Sign(key, Json);

        Assert.True(Verify(Json.Replace("\r\n", "\n"), signature, key.Public).Valid);
    }

    [Fact]
    public void Verify_FremderSchluesselWirdAbgelehnt()
    {
        var signer = CreateKey();
        var other = CreateKey();
        var check = Verify(Json, Sign(signer, Json), other.Public);

        Assert.False(check.Valid);
        Assert.Contains("unbekanntem Schlüssel", check.Reason);
    }

    [Fact]
    public void Verify_AbgelaufenerSchluesselWirdAbgelehnt()
    {
        var key = CreateKey(created: DateTime.UtcNow.AddDays(-10), expiresAfterSeconds: 86400);
        var check = Verify(Json, Sign(key, Json), key.Public);

        Assert.False(check.Valid);
        Assert.Contains("abgelaufen", check.Reason);
    }

    [Fact]
    public void Verify_NochGueltigerSchluesselMitAblaufdatumIstGueltig()
    {
        var key = CreateKey(expiresAfterSeconds: 3 * 365 * 86400L);

        Assert.True(Verify(Json, Sign(key, Json), key.Public).Valid);
    }

    [Fact]
    public void Verify_KeineSignaturImInhaltIstUngueltig()
    {
        var key = CreateKey();
        var check = Verify(Json, Encoding.ASCII.GetBytes("kein OpenPGP"), key.Public);

        Assert.False(check.Valid);
    }

    [Fact]
    public void VerifyFile_FehlendeSignaturIstUngueltig()
    {
        var directory = Directory.CreateTempSubdirectory("cb-sig-");
        try
        {
            var file = Path.Combine(directory.FullName, "appsettings.json");
            File.WriteAllText(file, Json);

            var check = PgpFileVerifier.VerifyFile(file, CreateKey().Public, DateTime.UtcNow);

            Assert.False(check.Valid);
            Assert.Contains("Signatur fehlt", check.Reason);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void SettingsSignatures_PrueftUmgebungsdateiNurWennSieExistiert()
    {
        var key = CreateKey();
        var directory = Directory.CreateTempSubdirectory("cb-sig-");
        try
        {
            var main = Path.Combine(directory.FullName, "appsettings.json");
            File.WriteAllText(main, Json);
            File.WriteAllBytes(main + ".asc", Sign(key, Json));

            var onlyMain = SettingsSignatures.Check(directory.FullName, "Development", key.Public, DateTime.UtcNow);
            Assert.Single(onlyMain);
            SettingsSignatures.RequireValid(onlyMain);

            File.WriteAllText(Path.Combine(directory.FullName, "appsettings.Development.json"), "{}");
            var withEnvironment = SettingsSignatures.Check(directory.FullName, "Development", key.Public, DateTime.UtcNow);
            var failure = Assert.Throws<SettingsSignatureException>(() => SettingsSignatures.RequireValid(withEnvironment));
            Assert.Contains("appsettings.Development.json", failure.Message);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void TrustedKeys_EingebetteterSchluesselIstDerCoreBridgeSchluessel()
    {
        var master = TrustedKeys.CoreBridge.GetKeyRings().Single().GetPublicKey();

        Assert.Equal(TrustedKeys.CoreBridgeFingerprint, Convert.ToHexString(master.GetFingerprint()));
        Assert.Equal(PublicKeyAlgorithmTag.EdDsa_Legacy, master.Algorithm);
    }
}
