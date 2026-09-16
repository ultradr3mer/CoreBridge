using System.Text.Json;
using CoreBridge.Core.Vm;

namespace Multipass.Experiments;

public sealed record MultipassVmInfo(string Name, string? State, IReadOnlyList<string> Ipv4)
{
    public bool IsDeleted => string.Equals(State, "Deleted", StringComparison.OrdinalIgnoreCase);
}

public static class MultipassVms
{
    public static List<MultipassVmInfo> List(MultipassCli? cli = null)
    {
        cli ??= new MultipassCli();
        var result = cli.Run(new[] { "list", "--format", "json" }, 60_000);
        Assert.True(
            result.ExitCode == 0,
            $"multipass list scheiterte: {result.StdErr.Trim()} / {result.StdOut.Trim()}");
        return Parse(result.StdOut);
    }

    public static List<MultipassVmInfo> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var list = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement
            : document.RootElement.TryGetProperty("list", out var property) && property.ValueKind == JsonValueKind.Array
                ? property
                : default;

        var vms = new List<MultipassVmInfo>();
        if (list.ValueKind != JsonValueKind.Array)
        {
            return vms;
        }

        foreach (var vm in list.EnumerateArray())
        {
            var name = vm.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString()
                : null;
            if (name is null)
            {
                continue;
            }

            var state = vm.TryGetProperty("state", out var stateElement) && stateElement.ValueKind == JsonValueKind.String
                ? stateElement.GetString()
                : null;
            var ipv4 = new List<string>();
            if (vm.TryGetProperty("ipv4", out var ipv4Element))
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

            vms.Add(new MultipassVmInfo(name, state, ipv4));
        }

        return vms;
    }
}
