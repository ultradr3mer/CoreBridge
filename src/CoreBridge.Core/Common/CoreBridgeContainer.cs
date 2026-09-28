using CoreBridge.Core.Config;
using CoreBridge.Core.Multipass;
using Microsoft.Extensions.DependencyInjection;

namespace CoreBridge.Core.Common;

/// <summary>
/// Baut den DI-Container (Microsoft.Extensions.DependencyInjection) aus
/// <see cref="AppConfig.Bootstrap"/>. Welche Implementierung hinter einer
/// Schnittstelle steckt, entscheidet damit allein die Config — Tests übergeben
/// eine eigene statt Umgebungsvariablen zu setzen.
/// </summary>
/// <remarks>
/// Der Container wählt den Konstruktor mit den meisten <em>auflösbaren</em>
/// Parametern; eine Implementierung sollte daher genau einen öffentlichen
/// Konstruktor haben, der nur Registriertes (etwa <see cref="AppConfig"/>) verlangt.
/// </remarks>
public static class CoreBridgeContainer
{
    /// <summary>
    /// Registriert <paramref name="config"/> als Instanz und jede
    /// Bootstrap-Zuordnung Schnittstelle → Implementierung. Die Konstruktoren
    /// werden schon hier geprüft (<c>ValidateOnBuild</c>), nicht erst beim ersten Aufruf.
    /// </summary>
    /// <exception cref="MultipassException">Typ unbekannt, nicht zuweisbar oder nicht konstruierbar.</exception>
    public static ServiceProvider Create(AppConfig config)
    {
        var services = new ServiceCollection();
        services.AddSingleton(config);
        foreach (var (from, to) in config.Bootstrap)
        {
            var fromType = FindType(from);
            var toType = FindType(to);
            if (!fromType.IsAssignableFrom(toType) || toType.IsAbstract)
            {
                throw new MultipassException(
                    $"Bootstrap-Konfiguration: {to} ist keine instanziierbare Implementierung von {from}.");
            }

            services.AddTransient(fromType, toType);
        }

        try
        {
            return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
        }
        catch (AggregateException exception)
        {
            throw new MultipassException(
                $"Bootstrap-Konfiguration: {string.Join(" ", exception.InnerExceptions.Select(e => e.Message))}",
                exception);
        }
    }

    /// <summary>
    /// Wie <c>GetRequiredService</c>, aber eine fehlende Registrierung wird zur
    /// <see cref="MultipassException"/>, die <c>Program.ToExecutionException</c> kennt.
    /// Exceptions aus Konstruktoren reicht der Container unverpackt durch.
    /// </summary>
    public static T Resolve<T>(this IServiceProvider services)
        where T : notnull
    {
        try
        {
            return services.GetRequiredService<T>();
        }
        catch (InvalidOperationException exception)
        {
            throw new MultipassException(
                $"Bootstrap-Konfiguration: {typeof(T).FullName} ließ sich nicht erzeugen: {exception.Message}",
                exception);
        }
    }

    private static Type FindType(string name)
    {
        return Type.GetType(name)
            ?? AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(name))
                .FirstOrDefault(type => type is not null)
            ?? throw new MultipassException($"Bootstrap-Konfiguration: Typ '{name}' nicht gefunden.");
    }
}
