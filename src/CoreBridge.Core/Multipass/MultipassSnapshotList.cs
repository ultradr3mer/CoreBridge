using System.Text.Json;
using CoreBridge.Core.Vm;

namespace CoreBridge.Core.Multipass;

/// <summary>
/// Parst <c>multipass list --snapshots --format json</c> (Multipass 1.16):
/// <c>{"errors":[],"info":{"&lt;vm&gt;":{"&lt;snapshot&gt;":{"comment":"…","parent":"…"}}}}</c>.
/// </summary>
public static class MultipassSnapshotList
{
    public static IReadOnlyList<VmSnapshotInfo> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var snapshots = new List<VmSnapshotInfo>();
        if (!document.RootElement.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object)
        {
            return snapshots;
        }

        foreach (var instance in info.EnumerateObject())
        {
            if (instance.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var snapshot in instance.Value.EnumerateObject())
            {
                snapshots.Add(new VmSnapshotInfo(
                    instance.Name,
                    snapshot.Name,
                    Text(snapshot.Value, "parent"),
                    Text(snapshot.Value, "comment")));
            }
        }

        return snapshots;
    }

    /// <summary>Textfeld; leer oder fehlend = <c>null</c>.</summary>
    private static string? Text(JsonElement element, string property)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrEmpty(value.GetString())
                ? value.GetString()
                : null;
    }
}
