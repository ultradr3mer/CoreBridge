using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CoreBridge.Core.Common;
using CoreBridge.Core.Vm;

namespace CoreBridge.Core.Multipass;

/// <summary>
/// Ein Aufruf des <see cref="IMultipassCli"/>, so wie er im JSON-Log landet.
/// Eine Zeile pro Aufruf (JSON Lines), damit sich Eingabe und Ausgabe
/// maschinell auswerten lassen.
/// </summary>
public sealed record MultipassCallLogEntry(
    [property: JsonPropertyName("seq")] int Seq,
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("verb")] string Verb,
    [property: JsonPropertyName("args")] string[] Args,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("timeoutMs")] int? TimeoutMs,
    [property: JsonPropertyName("exitCode")] int ExitCode,
    [property: JsonPropertyName("stdout")] string StdOut,
    [property: JsonPropertyName("stderr")] string StdErr,
    [property: JsonPropertyName("durationMs")] long DurationMs,
    [property: JsonPropertyName("at")] DateTimeOffset At);

/// <summary>Kopfzeile, die jeden Prozessstart im Log markiert.</summary>
public sealed record MultipassCallLogSession(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("cwd")] string Cwd,
    [property: JsonPropertyName("at")] DateTimeOffset At);

/// <summary>
/// Multipass-Double für Entwicklung ohne installiertes Multipass: hält den
/// VM-Zustand im Speicher, beantwortet <c>list</c>/<c>launch</c>/<c>start</c>/
/// <c>stop</c>/<c>delete</c>/<c>exec</c>/<c>shell</c>/<c>transfer</c>/
/// <c>version</c> deterministisch und schreibt jeden Aufruf als JSON Line mit.
/// Aktiviert wird er über die Env-Variable <see cref="EnableEnvironmentVariable"/>.
/// </summary>
/// <remarks>
/// Er ersetzt <em>das Multipass-Binary</em>, nicht die Cloud-init-Ausführung:
/// eine VM, die der Mock anlegt, installiert im Gast nichts. Für Aussagen über
/// wirklich installierte Pakete bleibt der Integrationstest mit echtem Multipass
/// zuständig.
/// </remarks>
public sealed class MockMultipassCli : IMultipassCli
{
    /// <summary>Aktiviert den Mock, wenn auf <c>1</c>/<c>true</c>/<c>on</c> gesetzt.</summary>
    public const string EnableEnvironmentVariable = "CORE_BRIDGE_MULTIPASS_MOCK";

    /// <summary>Ziel des JSON-Logs; Standard <c>&lt;StoreRoot&gt;/multipass-mock/calls.jsonl</c>.</summary>
    public const string LogPathEnvironmentVariable = "CORE_BRIDGE_MULTIPASS_MOCK_LOG";

    /// <summary>Unterverzeichnis des Stores, in dem Log und Zustand liegen.</summary>
    public const string MockFolderName = "multipass-mock";

    public const string LogFileName = "calls.jsonl";

    public const string StateFileName = "state.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly object gate = new();
    private readonly Dictionary<string, VmInfo> vms = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>In der VM liegende Dateien aus <c>vm-put</c>, für <c>vm-get</c>.</summary>
    private readonly Dictionary<(string Vm, string Remote), string> files = new();

    private readonly List<MultipassCallLogEntry> entries = new();

    public MockMultipassCli(string? logPath = null)
    {
        this.LogPath = logPath is null ? null : RepoPaths.Resolve(logPath);
        this.StatePath = this.LogPath is null
            ? null
            : Path.Combine(
                Path.GetDirectoryName(this.LogPath)!,
                StateFileName);

        this.Load();
        this.Append(new MultipassCallLogSession(
            "session",
            Environment.ProcessId,
            Environment.CurrentDirectory,
            DateTimeOffset.Now));
    }

    /// <summary>Wohin geschrieben wird; <c>null</c> = nur im Speicher.</summary>
    public string? LogPath { get; }

    /// <summary>
    /// Datei mit dem VM-Zustand. Ohne sie wäre der Mock pro Prozess frisch —
    /// <c>cb vm-create</c> und ein folgendes <c>cb vm-list</c> wären verschiedene
    /// Prozesse und das zweite sähe die VM nicht.
    /// </summary>
    public string? StatePath { get; }

    /// <summary>Alle Aufrufe in Reihenfolge (identisch zum Log, auch ohne Datei).</summary>
    public IReadOnlyList<MultipassCallLogEntry> Entries
    {
        get { lock (this.gate) { return this.entries.ToArray(); } }
    }

