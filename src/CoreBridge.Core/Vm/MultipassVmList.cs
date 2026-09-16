using System.Text.Json;

namespace CoreBridge.Core.Vm;

public static class MultipassVmList
{
    public static IReadOnlyList<VmInfo> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var list = root.ValueKind == JsonValueKind.Array
            ? root
            : root.TryGetProperty("list", out var property) && property.ValueKind == JsonValueKind.Array
                ? property
                : default;

        var vms = new List<VmInfo>();
        if (list.ValueKind != JsonValueKind.Array)
        {
            return vms;
        }

        foreach (var entry in list.EnumerateArray())
        {
            var name = entry.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString()
                : null;
            if (name is null)
            {
                continue;
            }

            var state = entry.TryGetProperty("state", out var stateElement) && stateElement.ValueKind == JsonValueKind.String
                ? stateElement.GetString()
                : null;

            var ipv4 = new List<string>();
            if (entry.TryGetProperty("ipv4", out var ipv4Element))
            {
                if (ipv4Element.ValueKind == JsonValueKind.Array)
                {
                    ipv4.AddRange(ipv4Element.EnumerateArray()
                        .Where(a => a.ValueKind == JsonValueKind.String)
                        .Select(a => a.GetString()!));
                }
                else if (ipv4Element.ValueKind == JsonValueKind.String && ipv4Element.GetString() is { } single)
                {
                    ipv4.Add(single);
                }
            }

            vms.Add(new VmInfo(name, state, ipv4));
        }

        return vms;
    }
}
