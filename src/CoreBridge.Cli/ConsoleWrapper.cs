using System.Runtime.CompilerServices;
using System.Text;
using CommandLine;
using CoreBridge.Core.Common;
using CoreBridge.Core.Config;
using CoreBridge.Core.Images;
using CoreBridge.Core.Multipass;
using CoreBridge.Core.Vm;
using Microsoft.Extensions.Configuration;

namespace CoreBridge.Cli;

public class ConsoleWrapper
{
    public void AcceptEventStream(IAsyncEnumerable<CliEvent> events)
    {
        foreach (var e in events)
        {
            this.AcceptEventItem(e);
        }
    }

    private void AcceptEventItem(CliEvent e)
    {
        switch (e)
        {
            case StdEvent std:
                this.WriteOutput.Add(std);
                break;
            case ErrEvent err:
                this.WriteError.Add(err);
                break;
        }
    }


    /// <summary>
    /// Schreibt eine Meldung; <see cref="Progress.ProgressTick"/>-Zeilen werden
    /// hintereinander in dieselbe Zeile geschrieben.
    /// </summary>
    private static void WriteOutput(string line)
    {
        if (line == Progress.ProgressTick)
        {
            Console.Write(line);
            _midProgressLine = true;
            return;
        }

        if (_midProgressLine && line == "OK")
        {
            Console.WriteLine(line);
            _midProgressLine = false;
            return;
        }

        EndProgressLine();
        Console.WriteLine(line);
    }

    /// <summary>Schließt eine offene Punktezeile ab, bevor anderer Text folgt.</summary>
    private static void EndProgressLine()
    {
        if (_midProgressLine)
        {
            Console.WriteLine();
            _midProgressLine = false;
        }
    }

    /// <summary>Ja/Nein-Rückfrage auf der Konsole; ohne interaktive Eingabe immer Nein.</summary>
    private static bool ConfirmOnConsole(string question)
    {
                new ChoiceEvent<ChoiceEvent.YesNoOptions>(message);

        
        EndProgressLine();
        if (Console.IsInputRedirected)
        {
            Console.WriteLine($"{question} [y/n] n (keine interaktive Eingabe)");
            return false;
        }

        Console.Write($"{question} [y/n] ");
        return Console.ReadLine()?.Trim().ToLowerInvariant() is "y" or "yes" or "j" or "ja";
    }

    /// <summary>Freitext-Eingabe auf der Konsole; ohne interaktive Eingabe <c>null</c>.</summary>
    private static string? PromptOnConsole(string question)
    {
        EndProgressLine();
        if (Console.IsInputRedirected)
        {
            Console.WriteLine($"{question}(no interactive input)");
            return null;
        }

        Console.Write(question);
        return Console.ReadLine();
    }

}