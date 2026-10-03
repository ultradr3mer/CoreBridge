namespace CoreBridge.Core.Tests;

/// <summary>
/// Ein Test, der Windows braucht (<c>cmd.exe</c>, <c>hostname.exe</c>, <c>pwsh</c>,
/// Windows-Pfade); anderswo übersprungen, damit der schnelle Lauf grün bleibt.
/// </summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "nur unter Windows";
        }
    }
}

/// <summary>Wie <see cref="WindowsFactAttribute"/>, für Theorien.</summary>
public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "nur unter Windows";
        }
    }
}