    public static bool IsEnabled(string? value = null)
    {
        value ??= Environment.GetEnvironmentVariable(EnableEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim().ToLowerInvariant();
        return normalized is "1" or "true" or "on" or "yes";
    }

    /// <summary>
    /// Logpfad aus <see cref="LogPathEnvironmentVariable"/> oder als Default
    /// <c>&lt;storeRoot&gt;/multipass-mock/calls.jsonl</c>.
    /// </summary>
    /// <remarks>
    /// Der Default läuft über <see cref="RepoPaths.Resolve"/> und damit über
    /// dieselbe Repo-Wurzel wie <c>InitStore</c>. Ein nacktes
    /// <c>Path.Combine(storeRoot, …)</c> wäre relativ zum aktuellen
    /// Arbeitsverzeichnis — Log und Zustand lägen dann je nach Aufruf an
    /// verschiedenen Orten und die VM verschwände zwischen zwei <c>cb</c>-Aufrufen.
    /// </remarks>
    public static string ResolveLogPath(string? storeRoot = null)
    {
        var configured = Environment.GetEnvironmentVariable(LogPathEnvironmentVariable);
        return RepoPaths.Resolve(
            string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(RepoPaths.Resolve(storeRoot), MockFolderName, LogFileName)
                : configured);
    }

    public MultipassRunResult Run(IReadOnlyList<string> args, int timeoutMs = 30000, string? exePath = null)
    {
        return this.Invoke(args, "Run", timeoutMs, () => this.Dispatch(args));
    }

    public MultipassRunResult RunWithExtensions(
        IReadOnlyList<string> args,
        TimeSpan slice,
        Func<bool> keepWaiting,
        string? exePath = null)
    {
        // Bewusst OHNE keepWaiting(): es gibt keinen Prozess, auf den gewartet
        // würde, also gibt es auch nichts zu fragen. keepWaiting aufzurufen würde
        // die interaktive Rückfrage "keep waiting?" auslösen und den Mock genau
        // dort blockieren, wo er gerade beschleunigen soll.
        return this.Invoke(args, "RunWithExtensions", (int)slice.TotalMilliseconds, () => this.Dispatch(args));
    }

    public int Attach(IReadOnlyList<string> args, string? exePath = null)
    {
        return this.Invoke(args, "Attach", null, () => this.Dispatch(args)).ExitCode;
    }

    private MultipassRunResult Invoke(
        IReadOnlyList<string> args,
        string mode,
        int? timeoutMs,
        Func<MultipassRunResult> dispatch)
    {
        var call = args.ToArray();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        MultipassRunResult result;
        try
        {
            result = dispatch();
        }
        catch (Exception exception)
        {
            result = new MultipassRunResult(1, string.Empty, exception.Message, stopwatch.ElapsedMilliseconds);
        }

        this.Record(new MultipassCallLogEntry(
            this.entries.Count + 1,
            Environment.ProcessId,
            call.Length > 0 ? call[0] : string.Empty,
            call,
            mode,
            timeoutMs,
            result.ExitCode,
            result.StdOut,
            result.StdErr,
            stopwatch.ElapsedMilliseconds,
            DateTimeOffset.Now));

        return result;
    }

    private void Record(MultipassCallLogEntry entry)
    {
        lock (this.gate)
        {
            this.entries.Add(entry);
            this.Append(entry);
        }
    }

    /// <summary>Hängt eine Zeile an; I/O-Fehler dürfen den Mock nie scheitern lassen.</summary>
    private void Append(object line)
    {
        if (this.LogPath is null)
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(this.LogPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.AppendAllText(
                this.LogPath,
                JsonSerializer.Serialize(line, JsonOptions) + Environment.NewLine,
                Encoding.UTF8);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private MultipassRunResult Dispatch(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return Fail("multipass: kein Verb angegeben.");
        }

        return args[0] switch
        {
            "version" => Ok("""{"multipass":"1.16.0","multipassd":"1.16.0"}"""),
            "list" or "ls" => this.List(),
            "launch" => this.Launch(args),
            "start" => this.Transition(args, "Running", "start"),
            "stop" => this.Transition(args, "Stopped", "stop"),
            "delete" or "rm" => this.Delete(args),
            "purge" => this.Purge(args),
            "recover" or "mount" or "umount" or "info" => Ok(string.Empty),
            "exec" => this.Exec(args),
            "shell" or "sh" => this.Shell(args),
            "transfer" => this.Transfer(args),
            _ => Fail($"multipass: unbekanntes Verb '{args[0]}' (Mock).")
        };
    }

    private MultipassRunResult List()
    {
        string Render()
        {
            var entries = this.vms.Values
                .OrderBy(vm => vm.Name, StringComparer.OrdinalIgnoreCase)
                .Select(vm => $$"""{"name":{{JsonSerializer.Serialize(vm.Name)}},"state":{{JsonSerializer.Serialize(vm.State)}},"ipv4":[{{string.Join(",", vm.Ipv4.Select(a => JsonSerializer.Serialize(a)))}}],"release":"26.10","image_hash":"mock"}""");
            return $$"""{"list":[{{string.Join(",", entries)}}]}""";
        }

        return Ok(Render());
    }

    private MultipassRunResult Launch(IReadOnlyList<string> args)
    {
        var name = Value(args, "--name") ?? args.ElementAtOrDefault(1);
        if (string.IsNullOrWhiteSpace(name))
        {
            return Fail("multipass launch: kein Name angegeben (--name).");
        }

        lock (this.gate)
        {
            this.vms[name] = new VmInfo(name, "Running", [$"172.30.99.{this.vms.Count + 1}"]);
        }

        this.Save();
        return Ok($"Launched: {name}\n");
    }

    private MultipassRunResult Transition(IReadOnlyList<string> args, string state, string verb)
    {
        var name = args.LastOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        lock (this.gate)
        {
            if (name is null || !this.vms.TryGetValue(name, out var vm))
            {
                return Fail($"multipass {verb}: VM '{name}' nicht gefunden.");
            }

            this.vms[name] = vm with { State = state };
        }

        this.Save();
        return Ok(string.Empty);
    }

    private MultipassRunResult Delete(IReadOnlyList<string> args)
    {
        var name = args.LastOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        lock (this.gate)
        {
            if (name is not null && this.vms.TryGetValue(name, out var vm))
            {
                this.vms[name] = vm with { State = "Deleted" };
            }
        }

        this.Save();
        return Ok(string.Empty);
    }

    private MultipassRunResult Purge(IReadOnlyList<string> args)
    {
        var name = args.LastOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        lock (this.gate)
        {
            if (name is not null)
            {
                this.vms.Remove(name);
            }
        }

        this.Save();
        return Ok(string.Empty);
    }

    private MultipassRunResult Shell(IReadOnlyList<string> args)
    {
        var name = args.ElementAtOrDefault(1);
        lock (this.gate)
        {
            if (name is null || !this.vms.TryGetValue(name, out var vm))
            {
                return Fail($"multipass shell: VM '{name}' nicht gefunden.");
            }

            if (!vm.IsRunning)
            {
                return Fail($"multipass shell: VM '{name}' ist {vm.State}.");
            }
        }

        return Ok(string.Empty);
    }

    /// <summary>
    /// Beantwortet die üblichen Sonden so, wie es ein provisionierter Gast täte:
    /// <c>claude --version</c>, <c>dotnet --version</c>, <c>/etc/os-release</c> und
    /// die <c>/var/lib/corebridge/*.ready</c>-Marker der Komponenten-Skripte.
    /// </summary>
    private MultipassRunResult Exec(IReadOnlyList<string> args)
    {
        var name = args.ElementAtOrDefault(1);
        var command = args.ElementAtOrDefault(5) ?? string.Empty;

        lock (this.gate)
        {
            if (name is not null && !this.vms.ContainsKey(name))
            {
                return Fail($"multipass exec: VM '{name}' nicht gefunden.");
            }
        }

        return Respond(command);
    }

    private static MultipassRunResult Respond(string command)
    {
        // Ein echtes `multipass exec` führt die ganze Shell-Zeile aus, also
        // werden die Segmente einzeln ausgewertet und ihre Ausgaben
        // aneinandergehängt — sonst lieferte `a; b; c` nur die Antwort zu `a`.
        var segments = command.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length <= 1)
        {
            return RespondOne(command);
        }

        var output = new StringBuilder();
        var failed = false;
        foreach (var segment in segments)
        {
            var result = RespondOne(segment);
            output.Append(result.StdOut);
            if (result.ExitCode != 0)
            {
                failed = true;
            }
        }

        return new MultipassRunResult(failed ? 1 : 0, output.ToString(), string.Empty, 0);
    }

    private static MultipassRunResult RespondOne(string command)
    {
        var trimmed = command.Trim();
        if (trimmed.StartsWith("echo ", StringComparison.Ordinal))
        {
            return Ok(trimmed[5..] + "\n");
        }

        if (trimmed.Contains("claude --version", StringComparison.Ordinal))
        {
            return Ok("2.0.14 (Claude Code)\n");
        }

        if (trimmed.Contains("dotnet --version", StringComparison.Ordinal))
        {
            return Ok("10.0.100\n");
        }

        if (trimmed.Contains("python3.14 --version", StringComparison.Ordinal)
            || trimmed.Contains("python3 --version", StringComparison.Ordinal))
        {
            return Ok("Python 3.14.0\n");
        }

        if (trimmed.Contains("cline --version", StringComparison.Ordinal))
        {
            return Ok("3.4.0\n");
        }

        if (trimmed.Contains("VERSION_ID", StringComparison.Ordinal)
            || trimmed.Contains("/etc/os-release", StringComparison.Ordinal))
        {
            return Ok("""
                NAME="Ubuntu"
                VERSION="26.10 (Resolute Raccoon)"
                ID=ubuntu
                ID_LIKE=debian
                VERSION_ID="26.10"
                """);
        }

        if (trimmed.Contains("uname -a", StringComparison.Ordinal))
        {
            return Ok("Linux cbx 6.17.0-41-generic #41-Ubuntu SMP x86_64 GNU/Linux\n");
        }

        if (trimmed.Contains("whoami", StringComparison.Ordinal))
        {
            return Ok("ubuntu\n");
        }

        if (trimmed.Contains("id -un", StringComparison.Ordinal))
        {
            return Ok("ubuntu\n");
        }

        // Marker der Komponenten-Skripte: /var/lib/corebridge/<component>.ready
        var marker = trimmed.Split('/').FirstOrDefault(part => part.Contains(".ready", StringComparison.Ordinal));
        if (marker is not null)
        {
            var component = marker[..marker.IndexOf(".ready", StringComparison.Ordinal)].Trim();
            if (component is not ("claude" or "dotnet" or "python" or "cline"))
            {
                return new MultipassRunResult(1, string.Empty, "No such file or directory", 0);
            }

            // Bei `test -f … && echo …` die Ausgabe des Echos mitliefern.
            const string echo = "&& echo";
            var echoed = trimmed.Contains(echo, StringComparison.Ordinal)
                ? trimmed[(trimmed.IndexOf(echo, StringComparison.Ordinal) + echo.Length)..].Trim() + "\n"
                : string.Empty;
            return Ok(echoed);
        }

        return Ok(string.Empty);
    }

    private MultipassRunResult Transfer(IReadOnlyList<string> args)
    {
        var operands = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).Skip(1).ToArray();
        if (operands.Length < 2)
        {
            return Fail("multipass transfer: Quelle und Ziel fehlen.");
        }

        var (source, destination) = (operands[0], operands[1]);
        var separator = destination.IndexOf(':');
        if (separator < 0)
        {
            return Ok(string.Empty);
        }

        var vmName = destination[..separator];
        var remote = destination[(separator + 1)..];
        lock (this.gate)
        {
            if (!this.vms.ContainsKey(vmName))
            {
                return Fail($"multipass transfer: VM '{vmName}' nicht gefunden.");
            }

            if (source.Contains(':'))
            {
                var key = (source.Split(':')[0], remote);
                if (!this.files.TryGetValue(key, out var content))
                {
                    return Fail($"multipass transfer: {remote} existiert in {vmName} nicht.");
                }

                this.files.Remove(key);

                try
                {
                    var directory = Path.GetDirectoryName(Path.GetFullPath(source));
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    File.WriteAllText(source, content);
                }
                catch (IOException exception)
                {
                    return Fail($"multipass transfer: {exception.Message}");
                }
            }
            else
            {
                if (!File.Exists(source))
                {
                    return Fail($"multipass transfer: {source} existiert nicht.");
                }

                this.files[(vmName, remote)] = File.ReadAllText(source);
            }
        }

        return Ok(string.Empty);
    }

