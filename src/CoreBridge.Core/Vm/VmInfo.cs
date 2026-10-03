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

    /// <summary>Mindestens eine echte IPv4 (Multipass meldet sonst <c>N/A</c> oder nichts).</summary>
    public bool HasAddress => Ipv4.Any(ip => !string.IsNullOrWhiteSpace(ip) && !string.Equals(ip, "N/A", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Läuft laut Multipass, ist aber ohne IPv4 nicht erreichbar: der normale
    /// <c>multipass stop</c> fährt über SSH herunter und hängt dann (beobachtet:
    /// VM nach dem Launch dauerhaft <c>Running</c>/<c>N/A</c>).
    /// </summary>
    public bool IsUnreachable => IsRunning && !HasAddress;

    /// <summary>Nur <c>stop --force</c> (hart ausschalten) bringt diese VM sicher herunter.</summary>
    public bool NeedsForceStop => IsTransitional || IsUnreachable;

    private bool Is(string state) => string.Equals(State, state, StringComparison.OrdinalIgnoreCase);
}
