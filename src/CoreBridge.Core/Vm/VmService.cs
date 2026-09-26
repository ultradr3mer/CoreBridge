using CoreBridge.Core.Multipass;

namespace CoreBridge.Core.Vm;

public sealed class VmService
{
    private readonly IMultipassCli _cli;

    public VmService(IMultipassCli? cli = null)
    {
        _cli = cli ?? new MultipassCli();
    }

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

    public MultipassRunResult Exec(string name, string command, int timeoutMs = 600_000)
    {
        return _cli.Run(new[] { "exec", name, "--", "/bin/sh", "-c", command }, timeoutMs);
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
    /// Testbare Konsolen-Session auf der VM: Befehle senden, Ausgaben sammeln
    /// und prüfen. Läuft über den exec-Kanal (pro Befehl ein exec) — gepipetes
    /// stdin forwardet der Multipass-Windows-Client nicht, `multipass shell`
    /// ist ohne TTY unbrauchbar. Optionaler log-Callback (z. B.
    /// ITestOutputHelper.WriteLine im Test) schreibt die Ausgabe live mit.
    /// </summary>
    public VmExecSession OpenConsole(string name, Action<string>? log = null)
    {
        return new VmExecSession(name, log);
    }

    /// <summary>
    /// Echte interaktive Shell über natives SSH (SSH.NET, PTY): gemeinsamer
    /// Shell-Status, Prompt-Ausgabe, vm-connect in testbarer Form. Benötigt
    /// SSH-Key auf der VM (cloud-init ssh_authorized_keys) — Verbindungsaufbau
    /// nur Host→VM (Plan §6).
    /// </summary>
    public VmSshSession OpenSshSession(string name, Action<string>? log = null, string? privateKeyPath = null)
    {
        return VmSshSession.Open(name, log, privateKeyPath);
    }
}
