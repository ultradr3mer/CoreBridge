using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CoreBridge.Simulation;

/// <summary>
/// Kleine Teilmenge von POSIX-<c>sh</c> für <c>multipass exec … /bin/sh -c</c> im
/// simulierten Gast: Quoting, <c>$VAR</c>/<c>${VAR:-x}</c>/<c>$?</c>, <c>$(…)</c>,
/// <c>$((…))</c>, <c>;</c>/Zeilenumbruch/<c>&amp;&amp;</c>/<c>||</c>/<c>|</c>/<c>!</c>,
/// Umleitungen (<c>&gt;</c>, <c>&gt;&gt;</c>, <c>2&gt;&amp;1</c>, <c>&lt;</c>) und die
/// Befehle, die CoreBridge und ein Benutzer typischerweise schicken (<c>cd</c>,
/// <c>pwd</c>, <c>echo</c>, <c>printf</c>, <c>test</c>, <c>.</c>, <c>cat</c>,
/// <c>ls</c>, <c>mkdir</c>, <c>install</c>, <c>command -v</c>, Komponenten-Sonden …).
/// </summary>
/// <remarks>
/// Das Dateisystem ist virtuell: per <c>transfer</c> oder Umleitung geschriebene
/// Dateien, die <c>.ready</c>-Marker und die installierten Komponenten der VM. Dazu
/// kommt eine echte „Disk“ der VM auf dem Host (<c>&lt;Simulator&gt;/disks/&lt;vm&gt;</c>):
/// dort landen übertragene Binärdateien (git bundles), und <c>git</c> läuft als echtes
/// git darauf — Gastpfade werden auf die Disk abgebildet. So tragen repo-send,
/// repo-receive und <c>cb work</c> samt Stellvertreter-Agent (<c>claude</c>,
/// <c>opencode</c>: Banner, Commit, Ende) im Simulator. Ablaufsteuerung: nur
/// <c>if … then … [else …] fi</c>; <c>for</c> und Funktionen gibt es nicht. Ein
/// unbekannter Befehl ohne Pfad gilt — wie bisher — als erfolgreich ohne Ausgabe,
/// damit freies Durchspielen nicht an <c>apt-get</c> &amp; Co. scheitert.
/// </remarks>
internal sealed class SimShell
{
    public const string Home = "/home/ubuntu";

    private const int NotFound = 127;

    private const string MarkerDirectory = "/var/lib/corebridge";

    /// <summary>Installierte Snaps, einer je Zeile — reist als Datei mit Klonen und Snapshots.</summary>
    internal const string SnapsFile = "/var/lib/snapd/corebridge-sim-snaps";

    /// <summary>Erstes Wort eines geparsten <c>if</c>: danach Bedingung, then- und else-Teil als Skripte.</summary>
    private const string IfMarker = "\u0001if";

    /// <summary>Agenten, deren Stellvertreter der Simulator spielt (<c>cb work</c>).</summary>
    private static readonly HashSet<string> Agents = new(StringComparer.Ordinal) { "claude", "opencode" };

    /// <summary>Was <c>&lt;programm&gt; --version</c> in einem provisionierten Gast meldet.</summary>
    private static readonly Dictionary<string, string> Versions = new(StringComparer.Ordinal)
    {
        ["claude"] = "2.0.14 (Claude Code)",
        ["dotnet"] = "10.0.100",
        ["python3.14"] = "Python 3.14.0",
        ["python3"] = "Python 3.14.0",
        ["cline"] = "3.4.0"
    };

    private static readonly HashSet<string> Builtins = new(StringComparer.Ordinal)
    {
        "cd", "pwd", "echo", "printf", "test", "[", "true", "false", ":", "exit", ".", "source",
        "export", "unset", "command", "set"
    };

    /// <summary>Programme, die <c>command -v</c> unter <c>/usr/bin</c> findet.</summary>
    private static readonly HashSet<string> SystemPrograms = new(StringComparer.Ordinal)
    {
        "sh", "bash", "dash", "cat", "ls", "grep", "head", "id", "whoami", "hostname", "uname", "mkdir",
        "touch", "rm", "install", "sudo", "env", "sleep", "apt-get", "apt", "snap", "cloud-init",
        "systemctl", "curl", "git", "tar", "sed", "awk"
    };

    private static readonly string[] BaseDirectories =
    [
        "/", "/bin", "/etc", "/home", Home, "/root", "/snap", "/snap/bin", "/tmp", "/usr", "/usr/bin",
        "/usr/local", "/usr/local/bin", "/usr/local/sbin", "/var", "/var/lib", MarkerDirectory, "/var/tmp"
    ];

    private readonly SimulatedMultipassCli.SimVm _vm;
    private readonly string? _disk;
    private readonly Dictionary<string, string> _vars;
    private readonly IReadOnlyList<string> _args;
    private readonly StringBuilder _out = new();
    private readonly StringBuilder _err = new();
    private string _cwd;
    private string _user;
    private int _status;
    private int _substitutions;
    private bool _exited;

