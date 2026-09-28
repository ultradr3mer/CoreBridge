namespace CoreBridge.Core.Common;

/// <summary>Bausteine für Shell-Zeilen, die im Gast per <c>/bin/sh -c</c> laufen.</summary>
public static class Shell
{
    /// <summary>Unverändert, wenn nur harmlose Zeichen vorkommen; sonst in einfache Anführungszeichen.</summary>
    public static string Quote(string value)
    {
        return value.Length > 0 && value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or ':' or '+' or '=')
            ? value
            : "'" + value.Replace("'", "'\\''") + "'";
    }
}
