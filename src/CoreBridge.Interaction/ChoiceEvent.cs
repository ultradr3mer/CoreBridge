using System.Text;

namespace CoreBridge.Interaction;

/// <summary>
/// Ja/Nein-Rückfrage; die Standardantwort ist <c>No</c> — so antworten alle
/// Rückfragen, wenn keine interaktive Eingabe möglich ist.
/// </summary>
public enum YesNoOptions
{
    [OptionDef("Yes", "y,yes,j,ja", [Keys.Enter])]
    Yes,

    [OptionDef("No", "n,no,nein", [Keys.Esc, Keys.Ctrl | Keys.C, Keys.Backspace], IsDefault = true)]
    No
}

/// <summary>Bestätigen/Abbrechen mit der Standardantwort <c>Cancel</c>.</summary>
public enum OkCancelOptions
{
    [OptionDef("Ok", "ok,k", [Keys.Enter])]
    Ok,

    [OptionDef("Cancel", "cancel,c", [Keys.Esc, Keys.Ctrl | Keys.C, Keys.Backspace], IsDefault = true)]
    Cancel
}

/// <summary>
/// Das typfreie Gesicht einer <see cref="ChoiceEvent{T}"/>: alles, was ein
/// Konsument zum Fragen und Beantworten braucht, ohne die Aufzählung zu
/// kennen. Konsumenten prüfen auf dieses Interface statt auf einzelne
/// <c>ChoiceEvent&lt;…&gt;</c> — so wird jede neue Aufzählung ohne Änderung
/// am Konsumenten beantwortet.
/// </summary>
public interface IChoiceInteraction : IOpenInteraction
{
    /// <summary>Die Frage.</summary>
    string Printable { get; }

    /// <summary>Die möglichen Antworten samt Beschriftung und Eingabemustern.</summary>
    IReadOnlyList<OptionDef> Options { get; }

    /// <summary>Die Antwort ohne Benutzereingabe, siehe <see cref="OptionDef.IsDefault"/>.</summary>
    OptionDef DefaultOption { get; }

    /// <inheritdoc cref="ChoiceEvent{T}.PrintOptions"/>
    string PrintOptions(bool display, bool values = true);

    /// <summary>
    /// Antwortet, falls <paramref name="input"/> zu einem der Muster passt;
    /// sonst bleibt die Rückfrage offen.
    /// </summary>
    bool TryParseChoice(string input);

    /// <summary>
    /// Antwortet, falls <paramref name="key"/> zu den <see cref="OptionDef.Keys"/> einer
    /// Antwort gehört — der Weg für Angebote auf Fortschritts-Ticks, bei denen keine
    /// Zeile gelesen wird, sondern einzelne Tasten.
    /// </summary>
    bool TryChooseKey(Keys key);

    /// <summary>Schließt die Rückfrage mit <see cref="DefaultOption"/>.</summary>
    bool SetDefault();
}

/// <summary>
/// Eine Frage mit festen Antwortmöglichkeiten. Der Konsument liest eine Zeile,
/// prüft sie mit <see cref="TryParseChoice(string, out T)"/> und darf bei
/// unbekannter Eingabe erneut fragen — beantwortet wird erst bei einem Treffer.
/// </summary>
/// <typeparam name="T">
/// Aufzählung, deren Werte antworten dürfen; jeder Wert trägt ein
/// <see cref="OptionDef"/>, genau einer davon mit <see cref="OptionDef.IsDefault"/>.
/// </typeparam>
public class ChoiceEvent<T> : InteractionEvent<T>, IChoiceInteraction where T : struct, Enum
{
    public ChoiceEvent(TimeSpan emissionTime, string message, TimeSpan? timeout = null)
        : base(emissionTime, message, timeout)
    {
        this.Options = Enum.GetValues<T>()
            .Select(OptionDef.FromAttribute)
            .ToList();
        this.DefaultOption = OptionDef.DefaultOf<T>();
    }

    /// <summary>Die möglichen Antworten samt Beschriftung und Eingabemustern.</summary>
    public List<OptionDefWithValue<T>> Options { get; }

    /// <summary>Die Antwort ohne Benutzereingabe, siehe <see cref="OptionDef.IsDefault"/>.</summary>
    public OptionDefWithValue<T> DefaultOption { get; }

    IReadOnlyList<OptionDef> IChoiceInteraction.Options => this.Options;

    OptionDef IChoiceInteraction.DefaultOption => this.DefaultOption;

    bool IChoiceInteraction.TryParseChoice(string input) => this.TryParseChoice(input, out _);

    /// <inheritdoc/>
    public bool TryChooseKey(Keys key)
    {
        var option = this.Options.FirstOrDefault(candidate => candidate.Keys.Contains(key));
        return option is not null && this.SetResult(option.Value);
    }

    /// <summary>Schließt die Rückfrage mit <see cref="DefaultOption"/>.</summary>
    public bool SetDefault() => this.SetResult(this.DefaultOption.Value);

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
