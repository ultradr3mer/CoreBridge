using System.Runtime.InteropServices;
using CoreBridge.Core.Common;

namespace CoreBridge.Cli;

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
            case ChoiceEvent<YesNoOptions> yesNo:
                this.AskChoice(yesNo);
                break;
            case ChoiceEvent<OkCancelOptions> okCancel:
                this.AskChoice(okCancel);
                break;
            case TextEvent text:
                this.AskText(text);
                break;
            case ErrEvent err:
                this.EndProgressLine();
                Console.Error.WriteLine(err.Printable);
                break;
            case ProgressEvent:
                Console.Write(Progress.ProgressTick);
                this._midProgressLine = true;
                break;
            case StdEvent std:
                this.WriteOutput(std.Printable);
                break;
            default:
                this.EndProgressLine();
                Console.WriteLine(cliEvent.Printable);
                break;
        }
    }

    /// <summary>
    /// Schreibt eine Meldung; eine laufende Punktezeile wird vorher abgeschlossen.
    /// </summary>
    private void WriteOutput(string line)
    {
        if (this._midProgressLine && line == "OK")
        {
            Console.WriteLine(line);
            this._midProgressLine = false;
            return;
        }

        this.EndProgressLine();
        Console.WriteLine(line);
    }

    /// <summary>Schließt eine offene Punktezeile ab, bevor anderer Text folgt.</summary>
    private void EndProgressLine()
    {
        if (this._midProgressLine)
        {
            Console.WriteLine();
            this._midProgressLine = false;
        }
    }

    private bool _midProgressLine;

    /// <summary>
    /// Fragt nach einer Auswahl und beantwortet das Ereignis. Unbekannte Eingabe
    /// fragt erneut; ohne interaktive Eingabe gewinnt die Standardantwort.
    /// </summary>
    private void AskChoice<T>(ChoiceEvent<T> choice) where T : struct, Enum
    {
        this.EndProgressLine();
        if (!ConsoleAvailability.CanPrompt)
        {
            Console.WriteLine($"{choice.Printable} {choice.PrintOptions(display: false, values: true)} "
                + $"{DefaultAnswer.Of<T>()} (keine interaktive Eingabe)");
            choice.SetResult(DefaultAnswer.Of<T>());
            return;
        }

        while (true)
        {
            Console.Write($"{choice.Printable} {choice.PrintOptions(display: true, values: true).TrimEnd()} ");
            var input = Console.ReadLine();
            if (input is null)
            {
                // EOF (Pipe zu Ende): Standardantwort statt Endlosschleife.
                choice.SetResult(DefaultAnswer.Of<T>());
                return;
            }

            if (choice.TryParseChoice(input.Trim(), out _))
            {
                return;
            }

            Console.WriteLine("  Unbekannte Eingabe — bitte erneut.");
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
            Console.WriteLine($"{text.Printable}(no interactive input)");
            text.SetResult(null);
            return;
        }

        Console.Write(text.Printable);
        text.SetResult(Console.ReadLine());
    }
}

/// <summary>
/// Die Antwort, mit der eine Rückfrage ohne Benutzereingabe geschlossen wird:
/// <c>No</c> bzw. <c>Cancel</c> und <c>null</c> für Freitext. So bestätigt
/// kein automatischer Pfad versehentlich einen riskanten Schritt.
/// </summary>
internal static class DefaultAnswer
{
    /// <summary>
    /// Die Antwort, mit der eine Rückfrage ohne Eingabe geschlossen wird: immer
    /// die ablehnende. Ausdrücklich statt <c>default(T)</c> — der erste Wert
    /// einer Aufzählung ist hier die bejahende, <c>default</c> würde also
    /// versehentlich löschen, stoppen oder überschreiben.
    /// </summary>
    public static T Of<T>() where T : struct, Enum
    {
        if (typeof(T) == typeof(YesNoOptions))
        {
            return (T)(object)YesNoOptions.No;
        }

        if (typeof(T) == typeof(OkCancelOptions))
        {
            return (T)(object)OkCancelOptions.Cancel;
        }

        return default;
    }
}
