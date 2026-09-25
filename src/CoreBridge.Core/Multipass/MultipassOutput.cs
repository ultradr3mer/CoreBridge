using System.Text.RegularExpressions;

namespace CoreBridge.Core.Multipass;

/// <summary>
/// Macht Multipass-Ausgaben zeilenweise lesbar: Multipass zeichnet Fortschritt
/// (z. B. "Stopping vm  /-\|") per ANSI-Steuersequenzen immer wieder neu. Nur
/// solche Zeilen werden bereinigt — es bleibt der letzte sichtbare Stand ohne
/// Spinner, oder nichts. Zeilen ohne Steuersequenz (z. B. Ausgabe von
/// <c>vm-exec</c>) bleiben unverändert.
/// </summary>
public static partial class MultipassOutput
{
    public static IEnumerable<string> CleanLines(string text)
    {
        foreach (var line in text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n'))
        {
            if (!AnsiSequence().IsMatch(line))
            {
                yield return line;
                continue;
            }

            var visible = AnsiSequence().Split(line)
                .Select(segment => segment.TrimEnd().TrimEnd('/', '-', '\\', '|').TrimEnd())
                .LastOrDefault(segment => segment.Length > 0);
            if (visible is not null)
            {
                yield return visible;
            }
        }
    }

    [GeneratedRegex(@"\x1B\[[0-9;?]*[A-Za-z]")]
    private static partial Regex AnsiSequence();
}
