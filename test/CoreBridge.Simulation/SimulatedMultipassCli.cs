using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CoreBridge.Core.Common;
using CoreBridge.Core.Config;
using CoreBridge.Core.Multipass;

namespace CoreBridge.Simulation;

/// <summary>
/// Multipass-Simulator zum freien Durchspielen der CLI ohne echtes Multipass:
/// hält VMs, Snapshots und übertragene Dateien als Zustand und akzeptiert
/// beliebige Namen und Reihenfolgen. Aktiviert über <see cref="AppConfig.Bootstrap"/>
/// (<see cref="IMultipassCli"/> → <see cref="SimulatedMultipassCli"/>).
/// </summary>
/// <remarks>
/// <para>
/// Ausgaben und Fehlertexte folgen echten Aufnahmen von Multipass 1.16.3
/// (<c>test/CoreBridge.Core.Tests/Recordings</c>); ein Test spielt die Aufnahme
/// gegen den Simulator und vergleicht. Wo es keine Aufnahme gibt (etwa Fehler auf
/// laufenden VMs, die die CLI vorher abfängt), ist der Text nachempfunden.
/// </para>
/// <para>
/// Der Zustand liegt in <c>&lt;StoreRoot&gt;/multipass-sim/state.json</c>, damit er
/// mehrere <c>cb</c>-Aufrufe überlebt; jeder Aufruf lädt und speichert ihn unter
/// einem prozessübergreifenden Mutex. Lang laufende Verben warten
/// <see cref="AppConfig.MultipassSimulationDelay"/> — in Abschnitten wie
/// <see cref="MultipassCli"/>, sodass „weiter warten?“ und Hintergrund-Abschluss
/// (der Zustand ist dann schon geändert, wie beim echten Daemon) durchspielbar sind.
/// </para>
/// <para>
/// <c>exec</c> läuft durch eine kleine simulierte <c>sh</c> (<see cref="SimShell"/>)
/// mit virtuellem Dateisystem: Variablen, <c>$(…)</c>, <c>$((…))</c>, <c>cd</c>
/// (auch über die Arbeitsverzeichnis-Weitergabe von vm-connect), Umleitungen.
/// </para>
/// <para>
/// Cloud-init wird nicht ausgeführt, sondern als erfolgreich angenommen: die
/// Komponenten einer Launch-cloud-init (<c>corebridge-install-&lt;name&gt;</c>)
/// gelten als installiert und beantworten ihre Sonden (<c>--version</c>,
/// <c>/var/lib/corebridge/&lt;name&gt;.ready</c>). Dasselbe gilt, wenn
/// <c>vm-install</c> ein installiertes Komponenten-Skript per exec ausführt.
/// Klone und Snapshots tragen beides mit.
/// </para>
/// </remarks>
public sealed class SimulatedMultipassCli : IMultipassCli, IMultipassDouble
{
    public const string FolderName = "multipass-sim";

    public const string StateFileName = "state.json";

    public const string Version = "1.16.3+sim";

    /// <summary>Verben, die in echt Sekunden bis Minuten dauern.</summary>
    private static readonly HashSet<string> LongRunning = new(StringComparer.Ordinal)
    {
        "launch", "start", "stop", "restart", "clone"
    };

