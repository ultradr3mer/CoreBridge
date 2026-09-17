using CoreBridge.Core.Config;
using CoreBridge.Core.Vm;

namespace CoreBridge.Core.Images;

/// <summary>
/// Komponenten-Auflösung gegen die Konfiguration (appsettings.json): Komponentenname
/// → Store-Snap für `ubuntu-image --snap`. Was eine Komponente ist, steht in der
/// Config, nicht im Code.
/// </summary>
public static class ImageComponents
{
    public static string[] ResolveSnaps(ImagesConfig config, IEnumerable<string> components)
    {
        return components
            .Select(c => c.Trim())
            .Where(c => c.Length > 0)
            .Select(c =>
            {
                if (!config.Components.TryGetValue(c, out var component))
                {
                    throw new MultipassException(
                        $"Unbekannte Komponente '{c}'. Bekannt (Config Images:Components): {string.Join(", ", config.Components.Keys)}.");
                }

                return component.Snap;
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
