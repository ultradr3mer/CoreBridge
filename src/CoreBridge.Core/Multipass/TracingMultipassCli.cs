using System.Diagnostics;
using CoreBridge.Interaction;

namespace CoreBridge.Core.Multipass;

/// <summary>
/// Meldet jeden Aufruf des inneren <see cref="IMultipassCli"/> als <see cref="ToolCallEvent"/>
/// über die <see cref="EventFactory"/> des Kommandos: Argumente, Exit-Code, Dauer, stdout und
/// stderr — beim echten Multipass wie bei einer Attrappe (dann unter deren
/// <see cref="IMultipassDouble.BinaryLabel"/>). Was der Konsument damit macht (Protokoll,
/// nichts), entscheidet er.
/// </summary>
/// <remarks>
/// Ob eine Attrappe dahinter steckt, verrät <see cref="MultipassCliExtensions.AsDouble"/> —
/// der Decorator selbst ist keine <see cref="IMultipassDouble"/>, weil er auch echte Clients umhüllt.
/// </remarks>
public sealed class TracingMultipassCli(IMultipassCli inner, EventFactory events) : IMultipassCli
{
    private readonly string tool = inner is IMultipassDouble multipassDouble ? multipassDouble.BinaryLabel : "multipass";

    /// <summary>Der umhüllte Client.</summary>
    public IMultipassCli Inner => inner;

    public MultipassRunResult Run(IReadOnlyList<string> args, int timeoutMs = 30000, string? exePath = null) =>
        this.Trace(args, () => inner.Run(args, timeoutMs, exePath));

    public MultipassRunResult RunWithExtensions(IReadOnlyList<string> args, TimeSpan slice, Func<bool> keepWaiting, string? exePath = null) =>
        this.Trace(args, () => inner.RunWithExtensions(args, slice, keepWaiting, exePath));

    public int Attach(IReadOnlyList<string> args, string? exePath = null) =>
        this.Trace(
            args,
            () => new MultipassRunResult(inner.Attach(args, exePath), string.Empty, string.Empty, 0),
            "interaktiv an der Konsole, Ausgabe nicht mitgeschnitten").ExitCode;

    private MultipassRunResult Trace(IReadOnlyList<string> args, Func<MultipassRunResult> call, string? note = null)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = call();
            events.Post(events.ToolCall(this.tool, args, result.ExitCode, stopwatch.Elapsed, result.StdOut, result.StdErr, note));
            return result;
        }
        catch (MultipassTimeoutException exception)
        {
            events.Post(events.ToolCall(this.tool, args, null, stopwatch.Elapsed, exception.StdOut, exception.StdErr,
                "nicht weiter gewartet, Prozess beendet"));
            throw;
        }
        catch (MultipassException exception)
        {
            events.Post(events.ToolCall(this.tool, args, null, stopwatch.Elapsed, null, null, exception.Message));
            throw;
        }
    }
}

public static class MultipassCliExtensions
{
    /// <summary>Die Attrappe hinter <paramref name="cli"/> (auch durch einen <see cref="TracingMultipassCli"/>), sonst <c>null</c>.</summary>
    public static IMultipassDouble? AsDouble(this IMultipassCli cli) =>
        (cli is TracingMultipassCli traced ? traced.Inner : cli) as IMultipassDouble;
}
