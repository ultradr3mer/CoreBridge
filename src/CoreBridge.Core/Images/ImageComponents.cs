using CoreBridge.Core.Config;
using CoreBridge.Core.Vm;

namespace CoreBridge.Core.Images;

/// <summary>
/// Löst Komponentennamen gegen die Konfiguration auf. Was eine Komponente ist,
/// steht in appsettings.json, nicht im Code.
/// </summary>
public static class ImageComponents
{
    public static string[] ResolveSnaps(ImagesConfig config, IEnumerable<string> components)
    {
        return Resolve(config, components)
            .Select(component => component.Snap)
            .Where(snap => !string.IsNullOrWhiteSpace(snap))
            .Select(snap => snap!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static ComponentConfig[] Resolve(ImagesConfig config, IEnumerable<string> components)
    {
        return components
            .Select(name => name.Trim())
            .Where(name => name.Length > 0)
            .Select(name =>
            {
                if (!config.Components.TryGetValue(name, out var component))
                {
                    throw new MultipassException(
                        $"Unbekannte Komponente '{name}'. Bekannt (Config Images:Components): {string.Join(", ", config.Components.Keys)}.");
                }

                return component;
            })
            .ToArray();
    }
}
