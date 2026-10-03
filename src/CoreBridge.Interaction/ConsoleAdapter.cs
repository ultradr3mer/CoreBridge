using System.Runtime.InteropServices;

namespace CoreBridge.Interaction;

/// <summary>
/// Erkennt, ob der Prozess überhaupt eine Konsole bedienen kann.
/// <c>Console.IsInputRedirected</c> allein reicht nicht: ohne umgeleitete
/// Eingabe <em>und</em> ohne angehängtes Konsolenfenster (Startdienst,
/// Prozessstart per <c>Process.Start</c>) blockiert <c>ReadLine</c> sonst
/// endlos auf Eingaben, die nie kommen.
/// </summary>
internal static class ConsoleAvailability
{
    public static bool CanPrompt => !Console.IsInputRedirected && HasConsoleWindow();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    private static bool HasConsoleWindow()
    {
        if (!OperatingSystem.IsWindows())
        {
            // Unter Unix genügt die Umleitung; ohne tty liefert ReadLine sofort EOF.
            return true;
        }

        return GetConsoleWindow() != IntPtr.Zero;
    }
}

/// <summary>
/// Nimmt den Ereignisstrom eines Kommandos entgegen und schreibt ihn auf die
/// Konsole. Eigentümer aller Konsolen-Ein-/Ausgabe der CLI — Rückfragen,
/// Fortschrittspunkte und der Exit-Code entstehen hier.
/// </summary>
public class ConsoleAdapter
{
    /// <summary>
    /// Verbraucht den Strom und liefert den Exit-Code. Jedes Ereignis wird
    /// behandelt, sobald es eintrifft, nicht erst am Ende des Kommandos.
    /// </summary>
    public async Task<int> AcceptEventStream(IAsyncEnumerable<CliEvent> events)
    {
        await foreach (var cliEvent in events)
        {
            this.AcceptEventItem(cliEvent);
        }

        return 0;
    }

    private void AcceptEventItem(CliEvent cliEvent)
    {
        switch (cliEvent)
        {
            // Reihenfolge zählt: InteractionEvent erbt von StdEvent, also müssen
            // die Rückfragen VOR dem StdEvent-Zweig geprüft werden.
            case IChoiceInteraction choice:
                this.AskChoice(choice);
                break;
            case TextEvent text:
                this.AskText(text);
                break;
            case ErrEvent err:
                this.EndProgressLine();
                WriteColored(Console.Error, ColorErr, ErrColor(err.Printable), err.Printable);
                break;
            case ProgressEvent progress:
                this.WriteProgress(progress);
                break;
            case StdEvent std:
                this.WriteOutput(std.Printable);
                break;
            case DiagnosticEvent:
                // Innenleben fürs Protokoll (CommandLog), nicht für die Konsole.
                break;
            default:
                this.EndProgressLine();
                Console.WriteLine(cliEvent.Printable);
                break;
        }
    }

    /// <summary>Spinner-Bilder, reihum: <c>- / | \</c>.</summary>
    private static readonly char[] SpinnerFrames = ['-', '/', '|', '\\'];

    /// <summary>
    /// Fortschrittspunkt bei umgeleiteter Ausgabe: die Punkte reihen sich in
    /// einer Zeile, statt Wagenrücklauf-Müll ins Log zu schreiben.
    /// </summary>
    private const string RedirectedTick = ".";

    /// <summary>
    /// An einer echten Konsole dreht sich ein Spinner an Ort und Stelle; bei
    /// umgeleiteter Ausgabe (Datei, Pipe) bleiben es Punkte, damit kein
    /// Wagenrücklauf-Müll im Log landet.
    /// </summary>
    private static readonly bool UseSpinner = !Console.IsOutputRedirected;

    private bool _midProgressLine;
    private int _spinnerFrame;
    private TimeSpan _progressStart;
    private int _progressWidth;
    private bool _hintShown;