    private static string? Value(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    /// <summary>Übernimmt den VM-Zustand aus einem früheren Prozess, falls vorhanden.</summary>
    private void Load()
    {
        if (this.StatePath is null || !File.Exists(this.StatePath))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(this.StatePath));
            if (!document.RootElement.TryGetProperty("vms", out var vmsElement)
                || vmsElement.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var entry in vmsElement.EnumerateArray())
            {
                if (!entry.TryGetProperty("name", out var nameElement)
                    || nameElement.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(nameElement.GetString()))
                {
                    continue;
                }

                var name = nameElement.GetString()!;
                var state = entry.TryGetProperty("state", out var s) ? s.GetString() : null;
                var ipv4 = entry.TryGetProperty("ipv4", out var i) && i.ValueKind == JsonValueKind.Array
                    ? i.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.String).Select(a => a.GetString()!).ToArray()
                    : [];
                this.vms[name] = new VmInfo(name, state, ipv4);
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            // Ein kaputter Zustand ist kein Grund, den Start zu verweigern.
        }
    }

    /// <summary>Schreibt den VM-Zustand zurück, damit er den Prozess überlebt.</summary>
    private void Save()
    {
        if (this.StatePath is null)
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(this.StatePath));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var payload = new
            {
                vms = this.vms.Values.Select(vm => new { vm.Name, vm.State, vm.Ipv4 })
            };
            File.WriteAllText(this.StatePath, JsonSerializer.Serialize(payload, JsonOptions));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static MultipassRunResult Ok(string stdOut) => new(0, stdOut, string.Empty, 0);

    private static MultipassRunResult Fail(string stdErr) => new(1, string.Empty, stdErr + "\n", 0);
}
