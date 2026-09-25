using System.Text.RegularExpressions;
using UnionTypes;

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

public class InlineException<TFake> : InlineException 
{
    public InlineException(string message) : base(message)
    {
    }
}

public class InlineException : Exception
{
    public InlineException()
    {
    }

    public InlineException(string message) : base(message)
    {
    }

        /// <summary>
    /// Act like returning T for compiler, actually throws an exception of type T. 
    /// Use for inline throw expressions.
    /// </summary>
    public T Throw<T>(string message) where T : Exception, new()
    {
        throw new InlineException(message);
    }

    /// <summary>
    /// Act like returning T for compiler, actually throws an exception of type T. 
    /// Use for inline throw expressions.
    /// </summary>
    public T Throw<T>() where T : Exception, new()
    {
        throw new InlineException();
    }
}