    /// <summary>
    /// Ein Fortschritts-Tick: Spinner mit Wartezeit seit dem ersten Tick
    /// dieser Zeile (<c>| 12 s</c>), sonst ein Punkt. Trägt der Tick ein offenes
    /// <see cref="ProgressEvent.Offer"/>, stehen dessen Tasten daneben
    /// (<c>— Stop hängt? k: jetzt beenden; Esc: Abbrechen</c>), und eine seit dem
    /// letzten Tick gedrückte Taste beantwortet es.
    /// </summary>
    private void WriteProgress(ProgressEvent progress)
    {
        if (!this._midProgressLine)
        {
            this._progressStart = progress.EmissionTime;
            this._spinnerFrame = 0;
            this._hintShown = false;
        }

        // Ohne Tastatur gibt es nichts anzubieten: dann ist es ein gewöhnlicher Tick,
        // und der Erzeuger schließt das Angebot am Ende mit der Standardantwort.
        var offer = progress.Offer is { IsOpen: true } open && ConsoleAvailability.CanPrompt ? open : null;
        var hint = offer is null ? null : OfferHint(offer);
        if (UseSpinner)
        {
            var frame = SpinnerFrames[this._spinnerFrame++ % SpinnerFrames.Length];
            var waited = progress.EmissionTime - this._progressStart;
            var text = $"{frame} {waited.TotalSeconds:F0} s ";
            Console.Write("\r");
            Write(Console.Out, ColorOut, ConsoleColor.DarkCyan, text);
            if (hint is not null)
            {
                Write(Console.Out, ColorOut, PromptColor, $"— {hint} ");
                text += $"— {hint} ";
            }

            // Ein kürzerer Tick nach einem längeren ließe Reste des alten stehen.
            Console.Write(new string(' ', Math.Max(0, this._progressWidth - text.Length)));
            this._progressWidth = Math.Max(this._progressWidth, text.Length);
        }
        else
        {
            if (hint is not null && !this._hintShown)
            {
                Console.Write($"({hint}) ");
                this._hintShown = true;
            }

            Console.Write(RedirectedTick);
        }

        this._midProgressLine = true;
        if (offer is not null)
        {
            AnswerByKey(offer);
        }
    }

    /// <summary>Frage und die Antworten mit Taste: <c>Stop hängt? k: jetzt beenden; Esc: Abbrechen</c>.</summary>
    private static string OfferHint(IChoiceInteraction offer)
    {
        var keyed = offer.Options
            .Where(option => option.Keys.Count > 0)
            .Select(option => $"{KeyLabel(option.Keys[0])}: {option.Display}");
        return $"{offer.Printable} {string.Join("; ", keyed)}";
    }

    /// <summary>
    /// Liest alle seit dem letzten Tick gedrückten Tasten, ohne zu blockieren, und
    /// beantwortet <paramref name="offer"/> mit der ersten passenden. Andere Tasten
    /// werden verworfen, damit sie nicht in einer späteren Eingabezeile landen.
    /// </summary>
    private static void AnswerByKey(IChoiceInteraction offer)
    {
        try
        {
            while (Console.KeyAvailable)
            {
                var key = ToKeys(Console.ReadKey(intercept: true));
                if (offer.IsOpen)
                {
                    offer.TryChooseKey(key);
                }
            }
        }
        catch (InvalidOperationException)
        {
            // Keine echte Konsoleneingabe — dann gibt es nichts zu lesen.
        }
    }

    /// <summary>Übersetzt eine Konsolentaste in <see cref="Keys"/> (Buchstaben klein, Strg als Flag).</summary>
    private static Keys ToKeys(ConsoleKeyInfo info)
    {
        var key = info.Key switch
        {
            ConsoleKey.Enter => Keys.Enter,
            ConsoleKey.Escape => Keys.Esc,
            ConsoleKey.Backspace => Keys.Backspace,
            ConsoleKey.Tab => Keys.Tab,
            >= ConsoleKey.A and <= ConsoleKey.Z => (Keys)('a' + (info.Key - ConsoleKey.A)),
            _ => Keys.None
        };

        return key != Keys.None && info.Modifiers.HasFlag(ConsoleModifiers.Control) ? key | Keys.Ctrl : key;
    }

    /// <summary>Anzeige einer Taste: <c>k</c>, <c>Strg+K</c>, <c>Enter</c>.</summary>
    private static string KeyLabel(Keys key)
    {
        var plain = key & ~Keys.Ctrl;
        var name = plain is >= Keys.A and <= Keys.Z ? ((char)plain).ToString() : plain.ToString();
        return key.HasFlag(Keys.Ctrl) ? $"Strg+{name.ToUpperInvariant()}" : name;
    }

    /// <summary>
    /// Schreibt eine Meldung; eine laufende Fortschrittszeile wird vorher abgeschlossen.
    /// </summary>
    private void WriteOutput(string line)
    {
        if (this._midProgressLine && line == "OK" && !UseSpinner)
        {
            WriteColored(Console.Out, ColorOut, ConsoleColor.Green, line);
            this._midProgressLine = false;
            return;
        }

        this.EndProgressLine();
        WriteColored(Console.Out, ColorOut, line == "OK" ? ConsoleColor.Green : null, line);
    }

