using CoreBridge.Signer;

// Aufruf: CoreBridge.Signer --key <secret.asc> [--key-id <fingerprint>] [--clearsign] <datei>…
// Schreibt je Datei <datei>.asc (abgetrennt) bzw. mit --clearsign <datei>.clearsigned.asc
// (Klartextsignatur; so signieren sich Bundles und Komponenten-Skripte). Die Passphrase
// kommt aus CORE_BRIDGE_SIGNING_PASSWORD, ein ungeschützter Schlüssel braucht keine, sonst
// wird sie verdeckt abgefragt. Für CI ohne gpg-agent; lokal genügt scripts/sign-settings.ps1.

string? keyFile = null;
string? keyId = null;
var clearSign = false;
var files = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--key" when i + 1 < args.Length:
            keyFile = args[++i];
            break;
        case "--key-id" when i + 1 < args.Length:
            keyId = args[++i];
            break;
        case "--clearsign":
            clearSign = true;
            break;
        case "-h" or "--help":
            PrintUsage();
            return 0;
        default:
            files.Add(args[i]);
            break;
    }
}

if (keyFile is null || files.Count == 0)
{
    PrintUsage();
    return 1;
}

var passphrase = (Environment.GetEnvironmentVariable("CORE_BRIDGE_SIGNING_PASSWORD") ?? string.Empty).ToCharArray();
try
{
    SigningKey key;
    try
    {
        key = ReadKey(passphrase);
    }
    catch (Org.BouncyCastle.Bcpg.OpenPgp.PgpException) when (passphrase.Length == 0)
    {
        // Geschützter Schlüssel ohne Variable: jetzt erst fragen.
        passphrase = ReadHidden("Passphrase: ").ToCharArray();
        key = ReadKey(passphrase);
    }

    foreach (var file in files)
    {
        var signaturePath = file + (clearSign ? ".clearsigned.asc" : ".asc");
        using (var output = File.Create(signaturePath))
        {
            if (clearSign)
            {
                PgpFileSigner.ClearSign(File.ReadAllBytes(file), output, key);
            }
            else
            {
                using var data = File.OpenRead(file);
                PgpFileSigner.Sign(data, output, key);
            }
        }

        Console.WriteLine($"signiert: {signaturePath} ({key.Fingerprint})");
    }

    return 0;
}
catch (Exception exception) when (exception is Org.BouncyCastle.Bcpg.OpenPgp.PgpException or IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"Signieren fehlgeschlagen: {exception.Message}");
    return 1;
}
finally
{
    Array.Clear(passphrase);
}

SigningKey ReadKey(char[] secret)
{
    using var keyStream = File.OpenRead(keyFile!);
    return PgpFileSigner.ReadSigningKey(keyStream, secret, keyId);
}

static void PrintUsage()
{
    Console.Error.WriteLine("Aufruf: CoreBridge.Signer --key <secret.asc> [--key-id <fingerprint>] [--clearsign] <datei>…");
}

static string ReadHidden(string prompt)
{
    Console.Error.Write(prompt);
    var text = new System.Text.StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
        {
            Console.Error.WriteLine();
            return text.ToString();
        }

        if (key.Key == ConsoleKey.Backspace)
        {
            if (text.Length > 0)
            {
                text.Length--;
            }
        }
        else
        {
            text.Append(key.KeyChar);
        }
    }
}
