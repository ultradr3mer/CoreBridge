namespace CoreBridge.Core.Vm;

public sealed record VmInfo(string Name, string? State, IReadOnlyList<string> Ipv4)
{
    /// <summary>Im Multipass-Papierkorb (<c>multipass recover</c> oder <c>delete --purge</c>).</summary>
    public bool IsDeleted => Is("Deleted");

    public bool IsRunning => Is("Running");

    /// <summary>Aus oder angehalten — ein <c>multipass start</c> bringt sie wieder hoch.</summary>
    public bool IsStopped => Is("Stopped") || Is("Suspended");

    /// <summary>
    /// Weder laufend noch aus noch gelöscht (<c>Starting</c>, <c>Restarting</c>,
    /// <c>Unknown</c> …): ein normaler <c>multipass stop</c> wird abgelehnt.
    /// </summary>
    public bool IsTransitional => !IsRunning && !IsStopped && !IsDeleted;

    private bool Is(string state) => string.Equals(State, state, StringComparison.OrdinalIgnoreCase);
}
