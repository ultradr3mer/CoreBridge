using System.Text;

namespace CoreBridge.Core.Authenticity;

/// <summary>
/// Eine mit <c>gpg --clearsign</c> signierte Textdatei, zerlegt in Klartext und
/// Signaturblock (RFC 4880 §7). <see cref="Cleartext"/> ist der Inhalt, wie er
/// in der Datei steht (Dash-Escaping entfernt, Zeilenenden unverändert) — das
/// bekommt z. B. der JSON-Parser. <see cref="CanonicalText"/> sind die signierten
/// Bytes: Leerzeichen/Tabs am Zeilenende entfernt, CRLF zwischen den Zeilen, kein
/// Zeilenende nach der letzten Zeile. <see cref="Signature"/> ist der armored
/// Signaturblock.
/// </summary>
public sealed record ClearsignedMessage(byte[] Cleartext, byte[] CanonicalText, byte[] Signature)
{
    private const string MessageHeader = "-----BEGIN PGP SIGNED MESSAGE-----";
    private const string SignatureHeader = "-----BEGIN PGP SIGNATURE-----";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Erkennt eine Clearsign-Datei am Kopf <c>-----BEGIN PGP SIGNED MESSAGE-----</c>
    /// (nach optionalem BOM und Leerraum). Ohne diesen Kopf ist der Inhalt Klartext
    /// und das Ergebnis <c>false</c>. Ein Kopf ohne Signaturblock ist kaputt und
    /// wirft <see cref="InvalidDataException"/>.
    /// </summary>
    public static bool TryParse(byte[] bytes, out ClearsignedMessage? message)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        message = null;

        var text = Utf8.GetString(bytes);
        if (text.Length > 0 && text[0] == '﻿')
        {
            text = text[1..];
        }

        text = text.TrimStart();
        if (!text.StartsWith(MessageHeader, StringComparison.Ordinal))
        {
            return false;
        }

        var lines = SplitLines(text);
        if (lines.Count == 0 || lines[0].Text.TrimEnd() != MessageHeader)
        {
            throw new InvalidDataException("Clearsign-Kopf ist nicht allein auf seiner Zeile.");
        }

        // Armor-Header (z. B. "Hash: SHA256") bis zur ersten Leerzeile.
        var index = 1;
        while (index < lines.Count && lines[index].Text.Trim().Length > 0)
        {
            index++;
        }

        if (index >= lines.Count)
        {
            throw new InvalidDataException("Clearsign-Datei ohne Leerzeile nach den Armor-Headern.");
        }

        index++;
        var textLines = new List<Line>();
        while (index < lines.Count && lines[index].Text.TrimEnd() != SignatureHeader)
        {
            var line = lines[index];
            textLines.Add(line.Text.StartsWith("- ", StringComparison.Ordinal) ? line with { Text = line.Text[2..] } : line);
            index++;
        }

        if (index >= lines.Count)
        {
            throw new InvalidDataException($"Clearsign-Datei ohne Signaturblock ({SignatureHeader}).");
        }

        var cleartext = new StringBuilder();
        var canonical = new StringBuilder();
        for (var i = 0; i < textLines.Count; i++)
        {
            // Das Zeilenende vor dem Signaturblock gehört nicht zum signierten Text.
            var last = i == textLines.Count - 1;
            cleartext.Append(textLines[i].Text);
            canonical.Append(textLines[i].Text.TrimEnd(' ', '\t'));
            if (!last)
            {
                cleartext.Append(textLines[i].Ending);
                canonical.Append("\r\n");
            }
        }

        var signature = string.Concat(lines.Skip(index).Select(line => line.Text + line.Ending));
        message = new ClearsignedMessage(
            Utf8.GetBytes(cleartext.ToString()),
            Utf8.GetBytes(canonical.ToString()),
            Encoding.ASCII.GetBytes(signature));
        return true;
    }

    private sealed record Line(string Text, string Ending);

    private static List<Line> SplitLines(string text)
    {
        var lines = new List<Line>();
        var start = 0;
        while (start < text.Length)
        {
            var end = text.IndexOf('\n', start);
            if (end < 0)
            {
                lines.Add(new Line(text[start..], string.Empty));
                break;
            }

            var crlf = end > start && text[end - 1] == '\r';
            lines.Add(new Line(text[start..(crlf ? end - 1 : end)], crlf ? "\r\n" : "\n"));
            start = end + 1;
        }

        return lines;
    }
}
