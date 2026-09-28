using System.Text;

namespace CoreBridge.Cli;

/// <summary>
/// Ja/Nein-Rückfrage; die Standardantwort ist <c>No</c> — so antworten alle
/// Rückfragen, wenn keine interaktive Eingabe möglich ist.
/// </summary>
public enum YesNoOptions
{
    [OptionDef("Yes", "y,yes,j,ja", [Keys.Enter])]
    Yes,

    [OptionDef("No", "n,no,nein", [Keys.Esc, Keys.Ctrl | Keys.C, Keys.Backspace])]
    No
}

/// <summary>Bestätigen/Abbrechen mit der Standardantwort <c>Cancel</c>.</summary>
public enum OkCancelOptions
{
    [OptionDef("Ok", "ok,k", [Keys.Enter])]
    Ok,

    [OptionDef("Cancel", "cancel,c", [Keys.Esc, Keys.Ctrl | Keys.C, Keys.Backspace])]
    Cancel
}

/// <summary>
/// Eine Frage mit festen Antwortmöglichkeiten. Der Konsument liest eine Zeile,
/// prüft sie mit <see cref="TryParseChoice"/> und darf bei unbekannter Eingabe
/// erneut fragen — beantwortet wird erst bei einem Treffer.
/// </summary>
/// <typeparam name="T">Aufzählung, deren Werte antworten dürfen.</typeparam>
public class ChoiceEvent<T> : InteractionEvent<T> where T : struct, Enum
{
    public ChoiceEvent(TimeSpan emissionTime, string message, TimeSpan? timeout = null)
        : base(emissionTime, message, timeout)
    {
        this.Options = Enum.GetValues<T>()
            .Select(OptionDef.FromAttribute)
            .ToList();
    }

    /// <summary>Die möglichen Antworten samt Beschriftung und Eingabemustern.</summary>
    public List<OptionDefWithValue<T>> Options { get; }

    /// <summary>
    /// Antwortet, falls <paramref name="input"/> zu einem der Muster passt.
    /// Bei unbekannter Eingabe bleibt das Ereignis offen, damit der Konsument
    /// erneut fragen kann — und <paramref name="choice"/> ist dann
    /// <c>default</c>.
    /// </summary>
    public bool TryParseChoice(string input, out T choice)
    {
        if (OptionDef.HasMatch<T>(input, out var def))
        {
            choice = def!.Value;
            this.SetResult(choice);
            return true;
        }

        choice = default;
        return false;
    }

    /// <summary>
    /// Die Antwortmöglichkeiten als Text: Beschriftungen und/oder Eingabemuster.
    /// </summary>
    /// <param name="display">Beschriftungen anzeigen (<c>Yes; No</c>).</param>
    /// <param name="values">Eingabemuster anzeigen (<c>[y,yes; n,no]</c>).</param>
    public string PrintOptions(bool display, bool values = true)
    {
        var result = new StringBuilder();
        if (display)
        {
            result.Append(string.Join("; ", this.Options.Select(option => option.Display)));
        }
        if (display && values)
        {
            result.Append(": ");
        }
        if (values)
        {
            var patterns = string.Join("; ", this.Options.Select(option => option.GetPatterns(2, compact: true)));
            result.Append($"[{patterns}]");
        }

        return result.ToString();
    }
}

/// <summary>
/// Freitext-Eingabe, z. B. das Folgekommando nach abgebrochenem Warten.
/// Die Standardantwort ist <c>null</c> (keine Eingabe).
/// </summary>
public class TextEvent : InteractionEvent<string?>
{
    public TextEvent(TimeSpan emissionTime, string message, TimeSpan? timeout = null)
        : base(emissionTime, message, timeout)
    {
    }
}
