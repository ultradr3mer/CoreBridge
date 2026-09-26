using System.Globalization;
using System.Text.RegularExpressions;

namespace CoreBridge.Core.Multipass;

/// <summary>
/// Größenangaben, wie Multipass sie annimmt und ausgibt (<c>20G</c>, <c>0.5G</c>,
/// <c>42MiB</c>, <c>1234B</c>; <c>multipass get local.&lt;vm&gt;.disk</c> liefert
/// <c>5.0GiB</c>). K/M/G zählt Multipass binär (1024), ein <c>iB</c>/<c>B</c>-Suffix
/// ändert daran nichts.
/// </summary>
public static partial class MultipassSize
{
    /// <summary>
    /// Größe in Bytes; <c>false</c> für alles, was Multipass als „not a valid memory
    /// size“ ablehnt (Dezimalzahlen nur mit Einheit).
    /// </summary>
    public static bool TryParse(string? text, out long bytes)
    {
        bytes = 0;
        var match = SizePattern().Match(text?.Trim() ?? string.Empty);
        if (!match.Success)
        {
            return false;
        }

        var number = match.Groups["number"].Value;
        var unit = match.Groups["unit"].Value.ToUpperInvariant();
        if (unit.Length == 0 && number.Contains('.'))
        {
            return false;
        }

        var factor = unit switch
        {
            "K" => 1024m,
            "M" => 1024m * 1024,
            "G" => 1024m * 1024 * 1024,
            _ => 1m
        };
        bytes = (long)(decimal.Parse(number, CultureInfo.InvariantCulture) * factor);
        return true;
    }

    [GeneratedRegex(@"^(?<number>\d+(?:\.\d+)?)(?<unit>[KMG])?(?:i?B)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SizePattern();
}