    /// <param name="disk">Host-Ordner, der die Disk der VM spielt (<c>null</c>: nur virtuell).</param>
    public SimShell(SimulatedMultipassCli.SimVm vm, string? disk = null)
        : this(
            vm,
            disk,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["HOME"] = Home,
                ["USER"] = "ubuntu",
                ["LOGNAME"] = "ubuntu",
                ["SHELL"] = "/bin/bash",
                ["PATH"] = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin:/snap/bin"
            },
            Home,
            "ubuntu",
            ["sh"])
    {
    }

    private SimShell(SimulatedMultipassCli.SimVm vm, string? disk, Dictionary<string, string> vars, string cwd, string user, IReadOnlyList<string> args)
    {
        _vm = vm;
        _disk = disk;
        _vars = vars;
        _cwd = cwd;
        _user = user;
        _args = args;
    }

    /// <summary>Führt das Skript aus wie <c>sh -c</c>: Exit-Code, stdout, stderr.</summary>
    public (int ExitCode, string StdOut, string StdErr) Run(string script, string stdin = "")
    {
        try
        {
            RunScript(script, stdin);
        }
        catch (FormatException exception)
        {
            // Arithmetikfehler brechen das Skript ab wie in dash.
            _err.Append($"sh: 1: {exception.Message}\n");
            _status = 2;
        }

        return (_status, _out.ToString(), _err.ToString());
    }

    /// <summary>Unterschale (<c>$(…)</c>, <c>sh -c</c>): eigene Variablen- und Verzeichniskopie.</summary>
    private SimShell Subshell(IReadOnlyList<string>? args = null) =>
        new(_vm, _disk, new Dictionary<string, string>(_vars, StringComparer.Ordinal), _cwd, _user, args ?? _args);

    // ---------------------------------------------------------------- Ablauf

    private void RunScript(string script, string stdin)
    {
        foreach (var list in Parser.Parse(script))
        {
            foreach (var (op, pipeline) in list)
            {
                if ((op == "&&" && _status != 0) || (op == "||" && _status == 0))
                {
                    continue;
                }

                var output = RunPipeline(pipeline, stdin);
                _out.Append(output);
                if (_exited)
                {
                    return;
                }
            }
        }
    }

    private string RunPipeline(Pipeline pipeline, string stdin)
    {
        var input = stdin;
        for (var i = 0; i < pipeline.Commands.Count && !_exited; i++)
        {
            input = RunSimple(pipeline.Commands[i], input);
        }

        if (pipeline.Negate)
        {
            _status = _status == 0 ? 1 : 0;
        }

        return input;
    }

    /// <summary>Ein einfacher Befehl samt Zuweisungen und Umleitungen; liefert sein stdout.</summary>
    private string RunSimple(SimpleCommand command, string stdin)
    {
        if (command.Words is [IfMarker, var condition, var then, var otherwise])
        {
            // if … then … [else …] fi: die Ausgabe der Zweige geht direkt in die Schale.
            RunScript(condition, stdin);
            if (_exited)
            {
                return string.Empty;
            }

            if (_status == 0)
            {
                RunScript(then, stdin);
            }
            else if (otherwise.Length > 0)
            {
                RunScript(otherwise, stdin);
            }
            else
            {
                _status = 0;
            }

            return Route(string.Empty, string.Empty, command.Redirects);
        }

        var substitutions = _substitutions;
        var assignments = new List<(string Name, string Value)>();
        var index = 0;
        for (; index < command.Words.Count; index++)
        {
            var match = Regex.Match(command.Words[index], "^([A-Za-z_][A-Za-z0-9_]*)=");
            if (!match.Success)
            {
                break;
            }

            assignments.Add((match.Groups[1].Value, string.Join(' ', Expand(command.Words[index][match.Length..]))));
        }

        var words = command.Words.Skip(index).SelectMany(Expand).ToList();
        if (words.Count == 0)
        {
            foreach (var (name, value) in assignments)
            {
                _vars[name] = value;
            }

            // Ohne Befehl zählt der Status der letzten Befehlsersetzung (x=$(false) → 1).
            if (_substitutions == substitutions)
            {
                _status = 0;
            }

            return Route(string.Empty, string.Empty, command.Redirects);
        }

        // Vorangestellte Zuweisungen gelten nur für diesen Befehl.
        var saved = assignments.Select(a => (a.Name, Old: _vars.GetValueOrDefault(a.Name))).ToList();
        foreach (var (name, value) in assignments)
        {
            _vars[name] = value;
        }

        try
        {
            foreach (var redirect in command.Redirects.Where(r => r.Op == "<"))
            {
                var path = string.Join(' ', Expand(redirect.Target));
                var content = path == "/dev/null" ? string.Empty : ReadFile(Resolve(path));
                if (content is null)
                {
                    _err.Append($"sh: 1: cannot open {path}: No such file\n");
                    _status = 2;
                    return string.Empty;
                }

                stdin = content;
            }

            var (code, stdOut, stdErr) = Invoke(words, stdin);
            _status = code;
            return Route(stdOut, stdErr, command.Redirects);
        }
        finally
        {
            foreach (var (name, old) in saved)
            {
                if (old is null)
                {
                    _vars.Remove(name);
                }
                else
                {
                    _vars[name] = old;
                }
            }
        }
    }

    private enum Sink
    {
        Out,
        Err,
        Null,
        File
    }

    private readonly record struct Target(Sink Sink, string? Path = null, bool Append = false);

    /// <summary>
    /// Wendet die Umleitungen in ihrer Reihenfolge an (<c>&gt;/dev/null 2&gt;&amp;1</c>
    /// schluckt beides, <c>2&gt;&amp;1 &gt;/dev/null</c> nur stdout); stderr landet
    /// direkt im stderr der Schale, stdout geht an den Aufrufer zurück.
    /// </summary>
    private string Route(string stdOut, string stdErr, IReadOnlyList<Redirect> redirects)
    {
        var fd1 = new Target(Sink.Out);
        var fd2 = new Target(Sink.Err);
        foreach (var redirect in redirects.Where(r => r.Op != "<"))
        {
            var target = string.Join(' ', Expand(redirect.Target));
            Target ToFile(bool append) => target == "/dev/null"
                ? new Target(Sink.Null)
                : new Target(Sink.File, Resolve(target), append);

            switch (redirect.Op)
            {
                case ">" or "1>":
                    fd1 = ToFile(false);
                    break;
                case ">>" or "1>>":
                    fd1 = ToFile(true);
                    break;
                case "2>":
                    fd2 = ToFile(false);
                    break;
                case "2>>":
                    fd2 = ToFile(true);
                    break;
                case "&>":
                    fd1 = fd2 = ToFile(false);
                    break;
                case ">&" or "1>&" when target == "2":
                    fd1 = fd2;
                    break;
                case "2>&" when target == "1":
                    fd2 = fd1;
                    break;
            }
        }

        var returned = new StringBuilder();
        var written = new HashSet<string>(StringComparer.Ordinal);
        void Send(Target target, string text)
        {
            switch (target.Sink)
            {
                case Sink.Out:
                    returned.Append(text);
                    break;
                case Sink.Err:
                    _err.Append(text);
                    break;
                case Sink.File:
                    // Erstes Schreiben dieses Befehls kürzt (außer bei >>), weitere hängen an.
                    WriteFile(target.Path!, text, target.Append || !written.Add(target.Path!));
                    break;
            }
        }

        Send(fd1, stdOut);
        Send(fd2, stdErr);
        return returned.ToString();
    }

    // ---------------------------------------------------------------- Befehle

    private (int Code, string Out, string Err) Invoke(List<string> words, string stdin)
    {
        var name = words[0];
        var args = words.Skip(1).ToList();

        if (name == "sudo")
        {
            var user = "root";
            while (args.Count > 0 && args[0].StartsWith('-'))
            {
                if (args[0] is "-u" && args.Count > 1)
                {
                    user = args[1];
                    args.RemoveAt(0);
                }

                var option = args[0];
                args.RemoveAt(0);
                if (option == "--")
                {
                    break;
                }
            }

            if (args.Count == 0)
            {
                return (1, string.Empty, "usage: sudo command\n");
            }

            var previous = _user;
            _user = user;
            try
            {
                return InvokeWithAssignments(args, stdin);
            }
            finally
            {
                _user = previous;
            }
        }

        if (name == "env")
        {
            return args.Count == 0
                ? (0, string.Concat(_vars.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => $"{v.Key}={v.Value}\n")), string.Empty)
                : InvokeWithAssignments(args, stdin);
        }

        var program = name.Contains('/') ? name[(name.LastIndexOf('/') + 1)..] : name;
        switch (name)
        {
            case ":" or "true":
                return (0, string.Empty, string.Empty);
            case "false":
                return (1, string.Empty, string.Empty);
            case "echo":
                return (0, Echo(args), string.Empty);
            case "printf":
                return args.Count == 0
                    ? (2, string.Empty, "printf: usage: printf format [arguments]\n")
                    : (0, Printf(args[0], args.Skip(1).ToList()), string.Empty);
            case "pwd":
                return (0, _cwd + "\n", string.Empty);
            case "cd":
                return Cd(args);
            case "test":
                return Test(args);
            case "[":
                return args.Count > 0 && args[^1] == "]"
                    ? Test(args[..^1])
                    : (2, string.Empty, "sh: 1: [: missing ]\n");
            case "exit":
                _exited = true;
                return (args.Count > 0 && int.TryParse(args[0], out var code) ? code & 0xFF : _status, string.Empty, string.Empty);
            case "." or "source":
                return Source(args);
            case "export":
                foreach (var arg in args)
                {
                    var eq = arg.IndexOf('=');
                    if (eq > 0)
                    {
                        _vars[arg[..eq]] = arg[(eq + 1)..];
                    }
                }

                return (0, string.Empty, string.Empty);
            case "unset":
                args.ForEach(arg => _vars.Remove(arg));
                return (0, string.Empty, string.Empty);
            case "set":
                return (0, string.Empty, string.Empty);
            case "command" when args.FirstOrDefault() == "-v":
                return CommandV(args.Skip(1).ToList());
            case "command":
                return args.Count == 0 ? (0, string.Empty, string.Empty) : Invoke(args, stdin);
            case "which":
                return CommandV(args);
        }

        // Ein installiertes Komponenten-Skript läuft durch (cloud-init bzw. vm-install):
        // die Komponente gilt als installiert und setzt ihren Marker wie das echte Skript.
        var install = Regex.Match(name, @"^/usr/local/sbin/corebridge-install-([A-Za-z0-9_-]+)$");
        if (install.Success)
        {
            var component = install.Groups[1].Value;
            if (!_vm.Components.Contains(component))
            {
                _vm.Components.Add(component);
            }

            if (!_vm.Markers.Contains(component))
            {
                _vm.Markers.Add(component);
            }

            return (0, $"[simulation] {component} installiert\n", string.Empty);
        }

        if (program == "git" && _disk is not null)
        {
            return Git(args, stdin);
        }

        if (program is "claude" or "opencode" && Agents.Contains(program) && ComponentInstalled(program)
            && !args.Any(a => a is "--version" or "-v"))
        {
            return Agent(program, args);
        }

        if (Versions.TryGetValue(program, out var version))
        {
            // Mit Pfad zählt nur, wohin die echten Skripte linken (/snap/bin/dotnet gibt es nicht).
            return ComponentInstalled(program) && (!name.Contains('/') || FileExists(Resolve(name)))
                ? (0, version + "\n", string.Empty)
                : (NotFound, string.Empty, $"sh: 1: {name}: not found\n");
        }

        switch (program)
        {
            case "sh" or "bash" or "dash":
                return Sh(args, stdin);
            case "id":
                return args.Contains("-un") || args.Contains("-nu") || (args.Contains("-u") && args.Contains("-n"))
                    ? (0, _user + "\n", string.Empty)
                    : args.Contains("-u")
                        ? (0, (_user == "root" ? "0" : "1000") + "\n", string.Empty)
                        : (0, _user == "root" ? "uid=0(root) gid=0(root) groups=0(root)\n" : "uid=1000(ubuntu) gid=1000(ubuntu) groups=1000(ubuntu),27(sudo)\n", string.Empty);
            case "whoami":
                return (0, _user + "\n", string.Empty);
            case "hostname":
                return (0, _vm.Name + "\n", string.Empty);
            case "uname":
                return (0, Uname(args), string.Empty);
            case "cat":
                return Cat(args, stdin);
            case "ls":
                return Ls(args);
            case "grep":
                return Grep(args, stdin);
            case "head":
                return Head(args, stdin);
            case "mkdir":
                foreach (var dir in args.Where(a => !a.StartsWith('-')))
                {
                    AddDirectory(Resolve(dir));
                }

                return (0, string.Empty, string.Empty);
            case "touch":
                foreach (var file in args.Where(a => !a.StartsWith('-')).Select(Resolve).Where(f => !FileExists(f)))
                {
                    WriteFile(file, string.Empty, append: false);
                }

                return (0, string.Empty, string.Empty);
            case "rm":
                return Rm(args);
            case "install" or "cp":
                return Copy(program, args);
            case "cloud-init" when args.FirstOrDefault() == "status":
                return (0, "status: done\n", string.Empty);
            case "snap":
                return Snap(args);
            case "sleep" or "apt-get" or "apt" or "systemctl":
                return (0, string.Empty, string.Empty);
        }

        if (name.Contains('/'))
        {
            var path = Resolve(name);
            var content = ReadFile(path);
            return content is null
                ? (NotFound, string.Empty, $"sh: 1: {name}: not found\n")
                : RunFile(content, [name, .. args], stdin);
        }

        // Unbekanntes Programm: erfolgreich ohne Ausgabe (siehe Klassenkommentar).
        return (0, string.Empty, string.Empty);
    }

    /// <summary>
    /// Echtes git auf der Host-Disk der VM: Arbeitsverzeichnis und absolute Gastpfade in den
    /// Argumenten werden auf die Disk abgebildet, in der Ausgabe wieder zurück.
    /// </summary>
    private (int, string, string) Git(List<string> args, string stdin)
    {
        var cwd = HostPath(_cwd)!;
        Directory.CreateDirectory(cwd);
        var home = HostPath(Home)!;
        Directory.CreateDirectory(home);
        var start = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg.StartsWith('/') ? HostPath(Normalize(arg))! : arg);
        }

        start.Environment["HOME"] = home;
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        // Die Disks liegen tief im Store; unter Windows sprengen git-Objekte sonst MAX_PATH. Über
        // die globale Config, weil git GIT_CONFIG_* für lokale Pushes (receive-pack) verwirft.
        var gitConfig = Path.Combine(home, ".gitconfig");
        if (!File.Exists(gitConfig))
        {
            File.WriteAllText(gitConfig, "[core]\n\tlongpaths = true\n");
        }
        using var process = System.Diagnostics.Process.Start(start)
            ?? throw new InvalidOperationException("git ließ sich nicht starten (Simulator braucht git auf dem Host).");
        process.StandardInput.Write(stdin);
        process.StandardInput.Close();
        var stdOut = process.StandardOutput.ReadToEndAsync();
        var stdErr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, ToGuest(stdOut.Result), ToGuest(stdErr.Result));
    }

    private string ToGuest(string text) =>
        _disk is null ? text : text.Replace(_disk.TrimEnd(Path.DirectorySeparatorChar), string.Empty, StringComparison.Ordinal).Replace('\\', '/');

    /// <summary>
    /// Stellvertreter für einen Agenten: meldet sich mit Arbeitsverzeichnis und legt — in
    /// einer git-Arbeitskopie — einen Commit an, so wie Claude seine Arbeit hinterlässt.
    /// Steht ein Merge offen (Integration aus <c>repo-receive</c>), löst er stattdessen die
    /// Konflikte (beide Seiten behalten), schließt den Merge ab und schreibt das Urteil in
    /// die Ergebnisdatei, die der Auftrag nennt.
    /// </summary>
    private (int, string, string) Agent(string program, List<string> args)
    {
        var output = new StringBuilder($"[simulation] {program} (Stellvertreter) in {_cwd}\n");
        if (HostPath(_cwd) is { } open && File.Exists(Path.Combine(open, ".git", "MERGE_HEAD")))
        {
            return ResolveMerge(open, args, output);
        }

        if (HostPath(_cwd) is { } work && Directory.Exists(Path.Combine(work, ".git")))
        {
            File.AppendAllText(Path.Combine(work, "corebridge-agent.txt"), $"{program}: Arbeit vom {DateTime.UtcNow:O}\n");
            var (addCode, _, addErr) = Git(["add", "corebridge-agent.txt"], string.Empty);
            var (commitCode, commitOut, commitErr) = Git(["commit", "-q", "-m", $"{program}: Arbeit des Stellvertreters"], string.Empty);
            if (addCode != 0 || commitCode != 0)
            {
                return (1, output.ToString(), addErr + commitErr);
            }

            var (_, head, _) = Git(["log", "-1", "--format=%h %s"], string.Empty);
            output.Append($"[simulation] Commit {head.Trim()}\n").Append(commitOut);
        }

        return (0, output.ToString(), string.Empty);
    }

    /// <summary><c>snap install</c> merkt sich die Namen, <c>snap list [name]</c> zeigt sie wie snapd; alles andere gelingt still.</summary>
    private (int, string, string) Snap(List<string> args)
    {
        var installed = (ReadFile(SnapsFile) ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        var names = args.Skip(1).Where(a => !a.StartsWith('-')).ToList();
        switch (args.FirstOrDefault())
        {
            case "install":
                WriteFile(SnapsFile, string.Concat(installed.Union(names).Select(name => name + "\n")), append: false);
                return (0, string.Concat(names.Select(name => $"{name} 1.0 from Simulation installed\n")), string.Empty);
            case "list":
                var shown = names.Count == 0 ? installed : installed.Intersect(names).ToList();
                if (shown.Count == 0)
                {
                    return (1, string.Empty, names.Count == 0 ? "No snaps are installed yet.\n" : "error: no matching snaps installed\n");
                }

                return (0, "Name  Version  Rev  Tracking       Publisher   Notes\n" + string.Concat(shown.Select(name => $"{name}  1.0  1  latest/stable  simulation  -\n")), string.Empty);
            default:
                return (0, string.Empty, string.Empty);
        }
    }

    private (int, string, string) ResolveMerge(string work, List<string> args, StringBuilder output)
    {
        var (_, unmerged, _) = Git(["diff", "--name-only", "--diff-filter=U"], string.Empty);
        foreach (var file in unmerged.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var path = Path.Combine(work, file);
            var kept = File.ReadAllLines(path).Where(line => !Regex.IsMatch(line, "^(<<<<<<<|=======|>>>>>>>)( |$)"));
            File.WriteAllText(path, string.Join('\n', kept) + "\n");
            Git(["add", file], string.Empty);
            output.Append($"[simulation] Konflikt gelöst: {file}\n");
        }

        var (code, _, err) = Git(["commit", "-q", "--no-edit"], string.Empty);
        if (Regex.Match(string.Join(' ', args), @"/home/ubuntu/\.corebridge/integrate-[A-Za-z0-9._-]+\.result") is { Success: true } result)
        {
            WriteFile(result.Value, code == 0 ? "success\nbeide Seiten behalten\n" : "failure\ncommit fehlgeschlagen\n", append: false);
        }

        return (code, output.ToString(), err);
    }

    /// <summary><c>sudo</c>/<c>env</c> lassen Zuweisungen vor dem Befehl zu (<c>sudo DEBIAN_FRONTEND=… apt-get</c>).</summary>
    private (int Code, string Out, string Err) InvokeWithAssignments(List<string> words, string stdin)
    {
        var saved = new List<(string Name, string? Old)>();
        while (words.Count > 1 && Regex.Match(words[0], "^([A-Za-z_][A-Za-z0-9_]*)=") is { Success: true } match)
        {
            saved.Add((match.Groups[1].Value, _vars.GetValueOrDefault(match.Groups[1].Value)));
            _vars[match.Groups[1].Value] = words[0][match.Length..];
            words = words.Skip(1).ToList();
        }

        try
        {
            return Invoke(words, stdin);
        }
        finally
        {
            foreach (var (name, old) in saved)
            {
                if (old is null)
                {
                    _vars.Remove(name);
                }
                else
                {
                    _vars[name] = old;
                }
            }
        }
    }

    private static string Echo(List<string> args)
    {
        var newline = true;
        if (args.FirstOrDefault() == "-n")
        {
            newline = false;
            args = args.Skip(1).ToList();
        }

        // dash-echo wertet Escapes immer aus; \c beendet die Ausgabe.
        var text = Unescape(string.Join(' ', args), out var stop);
        return text + (newline && !stop ? "\n" : string.Empty);
    }

    private static string Printf(string format, List<string> args)
    {
        var output = new StringBuilder();
        var next = 0;
        do
        {
            var consumed = false;
            for (var i = 0; i < format.Length; i++)
            {
                if (format[i] == '%' && i + 1 < format.Length)
                {
                    var spec = format[++i];
                    if (spec == '%')
                    {
                        output.Append('%');
                        continue;
                    }

                    var arg = next < args.Count ? args[next++] : string.Empty;
                    consumed = true;
                    output.Append(spec switch
                    {
                        'd' or 'i' => long.TryParse(arg, out var number) ? number.ToString(CultureInfo.InvariantCulture) : "0",
                        'b' => Unescape(arg, out _),
                        _ => arg
                    });
                }
                else if (format[i] == '\\' && i + 1 < format.Length)
                {
                    output.Append(Unescape(format.Substring(i, 2), out _));
                    i++;
                }
                else
                {
                    output.Append(format[i]);
                }
            }

            if (!consumed)
            {
                break;
            }
        }
        while (next < args.Count);

        return output.ToString();
    }

    private static string Unescape(string text, out bool stop)
    {
        stop = false;
        var output = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\\' || i + 1 >= text.Length)
            {
                output.Append(text[i]);
                continue;
            }

            switch (text[++i])
            {
                case 'n': output.Append('\n'); break;
                case 't': output.Append('\t'); break;
                case 'r': output.Append('\r'); break;
                case '\\': output.Append('\\'); break;
                case 'c':
                    stop = true;
                    return output.ToString();
                default:
                    output.Append('\\').Append(text[i]);
                    break;
            }
        }

        return output.ToString();
    }

    private (int, string, string) Cd(List<string> args)
    {
        var target = args.Count == 0 ? _vars.GetValueOrDefault("HOME", Home) : args[0];
        var path = Resolve(target);
        if (!DirectoryExists(path))
        {
            return (2, string.Empty, $"sh: 1: cd: can't cd to {target}\n");
        }

        _cwd = path;
        _vars["PWD"] = path;
        return (0, string.Empty, string.Empty);
    }

    private (int, string, string) Test(List<string> args)
    {
        try
        {
            return (EvaluateTest(args) ? 0 : 1, string.Empty, string.Empty);
        }
        catch (FormatException exception)
        {
            return (2, string.Empty, $"sh: 1: test: {exception.Message}\n");
        }
    }

    private bool EvaluateTest(List<string> args)
    {
        if (args.Count > 0 && args[0] == "!")
        {
            return !EvaluateTest(args[1..]);
        }

        return args.Count switch
        {
            0 => false,
            1 => args[0].Length > 0,
            2 => args[0] switch
            {
                "-n" => args[1].Length > 0,
                "-z" => args[1].Length == 0,
                "-e" => FileExists(Resolve(args[1])) || DirectoryExists(Resolve(args[1])),
                "-f" or "-r" or "-x" or "-s" => FileExists(Resolve(args[1])),
                "-d" => DirectoryExists(Resolve(args[1])),
                _ => throw new FormatException($"{args[0]}: unexpected operator")
            },
            3 => args[1] switch
            {
                "=" or "==" => args[0] == args[2],
                "!=" => args[0] != args[2],
                "-eq" => Integer(args[0]) == Integer(args[2]),
                "-ne" => Integer(args[0]) != Integer(args[2]),
                "-lt" => Integer(args[0]) < Integer(args[2]),
                "-le" => Integer(args[0]) <= Integer(args[2]),
                "-gt" => Integer(args[0]) > Integer(args[2]),
                "-ge" => Integer(args[0]) >= Integer(args[2]),
                _ => throw new FormatException($"{args[1]}: unexpected operator")
            },
            _ => throw new FormatException("too many arguments")
        };
    }

    private static long Integer(string text) =>
        long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new FormatException($"Illegal number: {text}");

    /// <summary><c>. datei</c>: wird in dieser Schale ausgeführt, setzt also Variablen (z. B. <c>/etc/os-release</c>).</summary>
    private (int, string, string) Source(List<string> args)
    {
        if (args.Count == 0)
        {
            return (2, string.Empty, "sh: 1: .: filename argument required\n");
        }

        var content = ReadFile(Resolve(args[0]));
        if (content is null)
        {
            return (2, string.Empty, $"sh: 1: .: cannot open {args[0]}: No such file\n");
        }

        var start = _out.Length;
        var errStart = _err.Length;
        RunScript(content, string.Empty);
        var output = _out.ToString(start, _out.Length - start);
        var error = _err.ToString(errStart, _err.Length - errStart);
        _out.Length = start;
        _err.Length = errStart;
        return (_status, output, error);
    }

    private (int, string, string) CommandV(List<string> names)
    {
        var output = new StringBuilder();
        var code = 0;
        foreach (var name in names)
        {
            if (Builtins.Contains(name))
            {
                output.Append(name).Append('\n');
            }
            else if (name.Contains('/'))
            {
                if (FileExists(Resolve(name)))
                {
                    output.Append(name).Append('\n');
                }
                else
                {
                    code = 1;
                }
            }
            else if (Versions.ContainsKey(name) && ComponentInstalled(name))
            {
                output.Append($"/usr/local/bin/{name}\n");
            }
            else if (SystemPrograms.Contains(name))
            {
                output.Append($"/usr/bin/{name}\n");
            }
            else if (FileExists($"/usr/local/bin/{name}") || FileExists($"/usr/local/sbin/{name}"))
            {
                output.Append(FileExists($"/usr/local/bin/{name}") ? $"/usr/local/bin/{name}\n" : $"/usr/local/sbin/{name}\n");
            }
            else
            {
                code = 1;
            }
        }

        return (code, output.ToString(), string.Empty);
    }

    /// <summary><c>sh -c 'skript' [name args…]</c>, <c>sh datei</c> oder ein Skript über stdin.</summary>
    private (int, string, string) Sh(List<string> args, string stdin)
    {
        var options = args.TakeWhile(a => a.StartsWith('-')).ToList();
        var rest = args.Skip(options.Count).ToList();
        if (options.Any(o => o.Contains('c')))
        {
            return rest.Count == 0
                ? (2, string.Empty, "sh: 0: -c requires an argument\n")
                : RunFile(rest[0], rest.Count > 1 ? rest.Skip(1).ToList() : ["sh"], stdin);
        }

        if (rest.Count == 0)
        {
            return RunFile(stdin, ["sh"], string.Empty);
        }

        var content = ReadFile(Resolve(rest[0]));
        return content is null
            ? (2, string.Empty, $"sh: 0: cannot open {rest[0]}: No such file\n")
            : RunFile(content, rest, stdin);
    }

    private (int, string, string) RunFile(string script, IReadOnlyList<string> args, string stdin)
    {
        var (code, stdOut, stdErr) = Subshell(args).Run(script, stdin);
        return (code, stdOut, stdErr);
    }

    private string Uname(List<string> args)
    {
        if (args.Contains("-r"))
        {
            return "6.17.0-41-generic\n";
        }

        if (args.Contains("-m"))
        {
            return "x86_64\n";
        }

        return args.Count == 0 || args.Contains("-s")
            ? "Linux\n"
            : $"Linux {_vm.Name} 6.17.0-41-generic #41-Ubuntu SMP x86_64 GNU/Linux\n";
    }

    private (int, string, string) Cat(List<string> args, string stdin)
    {
        var files = args.Where(a => a != "-" && !a.StartsWith('-')).ToList();
        if (files.Count == 0)
        {
            return (0, stdin, string.Empty);
        }

        var output = new StringBuilder();
        var error = new StringBuilder();
        var code = 0;
        foreach (var file in files)
        {
            var content = ReadFile(Resolve(file));
            if (content is null)
            {
                error.Append($"cat: {file}: No such file or directory\n");
                code = 1;
            }
            else
            {
                output.Append(content);
            }
        }

        return (code, output.ToString(), error.ToString());
    }

    private (int, string, string) Ls(List<string> args)
    {
        var all = args.Any(a => a.StartsWith('-') && a.Contains('a'));
        var targets = args.Where(a => !a.StartsWith('-')).ToList();
        if (targets.Count == 0)
        {
            targets.Add(".");
        }

        var output = new StringBuilder();
        var error = new StringBuilder();
        var code = 0;
        foreach (var target in targets)
        {
            var path = Resolve(target);
            if (DirectoryExists(path))
            {
                foreach (var entry in Children(path).Where(e => all || !e.StartsWith('.')).Order(StringComparer.Ordinal))
                {
                    output.Append(entry).Append('\n');
                }
            }
            else if (FileExists(path))
            {
                output.Append(target).Append('\n');
            }
            else
            {
                error.Append($"ls: cannot access '{target}': No such file or directory\n");
                code = 2;
            }
        }

        return (code, output.ToString(), error.ToString());
    }

    private (int, string, string) Grep(List<string> args, string stdin)
    {
        var quiet = args.Contains("-q");
        var invert = args.Contains("-v");
        var fixedText = args.Contains("-F");
        var operands = args.Where(a => !a.StartsWith('-')).ToList();
        if (operands.Count == 0)
        {
            return (2, string.Empty, "Usage: grep [OPTION]... PATTERNS [FILE]...\n");
        }

        var pattern = operands[0];
        string text;
        if (operands.Count > 1)
        {
            var content = ReadFile(Resolve(operands[1]));
            if (content is null)
            {
                return (2, string.Empty, $"grep: {operands[1]}: No such file or directory\n");
            }

            text = content;
        }
        else
        {
            text = stdin;
        }

        var matches = Lines(text)
            .Where(line => (fixedText ? line.Contains(pattern, StringComparison.Ordinal) : Regex.IsMatch(line, pattern)) != invert)
            .ToList();
        return (matches.Count > 0 ? 0 : 1, quiet ? string.Empty : string.Concat(matches.Select(l => l + "\n")), string.Empty);
    }

    private (int, string, string) Head(List<string> args, string stdin)
    {
        var count = 10;
        var files = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "-n" && i + 1 < args.Count)
            {
                count = int.TryParse(args[++i], out var n) ? n : count;
            }
            else if (Regex.Match(args[i], @"^-n?(\d+)$") is { Success: true } inline)
            {
                count = int.Parse(inline.Groups[1].Value, CultureInfo.InvariantCulture);
            }
            else
            {
                files.Add(args[i]);
            }
        }

        var text = files.Count == 0 ? stdin : ReadFile(Resolve(files[0]));
        return text is null
            ? (1, string.Empty, $"head: cannot open '{files[0]}' for reading: No such file or directory\n")
            : (0, string.Concat(Lines(text).Take(count).Select(l => l + "\n")), string.Empty);
    }

    private (int, string, string) Rm(List<string> args)
    {
        var force = args.Any(a => a.StartsWith('-') && a.Contains('f'));
        var recursive = args.Any(a => a.StartsWith('-') && (a.Contains('r') || a.Contains('R')));
        var error = new StringBuilder();
        var code = 0;
        foreach (var target in args.Where(a => !a.StartsWith('-')))
        {
            var path = Resolve(target);
            if (FileExists(path))
            {
                DeleteFile(path);
            }
            else if (recursive && DirectoryExists(path))
            {
                foreach (var file in _vm.Files.Keys.Where(f => f.StartsWith(path + "/", StringComparison.Ordinal)).ToList())
                {
                    _vm.Files.Remove(file);
                }

                _vm.Directories.RemoveAll(d => d == path || d.StartsWith(path + "/", StringComparison.Ordinal));
                if (HostPath(path) is { } host && Directory.Exists(host))
                {
                    Directory.Delete(host, recursive: true);
                }
            }
            else if (!force)
            {
                error.Append($"rm: cannot remove '{target}': No such file or directory\n");
                code = 1;
            }
        }

        return (code, string.Empty, error.ToString());
    }

    /// <summary><c>install [-m …] [-o …] [-g …] quelle ziel</c> und <c>cp quelle ziel</c>.</summary>
    private (int, string, string) Copy(string program, List<string> args)
    {
        var operands = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] is "-m" or "-o" or "-g")
            {
                i++;
            }
            else if (!args[i].StartsWith('-'))
            {
                operands.Add(args[i]);
            }
        }

        if (operands.Count < 2)
        {
            return (1, string.Empty, $"{program}: missing destination file operand\n");
        }

        var content = ReadFile(Resolve(operands[0]));
        if (content is null)
        {
            return (1, string.Empty, $"{program}: cannot stat '{operands[0]}': No such file or directory\n");
        }

        var destination = Resolve(operands[1]);
        if (DirectoryExists(destination))
        {
            destination = destination.TrimEnd('/') + "/" + operands[0][(operands[0].LastIndexOf('/') + 1)..];
        }

        WriteFile(destination, content, append: false);
        return (0, string.Empty, string.Empty);
    }

    private static IEnumerable<string> Lines(string text) =>
        text.Length == 0 ? [] : text.TrimEnd('\n').Split('\n');

    // ---------------------------------------------------------------- Dateisystem

    private bool ComponentInstalled(string program) =>
        _vm.Components.Contains(program.StartsWith("python", StringComparison.Ordinal) ? "python" : program);

    /// <summary>Absoluter, normalisierter Pfad (relativ zum Arbeitsverzeichnis, <c>.</c>/<c>..</c> aufgelöst).</summary>
    private string Resolve(string path) => Normalize(path.StartsWith('/') ? path : _cwd.TrimEnd('/') + "/" + path);

    /// <summary>Absoluter, normalisierter Gastpfad; relative Pfade zählen ab dem Home (wie <c>multipass transfer</c>).</summary>
    public static string Normalize(string path)
    {
        var parts = new List<string>();
        foreach (var part in (path.StartsWith('/') ? path : Home + "/" + path).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }
            }
            else if (part != ".")
            {
                parts.Add(part);
            }
        }

        return "/" + string.Join('/', parts);
    }

    private static string? MarkerOf(string path)
    {
        var match = Regex.Match(path, @"^/var/lib/corebridge/([A-Za-z0-9_-]+)\.ready$");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Dateien, die der Gast mitbringt oder die aus dem Zustand der VM folgen.</summary>
    private string? VirtualFile(string path)
    {
        if (path == "/etc/os-release")
        {
            var version = _vm.Release.Replace("Ubuntu ", string.Empty, StringComparison.Ordinal);
            return $"""
                PRETTY_NAME="{_vm.Release}"
                NAME="Ubuntu"
                VERSION_ID="{version}"
                ID=ubuntu

                """.Replace("\r\n", "\n");
        }

        if (path == "/etc/hostname")
        {
            return _vm.Name + "\n";
        }

        if (MarkerOf(path) is { } marker)
        {
            return _vm.Markers.Contains(marker) ? string.Empty : null;
        }

        var bin = Regex.Match(path, @"^/usr/local/bin/([A-Za-z0-9_.-]+)$");
        if (bin.Success && Versions.ContainsKey(bin.Groups[1].Value) && ComponentInstalled(bin.Groups[1].Value))
        {
            return "#!/bin/sh\n";
        }

        var install = Regex.Match(path, @"^/usr/local/sbin/corebridge-install-([A-Za-z0-9_-]+)$");
        return install.Success && _vm.Components.Contains(install.Groups[1].Value) ? "#!/bin/bash\n" : null;
    }

    private string? ReadFile(string path) =>
        _vm.Files.TryGetValue(path, out var content)
            ? content
            : VirtualFile(path) ?? (HostPath(path) is { } host && File.Exists(host) ? File.ReadAllText(host) : null);

    private bool FileExists(string path) => ReadFile(path) is not null;

    private void WriteFile(string path, string content, bool append)
    {
        if (MarkerOf(path) is { } marker)
        {
            if (!_vm.Markers.Contains(marker))
            {
                _vm.Markers.Add(marker);
            }

            return;
        }

        _vm.Files[path] = append && _vm.Files.TryGetValue(path, out var existing) ? existing + content : content;
    }

    private void DeleteFile(string path)
    {
        if (MarkerOf(path) is { } marker)
        {
            _vm.Markers.Remove(marker);
        }

        _vm.Files.Remove(path);
        if (HostPath(path) is { } host && File.Exists(host))
        {
            File.Delete(host);
        }
    }

    private void AddDirectory(string path)
    {
        for (var dir = path; dir.Length > 1; dir = dir[..Math.Max(1, dir.LastIndexOf('/'))])
        {
            if (!DirectoryExists(dir))
            {
                _vm.Directories.Add(dir);
            }
        }
    }

    private bool DirectoryExists(string path) =>
        BaseDirectories.Contains(path)
        || _vm.Directories.Contains(path)
        || _vm.Files.Keys.Any(f => f.StartsWith(path.TrimEnd('/') + "/", StringComparison.Ordinal))
        || (HostPath(path) is { } host && Directory.Exists(host));

    /// <summary>Der Ort eines Gastpfads auf der Host-Disk der VM; <c>null</c> ohne Disk.</summary>
    private string? HostPath(string guestPath) =>
        _disk is null ? null : Path.Combine(_disk, guestPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

    private IEnumerable<string> Children(string directory)
    {
        var prefix = directory.TrimEnd('/') + "/";
        var paths = _vm.Files.Keys
            .Concat(_vm.Directories)
            .Concat(BaseDirectories)
            .Concat(_vm.Markers.Select(m => $"{MarkerDirectory}/{m}.ready"))
            .Concat(_vm.Components.Select(c => $"/usr/local/sbin/corebridge-install-{c}"))
            .Concat(Versions.Keys.Where(ComponentInstalled).Select(v => $"/usr/local/bin/{v}"))
            .Concat(["/etc/os-release", "/etc/hostname"])
            .Concat(HostPath(directory) is { } host && Directory.Exists(host)
                ? Directory.EnumerateFileSystemEntries(host).Select(entry => prefix + Path.GetFileName(entry))
                : []);
        return paths
            .Where(p => p.StartsWith(prefix, StringComparison.Ordinal) && p.Length > prefix.Length)
            .Select(p => p[prefix.Length..].Split('/')[0])
            .Distinct(StringComparer.Ordinal);
    }

    // ---------------------------------------------------------------- Expansion

    /// <summary>
    /// Expandiert ein Rohwort zu Feldern: Quotes entfernen, <c>~</c>, Parameter,
    /// Befehls- und Arithmetik-Ersetzung; ungequotete Ersetzungen werden an
    /// Leerraum geteilt. Kein Globbing.
    /// </summary>
    private List<string> Expand(string raw)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var hasField = false;

        void AppendUnquoted(string value)
        {
            var pieces = Regex.Split(value, "[ \t\n]+");
            for (var k = 0; k < pieces.Length; k++)
            {
                if (k > 0 && (hasField || current.Length > 0))
                {
                    fields.Add(current.ToString());
                    current.Clear();
                    hasField = false;
                }

                current.Append(pieces[k]);
                hasField |= pieces[k].Length > 0;
            }
        }

        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            switch (c)
            {
                case '\'':
                {
                    var end = raw.IndexOf('\'', i + 1);
                    end = end < 0 ? raw.Length : end;
                    current.Append(raw, i + 1, end - i - 1);
                    hasField = true;
                    i = end;
                    break;
                }
                case '"':
                    i = ExpandDoubleQuoted(raw, i + 1, current);
                    hasField = true;
                    break;
                case '\\' when i + 1 < raw.Length:
                    current.Append(raw[++i]);
                    hasField = true;
                    break;
                case '$':
                    AppendUnquoted(ExpandDollar(raw, ref i));
                    break;
                case '`':
                {
                    var end = raw.IndexOf('`', i + 1);
                    end = end < 0 ? raw.Length : end;
                    AppendUnquoted(CommandSubstitution(raw[(i + 1)..end]));
                    i = end;
                    break;
                }
                case '~' when i == 0 && (raw.Length == 1 || raw[1] == '/'):
                    current.Append(_vars.GetValueOrDefault("HOME", Home));
                    hasField = true;
                    break;
                default:
                    current.Append(c);
                    hasField = true;
                    break;
            }
        }

        if (hasField || current.Length > 0)
        {
            fields.Add(current.ToString());
        }

        return fields;
    }

    /// <summary>Inhalt zwischen <c>"…"</c> ab <paramref name="start"/>; liefert den Index des schließenden Quotes.</summary>
    private int ExpandDoubleQuoted(string raw, int start, StringBuilder target)
    {
        var i = start;
        for (; i < raw.Length && raw[i] != '"'; i++)
        {
            switch (raw[i])
            {
                case '\\' when i + 1 < raw.Length && raw[i + 1] is '$' or '`' or '"' or '\\':
                    target.Append(raw[++i]);
                    break;
                case '\\' when i + 1 < raw.Length && raw[i + 1] == '\n':
                    i++;
                    break;
                case '$':
                    target.Append(ExpandDollar(raw, ref i));
                    break;
                case '`':
                {
                    var end = raw.IndexOf('`', i + 1);
                    end = end < 0 ? raw.Length : end;
                    target.Append(CommandSubstitution(raw[(i + 1)..end]));
                    i = end;
                    break;
                }
                default:
                    target.Append(raw[i]);
                    break;
            }
        }

        return i;
    }

    /// <summary>Expandiert ab <c>raw[i] == '$'</c>; <paramref name="i"/> steht danach auf dem letzten verbrauchten Zeichen.</summary>
    private string ExpandDollar(string raw, ref int i)
    {
        if (i + 1 >= raw.Length)
        {
            return "$";
        }

        var next = raw[i + 1];
        if (next == '(')
        {
            var end = Lexer.MatchingParen(raw, i + 1);
            if (i + 2 < raw.Length && raw[i + 2] == '(' && raw[end - 1] == ')')
            {
                var expression = raw[(i + 3)..(end - 1)];
                i = end;
                var expanded = new StringBuilder();
                ExpandDoubleQuoted(expression + "\"", 0, expanded);
                return Arithmetic.Evaluate(expanded.ToString(), name => _vars.GetValueOrDefault(name))
                    .ToString(CultureInfo.InvariantCulture);
            }

            var command = raw[(i + 2)..Math.Min(end, raw.Length)];
            i = end;
            return CommandSubstitution(command);
        }

        if (next == '{')
        {
            var end = raw.IndexOf('}', i + 2);
            end = end < 0 ? raw.Length : end;
            var body = raw[(i + 2)..end];
            i = end;
            if (body.StartsWith('#'))
            {
                return Parameter(body[1..]).Length.ToString(CultureInfo.InvariantCulture);
            }

            var op = Regex.Match(body, "^([A-Za-z_][A-Za-z0-9_]*|[0-9?#@*$])(:?[-=+?])(.*)$");
            if (!op.Success)
            {
                return Parameter(body);
            }

            var value = Parameter(op.Groups[1].Value);
            var empty = op.Groups[2].Value.StartsWith(':') ? value.Length == 0 : !IsSet(op.Groups[1].Value);
            var word = new StringBuilder();
            ExpandDoubleQuoted(op.Groups[3].Value + "\"", 0, word);
            switch (op.Groups[2].Value.TrimStart(':'))
            {
                case "-":
                    return empty ? word.ToString() : value;
                case "=":
                    if (empty)
                    {
                        _vars[op.Groups[1].Value] = word.ToString();
                        return word.ToString();
                    }

                    return value;
                case "+":
                    return empty ? string.Empty : word.ToString();
                default:
                    return value;
            }
        }

        if (next is '?' or '$' or '#' or '@' or '*' or '!' || char.IsDigit(next))
        {
            i++;
            return Parameter(next.ToString());
        }

        var name = Regex.Match(raw[(i + 1)..], "^[A-Za-z_][A-Za-z0-9_]*");
        if (!name.Success)
        {
            return "$";
        }

        i += name.Length;
        return Parameter(name.Value);
    }

    private bool IsSet(string name) =>
        _vars.ContainsKey(name) || (int.TryParse(name, out var index) && index < _args.Count);

    private string Parameter(string name)
    {
        switch (name)
        {
            case "?":
                return _status.ToString(CultureInfo.InvariantCulture);
            case "$":
                return "4242";
            case "!":
                return string.Empty;
            case "#":
                return Math.Max(0, _args.Count - 1).ToString(CultureInfo.InvariantCulture);
            case "@" or "*":
                return string.Join(' ', _args.Skip(1));
        }

        if (int.TryParse(name, out var index))
        {
            return index < _args.Count ? _args[index] : string.Empty;
        }

        return name == "PWD" ? _cwd : _vars.GetValueOrDefault(name, string.Empty);
    }

    /// <summary><c>$(…)</c>: Unterschale, stdout ohne abschließende Zeilenumbrüche; stderr geht durch.</summary>
    private string CommandSubstitution(string command)
    {
        var (code, stdOut, stdErr) = Subshell().Run(command);
        _err.Append(stdErr);
        _status = code;
        _substitutions++;
        return stdOut.TrimEnd('\n');
    }

    // ---------------------------------------------------------------- Syntax

    private sealed record Redirect(string Op, string Target);

    private sealed record SimpleCommand(List<string> Words, List<Redirect> Redirects);

    private sealed record Pipeline(bool Negate, List<SimpleCommand> Commands);

    private enum TokenKind
    {
        Word,
        Operator
    }

    private readonly record struct Token(TokenKind Kind, string Text);

    /// <summary>Zerlegt in Wörter (roh, mit Quotes) und Operatoren; Zeilenumbruch wird zu <c>;</c>.</summary>
    private static class Lexer
    {
        public static List<Token> Tokenize(string script)
        {
            var tokens = new List<Token>();
            var word = new StringBuilder();
            var inWord = false;

            void Flush()
            {
                if (inWord)
                {
                    tokens.Add(new Token(TokenKind.Word, word.ToString()));
                    word.Clear();
                    inWord = false;
                }
            }

            void Operator(string text)
            {
                Flush();
                tokens.Add(new Token(TokenKind.Operator, text));
            }

            for (var i = 0; i < script.Length; i++)
            {
                var c = script[i];
                var next = i + 1 < script.Length ? script[i + 1] : '\0';
                switch (c)
                {
                    case ' ' or '\t' or '\r':
                        Flush();
                        break;
                    case '\\' when next == '\n':
                        i++;
                        break;
                    case '\\':
                        word.Append(c);
                        if (i + 1 < script.Length)
                        {
                            word.Append(script[++i]);
                        }

                        inWord = true;
                        break;
                    case '\n' or ';':
                        Operator(";");
                        break;
                    case '#' when !inWord:
                        while (i + 1 < script.Length && script[i + 1] != '\n')
                        {
                            i++;
                        }

                        break;
                    case '&' when next == '&':
                        Operator("&&");
                        i++;
                        break;
                    case '&' when next == '>':
                        Operator("&>");
                        i++;
                        break;
                    case '&':
                        // Hintergrund gibt es nicht — der Befehl läuft einfach durch.
                        Operator(";");
                        break;
                    case '|' when next == '|':
                        Operator("||");
                        i++;
                        break;
                    case '|':
                        Operator("|");
                        break;
                    case '>' or '<':
                    {
                        var fd = inWord && word.ToString() is "1" or "2" ? word.ToString() : string.Empty;
                        if (fd.Length > 0)
                        {
                            word.Clear();
                            inWord = false;
                        }

                        var op = fd + c;
                        if (c == '>' && next == '>')
                        {
                            op += ">";
                            i++;
                        }
                        else if (c == '>' && next == '&')
                        {
                            op += "&";
                            i++;
                        }

                        Operator(op);
                        break;
                    }
                    case '\'':
                    {
                        var end = script.IndexOf('\'', i + 1);
                        end = end < 0 ? script.Length - 1 : end;
                        word.Append(script, i, end - i + 1);
                        inWord = true;
                        i = end;
                        break;
                    }
                    case '"':
                    {
                        var end = ClosingDoubleQuote(script, i + 1);
                        word.Append(script, i, Math.Min(end, script.Length - 1) - i + 1);
                        inWord = true;
                        i = end;
                        break;
                    }
                    case '$' when next == '(':
                    {
                        var end = MatchingParen(script, i + 1);
                        word.Append(script, i, Math.Min(end, script.Length - 1) - i + 1);
                        inWord = true;
                        i = end;
                        break;
                    }
                    case '`':
                    {
                        var end = script.IndexOf('`', i + 1);
                        end = end < 0 ? script.Length - 1 : end;
                        word.Append(script, i, end - i + 1);
                        inWord = true;
                        i = end;
                        break;
                    }
                    default:
                        word.Append(c);
                        inWord = true;
                        break;
                }
            }

            Flush();
            return tokens;
        }

        private static int ClosingDoubleQuote(string text, int start)
        {
            for (var i = start; i < text.Length; i++)
            {
                if (text[i] == '\\')
                {
                    i++;
                }
                else if (text[i] == '"')
                {
                    return i;
                }
                else if (text[i] == '$' && i + 1 < text.Length && text[i + 1] == '(')
                {
                    i = MatchingParen(text, i + 1);
                }
            }

            return text.Length;
        }

        /// <summary>Index der schließenden Klammer zu <c>text[open] == '('</c>, Quotes berücksichtigt.</summary>
        public static int MatchingParen(string text, int open)
        {
            var depth = 0;
            for (var i = open; i < text.Length; i++)
            {
                switch (text[i])
                {
                    case '\\':
                        i++;
                        break;
                    case '\'':
                        var end = text.IndexOf('\'', i + 1);
                        i = end < 0 ? text.Length : end;
                        break;
                    case '"':
                        i = ClosingDoubleQuote(text, i + 1);
                        break;
                    case '(':
                        depth++;
                        break;
                    case ')':
                        if (--depth == 0)
                        {
                            return i;
                        }

                        break;
                }
            }

            return text.Length;
        }
    }

    /// <summary>Listen (<c>;</c>) aus Und-Oder-Ketten aus Pipelines aus einfachen Befehlen.</summary>
    private static class Parser
    {
        public static List<List<(string Op, Pipeline Pipeline)>> Parse(string script)
        {
            var lists = new List<List<(string, Pipeline)>>();
            var list = new List<(string, Pipeline)>();
            var op = string.Empty;
            var commands = new List<SimpleCommand>();
            var negate = false;
            var words = new List<string>();
            var redirects = new List<Redirect>();

            void EndCommand()
            {
                if (words.Count > 0 || redirects.Count > 0)
                {
                    commands.Add(new SimpleCommand(words, redirects));
                }

                words = [];
                redirects = [];
            }

            void EndPipeline()
            {
                EndCommand();
                if (commands.Count > 0)
                {
                    list.Add((op, new Pipeline(negate, commands)));
                }

                commands = [];
                negate = false;
            }

            void EndList()
            {
                EndPipeline();
                if (list.Count > 0)
                {
                    lists.Add(list);
                }

                list = [];
                op = string.Empty;
            }

            var tokens = Lexer.Tokenize(script);
            for (var i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (token.Kind == TokenKind.Word && token.Text == "if" && words.Count == 0)
                {
                    words.AddRange(ParseIf(tokens, ref i));
                    continue;
                }

                if (token.Kind == TokenKind.Word)
                {
                    if (token.Text == "!" && words.Count == 0 && commands.Count == 0)
                    {
                        negate = !negate;
                    }
                    else
                    {
                        words.Add(token.Text);
                    }

                    continue;
                }

                switch (token.Text)
                {
                    case ";":
                        EndList();
                        break;
                    case "&&" or "||":
                        EndPipeline();
                        op = token.Text;
                        break;
                    case "|":
                        EndCommand();
                        break;
                    default:
                        // Umleitung: das nächste Wort ist ihr Ziel.
                        var target = i + 1 < tokens.Count && tokens[i + 1].Kind == TokenKind.Word ? tokens[++i].Text : string.Empty;
                        redirects.Add(new Redirect(token.Text, target));
                        break;
                }
            }

            EndList();
            return lists;
        }

        /// <summary>
        /// <c>if A; then B; [else C;] fi</c> ab <paramref name="i"/> (dem <c>if</c>): liefert
        /// <see cref="IfMarker"/> und die drei Teile als Skripttext; <paramref name="i"/> steht
        /// danach auf dem <c>fi</c>. Verschachtelte <c>if</c> zählen mit, <c>elif</c> nicht.
        /// </summary>
        private static string[] ParseIf(List<Token> tokens, ref int i)
        {
            var parts = new List<List<Token>> { new() };
            var depth = 0;
            var commandStart = true;
            for (i++; i < tokens.Count; i++)
            {
                var token = tokens[i];
                var keyword = token.Kind == TokenKind.Word && commandStart ? token.Text : null;
                if (keyword == "if")
                {
                    depth++;
                }
                else if (keyword == "fi" && depth-- == 0)
                {
                    break;
                }
                else if (depth == 0 && keyword is "then" or "else")
                {
                    parts.Add([]);
                    continue;
                }
                else if (depth == 0 && keyword == "elif")
                {
                    throw new FormatException("elif is not supported (simulation)");
                }

                parts[^1].Add(token);
                commandStart = token.Kind == TokenKind.Operator && token.Text is ";" or "&&" or "||" or "|";
            }

            if (i >= tokens.Count || parts.Count < 2)
            {
                throw new FormatException("Syntax error: if without then/fi");
            }

            static string Text(List<Token> part) => string.Join(' ', part.Select(t => t.Text));
            return [IfMarker, Text(parts[0]), Text(parts[1]), parts.Count > 2 ? Text(parts[2]) : string.Empty];
        }
    }

    /// <summary><c>$((…))</c>: Ganzzahlen, Variablen, <c>+ - * / %</c>, Vergleiche, Klammern.</summary>
    private sealed class Arithmetic
    {
        private readonly string _text;
        private readonly Func<string, string?> _lookup;
        private int _pos;

        private Arithmetic(string text, Func<string, string?> lookup)
        {
            _text = text;
            _lookup = lookup;
        }

        public static long Evaluate(string text, Func<string, string?> lookup)
        {
            var parser = new Arithmetic(text, lookup);
            var value = parser.Comparison();
            parser.Skip();
            return parser._pos == text.Length ? value : throw new FormatException($"arithmetic expression: expecting EOF: \"{text}\"");
        }

        private long Comparison()
        {
            var left = Additive();
            while (true)
            {
                Skip();
                var op = new[] { "==", "!=", "<=", ">=", "<", ">" }.FirstOrDefault(o => _text.AsSpan(_pos).StartsWith(o));
                if (op is null)
                {
                    return left;
                }

                _pos += op.Length;
                var right = Additive();
                left = op switch
                {
                    "==" => left == right ? 1 : 0,
                    "!=" => left != right ? 1 : 0,
                    "<=" => left <= right ? 1 : 0,
                    ">=" => left >= right ? 1 : 0,
                    "<" => left < right ? 1 : 0,
                    _ => left > right ? 1 : 0
                };
            }
        }

        private long Additive()
        {
            var left = Multiplicative();
            while (Peek() is '+' or '-')
            {
                var op = _text[_pos++];
                var right = Multiplicative();
                left = op == '+' ? left + right : left - right;
            }

            return left;
        }

        private long Multiplicative()
        {
            var left = Unary();
            while (Peek() is '*' or '/' or '%')
            {
                var op = _text[_pos++];
                var right = Unary();
                if (op != '*' && right == 0)
                {
                    throw new FormatException("arithmetic expression: division by zero");
                }

                left = op switch
                {
                    '*' => left * right,
                    '/' => left / right,
                    _ => left % right
                };
            }

            return left;
        }

        private long Unary()
        {
            switch (Peek())
            {
                case '-':
                    _pos++;
                    return -Unary();
                case '+':
                    _pos++;
                    return Unary();
                case '!':
                    _pos++;
                    return Unary() == 0 ? 1 : 0;
                case '(':
                {
                    _pos++;
                    var value = Comparison();
                    if (Peek() != ')')
                    {
                        throw new FormatException("arithmetic expression: missing )");
                    }

                    _pos++;
                    return value;
                }
            }

            Skip();
            var number = Regex.Match(_text[_pos..], "^[0-9]+");
            if (number.Success)
            {
                _pos += number.Length;
                return long.Parse(number.Value, CultureInfo.InvariantCulture);
            }

            var name = Regex.Match(_text[_pos..], "^[A-Za-z_][A-Za-z0-9_]*");
            if (name.Success)
            {
                _pos += name.Length;
                return long.TryParse(_lookup(name.Value), out var value) ? value : 0;
            }

            throw new FormatException($"arithmetic expression: expecting primary: \"{_text}\"");
        }

        private char Peek()
        {
            Skip();
            return _pos < _text.Length ? _text[_pos] : '\0';
        }

        private void Skip()
        {
            while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos]))
            {
                _pos++;
            }
        }
    }
}
