using System.Text.Json;
using Org.BouncyCastle.Bcpg.OpenPgp;

namespace CoreBridge.Core.Authenticity;

/// <summary>
/// Liest JSON als <typeparamref name="T"/>, unsigniert oder mit
/// <c>gpg --clearsign</c> signiert. Unsigniert wird nur deserialisiert, ohne
/// Aufruf von <see cref="ISignature.OnSignature"/>. Signiert wird der Klartext
/// deserialisiert und danach gegen <paramref name="trusted"/> geprüft; das
/// Ergebnis geht an <see cref="ISignature.OnSignature"/>. Deserialisiert wird
/// auch bei fehlgeschlagener Prüfung — was daraus folgt, entscheidet der Aufrufer
/// anhand von <see cref="SignatureInfo.Status"/>.
/// </summary>
public sealed class SignedJsonProvider<T>(
    PgpPublicKeyRingBundle trusted,
    JsonSerializerOptions? options = null,
    TimeProvider? time = null)
    where T : ISignature
{
    private readonly PgpPublicKeyRingBundle trusted = trusted ?? throw new ArgumentNullException(nameof(trusted));
    private readonly TimeProvider time = time ?? TimeProvider.System;

    /// <summary>Liest <paramref name="path"/>; <paramref name="path"/> steht auch in <see cref="SignatureInfo.Reason"/>-Kontexten.</summary>
    public T? ReadFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream, path);
    }

    /// <summary>Liest <paramref name="json"/>; <paramref name="name"/> bezeichnet die Quelle.</summary>
    public T? Read(Stream json, string name = "<stream>")
    {
        ArgumentNullException.ThrowIfNull(json);

        using var buffer = new MemoryStream();
        json.CopyTo(buffer);
        var bytes = buffer.ToArray();

        if (!ClearsignedMessage.TryParse(bytes, out var message))
        {
            return JsonSerializer.Deserialize<T>(bytes, options);
        }

        var result = JsonSerializer.Deserialize<T>(message!.Cleartext, options);
        if (result is not null)
        {
            var check = PgpFileVerifier.VerifyClearsigned(name, message, trusted, time.GetUtcNow().UtcDateTime);
            result.OnSignature(SignatureInfo.From(check));
        }

        return result;
    }
}
