namespace CoreBridge.Core.Diff
{
  using System;
  using System.Collections.Concurrent;
  using System.Collections.Generic;
  using System.Linq;
  using System.Reflection;

  /// <summary>
  /// Stellt Erweiterungsmethoden für Objekte bereit, die das <see cref="ITrackable"/>-Interface implementieren.
  /// Ermöglicht das Erstellen von Hash-basierten Snapshots des aktuellen Objektzustands
  /// und den anschließenden Vergleich, um geänderte Eigenschaften zu identifizieren.
  /// Verwendet Reflection und einen internen Cache zur Performance-Optimierung.
  /// </summary>
  public static class TrackableExtensions
  {
    #region Fields

    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> PropertyCache = new();

    #endregion Fields

    #region Methods

    /// <summary>
    /// Erstellt einen hash-basierten Snapshot des aktuellen Objektzustands.
    /// </summary>
    /// <param name="obj">Das trackbare Objekt.</param>
    /// <returns>Ein <see cref="HashSnapshot"/>-Objekt mit den aktuellen Hash-Werten der Eigenschaften.</returns>
    public static HashSnapshot CreateSnapshot(this ITrackable obj)
    {
      var snapshot = new HashSnapshot();
      var properties = GetProperties(obj.GetType());

      foreach (var prop in properties)
      {
        var value = prop.GetValue(obj);
        snapshot.Hashes[prop.Name] = value?.GetHashCode() ?? 0;
      }
      return snapshot;
    }

    /// <summary>
    /// Ermittelt die zu insertierenden Spalten anhand eines Snapshots, eines Mappings
    /// sowie einer White- und Blacklist.
    /// </summary>
    /// <param name="obj">Das trackbare Objekt.</param>
    /// <param name="columnMapping">Mapping von Datenbank-Spalte auf Property-Name.</param>
    /// <param name="snapshot">Der Ursprungszustand. Wenn null, werden alle Eigenschaften als geändert betrachtet.</param>
    /// <param name="whitelist">Spalten, die immer insertet werden sollen.</param>
    /// <param name="blacklist">Spalten, die niemals insertet werden sollen.</param>
    /// <returns>Dictionary der zu insertierenden Spalten.</returns>
    public static Dictionary<string, string> GetColumns(
        this ITrackable obj,
        Dictionary<string, string> columnMapping,
        HashSnapshot snapshot = null,
        HashSet<string> whitelist = null,
        HashSet<string> blacklist = null)
    {
      var geaenderteProperties = obj.GetChangedProps(snapshot);
      return GetColumnsSqlInternal(geaenderteProperties, columnMapping, whitelist, blacklist);
    }

    /// <summary>
    /// Ermittelt die definierten Datenbank-Spalten für einen Typen, basierend auf dem Mapping und White-/Blacklist.
    /// </summary>
    /// <param name="t">Der Typ des trackbaren Objekts.</param>
    /// <param name="columnMapping">Mapping von Datenbank-Spalte auf Property-Name.</param>
    /// <param name="whitelist">Spalten, die immer insertet werden sollen.</param>
    /// <param name="blacklist">Spalten, die niemals insertet werden sollen.</param>
    /// <returns>Dictionary der zu insertierenden Spalten.</returns>
    public static Dictionary<string, string> GetColumns<T>(IEnumerable<KeyValuePair<string, string>> columnMapping,
                                                           HashSet<string> whitelist = null,
                                                           HashSet<string> blacklist = null) where T : ITrackable
    {
      var geaenderteProperties = typeof(T).GetTypeProps();
      return GetColumnsSqlInternal(geaenderteProperties, columnMapping, whitelist, blacklist);
    }


    /// <summary>
    /// Prüft, ob sich das Objekt im Vergleich zum Snapshot geändert hat.
    /// </summary>
    /// <param name="obj">Das trackbare Objekt.</param>
    /// <param name="snapshot">Der Ursprungszustand.</param>
    /// <returns><c>true</c>, wenn Änderungen vorhanden sind, andernfalls <c>false</c>.</returns>
    public static bool WasChanged(this ITrackable obj, HashSnapshot snapshot)
    {
      return WasChanged(obj, snapshot, out var _);
    }

