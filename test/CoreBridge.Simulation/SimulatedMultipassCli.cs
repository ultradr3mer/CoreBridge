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
/// Cloud-init wird nicht ausgeführt, sondern als erfolgreich angenommen: die
/// Komponenten einer Launch-cloud-init (<c>corebridge-install-&lt;name&gt;</c>)
/// gelten als installiert und beantworten ihre Sonden (<c>--version</c>,
/// <c>/var/lib/corebridge/&lt;name&gt;.ready</c>). Dasselbe gilt, wenn
/// <c>vm-install</c> ein installiertes Komponenten-Skript per exec ausführt.
/// Klone und Snapshots tragen beides mit. Wie in echt setzt
/// <see cref="ComponentsWithoutMarker"/> (dotnet) keinen Marker.
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

    /// <summary>
    /// Komponenten, deren echtes Skript keinen <c>.ready</c>-Marker anlegt:
    /// <c>scripts/components/install-dotnet.sh</c> hat kein <c>touch</c>. Bleibt
    /// hier, bis das Skript repariert ist — sonst wäre der Simulator grün, wo
    /// die echte VM rot ist.
    /// </summary>
    private static readonly HashSet<string> ComponentsWithoutMarker = new(StringComparer.Ordinal) { "dotnet" };

    /// <summary>
    /// Komponenten, die nur eine Login-Shell findet: <c>install-dotnet.sh</c> verlinkt
    /// dotnet nach <c>~/.local/bin</c>, das erst die <c>.profile</c> in den PATH holt.
    /// <c>multipass exec … /bin/sh -c</c> ist keine Login-Shell → Exit 127
    /// (verifiziert am 2026-09-26 mit ai-ubuntu-dotnet, Multipass 1.16.3).
    /// </summary>
    private static readonly HashSet<string> ComponentsOnlyInLoginShell = new(StringComparer.Ordinal) { "dotnet" };

    /// <summary>Multipass-Standard ohne <c>launch --disk</c>.</summary>
    private const string DefaultDisk = "5.0GiB";

    private readonly TimeSpan _delay;

    /// <summary>Einziger öffentlicher Konstruktor — den wählt der Container.</summary>
    public SimulatedMultipassCli(AppConfig config)
        : this(
            Path.Combine(RepoPaths.Resolve(config.Images.StoreRoot), FolderName, StateFileName),
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
    public int Attach(IReadOnlyList<string> args, string? exePath = null)
    {
        return Timed(args, Timeout.InfiniteTimeSpan, () => true).ExitCode;
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
            "list" or "ls" => args.Contains("--snapshots") ? ListSnapshots(state) : List(state),
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

    private static MultipassRunResult List(SimState state)
    {
        // Multipass 1.16 hat keine feste Reihenfolge (meist aktive vor dem Papierkorb,
        // aber nicht immer alphabetisch); der Simulator bleibt deterministisch:
        // aktive VMs alphabetisch, danach der Papierkorb.
        var entries = state.Vms
            .OrderBy(vm => vm.State == "Deleted")
            .ThenBy(vm => vm.Name, StringComparer.Ordinal)
            .Select(vm => new Dictionary<string, object?>
            {
                ["ipv4"] = vm.State == "Running" ? new[] { vm.Ip } : Array.Empty<string>(),
                ["name"] = vm.Name,
                ["release"] = vm.Release,
                ["state"] = vm.State
            });
        return Ok(JsonSerializer.Serialize(new { list = entries }, JsonOptions) + "\n");
    }

    private static MultipassRunResult ListSnapshots(SimState state)
    {
        var info = state.Vms
            .Where(vm => vm.Snapshots.Count > 0)
            .OrderBy(vm => vm.Name, StringComparer.Ordinal)
            .ToDictionary(
                vm => vm.Name,
                vm => vm.Snapshots.ToDictionary(
                    s => s.Name,
                    s => new { comment = s.Comment, parent = s.Parent }));
        return Ok(JsonSerializer.Serialize(new { errors = Array.Empty<string>(), info }, JsonOptions) + "\n");
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
        }

        state.Vms.Add(new SimVm
        {
            Name = name,
            State = "Running",
            Release = ReleaseOf(image),
            Ip = NextIp(state),
            Disk = Value(args, "--disk", "-d") is { } disk && MultipassSize.TryParse(disk, out var bytes) ? FormatSize(bytes) : DefaultDisk,
            Components = components,
            Markers = components.Where(c => !ComponentsWithoutMarker.Contains(c)).ToList()
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

                continue;
            }

            if (purge)
            {
                state.Vms.Remove(vm);
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
        state.Vms.RemoveAll(vm => vm.State == "Deleted");
        return Ok(string.Empty);
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
            Components = [.. vm.Components],
            Markers = [.. vm.Markers],
            Files = new Dictionary<string, string>(vm.Files)
        });
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
            Files = new Dictionary<string, string>(vm.Files)
        });
        vm.CurrentSnapshot = snapshot;
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
        vm.CurrentSnapshot = saved.Name;
        return Ok($"Snapshot restored: {vm.Name}.{saved.Name}\n");
    }

    private static MultipassRunResult Shell(SimState state, IReadOnlyList<string> args)
    {
        var name = args.ElementAtOrDefault(1) ?? string.Empty;
        return RequireRunning(state, "shell", name, out _) ?? Ok(string.Empty);
    }

    private static MultipassRunResult Info(SimState state, IReadOnlyList<string> args)
    {
        foreach (var name in Positionals(args))
        {
            if (Find(state, SplitSnapshot(name).Name) is null)
            {
                return NotFound("info", name);
            }
        }

        return Ok(string.Empty);
    }

    /// <summary>Nur <c>local.&lt;vm&gt;.disk</c>; Fehlertext wie Multipass 1.16.3.</summary>
    private static MultipassRunResult Get(SimState state, IReadOnlyList<string> args)
    {
        var key = Regex.Match(args.ElementAtOrDefault(1) ?? string.Empty, @"^local\.(.+)\.disk$");
        if (!key.Success)
        {
            return Fail($"get failed: unknown setting '{args.ElementAtOrDefault(1)}' (simulation).");
        }

        var vm = Find(state, key.Groups[1].Value);
        return vm is null
            ? Fail($"get failed: Cannot obtain instance settings; instance: {key.Groups[1].Value}; reason: No such instance")
            : Ok(vm.Disk + "\n");
    }

    /// <summary>
    /// <c>set local.&lt;vm&gt;.disk=&lt;size&gt;</c> mit den Ablehnungen von Multipass 1.16.3
    /// (laufende VM, ungültige Größe, Verkleinern — Texte am 2026-09-26 aufgenommen).
    /// </summary>
    private static MultipassRunResult Set(SimState state, IReadOnlyList<string> args)
    {
        var setting = args.ElementAtOrDefault(1) ?? string.Empty;
        var key = Regex.Match(setting, @"^local\.(.+)\.disk=(.*)$");
        if (!key.Success)
        {
            return Fail($"set failed: unknown setting '{setting}' (simulation).");
        }

        var (name, size) = (key.Groups[1].Value, key.Groups[2].Value);
        var vm = Find(state, name);
        if (vm is null)
        {
            return Fail($"set failed: Cannot update instance settings; instance: {name}; reason: No such instance");
        }

        if (vm.State != "Stopped")
        {
            return Fail($"set failed: Cannot update instance settings; instance: {name}; reason: Instance must be stopped for modification");
        }

        if (!MultipassSize.TryParse(size, out var bytes))
        {
            return Fail($"set failed: Invalid setting '{setting}': {size} is not a valid memory size - need a non-negative integer (in base 10) or a decimal followed by K, M, or G (e.g. 1234B, 42MiB, 0.5G)");
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

        var separator = args.ToList().IndexOf("--");
        var command = separator < 0 ? [] : args.Skip(separator + 1).ToArray();
        var line = command.Length >= 3 && command[1] == "-c" ? command[2] : string.Join(' ', command);
        return Respond(vm!, line);
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
            var failure = RequireRunning(state, "transfer", vmName, out var vm);
            if (failure is not null)
            {
                return failure;
            }

            if (!vm!.Files.TryGetValue(remote, out var content))
            {
                return Fail($"transfer failed: remote \"{remote}\" does not exist");
            }

            var directory = Path.GetDirectoryName(Path.GetFullPath(destination));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(destination, content);
            return Ok(string.Empty);
        }

        if (IsRemote(destination))
        {
            var (vmName, remote) = SplitRemote(destination);
            var failure = RequireRunning(state, "transfer", vmName, out var vm);
            if (failure is not null)
            {
                return failure;
            }

            if (!File.Exists(source))
            {
                return Fail($"transfer failed: source \"{source}\" does not exist");
            }

            vm!.Files[remote] = File.ReadAllText(source);
            return Ok(string.Empty);
        }

        return Fail("transfer failed: one of source or destination must be an instance");
    }

    /// <summary>
    /// Beantwortet eine Shell-Zeile so, wie es ein provisionierter Gast täte;
    /// Segmente (<c>;</c>, <c>&amp;&amp;</c>) werden einzeln ausgewertet.
    /// </summary>
    private static MultipassRunResult Respond(SimVm vm, string line, bool loginShell = false)
    {
        var output = new StringBuilder();
        var exitCode = 0;
        foreach (var statement in line.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var segment in statement.Split("&&", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var (code, text) = RespondOne(vm, segment, loginShell);
                output.Append(text);
                exitCode = code;
                if (code != 0)
                {
                    break;
                }
            }
        }

        return new MultipassRunResult(exitCode, output.ToString(), exitCode == 0 ? string.Empty : "simulated command failed\n", 0);
    }

    private static (int ExitCode, string Output) RespondOne(SimVm vm, string command, bool loginShell)
    {
        const int NotFound = 127;

        // `sudo` und vorangestellte Variablen (DEBIAN_FRONTEND=…) ändern an der Antwort nichts.
        command = Regex.Replace(command, @"^(sudo\s+)?([A-Z_]+=\S+\s+)*", string.Empty);

        // `bash -lc '…'` (Login-Shell): den inneren Befehl beantworten, statt ihn als
        // unbekannt durchzuwinken — sonst wäre jede so verpackte Sonde grün.
        var login = Regex.Match(command, @"^(?:ba)?sh\s+-l?c\s+'(.*)'$");
        if (login.Success)
        {
            var inner = Respond(vm, login.Groups[1].Value, loginShell: true);
            return (inner.ExitCode, inner.StdOut);
        }

        // Ein installiertes Komponenten-Skript läuft durch (vm-install): Komponente
        // gilt als installiert; den Marker setzt es nur, wenn das echte Skript das tut.
        var install = Regex.Match(command, @"^'?/usr/local/sbin/corebridge-install-([A-Za-z0-9_-]+)'?(\s|$)");
        if (install.Success)
        {
            var component = install.Groups[1].Value;
            if (!vm.Components.Contains(component))
            {
                vm.Components.Add(component);
            }

            if (!ComponentsWithoutMarker.Contains(component) && !vm.Markers.Contains(component))
            {
                vm.Markers.Add(component);
            }

            return (0, $"[simulation] {component} installiert\n");
        }
        if (command.StartsWith("echo ", StringComparison.Ordinal))
        {
            return (0, command[5..].Trim().Trim('"', '\'') + "\n");
        }

        if (command is "true" or ":" or "")
        {
            return (0, string.Empty);
        }

        if (command == "false")
        {
            return (1, string.Empty);
        }

        if (command == "hostname")
        {
            return (0, vm.Name + "\n");
        }

        if (command is "whoami" or "id -un")
        {
            return (0, "ubuntu\n");
        }

        if (command.StartsWith("uname", StringComparison.Ordinal))
        {
            return (0, $"Linux {vm.Name} 6.17.0-41-generic #41-Ubuntu SMP x86_64 GNU/Linux\n");
        }

        var version = vm.Release.Replace("Ubuntu ", string.Empty, StringComparison.Ordinal);
        if (command.Contains("/etc/os-release", StringComparison.Ordinal))
        {
            var lines = new[]
            {
                $"PRETTY_NAME=\"{vm.Release}\"",
                "NAME=\"Ubuntu\"",
                $"VERSION_ID=\"{version}\"",
                "ID=ubuntu"
            };
            var grep = Regex.Match(command, @"^grep\s+(\S+)");
            var selected = grep.Success ? lines.Where(l => l.Contains(grep.Groups[1].Value, StringComparison.Ordinal)).ToArray() : lines;
            return (selected.Length == 0 ? 1 : 0, string.Concat(selected.Select(l => l + "\n")));
        }

        // Komponenten-Sonden: <component> --version und die .ready-Marker.
        var versions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["claude"] = "2.0.14 (Claude Code)",
            ["dotnet"] = "10.0.100",
            ["python3.14"] = "Python 3.14.0",
            ["python3"] = "Python 3.14.0",
            ["cline"] = "3.4.0"
        };
        // Auch mit Pfad (/snap/bin/dotnet, /usr/local/bin/python3.14): zählt der Programmname.
        var probe = Regex.Match(command, @"^(?:command -v\s+)?(?:\S*/)?([A-Za-z0-9_.-]+)(?:\s+--version)?$");
        if (probe.Success && versions.TryGetValue(probe.Groups[1].Value, out var reported))
        {
            var component = probe.Groups[1].Value.StartsWith("python", StringComparison.Ordinal) ? "python" : probe.Groups[1].Value;
            // Ein Pfad zählt nur, wo das echte Skript hinlinkt (/snap/bin/dotnet gibt es nicht);
            // ohne Pfad findet eine Nicht-Login-Shell manche Komponenten nicht.
            var path = command.StartsWith("command -v", StringComparison.Ordinal) ? string.Empty : command.Split(' ')[0];
            var reachable = path.Contains('/')
                ? !path.StartsWith("/snap/bin/", StringComparison.Ordinal)
                : loginShell || !ComponentsOnlyInLoginShell.Contains(component);
            return vm.Components.Contains(component) && reachable
                ? (0, command.StartsWith("command -v", StringComparison.Ordinal) ? $"/usr/local/bin/{probe.Groups[1].Value}\n" : reported + "\n")
                : (NotFound, string.Empty);
        }

        if (Regex.IsMatch(command, @"^ls(\s+-1)?\s+/var/lib/corebridge/?$"))
        {
            return (0, string.Concat(vm.Markers.Order(StringComparer.Ordinal).Select(c => $"{c}.ready\n")));
        }

        var marker = Regex.Match(command, @"/var/lib/corebridge/([A-Za-z0-9_-]+)\.ready");
        if (marker.Success)
        {
            return vm.Markers.Contains(marker.Groups[1].Value) ? (0, string.Empty) : (1, string.Empty);
        }

        var cat = Regex.Match(command, @"^cat\s+(\S+)$");
        if (cat.Success)
        {
            return vm.Files.TryGetValue(cat.Groups[1].Value, out var content)
                ? (0, content)
                : (1, string.Empty);
        }

        var test = Regex.Match(command, @"^test\s+-[ef]\s+(\S+)$");
        if (test.Success)
        {
            return vm.Files.ContainsKey(test.Groups[1].Value) ? (0, string.Empty) : (1, string.Empty);
        }

        // Unbekanntes Kommando: als erfolgreich ohne Ausgabe behandeln, damit
        // Durchspielen nicht an beliebigen Befehlen scheitert.
        return (0, string.Empty);
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
        public List<SimVm> Vms { get; set; } = [];

        public int IpCount { get; set; }
    }

    private sealed class SimVm
    {
        public string Name { get; set; } = string.Empty;

        public string State { get; set; } = "Stopped";

        public string Release { get; set; } = "Ubuntu 26.10";

        public string Ip { get; set; } = string.Empty;

        /// <summary>Wie <c>multipass get local.&lt;vm&gt;.disk</c> es ausgibt.</summary>
        public string Disk { get; set; } = DefaultDisk;

        public List<string> Components { get; set; } = [];

        /// <summary>Komponenten, deren <c>/var/lib/corebridge/&lt;name&gt;.ready</c> existiert.</summary>
        public List<string> Markers { get; set; } = [];

        /// <summary>Per <c>transfer</c> in die VM kopierte Dateien (Pfad → Inhalt).</summary>
        public Dictionary<string, string> Files { get; set; } = new(StringComparer.Ordinal);

        public List<SimSnapshot> Snapshots { get; set; } = [];

        public int SnapshotCount { get; set; }

        public int CloneCount { get; set; }

        public string? CurrentSnapshot { get; set; }
    }

    private sealed class SimSnapshot
    {
        public string Name { get; set; } = string.Empty;

        public string Parent { get; set; } = string.Empty;

        public string Comment { get; set; } = string.Empty;

        public List<string> Components { get; set; } = [];

        public List<string> Markers { get; set; } = [];

        public Dictionary<string, string> Files { get; set; } = new(StringComparer.Ordinal);
    }
}
