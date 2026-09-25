using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace CoreBridge.Core.Common;

public static partial class StringExtensions
{
    /// <summary>
    /// Liest eine Zeitangabe wie <c>10s</c> (Sekunden) oder <c>1000ms</c>
    /// (Millisekunden); Groß-/Kleinschreibung und Leerraum sind egal.
    /// </summary>
    /// <exception cref="FormatException">Wenn der Wert leer ist oder keinem der Formate entspricht.</exception>
    public static TimeSpan ReadTime(this string? value)
    {
        var match = TimePattern().Match(value ?? string.Empty);
        if (!match.Success)
        {
            throw new FormatException(
                $"Invalid time value \"{value}\" — expected e.g. \"10s\" (seconds) or \"1000ms\" (milliseconds).");
        }

        var amount = long.Parse(match.Groups[1].Value);
        return match.Groups[2].Value.Equals("ms", StringComparison.OrdinalIgnoreCase)
            ? TimeSpan.FromMilliseconds(amount)
            : TimeSpan.FromSeconds(amount);
    }

    [GeneratedRegex(@"^\s*(\d+)\s*(ms|s)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TimePattern();
}

/// <summary>Gemeinsame Marker für gestreamte Fortschrittsmeldungen.</summary>
public static class Progress
{
    /// <summary>
    /// Eine Zeile, die nur aus diesem Wert besteht, ist ein Fortschrittspunkt
    /// eines Warte-Loops; die CLI schreibt solche Punkte hintereinander in eine Zeile.
    /// </summary>
    public const string ProgressTick = ".";
}

/// <summary>
/// Throws where C# allows no <c>throw</c> expression (e.g. method arguments); <c>T</c> is the type expected there.
/// </summary>
public static class Inline
{
    /// <summary>Wirft <paramref name="exception"/>.</summary>
    [DoesNotReturn]
    public static T Throw<T>(Exception exception) => throw exception;

    /// <summary>Platzhalter für einen noch nicht implementierten Wert; wirft <see cref="NotImplementedException"/>.</summary>
    [DoesNotReturn]
    public static T Todo<T>(string what) => throw new NotImplementedException($"{what} is not implemented yet.");
}