    /// <summary>
    /// Prüft, ob sich das Objekt im Vergleich zum Snapshot geändert hat und gibt die geänderten Eigenschaften aus.
    /// </summary>
    /// <param name="obj">Das trackbare Objekt.</param>
    /// <param name="snapshot">Der Ursprungszustand.</param>
    /// <param name="props">Ausgabeparameter für die Liste der geänderten Eigenschaften.</param>
    /// <returns><c>true</c>, wenn Änderungen vorhanden sind, andernfalls <c>false</c>.</returns>
    public static bool WasChanged(this ITrackable obj, HashSnapshot snapshot, out List<string> props)
    {
      props = obj.GetChangedProps(snapshot);
      return props.Count > 0;
    }

    /// <summary>
    /// Ermittelt die geänderten Eigenschaften des Objekts im Vergleich zum übergebenen Snapshot.
    /// </summary>
    /// <param name="obj">Das trackbare Objekt.</param>
    /// <param name="snapshot">Der Ursprungszustand. Wenn null, werden alle Eigenschaften zurückgegeben.</param>
    /// <returns>Eine Liste der geänderten Eigenschaftsnamen.</returns>
    private static List<string> GetChangedProps(this ITrackable obj, HashSnapshot snapshot)
    {
      var properties = GetProperties(obj.GetType());
      return GetPropsInternal(obj, snapshot, properties);
    }

    private static Dictionary<string, string> GetColumnsSqlInternal(
            List<string> geaenderteProperties,
            IEnumerable<KeyValuePair<string, string>> columnMapping,
            HashSet<string> whitelist,
            HashSet<string> blacklist)
    {
      var result = new Dictionary<string, string>();

      foreach (var kvp in columnMapping)
      {
        string dbColumn = kvp.Key;
        string propertyName = kvp.Value;

        // 1. Blacklist prüfen (überspringen, wenn verboten)
        if (blacklist != null && blacklist.Contains(dbColumn))
          continue;

        // 2. Whitelist prüfen (immer hinzufügen)
        if (whitelist != null && whitelist.Contains(dbColumn))
        {
          result[dbColumn] = propertyName;
          continue;
        }

        // 3. Geänderte Felder prüfen
        if (geaenderteProperties.Contains(propertyName))
        {
          result[dbColumn] = propertyName;
        }
      }

      return result;
    }

    private static PropertyInfo[] GetProperties(Type type)
    {
      return PropertyCache.GetOrAdd(type, t =>
          t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
           .Where(p => p.CanRead && !p.GetIndexParameters().Any())
           .ToArray());
    }

    private static int GetPropHash(ITrackable obj, PropertyInfo prop)
    {
      var aktuellerWert = prop.GetValue(obj);
      return aktuellerWert?.GetHashCode() ?? 0;
    }

    private static List<string> GetPropsInternal(ITrackable obj, HashSnapshot snapshot, PropertyInfo[] properties)
    {
      if (snapshot == null)
      {
        return properties.Select(p => p.Name).ToList();
      }

      var geaendert = new List<string>();
      foreach (var prop in properties)
      {
        if (obj == null
          || !snapshot.Hashes.TryGetValue(prop.Name, out int alterHash)
          || alterHash != GetPropHash(obj, prop))
        {
          geaendert.Add(prop.Name);
        }
      }
      return geaendert;
    }

    /// <summary>
    /// Ermittelt alle Properties (Eigenschaftsnamen) eines Typs.
    /// </summary>
    /// <param name="t">Der Typ des trackbaren Objekts.</param>
    /// <returns>Eine Liste aller Property-Namen.</returns>
    private static List<string> GetTypeProps(this Type t)
    {
      var properties = GetProperties(t);
      return GetPropsInternal(null, null, properties);
    }

    #endregion Methods
  }
}