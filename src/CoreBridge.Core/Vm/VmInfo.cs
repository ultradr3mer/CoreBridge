namespace CoreBridge.Core.Vm;

public sealed record VmInfo(string Name, string? State, IReadOnlyList<string> Ipv4)
{
    public bool IsDeleted => string.Equals(State, "Deleted", StringComparison.OrdinalIgnoreCase);
}
