using CoreBridge.Core.Multipass;

namespace CoreBridge.Core.Vm;

public sealed class VmService
{
    private readonly IMultipassCli _cli;

    public VmService(IMultipassCli? cli = null)
    {
        _cli = cli ?? new MultipassCli();
    }

    /// <summary>Der Client, über den alle Verben laufen.</summary>
    public IMultipassCli Cli => _cli;

    public IReadOnlyList<VmInfo> List(int timeoutMs = 60_000)
    {
        var result = _cli.Run(new[] { "list", "--format", "json" }, timeoutMs);
        if (result.ExitCode != 0)
        {
            var message = MultipassStdErr.Clean(result.StdErr);
            throw new MultipassException(string.IsNullOrEmpty(message)
                ? $"multipass list beendet mit Exit {result.ExitCode}."
                : $"{message} (multipass Exit {result.ExitCode})");
        }

        return MultipassVmList.Parse(result.StdOut);
    }

    /// <summary>
    /// Obergrenze, die multipass selbst per <c>launch --timeout</c> bekommt (inkl. cloud-init).
    /// </summary>
    public const int LaunchTimeoutSeconds = 2_400;

    /// <summary>
    /// Legt die VM per <c>multipass launch</c> an. Gewartet wird in Abschnitten von
    /// <paramref name="waitSlice"/> (Standard: <see cref="LaunchTimeoutSeconds"/>);
    /// nach jedem Abschnitt entscheidet <paramref name="keepWaiting"/> (Standard: nein),
    /// ob weiter gewartet wird. Sonst wirft der Aufruf <see cref="MultipassTimeoutException"/>;
    /// der Multipass-Daemon legt die VM dann im Hintergrund weiter an.
    /// </summary>
    public MultipassRunResult Create(
        string name,
        string image,
        string? disk = null,
        string? cloudInitPath = null,
        TimeSpan? waitSlice = null,
        Func<bool>? keepWaiting = null)
    {
        // multipass hat ein eigenes Launch-Timeout (Default 300 s, inkl. cloud-init) —
        // ohne --timeout bricht es lange vor unserem Prozess-Timeout ab. Die feste
        // Obergrenze sorgt dafür, dass der Daemon nicht vor dem Benutzer aufgibt.
        var args = new List<string> { "launch", image, "--name", name, "--timeout", LaunchTimeoutSeconds.ToString() };
        if (disk is not null)
        {
            args.Add("--disk");
            args.Add(disk);
        }

        if (cloudInitPath is not null)
        {
            args.Add("--cloud-init");
            args.Add(cloudInitPath);
        }

        return _cli.RunWithExtensions(
            args,
            waitSlice ?? TimeSpan.FromSeconds(LaunchTimeoutSeconds),
            keepWaiting ?? (() => false));
    }

    /// <param name="keepWaiting">
    /// Optional: nach je <paramref name="timeoutMs"/> gefragt, ob weiter gewartet
    /// wird (siehe <see cref="MultipassCli.RunWithExtensions"/>); ohne Angabe ist
    /// <paramref name="timeoutMs"/> ein festes Limit.
    /// </param>
    public MultipassRunResult Start(string name, int timeoutMs = 180_000, Func<bool>? keepWaiting = null)
    {
        return Run(new[] { "start", name }, timeoutMs, keepWaiting);
    }

    /// <inheritdoc cref="Start"/>
    /// <param name="force">
    /// <c>stop --force</c>: schaltet hart aus. Nötig für VMs in Übergangszuständen
    /// wie <c>Starting</c>, die Multipass sonst nicht stoppt.
    /// </param>
    public MultipassRunResult Stop(string name, int timeoutMs = 180_000, Func<bool>? keepWaiting = null, bool force = false)
    {
        return Run(force ? new[] { "stop", "--force", name } : new[] { "stop", name }, timeoutMs, keepWaiting);
    }

    /// <inheritdoc cref="Start"/>
    public MultipassRunResult Delete(string name, bool purge = false, int timeoutMs = 180_000, Func<bool>? keepWaiting = null)
    {
        var args = new List<string> { "delete", name };
        if (purge)
        {
            args.Add("--purge");
        }

        return Run(args, timeoutMs, keepWaiting);
    }

    /// <summary>
    /// <c>multipass clone</c>: unabhängige Kopie einer <em>gestoppten</em> VM —
    /// samt allem, was cloud-init darin schon installiert hat. Ohne
    /// <paramref name="destination"/> vergibt Multipass <c>&lt;source&gt;-cloneN</c>.
    /// </summary>
    /// <inheritdoc cref="Start"/>
    public MultipassRunResult Clone(
        string source,
        string? destination = null,
        int timeoutMs = 600_000,
        Func<bool>? keepWaiting = null)
    {
        var args = new List<string> { "clone", source };
        if (destination is not null)
        {
            args.Add("--name");
            args.Add(destination);
        }

        return Run(args, timeoutMs, keepWaiting);
    }

    /// <summary>
    /// <c>multipass snapshot</c> einer <em>gestoppten</em> VM. Ohne
    /// <paramref name="snapshot"/> vergibt Multipass <c>snapshotN</c>.
    /// </summary>
    /// <inheritdoc cref="Start"/>
    public MultipassRunResult Snapshot(
        string name,
        string? snapshot = null,
        string? comment = null,
        int timeoutMs = 600_000,
        Func<bool>? keepWaiting = null)
    {
        var args = new List<string> { "snapshot", name };
        if (snapshot is not null)
        {
            args.Add("--name");
            args.Add(snapshot);
        }

        if (comment is not null)
        {
            args.Add("--comment");
            args.Add(comment);
        }

        return Run(args, timeoutMs, keepWaiting);
    }