    /// <summary>
    /// Schließt eine offene Fortschrittszeile ab, bevor anderer Text folgt:
    /// den Spinner wegwischen, eine Punktezeile umbrechen.
    /// </summary>
    private void EndProgressLine()
    {
        if (!this._midProgressLine)
        {
            return;
        }

        if (UseSpinner)
        {
            Console.Write("\r" + new string(' ', Math.Max(16, this._progressWidth)) + "\r");
            this._progressWidth = 0;
        }
        else
        {
            Console.WriteLine();
        }

        this._midProgressLine = false;
    }

    /// <summary>
    /// Fragt nach einer Auswahl und beantwortet das Ereignis. Unbekannte Eingabe
    /// fragt erneut; ohne interaktive Eingabe gewinnt die Standardantwort.
    /// </summary>
    private void AskChoice(IChoiceInteraction choice)
    {
        this.EndProgressLine();
        if (!ConsoleAvailability.CanPrompt)
        {
            WriteColored(Console.Out, ColorOut, PromptColor, $"{choice.Printable} {choice.PrintOptions(display: false, values: true)} "
                + $"{choice.DefaultOption} (keine interaktive Eingabe)");
            choice.SetDefault();
            return;
        }

        while (true)
        {
            Write(Console.Out, ColorOut, PromptColor, $"{choice.Printable} {choice.PrintOptions(display: true, values: true).TrimEnd()} ");
            var input = Console.ReadLine();
            if (input is null)
            {
                // EOF (Pipe zu Ende): Standardantwort statt Endlosschleife.
                choice.SetDefault();
                return;
            }

            if (choice.TryParseChoice(input.Trim()))
            {
                return;
            }

            WriteColored(Console.Out, ColorOut, ConsoleColor.Red, "  Unbekannte Eingabe — bitte erneut.");
        }
    }

    /// <summary>
    /// Fragt nach Freitext und beantwortet das Ereignis. Ohne interaktive
    /// Eingabe oder bei EOF ist die Antwort <c>null</c>.
    /// </summary>
    private void AskText(TextEvent text)
    {
        this.EndProgressLine();
        if (!ConsoleAvailability.CanPrompt)
        {
            WriteColored(Console.Out, ColorOut, PromptColor, $"{text.Printable}(no interactive input)");
            text.SetResult(null);
            return;
        }

        Write(Console.Out, ColorOut, PromptColor, text.Printable);
        text.SetResult(Console.ReadLine());
    }

    /// <summary>Farbe der Rückfragen, damit sie sich von der Ausgabe abheben.</summary>
    private const ConsoleColor PromptColor = ConsoleColor.Yellow;

    /// <summary>
    /// Farbe nur an einer echten Konsole und ohne <c>NO_COLOR</c>; in Dateien
    /// und Pipes bleibt der Text unverändert, damit Logs frei von Steuerzeichen bleiben.
    /// </summary>
    private static readonly bool ColorOut = !Console.IsOutputRedirected && !NoColor();

    private static readonly bool ColorErr = !Console.IsErrorRedirected && !NoColor();

    private static bool NoColor() => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));

    /// <summary>
    /// stderr ist rot — außer dem Hinweis eines Multipass-Doubles
    /// (<c>[simulation://…] kein echtes Multipass</c>), der nur grau informiert.
    /// </summary>
    private static ConsoleColor ErrColor(string line) =>
        line.StartsWith('[') && line.Contains("kein echtes Multipass", StringComparison.Ordinal)
            ? ConsoleColor.DarkGray
            : ConsoleColor.Red;

    private static void WriteColored(TextWriter writer, bool enabled, ConsoleColor? color, string line)
    {
        Write(writer, enabled, color, line);
        writer.WriteLine();
    }

    /// <summary>
    /// Schreibt <paramref name="text"/> in <paramref name="color"/> und stellt
    /// die vorige Farbe wieder her, auch wenn das Schreiben wirft.
    /// </summary>
    private static void Write(TextWriter writer, bool enabled, ConsoleColor? color, string text)
    {
        if (!enabled || color is null)
        {
            writer.Write(text);
            return;
        }

        var previous = Console.ForegroundColor;
        Console.ForegroundColor = color.Value;
        try
        {
            writer.Write(text);
        }
        finally
        {
            Console.ForegroundColor = previous;
        }
    }
}
