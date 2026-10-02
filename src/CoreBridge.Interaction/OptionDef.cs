namespace CoreBridge.Interaction;

/// <summary>
/// Beschreibt einen Auswahlpunkt eines <see cref="ChoiceEvent{T}"/>: die
/// Anzeige, die über <c>TryParseChoice</c> akzeptierten Eingaben und die
/// Tastenkürzel, die ein Terminal-UI erwarten würde.
/// </summary>
/// <param name="display">Beschriftung, z. B. <c>Yes</c>.</param>
/// <param name="pattern">Kommagetrennte Eingaben, z. B. <c>y,yes,j,ja</c>.</param>
/// <param name="keys">
/// Tastenkürzel. Reine Dokumentation — die Konsolen-Eingabe wertet sie nicht
/// aus, weil <c>Console.ReadLine</c> keine Einzeltasten kennt.
/// </param>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public class OptionDef : Attribute
{
    public OptionDef(string display, string pattern, Keys[]? keys = null)
    {
        this.Display = display;
        this.Patterns = pattern
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        this.Keys = keys ?? [];
    }

    /// <summary>Beschriftung des Auswahlpunkts.</summary>
    public string Display { get; }

    /// <summary>Alle akzeptierten Eingaben, z. B. <c>y, yes, j, ja</c>.</summary>
    public IReadOnlyList<string> Patterns { get; }

    /// <summary>Tastenkürzel; werden von der Konsolen-Eingabe nicht ausgewertet.</summary>
    public IReadOnlyList<Keys> Keys { get; }

    /// <summary>
    /// Die Antwort, mit der die Rückfrage ohne Benutzereingabe geschlossen wird
    /// (keine Konsole, EOF). Genau ein Wert je Aufzählung trägt sie — und das
    /// sollte der ablehnende sein, damit kein automatischer Pfad versehentlich
    /// löscht, stoppt oder überschreibt. Bewusst ausdrücklich statt
    /// <c>default(T)</c>: der erste Wert ist hier meist der bejahende.
    /// </summary>
    public bool IsDefault { get; set; }

    public bool IsMatch(string input)
    {
        return this.Patterns.Any(pattern => pattern.Equals(input, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Passt <paramref name="input"/> zu <paramref name="option"/>?</summary>
    public static bool IsMatch<T>(T option, string input) where T : struct, Enum
    {
        return FromAttribute(option).IsMatch(input);
    }

    /// <summary>
    /// Passt <paramref name="input"/> zu einem der Werte von <typeparamref name="T"/>?
    /// </summary>
    public static bool HasMatch<T>(string input, out OptionDefWithValue<T>? def) where T : struct, Enum
    {
        foreach (var option in Enum.GetValues<T>())
        {
            var candidate = FromAttribute(option);
            if (candidate.IsMatch(input))
            {
                def = candidate;
                return true;
            }
        }

        def = null;
        return false;
    }

    /// <summary>
    /// Liest das <see cref="OptionDef"/>-Attribut vom <em>Feld</em> des
    /// Aufzählungswerts. Die Attribute hängen am Feld, nicht am Typ — deshalb
    /// <c>typeof(T).GetCustomAttributes</c> hier nichts finden würde.
    /// </summary>
    public static OptionDefWithValue<T> FromAttribute<T>(T option) where T : struct, Enum
    {
        var name = option.ToString();
        var field = typeof(T).GetField(name ?? string.Empty)
            ?? throw new InvalidOperationException(
                $"'{typeof(T).Name}' kennt keinen Wert mit dem Namen '{name}'.");

        var def = field.GetCustomAttributes(typeof(OptionDef), inherit: false)
            .Cast<OptionDef>()
            .SingleOrDefault()
            ?? throw new InvalidOperationException(
                $"'{typeof(T).Name}.{name}' trägt kein [OptionDef]-Attribut.");

        return new OptionDefWithValue<T>(def, option);
    }

    /// <summary>
    /// Der Wert von <typeparamref name="T"/> mit <see cref="IsDefault"/>. Fehlt
    /// er oder gibt es mehrere, ist die Aufzählung falsch deklariert — das
    /// fällt beim Erzeugen der Rückfrage auf, nicht erst ohne Konsole.
    /// </summary>
    public static OptionDefWithValue<T> DefaultOf<T>() where T : struct, Enum
    {
        var defaults = Enum.GetValues<T>()
            .Select(FromAttribute)
            .Where(option => option.IsDefault)
            .ToList();

        return defaults.Count == 1
            ? defaults[0]
            : throw new InvalidOperationException(
                $"'{typeof(T).Name}' braucht genau einen Wert mit [OptionDef(..., IsDefault = true)], "
                + $"hat aber {defaults.Count}.");
    }

    /// <summary>
    /// Die ersten <paramref name="count"/> Muster als Anzeige: kompakt
    /// kommagetrennt, sonst mit <c>oder</c> vor dem letzten Eintrag.
    /// </summary>
    public string GetPatterns(int count, bool compact)
    {
        var shown = this.Patterns.Take(Math.Max(count, 1)).ToArray();
        if (shown.Length == 0)
        {
            return string.Empty;
        }

        return compact || shown.Length == 1
            ? string.Join(",", shown)
            : string.Join(", ", shown[..^1]) + " or " + shown[^1];
    }

    public override string ToString()
    {
        return this.Display;
    }
}

/// <summary>
/// Ein <see cref="OptionDef"/> zusammen mit dem Aufzählungswert, den es
/// beschreibt — damit <c>HasMatch</c> beides in einem Rutsch zurückgeben kann.
/// </summary>
public class OptionDefWithValue<T> : OptionDef where T : Enum
{
    public OptionDefWithValue(OptionDef def, T value)
        : base(def.Display, string.Join(',', def.Patterns), def.Keys.ToArray())
    {
        this.IsDefault = def.IsDefault;
        this.Value = value;
    }

    /// <summary>Der Aufzählungswert, den dieses Attribut beschreibt.</summary>
    public T Value { get; }
}
