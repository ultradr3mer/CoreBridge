using CoreBridge.Interaction;
using CoreBridge.Core.Config;

namespace CoreBridge.Cli.Tests;

/// <summary>Ergebnis eines Aufrufs: stdout- und stderr-Zeilen getrennt, Exit-Code und Fehlermeldung.</summary>
internal sealed record HarnessResult(int ExitCode, IReadOnlyList<string> Lines, IReadOnlyList<string> Errors, string? Error)
{
    public string All => string.Join("\n", Lines.Concat(Errors).Append(Error ?? string.Empty));
}

/// <summary>
/// Schlanker Konsument von <see cref="Program.Run"/> für Tests außerhalb des Lebenszyklus:
/// sammelt Ausgabe, beantwortet Rückfragen der Reihe nach aus <c>dialog</c> und jede
/// weitere mit ihrer Standardantwort, wie ohne Konsole — statt zu hängen.
/// </summary>
internal static class CliHarness
{
    public static async Task<HarnessResult> RunAsync(AppConfig config, IOptions options, params DialogStep[] dialog)
    {
        var lines = new List<string>();
        var errors = new List<string>();
        var pending = new Queue<DialogStep>(dialog);
        try
        {
            await foreach (var cliEvent in Program.Run(config, options))
            {
                switch (cliEvent)
                {
                    case IOpenInteraction interaction:
                        lines.Add(cliEvent.Printable);
                        var answered = pending.TryPeek(out var step)
                            && cliEvent.Printable.Contains(step.Prompt, StringComparison.Ordinal)
                            && cliEvent switch
                            {
                                IChoiceInteraction choice => step.Input is not null && choice.TryParseChoice(step.Input),
                                TextEvent text => text.SetResult(step.Input),
                                _ => false
                            };
                        if (answered)
                        {
                            pending.Dequeue();
                        }
                        else
                        {
                            // Wie ohne Konsole: die Standardantwort (Nein, leerer Text).
                            switch (cliEvent)
                            {
                                case IChoiceInteraction choice:
                                    choice.SetDefault();
                                    break;
                                case TextEvent text:
                                    text.SetResult(null);
                                    break;
                                default:
                                    interaction.Cancel();
                                    break;
                            }
                        }

                        break;
                    case ProgressEvent:
                    case DiagnosticEvent: // wie ConsoleAdapter: Innenleben ist keine Ausgabe
                        break;
                    case ErrEvent err:
                        errors.Add(err.Printable);
                        break;
                    default:
                        lines.Add(cliEvent.Printable);
                        break;
                }
            }

            return new HarnessResult(0, lines, errors, null);
        }
        catch (ProgramExecutionException exception)
        {
            return new HarnessResult(exception.ExitCode, lines, errors, exception.Message);
        }
    }
}
