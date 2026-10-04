using System.Globalization;
using CoreBridge.Core.Multipass;

namespace CoreBridge.Core.Vm;

/// <summary>
/// CPU, Arbeitsspeicher und Disk einer VM, wie <c>multipass launch --cpus/--memory/--disk</c>
/// und <c>multipass set local.&lt;vm&gt;.cpus|memory|disk</c> sie annehmen. <c>null</c> heißt:
/// nicht angegeben (Multipass-Standard bzw. unverändert).
/// </summary>
public sealed record VmResources(int? Cpus = null, string? Memory = null, string? Disk = null)
{
    /// <summary>Untergrenze von Multipass für <c>--memory</c>.</summary>
    public const long MinimumMemoryBytes = 128L * 1024 * 1024;

    /// <summary>Untergrenze von Multipass für <c>--disk</c>.</summary>
    public const long MinimumDiskBytes = 512L * 1024 * 1024;

    public static readonly VmResources None = new();

    public bool IsEmpty => Cpus is null && Memory is null && Disk is null;

    /// <summary>
    /// Prüft die Angaben, bevor irgendetwas an einer VM passiert, und liefert sie
    /// getrimmt; <see cref="MultipassException"/> mit fertiger Meldung bei ungültigen Werten.
    /// </summary>
    public static VmResources Parse(int? cpus, string? memory, string? disk)
    {
        if (cpus is < 1)
        {
            throw new MultipassException($"Invalid CPU count `{cpus}` — use a whole number of at least 1, e.g. --cpus 2.");
        }

        return new VmResources(
            cpus,
            ParseSize(memory, "memory size", MinimumMemoryBytes, "4G"),
            ParseSize(disk, "disk size", MinimumDiskBytes, "20G"));
    }

    /// <summary>Eigene Werte vor denen von <paramref name="fallback"/> (CLI vor Profil).</summary>
    public VmResources Over(VmResources fallback) =>
        new(Cpus ?? fallback.Cpus, Memory ?? fallback.Memory, Disk ?? fallback.Disk);

    /// <summary>Argumente für <c>multipass launch</c>.</summary>
    public IEnumerable<string> LaunchArguments()
    {
        if (Cpus is { } cpus)
        {
            yield return "--cpus";
            yield return cpus.ToString(CultureInfo.InvariantCulture);
        }

        if (Memory is { } memory)
        {
            yield return "--memory";
            yield return memory;
        }

        if (Disk is { } disk)
        {
            yield return "--disk";
            yield return disk;
        }
    }

    /// <summary>Für Meldungen, z. B. <c>CPUs: 2, Memory: 4G, Disk: 20G</c>; leer ohne Angaben.</summary>
    public override string ToString() =>
        string.Join(", ", new[]
        {
            Cpus is null ? null : $"CPUs: {Cpus}",
            Memory is null ? null : $"Memory: {Memory}",
            Disk is null ? null : $"Disk: {Disk}"
        }.OfType<string>());

    private static string? ParseSize(string? value, string what, long minimum, string example)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var size = value.Trim();
        if (!MultipassSize.TryParse(size, out var bytes))
        {
            throw new MultipassException($"Invalid {what} `{size}` — use a number followed by K, M or G, e.g. {example} or 0.5G.");
        }

        if (bytes < minimum)
        {
            throw new MultipassException($"The {what} `{size}` is too small — Multipass needs at least {minimum / (1024 * 1024)}M.");
        }

        return size;
    }
}