    /// <summary>
    /// <c>multipass restore &lt;vm&gt;.&lt;snapshot&gt; --destructive</c> auf einer
    /// <em>gestoppten</em> VM. Immer <c>--destructive</c>: ohne fragt Multipass
    /// interaktiv, ob der aktuelle Stand gesichert werden soll — mit umgeleiteter
    /// Ausgabe sähe niemand die Frage und der Aufruf hinge. Das Sichern übernimmt
    /// bei Bedarf der Aufrufer vorher per <see cref="Snapshot"/>.
    /// </summary>
    /// <inheritdoc cref="Start"/>
    public MultipassRunResult Restore(
        string name,
        string snapshot,
        int timeoutMs = 600_000,
        Func<bool>? keepWaiting = null)
    {
        return Run(new[] { "restore", $"{name}.{snapshot}", "--destructive" }, timeoutMs, keepWaiting);
    }

    /// <summary>Disk-Größe laut <c>multipass get local.&lt;vm&gt;.disk</c>, z. B. <c>5.0GiB</c>.</summary>
    public string GetDisk(string name, int timeoutMs = 60_000)
    {
        var result = _cli.Run(new[] { "get", $"local.{name}.disk" }, timeoutMs);
        if (result.ExitCode != 0)
        {
            var message = MultipassStdErr.Clean(result.StdErr);
            throw new MultipassException(string.IsNullOrEmpty(message)
                ? $"multipass get local.{name}.disk beendet mit Exit {result.ExitCode}."
                : $"{message} (multipass Exit {result.ExitCode})");
        }

        return MultipassOutput.CleanLines(result.StdOut).FirstOrDefault()?.Trim() ?? string.Empty;
    }

    /// <summary>
    /// <c>multipass set local.&lt;vm&gt;.disk=&lt;size&gt;</c> auf einer <em>gestoppten</em>
    /// VM. Multipass vergrößert nur („Disk can only be expanded“); die Partition im
    /// Gast wächst beim nächsten Boot per cloud-init <c>growpart</c> mit.
    /// </summary>
    /// <inheritdoc cref="Start"/>
    public MultipassRunResult SetDisk(string name, string size, int timeoutMs = 180_000, Func<bool>? keepWaiting = null)
    {
        return Run(new[] { "set", $"local.{name}.disk={size}" }, timeoutMs, keepWaiting);
    }

    /// <summary>Snapshots laut <c>multipass list --snapshots</c>, optional nur die einer VM.</summary>
    public IReadOnlyList<VmSnapshotInfo> ListSnapshots(string? name = null, int timeoutMs = 60_000)
    {
        var result = _cli.Run(new[] { "list", "--snapshots", "--format", "json" }, timeoutMs);
        if (result.ExitCode != 0)
        {
            var message = MultipassStdErr.Clean(result.StdErr);
            throw new MultipassException(string.IsNullOrEmpty(message)
                ? $"multipass list --snapshots beendet mit Exit {result.ExitCode}."
                : $"{message} (multipass Exit {result.ExitCode})");
        }

        var snapshots = MultipassSnapshotList.Parse(result.StdOut);
        return name is null
            ? snapshots
            : snapshots.Where(s => string.Equals(s.Instance, name, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    /// <summary>VM laut <c>multipass list</c>; <c>null</c>, wenn es sie nicht gibt (auch nicht im Papierkorb).</summary>
    public VmInfo? Find(string name, int timeoutMs = 60_000)
    {
        return List(timeoutMs)
            .FirstOrDefault(vm => string.Equals(vm.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private MultipassRunResult Run(IReadOnlyList<string> args, int timeoutMs, Func<bool>? keepWaiting)
    {
        return keepWaiting is null
            ? _cli.Run(args, timeoutMs)
            : _cli.RunWithExtensions(args, TimeSpan.FromMilliseconds(timeoutMs), keepWaiting);
    }

    /// <param name="keepWaiting">
    /// Optional: nach je <paramref name="timeoutMs"/> gefragt, ob weiter gewartet wird —
    /// für lange Installationen (apt, snap); ohne Angabe ein festes Limit.
    /// </param>
    public MultipassRunResult Exec(string name, string command, int timeoutMs = 600_000, Func<bool>? keepWaiting = null)
    {
        return Run(new[] { "exec", name, "--", "/bin/sh", "-c", command }, timeoutMs, keepWaiting);
    }

    public MultipassRunResult Put(string localPath, string name, string? remotePath = null, int timeoutMs = 600_000)
    {
        var target = remotePath is null ? Path.GetFileName(localPath) : remotePath;
        return _cli.Run(new[] { "transfer", localPath, $"{name}:{target}" }, timeoutMs);
    }

    public MultipassRunResult Get(string name, string remotePath, string localPath, int timeoutMs = 600_000)
    {
        return _cli.Run(new[] { "transfer", $"{name}:{remotePath}", localPath }, timeoutMs);
    }

    public int Connect(string name)
    {
        return _cli.Attach(new[] { "shell", name });
    }

    /// <summary>
    /// vm-connect in programmierbarer Form: Befehle senden, Ausgaben sammeln
    /// und prüfen. Läuft über den exec-Kanal (pro Befehl ein exec, das
    /// Arbeitsverzeichnis wandert mit) — gepipetes stdin forwardet der
    /// Multipass-Windows-Client nicht, `multipass shell` ist ohne TTY
    /// unbrauchbar. Optionaler log-Callback (z. B. ITestOutputHelper.WriteLine
    /// im Test) schreibt die Ausgabe live mit.
    /// </summary>
    public VmExecSession OpenConsole(string name, Action<string>? log = null, int timeoutMs = 600_000)
    {
        return new VmExecSession(name, log, timeoutMs, _cli);
    }
}
