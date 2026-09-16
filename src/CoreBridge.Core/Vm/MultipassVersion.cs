using System.Text.Json;

namespace CoreBridge.Core.Vm;

public static class MultipassVersion
{
    public static (string? Client, string? Daemon) Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        string? ReadString(string propertyName)
        {
            return root.TryGetProperty(propertyName, out var value)
                && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
        }

        return (ReadString("multipass"), ReadString("multipassd"));
    }
}
