using System.Text;
using CoreBridge.Interaction;
using CoreBridge.Core.Multipass;

namespace CoreBridge.Cli;

public static partial class Program
{
  /// <summary>
  /// Zerlegt eine eingegebene Kommandozeile an Leerzeichen; Abschnitte in
  /// doppelten Anführungszeichen bleiben zusammen (ohne die Anführungszeichen).
  /// </summary>
  private static string[] SplitCommandLine(string commandLine)
  {
    var args = new List<string>();
    var current = new StringBuilder();
    var inQuotes = false;
    var hasToken = false;
    foreach (var character in commandLine)
    {
      if (character == '"')
      {
        inQuotes = !inQuotes;
        hasToken = true;
      }
      else if (char.IsWhiteSpace(character) && !inQuotes)
      {
        if (hasToken)
        {
          args.Add(current.ToString());
          current.Clear();
          hasToken = false;
        }
      }
      else
      {
        current.Append(character);
        hasToken = true;
      }
    }

    if (hasToken)
    {
      args.Add(current.ToString());
    }

    return args.ToArray();
  }

  private static IEnumerable<string> Lines(string text)
  {
    return string.IsNullOrEmpty(text)
        ? []
        : MultipassOutput.CleanLines(text);
  }

  private static ProgramExecutionException Failure(MultipassRunResult result)
  {
    var stdErr = MultipassStdErr.Clean(result.StdErr);
    var message = string.IsNullOrEmpty(stdErr)
        ? $"multipass beendet mit Exit {result.ExitCode}."
        : stdErr;
    return new ProgramExecutionException(result.ExitCode, message);
  }

  /// <summary>
  /// Liefert stdout des Multipass-Aufrufs zeilenweise. Bei Exit ≠ 0 folgt die
  /// Exception mit stderr; sonst wird stderr als reguläre Meldung geliefert.
  /// </summary>
  private static async Task Report(EventFactory events, MultipassRunResult result)
  {
    foreach (var line in Lines(result.StdOut))
    {
      await events.Emit(events.Std(line));
    }

    if (result.ExitCode != 0)
    {
      throw Failure(result);
    }

    foreach (var line in Lines(result.StdErr))
    {
      await events.Emit(events.Std(line));
    }
  }

  /// <summary>
  /// Rückfrage „weiter warten?“ für VM-Kommandos, die länger als
  /// <c>DefaultTimeout</c> brauchen; die Zeit läuft ab Erzeugung. Wird von
  /// CoreBridge.Core auf einem Threadpool-Thread aufgerufen, kann also nicht
  /// <c>await</c>en — der <see cref="EventFactory.Ask{T}"/>-Weg blockiert
  /// stattdessen, während der Konsument die Frage über den Kanal beantwortet.
  /// </summary>
  private static Func<bool> KeepWaiting(CommandContext context, string label)
  {
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    return () => context.Events.Ask(context.Events.Choice<YesNoOptions>(
        $"`{label}` is still running after {stopwatch.Elapsed.TotalSeconds:F0} s — keep waiting?")) == YesNoOptions.Yes;
  }

  /// <summary>Abstand der Fortschritts-Ticks während einer lang laufenden Multipass-Aktion.</summary>
  private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(200);

  /// <summary>
  /// Führt eine blockierende Multipass-Aktion auf dem Threadpool aus und meldet
  /// währenddessen alle <see cref="ProgressInterval"/> einen Tick, den der
  /// <see cref="ConsoleAdapter"/> als Spinner zeichnet. Solange eine Rückfrage
  /// offen ist (etwa „weiter warten?“ aus <c>keepWaiting</c>), pausiert er —
  /// sonst schriebe er in die Eingabezeile.
  /// </summary>
  private static async Task<T> WithProgress<T>(CommandContext context, Func<T> action)
  {
    var events = context.Events;
    var work = Task.Run(action, context.CancellationToken);
    while (await Task.WhenAny(work, Task.Delay(ProgressInterval, context.CancellationToken)) != work)
    {
      if (events.OpenInteractionCount == 0)
      {
        await events.Emit(events.Tick());
      }
    }

    return await work;
  }

  /// <summary>Kündigt das Kommando an, führt es aus und meldet stdout; bei Exit ≠ 0 folgt die fertige Fehlermeldung.</summary>
  private static async Task<MultipassRunResult> RunReported(
      CommandContext context,
      string label,
      Func<MultipassRunResult> action)
  {
    await context.Events.Emit(context.Events.Std($"{label} ..."));
    var result = await WithProgress(context, action);
    await Report(context.Events, result);
    return result;
  }

  private static void RequireValidName(string name)
  {
    if (!VmNamePattern.IsMatch(name))
    {
      throw new ProgramExecutionException(
          1,
          $"Invalid name `{name}` — use letters, digits and hyphens, starting with a letter and not ending with a hyphen (e.g. dev-01).");
    }
  }
}