    /// <summary>Optionen, denen ein Wert folgt (für die Suche nach Positionsargumenten).</summary>
    private static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal)
    {
        "--name", "-n", "--cloud-init", "--disk", "-d", "--timeout", "--cpus", "-c",
        "--memory", "-m", "--network", "--mount", "--comment", "--format"
    };

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Multipass-Standard ohne <c>launch --disk</c>.</summary>
    private const string DefaultDisk = "5.0GiB";

    /// <summary>Multipass-Standard ohne <c>launch --memory</c>.</summary>
    private const string DefaultMemory = "1.0GiB";

    private readonly TimeSpan _delay;

    /// <summary>Einziger öffentlicher Konstruktor — den wählt der Container.</summary>
    public SimulatedMultipassCli(AppConfig config)
        : this(
            Path.Combine(RepoPaths.Resolve(config.Inits.StoreRoot), FolderName, StateFileName),
            config.MultipassSimulationDelay.ReadTime())
    {
    }

    private SimulatedMultipassCli(string statePath, TimeSpan delay)
    {
        StatePath = Path.GetFullPath(statePath);
        _delay = delay;
    }

    /// <summary>Simulator mit eigener Zustandsdatei und Dauer (Tests).</summary>
    public static SimulatedMultipassCli At(string statePath, TimeSpan delay) => new(statePath, delay);

    /// <summary>Datei mit dem simulierten Zustand.</summary>
    public string StatePath { get; }

    public string BinaryLabel => "simulation://multipass";

    public MultipassRunResult Run(IReadOnlyList<string> args, int timeoutMs = 30000, string? exePath = null)
    {
        return Timed(args, TimeSpan.FromMilliseconds(timeoutMs), () => false);
    }

    public MultipassRunResult RunWithExtensions(
        IReadOnlyList<string> args,
        TimeSpan slice,
        Func<bool> keepWaiting,
        string? exePath = null)
    {
        return Timed(args, slice, keepWaiting);
    }

    /// <summary><c>shell</c> braucht ein TTY; simuliert wird nur, ob die VM läuft.</summary>
    /// <summary>
    /// Wie das echte <c>multipass exec</c>/<c>shell</c> mit geerbter Konsole: die Ausgabe
    /// des Gasts erscheint direkt (<c>cb work</c> übergibt so an den Agenten).
    /// </summary>
    public int Attach(IReadOnlyList<string> args, string? exePath = null)
    {
        var result = Timed(args, Timeout.InfiniteTimeSpan, () => true);
        Console.Out.Write(result.StdOut);
        Console.Error.Write(result.StdErr);
        return result.ExitCode;
    }

    /// <summary>
    /// Wendet den Aufruf sofort auf den Zustand an und wartet danach die
    /// simulierte Dauer ab — wie beim Daemon, der nach einem Timeout des
    /// Clients im Hintergrund weitermacht.
    /// </summary>
    private MultipassRunResult Timed(IReadOnlyList<string> args, TimeSpan slice, Func<bool> keepWaiting)
    {
        var result = Locked(state => Dispatch(state, args));
        var delay = result.ExitCode == 0 && args.Count > 0 && LongRunning.Contains(args[0])
            ? _delay
            : TimeSpan.Zero;

        var remaining = delay;
        var slices = 0;
        while (remaining > TimeSpan.Zero)
        {
            var step = slice == Timeout.InfiniteTimeSpan || remaining < slice ? remaining : slice;
            Thread.Sleep(step);
            remaining -= step;
            slices++;
            if (remaining > TimeSpan.Zero && !keepWaiting())
            {
                throw new MultipassTimeoutException(
                    string.Join(' ', args),
                    (int)(slice.TotalMilliseconds * slices),
                    string.Empty,
                    string.Empty);
            }
        }

        return result with { DurationMs = (long)delay.TotalMilliseconds };
    }

    /// <summary>
    /// Spiegelt den Bestand eines echten Multipass in den simulierten Zustand: danach hat
    /// der Simulator dieselben VMs (Name, Zustand, IPv4, Release, Disk) und Snapshots
    /// (Name, Parent, Kommentar). Vom echten Multipass wird nur gelesen —
    /// <c>list</c>, <c>list --snapshots</c>, <c>get local.&lt;vm&gt;.disk</c> und mit
    /// <paramref name="probeMarkers"/> ein <c>ls /var/lib/corebridge</c> in laufenden VMs.
    /// </summary>
    /// <remarks>
    /// Was <c>list</c> nicht verrät (Komponenten, Marker, Dateien im Gast), behält eine VM,
    /// die der Simulator unter diesem Namen schon kennt; mit <paramref name="probeMarkers"/>
    /// kommen Marker und Komponenten laufender VMs aus dem echten Gast. Simulierte VMs, die
    /// es echt nicht gibt, verschwinden.
    /// </remarks>
    /// <returns>Zeilen für die Ausgabe: Zusammenfassung und je VM eine Zeile.</returns>
    public IReadOnlyList<string> SyncFrom(IMultipassCli real, bool probeMarkers = false, int timeoutMs = 60_000)
    {
        var list = real.Run(["list", "--format", "json"], timeoutMs);
        if (list.ExitCode != 0)
        {
            throw new MultipassException($"echtes multipass list scheitert (Exit {list.ExitCode}): {MultipassStdErr.Clean(list.StdErr)}");
        }

        var vms = new List<(string Name, string State, string? Ip, string Release)>();
        using (var document = JsonDocument.Parse(list.StdOut))
        {
            if (document.RootElement.TryGetProperty("list", out var entries) && entries.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in entries.EnumerateArray())
                {
                    string? Text(string property) =>
                        entry.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

                    var ip = entry.TryGetProperty("ipv4", out var ips) && ips.ValueKind == JsonValueKind.Array
                        ? ips.EnumerateArray().Select(a => a.GetString()).FirstOrDefault(a => !string.IsNullOrEmpty(a))
                        : null;
                    if (Text("name") is { } name)
                    {
                        vms.Add((name, Text("state") ?? "Unknown", ip, Text("release") ?? "Ubuntu 26.10"));
                    }
                }
            }
        }

        var snapshotsResult = real.Run(["list", "--snapshots", "--format", "json"], timeoutMs);
        var snapshots = snapshotsResult.ExitCode == 0 ? MultipassSnapshotList.Parse(snapshotsResult.StdOut) : [];
        var disks = vms.ToDictionary(
            vm => vm.Name,
            vm => real.Run(["get", $"local.{vm.Name}.disk"], timeoutMs) is { ExitCode: 0 } disk && disk.StdOut.Trim().Length > 0
                ? disk.StdOut.Trim()
                : DefaultDisk);
        string Setting(string vm, string key, string fallback) =>
            real.Run(["get", $"local.{vm}.{key}"], timeoutMs) is { ExitCode: 0 } result && result.StdOut.Trim().Length > 0
                ? result.StdOut.Trim()
                : fallback;
        var cpus = vms.ToDictionary(vm => vm.Name, vm => int.TryParse(Setting(vm.Name, "cpus", "1"), out var count) ? count : 1);
        var memories = vms.ToDictionary(vm => vm.Name, vm => Setting(vm.Name, "memory", DefaultMemory));
        var markers = new Dictionary<string, List<string>>();
        if (probeMarkers)
        {
            foreach (var vm in vms.Where(vm => vm.State == "Running"))
            {
                var ls = real.Run(["exec", vm.Name, "--", "/bin/sh", "-c", "ls /var/lib/corebridge 2>/dev/null || true"], timeoutMs);
                markers[vm.Name] = ls.ExitCode == 0
                    ? Regex.Matches(ls.StdOut, @"^([A-Za-z0-9_-]+)\.ready\s*$", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList()
                    : [];
            }
        }

        var lines = new List<string>();
        Locked(state =>
        {
            var known = state.Vms.ToDictionary(vm => vm.Name, StringComparer.OrdinalIgnoreCase);
            state.Vms = vms.Select(vm =>
            {
                var previous = known.GetValueOrDefault(vm.Name);
                var own = snapshots.Where(s => s.Instance == vm.Name).ToList();
                var sim = new SimVm
                {
                    Name = vm.Name,
                    State = vm.State,
                    Release = vm.Release,
                    Disk = disks[vm.Name],
                    Cpus = cpus[vm.Name],
                    Memory = memories[vm.Name],
                    Ip = vm.Ip ?? previous?.Ip ?? NextIp(state),
                    Components = markers.TryGetValue(vm.Name, out var probed) ? [.. probed] : [.. previous?.Components ?? []],
                    Markers = markers.TryGetValue(vm.Name, out var probedMarkers) ? [.. probedMarkers] : [.. previous?.Markers ?? []],
                    Files = previous?.Files ?? new Dictionary<string, string>(StringComparer.Ordinal),
                    Directories = previous?.Directories ?? [],
                    Snapshots = own.Select(s => new SimSnapshot
                    {
                        Name = s.Name,
                        Parent = s.Parent ?? string.Empty,
                        Comment = s.Comment ?? string.Empty
                    }).ToList(),
                    // Namenszähler so, dass die nächsten Standardnamen frei sind.
                    SnapshotCount = Math.Max(own.Count, own.Select(s => Regex.Match(s.Name, @"^snapshot(\d+)$")).Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value)).DefaultIfEmpty(0).Max()),
                    CloneCount = vms.Select(o => Regex.Match(o.Name, $@"^{Regex.Escape(vm.Name)}-clone(\d+)$")).Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value)).DefaultIfEmpty(0).Max(),
                    // Parent des nächsten Snapshots: ein Blatt des Baums (welcher zuletzt genommen wurde, verrät list nicht).
                    CurrentSnapshot = own.LastOrDefault(s => own.All(o => o.Parent != s.Name))?.Name
                };
                lines.Add($"  {sim.Name,-32} {sim.State,-10} {sim.Disk,-9} Snapshots: {sim.Snapshots.Count,-3} Marker: {(sim.Markers.Count == 0 ? "--" : string.Join(",", sim.Markers))}");
                return sim;
            }).ToList();
            return Ok(string.Empty);
        });

        lines.Insert(0, $"Synchronisiert: {vms.Count} VM(s), {snapshots.Count} Snapshot(s) aus dem echten Multipass{(probeMarkers ? " (Marker aus laufenden Gästen)" : string.Empty)}.");
        return lines;
    }

    private MultipassRunResult Locked(Func<SimState, MultipassRunResult> action)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(StatePath.ToLowerInvariant())))[..16];
        using var mutex = new Mutex(false, $@"Local\CoreBridge.MultipassSimulation.{key}");
        try
        {
            mutex.WaitOne();
        }
        catch (AbandonedMutexException)
        {
            // Ein abgestürzter Prozess gibt den Mutex so frei — der Zustand auf Platte zählt.
        }

        try
        {
            var state = Load();
            state.DisksRoot = Path.Combine(Path.GetDirectoryName(StatePath)!, "disks");
            var result = action(state);
            Save(state);
            return result;
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private MultipassRunResult Dispatch(SimState state, IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return Fail("multipass: no command given (simulation).");
        }

        return args[0] switch
        {
            "version" => Version_(args),
            "list" or "ls" => args.Contains("--snapshots") ? ListSnapshots(state, args) : List(state, args),
            "launch" => Launch(state, args),
            "start" => Start(state, args),
            "stop" => Stop(state, args),
            "restart" => Start(state, args),
            "delete" or "rm" => Delete(state, args),
            "purge" => Purge(state),
            "recover" => Recover(state, args),
            "clone" => Clone(state, args),
            "snapshot" => Snapshot(state, args),
            "restore" => Restore(state, args),
            "exec" => Exec(state, args),
            "shell" or "sh" => Shell(state, args),
            "transfer" => Transfer(state, args),
            "info" => Info(state, args),
            "get" => Get(state, args),
            "set" => Set(state, args),
            _ => Fail($"multipass: unknown command '{args[0]}' (simulation).")
        };
    }

    private static MultipassRunResult Version_(IReadOnlyList<string> args)
    {
        return args.Contains("json")
            ? Ok($$"""
                {
                    "multipass": "{{Version}}",
                    "multipassd": "{{Version}}"
                }

                """)
            : Ok($"multipass   {Version}\nmultipassd  {Version}\n");
    }

    /// <summary>Ohne <c>--format</c> eine Tabelle wie bei Multipass, sonst json oder csv.</summary>
    private static MultipassRunResult List(SimState state, IReadOnlyList<string> args)
    {
        // Multipass 1.16 hat keine feste Reihenfolge (meist aktive vor dem Papierkorb,
        // aber nicht immer alphabetisch); der Simulator bleibt deterministisch:
        // aktive VMs alphabetisch, danach der Papierkorb.
        var vms = state.Vms
            .OrderBy(vm => vm.State == "Deleted")
            .ThenBy(vm => vm.Name, StringComparer.Ordinal)
            .ToList();
        switch (Value(args, "--format") ?? "table")
        {
            case "json":
                var entries = vms.Select(vm => new Dictionary<string, object?>
                {
                    ["ipv4"] = vm.State == "Running" ? new[] { vm.Ip } : Array.Empty<string>(),
                    ["name"] = vm.Name,
                    ["release"] = vm.Release,
                    ["state"] = vm.State
                });
                return Ok(JsonSerializer.Serialize(new { list = entries }, JsonOptions) + "\n");
            case "csv":
                return Ok("Name,State,IPv4,IPv6,Release,AllIPv4\n" + string.Concat(vms.Select(vm =>
                    $"{vm.Name},{vm.State},{IpOf(vm)},,{vm.Release},{IpOf(vm)}\n")));
            case "table":
                return vms.Count == 0
                    ? Ok("No instances found.\n")
                    : Ok(Table(
                        ["Name", "State", "IPv4", "Image"],
                        [24, 18, 17, 0],
                        vms.Select(vm => new[] { vm.Name, vm.State, vm.State == "Running" ? vm.Ip : "--", vm.Release })));
            default:
                return Fail("Invalid format type given.");
        }
    }

    private static string IpOf(SimVm vm) => vm.State == "Running" ? vm.Ip : string.Empty;

    private static MultipassRunResult ListSnapshots(SimState state, IReadOnlyList<string> args)
    {
        var vms = state.Vms
            .Where(vm => vm.Snapshots.Count > 0)
            .OrderBy(vm => vm.Name, StringComparer.Ordinal)
            .ToList();
        switch (Value(args, "--format") ?? "table")
        {
            case "json":
                var info = vms.ToDictionary(
                    vm => vm.Name,
                    vm => vm.Snapshots.ToDictionary(
                        s => s.Name,
                        s => new { comment = s.Comment, parent = s.Parent }));
                return Ok(JsonSerializer.Serialize(new { errors = Array.Empty<string>(), info }, JsonOptions) + "\n");
            case "csv":
                return Ok("Instance,Snapshot,Parent,Comment\n" + string.Concat(vms.SelectMany(vm => vm.Snapshots.Select(s =>
                    $"{vm.Name},{s.Name},{s.Parent},\"{s.Comment}\"\n"))));
            case "table":
                return vms.Count == 0
                    ? Ok("No snapshots found.\n")
                    : Ok(Table(
                        ["Instance", "Snapshot", "Parent", "Comment"],
                        [0, 0, 0, 0],
                        vms.SelectMany(vm => vm.Snapshots.Select(s => new[]
                        {
                            vm.Name, s.Name, Dash(s.Parent), Dash(s.Comment)
                        }))));
            default:
                return Fail("Invalid format type given.");
        }
    }

    private static string Dash(string value) => string.IsNullOrEmpty(value) ? "--" : value;

    /// <summary>
    /// Spalten wie in Multipass' Tabellen: jede so breit wie ihr längster Eintrag plus
    /// drei Leerzeichen, mindestens <paramref name="minimum"/>; die letzte ungepolstert.
    /// </summary>
    private static string Table(string[] headers, int[] minimum, IEnumerable<string[]> rows)
    {
        var all = rows.Prepend(headers).ToList();
        var widths = headers.Select((_, c) => Math.Max(minimum[c], all.Max(r => r[c].Length) + 3)).ToArray();
        return string.Concat(all.Select(r =>
            string.Concat(r.Select((cell, c) => c == r.Length - 1 ? cell : cell.PadRight(widths[c]))).TrimEnd() + "\n"));
    }

    private static MultipassRunResult Launch(SimState state, IReadOnlyList<string> args)
    {
        var positionals = Positionals(args);
        var image = positionals.FirstOrDefault() ?? "daily:26.10";
        if (!IsKnownImage(image))
        {
            var remote = image.Contains(':') ? image[..image.IndexOf(':')] : string.Empty;
            return Fail($"launch failed: Unable to find an image matching \"{image}\" in remote \"{remote}\".");
        }

        var name = Value(args, "--name", "-n") ?? $"sim-{state.Vms.Count + 1}";
        if (Find(state, name) is not null)
        {
            return Fail($"launch failed: instance \"{name}\" already exists");
        }

        var components = new List<string>();
        var snaps = new List<string>();
        var cloudInit = Value(args, "--cloud-init");
        if (cloudInit is not null)
        {
            if (!File.Exists(cloudInit))
            {
                return Fail($"launch failed: cannot read cloud-init file \"{cloudInit}\"");
            }

            components.AddRange(Regex.Matches(File.ReadAllText(cloudInit), @"corebridge-install-([A-Za-z0-9_-]+)")
                .Select(m => m.Groups[1].Value)
                .Distinct(StringComparer.Ordinal));
            snaps.AddRange(Regex.Matches(File.ReadAllText(cloudInit), @"\[snap, install, ""?([A-Za-z0-9._-]+)""?")
                .Select(m => m.Groups[1].Value)
                .Distinct(StringComparer.Ordinal));
        }

        state.Vms.Add(new SimVm
        {
            Name = name,
            State = "Running",
            Release = ReleaseOf(image),
            Ip = NextIp(state),
            Disk = Value(args, "--disk", "-d") is { } disk && MultipassSize.TryParse(disk, out var bytes) ? FormatSize(bytes) : DefaultDisk,
            Cpus = Value(args, "--cpus", "-c") is { } cpus && int.TryParse(cpus, out var cpuCount) ? cpuCount : 1,
            Memory = Value(args, "--memory", "-m") is { } memory && MultipassSize.TryParse(memory, out var memoryBytes) ? FormatSize(memoryBytes) : DefaultMemory,
            Components = components,
            Markers = components.ToList(),
            Files = snaps.Count == 0
                ? new(StringComparer.Ordinal)
                : new(StringComparer.Ordinal) { [SimShell.SnapsFile] = string.Concat(snaps.Select(snap => snap + "\n")) }
        });
        return Ok($"Launched: {name}\n");
    }

    private static MultipassRunResult Start(SimState state, IReadOnlyList<string> args)
    {
        var verb = args[0];
        foreach (var name in Positionals(args))
        {
            var vm = Find(state, name);
            if (vm is null)
            {
                return Fail($"{verb} failed: instance(s) missing\nInstance '{name}' does not exist.\n");
            }

            if (vm.State == "Deleted")
            {
                return Fail($"{verb} failed: instance(s) deleted\nInstance '{name}' is deleted. Use 'recover' to recover it or 'purge' to permanently delete it.\n");
            }

            vm.State = "Running";
        }

        return Ok(string.Empty);
    }

    private static MultipassRunResult Stop(SimState state, IReadOnlyList<string> args)
    {
        foreach (var name in Positionals(args))
        {
            var vm = Find(state, name);
            if (vm is null)
            {
                return NotFound("stop", name);
            }

            if (vm.State == "Deleted")
            {
                return Fail($"stop failed: The following errors occurred:\ninstance \"{name}\" is deleted");
            }

            vm.State = "Stopped";
        }

        return Ok(string.Empty);
    }

    private static MultipassRunResult Delete(SimState state, IReadOnlyList<string> args)
    {
        var purge = args.Contains("--purge") || args.Contains("-p");
        foreach (var target in Positionals(args))
        {
            var (name, snapshot) = SplitSnapshot(target);
            var vm = Find(state, name);
            if (vm is null)
            {
                return NotFound("delete", name);
            }

            if (snapshot is not null)
            {
                if (!purge)
                {
                    return Fail("delete failed: Snapshots can only be purged (after deletion, they cannot be recovered). Please use the `--purge` flag if that is what you want.");
                }

                if (vm.Snapshots.RemoveAll(s => s.Name == snapshot) == 0)
                {
                    return Fail($"delete failed: The following errors occurred:\nNo such snapshot: {name}.{snapshot}");
                }

                DeleteDirectory(state.Disk(vm.Name, snapshot));

                continue;
            }

            if (purge)
            {
                state.Vms.Remove(vm);
                DeleteDisks(state, vm);
            }
            else
            {
                vm.State = "Deleted";
            }
        }

        return Ok(string.Empty);
    }

    private static MultipassRunResult Purge(SimState state)
    {
        foreach (var vm in state.Vms.Where(vm => vm.State == "Deleted").ToList())
        {
            state.Vms.Remove(vm);
            DeleteDisks(state, vm);
        }

        return Ok(string.Empty);
    }

    /// <summary>Wo ein Gastpfad auf der Host-Disk der VM liegt.</summary>
    private static string OnDisk(SimState state, string vm, string guestPath) =>
        Path.Combine(state.Disk(vm), guestPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

    private static void DeleteDisks(SimState state, SimVm vm)
    {
        DeleteDirectory(state.Disk(vm.Name));
        foreach (var snapshot in vm.Snapshots)
        {
            DeleteDirectory(state.Disk(vm.Name, snapshot.Name));
        }
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    /// <summary>Kopiert eine Disk (Klon, Snapshot, Restore); eine fehlende Quelle ergibt eine leere.</summary>
    private static void CopyDisk(string from, string to)
    {
        DeleteDirectory(to);
        if (!Directory.Exists(from))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, directory)));
        }

        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
        }
    }

    private static MultipassRunResult Recover(SimState state, IReadOnlyList<string> args)
    {
        foreach (var name in Positionals(args))
        {
            var vm = Find(state, name);
            if (vm is null)
            {
                return NotFound("recover", name);
            }

            if (vm.State == "Deleted")
            {
                vm.State = "Stopped";
            }
        }

        return Ok(string.Empty);
    }

    /// <summary>
    /// Wie Multipass: nur gestoppte Quellen; Standardname <c>&lt;source&gt;-cloneN</c>,
    /// wobei N alle Klone der Quelle zählt, auch benannte. Snapshots werden nicht mitgeklont.
    /// </summary>
    private static MultipassRunResult Clone(SimState state, IReadOnlyList<string> args)
    {
        var source = Positionals(args).FirstOrDefault() ?? string.Empty;
        var vm = Find(state, source);
        if (vm is null)
        {
            return Fail($"clone failed: instance \"{source}\" does not exist");
        }

        if (vm.State != "Stopped")
        {
            return Fail("clone failed: Multipass can only clone stopped instances.");
        }

        vm.CloneCount++;
        var name = Value(args, "--name", "-n") ?? $"{vm.Name}-clone{vm.CloneCount}";
        if (Find(state, name) is not null)
        {
            return Fail($"clone failed: instance \"{name}\" already exists");
        }

        state.Vms.Add(new SimVm
        {
            Name = name,
            State = "Stopped",
            Release = vm.Release,
            Ip = NextIp(state),
            Disk = vm.Disk,
            Cpus = vm.Cpus,
            Memory = vm.Memory,
            Components = [.. vm.Components],
            Markers = [.. vm.Markers],
            Files = new Dictionary<string, string>(vm.Files),
            Directories = [.. vm.Directories]
        });
        CopyDisk(state.Disk(vm.Name), state.Disk(name));
        return Ok($"Cloned from {vm.Name} to {name}.\n");
    }

    /// <summary>
    /// Standardname <c>snapshotN</c> (N = 1 + Zahl aller je genommenen Snapshots);
    /// Parent ist der zuletzt genommene oder wiederhergestellte Snapshot.
    /// </summary>
    private static MultipassRunResult Snapshot(SimState state, IReadOnlyList<string> args)
    {
        var name = Positionals(args).FirstOrDefault() ?? string.Empty;
        var vm = Find(state, name);
        if (vm is null)
        {
            return Fail($"snapshot failed: instance \"{name}\" does not exist");
        }

        if (vm.State != "Stopped")
        {
            return Fail("snapshot failed: Multipass can only take snapshots of stopped instances.");
        }

        var snapshot = Value(args, "--name", "-n") ?? $"snapshot{vm.SnapshotCount + 1}";
        if (vm.Snapshots.Any(s => s.Name == snapshot))
        {
            return Fail($"snapshot failed: Snapshot already exists: {vm.Name}.{snapshot}");
        }

        vm.SnapshotCount++;
        vm.Snapshots.Add(new SimSnapshot
        {
            Name = snapshot,
            Parent = vm.CurrentSnapshot ?? string.Empty,
            Comment = Value(args, "--comment", "-c", "-m") ?? string.Empty,
            Components = [.. vm.Components],
            Markers = [.. vm.Markers],
            Files = new Dictionary<string, string>(vm.Files),
            Directories = [.. vm.Directories]
        });
        vm.CurrentSnapshot = snapshot;
        CopyDisk(state.Disk(vm.Name), state.Disk(vm.Name, snapshot));
        return Ok($"Snapshot taken: {vm.Name}.{snapshot}\n");
    }

    private static MultipassRunResult Restore(SimState state, IReadOnlyList<string> args)
    {
        var (name, snapshot) = SplitSnapshot(Positionals(args).FirstOrDefault() ?? string.Empty);
        var vm = Find(state, name);
        if (vm is null)
        {
            return Fail($"restore failed: instance \"{name}\" does not exist");
        }

        var saved = vm.Snapshots.FirstOrDefault(s => s.Name == snapshot);
        if (saved is null)
        {
            return Fail($"restore failed: No such snapshot: {name}.{snapshot}");
        }

        if (vm.State != "Stopped")
        {
            return Fail("restore failed: Multipass can only restore snapshots of stopped instances.");
        }

        vm.Components = [.. saved.Components];
        vm.Markers = [.. saved.Markers];
        vm.Files = new Dictionary<string, string>(saved.Files);
        vm.Directories = [.. saved.Directories];
        vm.CurrentSnapshot = saved.Name;
        CopyDisk(state.Disk(vm.Name, saved.Name), state.Disk(vm.Name));
        return Ok($"Snapshot restored: {vm.Name}.{saved.Name}\n");
    }

    private static MultipassRunResult Shell(SimState state, IReadOnlyList<string> args)
    {
        var name = args.ElementAtOrDefault(1) ?? string.Empty;
        return RequireRunning(state, "shell", name, out _) ?? Ok(string.Empty);
    }

    /// <summary>
    /// <c>info &lt;vm&gt;… | &lt;vm&gt;.&lt;snapshot&gt;</c> als Tabelle oder json; CPU, Last und
    /// Speicher sind feste Werte, der Rest folgt dem Zustand.
    /// </summary>
    private static MultipassRunResult Info(SimState state, IReadOnlyList<string> args)
    {
        var targets = Positionals(args);
        if (targets.Count == 0)
        {
            return Fail("info failed: instance name(s) required (simulation).");
        }

        var blocks = new List<string>();
        var info = new Dictionary<string, object>();
        foreach (var target in targets)
        {
            var (name, snapshotName) = SplitSnapshot(target);
            var vm = Find(state, name);
            if (vm is null)
            {
                return NotFound("info", name);
            }

            if (snapshotName is not null)
            {
                var snapshot = vm.Snapshots.FirstOrDefault(s => s.Name == snapshotName);
                if (snapshot is null)
                {
                    return Fail($"info failed: The following errors occurred:\nNo such snapshot: {name}.{snapshotName}");
                }

                var children = string.Join(", ", vm.Snapshots.Where(s => s.Parent == snapshot.Name).Select(s => s.Name));
                blocks.Add(string.Join('\n',
                    $"Snapshot:       {snapshot.Name}",
                    $"Instance:       {vm.Name}",
                    $"Disk space:     {vm.Disk}",
                    $"Parent:         {Dash(snapshot.Parent)}",
                    $"Children:       {Dash(children)}",
                    $"Comment:        {Dash(snapshot.Comment)}"));
                info[target] = new { snapshot = snapshot.Name, instance = vm.Name, parent = snapshot.Parent, children, comment = snapshot.Comment };
                continue;
            }

            var running = vm.State == "Running";
            blocks.Add(string.Join('\n',
                $"Name:           {vm.Name}",
                $"State:          {vm.State}",
                $"Snapshots:      {vm.Snapshots.Count}",
                $"IPv4:           {(running ? vm.Ip : "--")}",
                $"Release:        {(running ? vm.Release : "--")}",
                "Image hash:     --",
                $"CPU(s):         {(running ? vm.Cpus.ToString(System.Globalization.CultureInfo.InvariantCulture) : "--")}",
                $"Load:           {(running ? "0.00 0.00 0.00" : "--")}",
                $"Disk usage:     {(running ? "2.1GiB out of " + vm.Disk : "--")}",
                $"Memory usage:   {(running ? "312.4MiB out of " + vm.Memory : "--")}",
                "Mounts:         --"));
            info[vm.Name] = new Dictionary<string, object?>
            {
                ["cpu_count"] = running ? vm.Cpus.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty,
                ["disks"] = new { sda1 = new { total = vm.Disk } },
                ["ipv4"] = running ? new[] { vm.Ip } : Array.Empty<string>(),
                ["release"] = running ? vm.Release : string.Empty,
                ["snapshot_count"] = vm.Snapshots.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["state"] = vm.State
            };
        }

        return Value(args, "--format") == "json"
            ? Ok(JsonSerializer.Serialize(new { errors = Array.Empty<string>(), info }, JsonOptions) + "\n")
            : Ok(string.Join("\n\n", blocks) + "\n");
    }

    /// <summary><c>local.&lt;vm&gt;.cpus|memory|disk</c>; Fehlertext wie Multipass 1.16.3.</summary>
    private static MultipassRunResult Get(SimState state, IReadOnlyList<string> args)
    {
        var key = Regex.Match(args.ElementAtOrDefault(1) ?? string.Empty, @"^local\.(.+)\.(cpus|memory|disk)$");
        if (!key.Success)
        {
            return Fail($"get failed: unknown setting '{args.ElementAtOrDefault(1)}' (simulation).");
        }

        var vm = Find(state, key.Groups[1].Value);
        return vm is null
            ? Fail($"get failed: Cannot obtain instance settings; instance: {key.Groups[1].Value}; reason: No such instance")
            : Ok(key.Groups[2].Value switch
            {
                "cpus" => vm.Cpus.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "memory" => vm.Memory,
                _ => vm.Disk
            } + "\n");
    }

    /// <summary>
    /// <c>set local.&lt;vm&gt;.disk=&lt;size&gt;</c> mit den Ablehnungen von Multipass 1.16.3
    /// (laufende VM, ungültige Größe, Verkleinern — Texte am 2026-09-26 aufgenommen);
    /// <c>cpus</c>/<c>memory</c> mit Texten in Anlehnung daran.
    /// </summary>
    private static MultipassRunResult Set(SimState state, IReadOnlyList<string> args)
    {
        var setting = args.ElementAtOrDefault(1) ?? string.Empty;
        var key = Regex.Match(setting, @"^local\.(.+)\.(cpus|memory|disk)=(.*)$");
        if (!key.Success)
        {
            return Fail($"set failed: unknown setting '{setting}' (simulation).");
        }

        var (name, property, size) = (key.Groups[1].Value, key.Groups[2].Value, key.Groups[3].Value);
        var vm = Find(state, name);
        if (vm is null)
        {
            return Fail($"set failed: Cannot update instance settings; instance: {name}; reason: No such instance");
        }

        if (vm.State != "Stopped")
        {
            return Fail($"set failed: Cannot update instance settings; instance: {name}; reason: Instance must be stopped for modification");
        }

        if (property == "cpus")
        {
            // Text in Anlehnung an die Disk-Ablehnungen (nicht aufgenommen).
            if (!int.TryParse(size, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count) || count < 1)
            {
                return Fail($"set failed: Invalid setting '{setting}': Need a positive integer (in decimal format) for the number of CPUs");
            }

            vm.Cpus = count;
            return Ok(string.Empty);
        }

        if (!MultipassSize.TryParse(size, out var bytes))
        {
            return Fail($"set failed: Invalid setting '{setting}': {size} is not a valid memory size - need a non-negative integer (in base 10) or a decimal followed by K, M, or G (e.g. 1234B, 42MiB, 0.5G)");
        }

        if (property == "memory")
        {
            if (bytes < 128L * 1024 * 1024)
            {
                return Fail($"set failed: Invalid setting '{setting}': Memory can't be less than 128M");
            }

            vm.Memory = FormatSize(bytes);
            return Ok(string.Empty);
        }

        if (MultipassSize.TryParse(vm.Disk, out var current) && bytes < current)
        {
            return Fail($"set failed: Invalid setting '{setting}': Disk can only be expanded");
        }

        vm.Disk = FormatSize(bytes);
        return Ok(string.Empty);
    }

    /// <summary>Anzeige wie <c>multipass get</c> (<c>5.0GiB</c>); unter 1 GiB in MiB.</summary>
    private static string FormatSize(long bytes)
    {
        const double GiB = 1024d * 1024 * 1024;
        return bytes >= GiB
            ? (bytes / GiB).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "GiB"
            : (bytes / (1024d * 1024)).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "MiB";
    }

    private static MultipassRunResult Exec(SimState state, IReadOnlyList<string> args)
    {
        var name = args.ElementAtOrDefault(1) ?? string.Empty;
        var failure = RequireRunning(state, "exec", name, out var vm);
        if (failure is not null)
        {
            return failure;
        }

        // Wie der echte Gast: das Kommando läuft ohne Shell, `sh -c` liefert der Aufrufer
        // selbst mit — die simulierte Shell bekommt es also als neu zusammengesetzte Zeile.
        var separator = args.ToList().IndexOf("--");
        var command = separator < 0 ? [] : args.Skip(separator + 1).ToArray();
        var script = string.Join(' ', command.Select(CoreBridge.Core.Common.Shell.Quote));
        if (Value(args, "--working-directory", "-d") is { } directory)
        {
            script = $"cd {CoreBridge.Core.Common.Shell.Quote(directory)} && {script}";
        }

        var (code, stdOut, stdErr) = new SimShell(vm!, state.Disk(vm!.Name)).Run(script);
        return new MultipassRunResult(code, stdOut, stdErr, 0);
    }

    private static MultipassRunResult Transfer(SimState state, IReadOnlyList<string> args)
    {
        var operands = Positionals(args);
        if (operands.Count < 2)
        {
            return Fail("transfer failed: source and destination required");
        }

        var (source, destination) = (operands[0], operands[1]);
        if (IsRemote(source))
        {
            var (vmName, remote) = SplitRemote(source);
            remote = SimShell.Normalize(remote);
            var failure = RequireRunning(state, "transfer", vmName, out var vm);
            if (failure is not null)
            {
                return failure;
            }

            var onDisk = OnDisk(state, vm!.Name, remote);
            if (!vm.Files.TryGetValue(remote, out var content) && !File.Exists(onDisk))
            {
                return Fail($"transfer failed: remote \"{remote}\" does not exist");
            }

            var directory = Path.GetDirectoryName(Path.GetFullPath(destination));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (content is not null)
            {
                File.WriteAllText(destination, content);
            }
            else
            {
                File.Copy(onDisk, destination, overwrite: true);
            }

            return Ok(string.Empty);
        }

        if (IsRemote(destination))
        {
            var (vmName, remote) = SplitRemote(destination);
            remote = SimShell.Normalize(remote);
            var failure = RequireRunning(state, "transfer", vmName, out var vm);
            if (failure is not null)
            {
                return failure;
            }

            if (!File.Exists(source))
            {
                return Fail($"transfer failed: source \"{source}\" does not exist");
            }

            // Text bleibt im Zustand (lesbar für die Schale), Binäres (git bundles) nur auf der Disk.
            var bytes = File.ReadAllBytes(source);
            var target = OnDisk(state, vm!.Name, remote);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, bytes);
            if (Array.IndexOf(bytes, (byte)0) < 0)
            {
                vm.Files[remote] = Encoding.UTF8.GetString(bytes);
            }
            else
            {
                vm.Files.Remove(remote);
            }

            return Ok(string.Empty);
        }

        return Fail("transfer failed: one of source or destination must be an instance");
    }

    private static MultipassRunResult? RequireRunning(SimState state, string verb, string name, out SimVm? vm)
    {
        vm = Find(state, name);
        if (vm is null)
        {
            return NotFound(verb, name);
        }

        return vm.State == "Running"
            ? null
            : Fail($"{verb} failed: The following errors occurred:\ninstance \"{name}\" is not running");
    }

    private static SimVm? Find(SimState state, string name)
    {
        return state.Vms.FirstOrDefault(vm => string.Equals(vm.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Katalog laut <c>multipass find</c> (1.16.3): Versionen, Aliase, Core,
    /// Appliances, <c>daily:</c>/<c>release:</c>-Versionen sowie Datei/URL.
    /// Ein vertippter Profilname landet so wie in echt bei „Unable to find an image“.
    /// </summary>
    private static bool IsKnownImage(string image)
    {
        return Regex.IsMatch(
            image,
            @"^((daily|release):)?\d{2}\.\d{2}$|^(jammy|noble|resolute|lts|stonking|devel)$|^core\d*$|^appliance:[a-z-]+$|^(file|https?)://",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string ReleaseOf(string image)
    {
        var version = Regex.Match(image, @"(\d{2}\.\d{2})");
        return version.Success ? $"Ubuntu {version.Value}" : $"Ubuntu ({image})";
    }

    private static string NextIp(SimState state)
    {
        state.IpCount++;
        return $"172.30.99.{state.IpCount % 254 + 1}";
    }

    private static List<string> Positionals(IReadOnlyList<string> args)
    {
        var positionals = new List<string>();
        for (var i = 1; i < args.Count; i++)
        {
            if (args[i] == "--")
            {
                break;
            }

            if (ValueOptions.Contains(args[i]))
            {
                i++;
                continue;
            }

            if (!args[i].StartsWith('-'))
            {
                positionals.Add(args[i]);
            }
        }

        return positionals;
    }

    private static string? Value(IReadOnlyList<string> args, params string[] names)
    {
        for (var i = 1; i < args.Count - 1; i++)
        {
            if (names.Contains(args[i]))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static (string Name, string? Snapshot) SplitSnapshot(string target)
    {
        var dot = target.IndexOf('.');
        return dot < 0 ? (target, null) : (target[..dot], target[(dot + 1)..]);
    }

    /// <summary><c>vm:pfad</c>, aber kein Windows-Laufwerk wie <c>C:\…</c>.</summary>
    private static bool IsRemote(string operand)
    {
        var colon = operand.IndexOf(':');
        return colon > 1;
    }

    private static (string Vm, string Path) SplitRemote(string operand)
    {
        var colon = operand.IndexOf(':');
        return (operand[..colon], operand[(colon + 1)..]);
    }

    private SimState Load()
    {
        if (!File.Exists(StatePath))
        {
            return new SimState();
        }

        try
        {
            return JsonSerializer.Deserialize<SimState>(File.ReadAllText(StatePath)) ?? new SimState();
        }
        catch (JsonException)
        {
            // Ein kaputter Zustand ist kein Grund, das Durchspielen zu verweigern.
            return new SimState();
        }
    }

    private void Save(SimState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        File.WriteAllText(StatePath, JsonSerializer.Serialize(state, JsonOptions));
    }

    private static MultipassRunResult NotFound(string verb, string name)
    {
        return Fail($"{verb} failed: The following errors occurred:\ninstance \"{name}\" does not exist");
    }

    private static MultipassRunResult Ok(string stdOut) => new(0, stdOut, string.Empty, 0);

    /// <summary>Multipass beendet Fehler mit Exit 2.</summary>
    private static MultipassRunResult Fail(string stdErr) => new(2, string.Empty, stdErr.EndsWith('\n') ? stdErr : stdErr + "\n", 0);

    private sealed class SimState
    {
        /// <summary>Host-Ordner der VM-Disks (neben state.json); nicht Teil des Zustands.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string DisksRoot { get; set; } = string.Empty;

        /// <summary>Die Disk einer VM (bzw. eines Snapshots: <c>&lt;vm&gt;@&lt;snapshot&gt;</c>) auf dem Host.</summary>
        public string Disk(string vm, string? snapshot = null) =>
            Path.Combine(DisksRoot, snapshot is null ? vm : $"{vm}@{snapshot}");

        public List<SimVm> Vms { get; set; } = [];

        public int IpCount { get; set; }
    }

    internal sealed class SimVm
    {
        public string Name { get; set; } = string.Empty;

        public string State { get; set; } = "Stopped";

        public string Release { get; set; } = "Ubuntu 26.10";

        public string Ip { get; set; } = string.Empty;

        /// <summary>Wie <c>multipass get local.&lt;vm&gt;.disk</c> es ausgibt.</summary>
        public string Disk { get; set; } = DefaultDisk;

        public int Cpus { get; set; } = 1;

        /// <summary>Wie <c>multipass get local.&lt;vm&gt;.memory</c> es ausgibt.</summary>
        public string Memory { get; set; } = DefaultMemory;

        public List<string> Components { get; set; } = [];

        /// <summary>Komponenten, deren <c>/var/lib/corebridge/&lt;name&gt;.ready</c> existiert.</summary>
        public List<string> Markers { get; set; } = [];

        /// <summary>Per <c>transfer</c> in die VM kopierte Dateien (Pfad → Inhalt).</summary>
        public Dictionary<string, string> Files { get; set; } = new(StringComparer.Ordinal);

        /// <summary>Per <c>mkdir</c> angelegte Verzeichnisse (absolut); Elternordner von Dateien gelten ohnehin.</summary>
        public List<string> Directories { get; set; } = [];

        public List<SimSnapshot> Snapshots { get; set; } = [];

        public int SnapshotCount { get; set; }

        public int CloneCount { get; set; }

        public string? CurrentSnapshot { get; set; }
    }

    internal sealed class SimSnapshot
    {
        public string Name { get; set; } = string.Empty;

        public string Parent { get; set; } = string.Empty;

        public string Comment { get; set; } = string.Empty;

        public List<string> Components { get; set; } = [];

        public List<string> Markers { get; set; } = [];

        public Dictionary<string, string> Files { get; set; } = new(StringComparer.Ordinal);

        public List<string> Directories { get; set; } = [];
    }
}
