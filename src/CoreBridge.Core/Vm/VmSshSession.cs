using Renci.SshNet;
using Renci.SshNet.Common;

namespace CoreBridge.Core.Vm;

/// <summary>
/// VM-Session über natives SSH (SSH.NET): echte interaktive Shell mit PTY —
/// im Gegensatz zu <see cref="VmExecSession"/> mit gemeinsamem Shell-Status
/// (cwd, Umgebungsvariablen) und Prompt-Ausgabe. Verbindungsaufbau nur vom
/// Host zur VM (Plan §6: die VM baut nie selbst eine Verbindung auf).
///
/// Voraussetzung: SSH-Key auf der VM (cloud-init ssh_authorized_keys, siehe
/// EXPERIMENT_LOG T06) + Private Key auf dem Host. IP kommt aus
/// `multipass list` (erste IPv4).
/// </summary>
public sealed class VmSshSession : VmSession
{
  private readonly SshClient _client;
  private readonly ShellStream _shell;
  private readonly Task _pumpTask;
  private bool _disposed;

  private VmSshSession(SshClient client, ShellStream shell, Action<string>? log)
      : base(log)
  {
    _client = client;
    _shell = shell;
    _pumpTask = Task.Run(() => Pump(_shell));
  }

  /// <summary>
  /// Öffnet eine SSH-Session auf der VM. Ermittelt die IP über
  /// <c>vm list</c> (erste IPv4) und verbindet als <c>ubuntu</c> mit dem
  /// angegebenen Private Key (Standard: ~/.ssh/id_ed25519 bzw. id_rsa —
  /// derselbe Key, den cloud-init als authorized_key injiziert hat).
  /// </summary>
  public static VmSshSession Open(
      string vmName,
      Action<string>? log = null,
      string? privateKeyPath = null,
      string? exePath = null,
      int connectTimeoutMs = 30_000)
  {
    var vms = new VmService();
    var info = vms.List().FirstOrDefault(v => v.Name == vmName && !v.IsDeleted)
        ?? throw new MultipassException($"VM '{vmName}' existiert nicht.");
    var ip = info.Ipv4.FirstOrDefault()
        ?? throw new MultipassException($"VM '{vmName}' hat keine IPv4-Adresse (läuft sie?).");

    var keyPath = privateKeyPath ?? DefaultPrivateKey();
    if (!File.Exists(keyPath))
    {
      throw new MultipassException(
          $"SSH-Private-Key fehlt: {keyPath} — mit --private-key angeben oder Standard-Key erzeugen (cloud-init muss den zugehörigen Public Key als authorized_key kennen).");
    }

    var key = new PrivateKeyFile(keyPath);
    var connectionInfo = new ConnectionInfo(ip, "ubuntu", new PrivateKeyAuthenticationMethod("ubuntu", key))
    {
      Timeout = TimeSpan.FromMilliseconds(connectTimeoutMs)
    };

    var client = new SshClient(connectionInfo);
    try
    {
      client.Connect();
    }
    catch (Exception exception)
    {
      client.Dispose();
      throw new MultipassException($"SSH-Verbindung zu {vmName} ({ip}) fehlgeschlagen: {exception.Message}", exception);
    }

    try
    {
      // PTY anfordern (xterm) — ECHO aus, damit Befehle nicht doppelt erscheinen.
      var modes = new Dictionary<Renci.SshNet.Common.TerminalModes, uint> { { Renci.SshNet.Common.TerminalModes.ECHO, 0 } };
      var shell = client.CreateShellStream("xterm", 80, 24, 800, 600, 4096, modes);
      return new VmSshSession(client, shell, log);
    }
    catch (Exception exception)
    {
      client.Dispose();
      throw new MultipassException($"Shell-Stream auf {vmName} ({ip}) konnte nicht geöffnet werden: {exception.Message}", exception);
    }
  }

  private static string DefaultPrivateKey()
  {
    var sshDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
    var ed25519 = Path.Combine(sshDir, "id_ed25519");
    return File.Exists(ed25519) ? ed25519 : Path.Combine(sshDir, "id_rsa");
  }

  public override void Send(string input)
  {
    _shell.Write(input);
    _shell.Flush();
  }

  /// <summary>Shell sauber verlassen (exit) und Verbindung schließen.</summary>
  public override int Exit()
  {
    try
    {
      SendLine("exit");
    }
    catch (Exception)
    {
      // Shell evtl. schon zu — Rest übernimmt Dispose.
    }

    return 0;
  }

  public override void Dispose()
  {
    if (_disposed)
    {
      return;
    }

    _disposed = true;
    try
    {
      _shell.Dispose();
    }
    catch (Exception)
    {
    }

    _client.Dispose();
  }

  protected override bool HasExited => !_client.IsConnected;

  protected override int ExitCode => -1;

  private void Pump(ShellStream shell)
  {
    var buffer = new byte[4096];
    try
    {
      int read;
      while ((read = shell.Read(buffer, 0, buffer.Length)) > 0)
      {
        AppendOutput(System.Text.Encoding.UTF8.GetString(buffer, 0, read));
      }
    }
    catch (Exception)
    {
      // Stream endet mit der Session — kein Fehler.
    }
  }
}
