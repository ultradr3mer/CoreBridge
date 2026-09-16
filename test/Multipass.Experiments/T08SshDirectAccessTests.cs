using System.Diagnostics;
using CoreBridge.Core.Vm;

namespace Multipass.Experiments;

[Collection("MultipassExperiments")]
public class T08SshDirectAccessTests : MultipassExperiment
{
    private const string Vm = "cbx-t08";
    private const string Image = "core26";
    private const string ProbeVm = "cbx-t03-b";
    private const string Marker = "cbx-t08-ssh-ok";
    private const int LaunchTimeoutMs = 2_400_000;

    [MultipassFact]
    public void SshDirektzugriffMitEigenemKey()
    {
        var names = MultipassVms.List().Select(v => v.Name).ToList();
        if (names.Contains(Vm))
        {
            ExperimentLog.Append(
                "T08",
                "Native SSH mit Provisioning-Key",
                $"launch --name {Vm} --cloud-init <yaml> {Image}",
                MultipassBinaryLocator.LocateOrNull(),
                new MultipassRunResult(0, string.Empty, string.Empty, 0),
                $"Launch übersprungen — VM existierte bereits: {Vm}");
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), "cbx-t08-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var keyPath = Path.Combine(directory, "cbx-t08-key");
            var keygen = new MultipassCli().Run(
                new[] { "-t", "ed25519", "-f", keyPath, "-N", string.Empty, "-C", "cbx-t08" },
                60_000,
                "ssh-keygen.exe");
            ExperimentLog.Append(
                "T08",
                "ssh-keygen — Schlüsselpair erzeugen",
                "ssh-keygen -t ed25519 -f <temp> -N \"\" -C cbx-t08",
                "ssh-keygen.exe",
                keygen,
                $"Dauer: {keygen.DurationMs} ms");
            Assert.True(keygen.ExitCode == 0 && File.Exists(keyPath + ".pub"), $"ssh-keygen scheiterte: {keygen.StdErr.Trim()}");

            var cli = new MultipassCli();
            var userProbe = cli.Run(
                new[] { "exec", ProbeVm, "--", "/bin/sh", "-c", "id -un 2>/dev/null || whoami 2>/dev/null || echo unbekannt" },
                60_000);
            ExperimentLog.Append(
                "T08",
                "Default-User ermitteln",
                $"exec {ProbeVm} -- /bin/sh -c 'id -un || whoami'",
                MultipassBinaryLocator.LocateOrNull(),
                userProbe,
                $"Dauer: {userProbe.DurationMs} ms");
            var user = userProbe.StdOut.Trim().Split('\n').Last().Trim();
            if (string.IsNullOrWhiteSpace(user) || user == "unbekannt")
            {
                user = "ubuntu";
            }

            var publicKey = File.ReadAllText(keyPath + ".pub").Trim();
            var yamlPath = Path.Combine(directory, "cbx-t08-cloud-init.yaml");
            File.WriteAllText(
                yamlPath,
                "#cloud-config\n"
                + "ssh_authorized_keys:\n"
                + $"  - {publicKey}\n");

            var launch = RunExperiment(
                "T08",
                "`launch --cloud-init` — SSH-Key für Default-User",
                new[] { "launch", "--name", Vm, "--cloud-init", yamlPath, Image },
                LaunchTimeoutMs,
                r => $"Dauer: {r.DurationMs} ms — Ziel-User: {user}");
            Assert.Equal(0, launch.ExitCode);

            string? ip = null;
            for (var attempt = 1; attempt <= 6 && ip is null; attempt++)
            {
                Thread.Sleep(10_000);
                ip = MultipassVms.List().SingleOrDefault(v => v.Name == Vm)?.Ipv4.FirstOrDefault(a => a.Length > 0);
            }

            Assert.False(string.IsNullOrWhiteSpace(ip), $"VM {Vm} hat keine IPv4 bekommen");

            var (found, waitMs, detail) = TrySsh(keyPath, user, ip!);
            ExperimentLog.Append(
                "T08",
                "Native SSH mit Provisioning-Key",
                $"ssh -i <key> -o BatchMode=yes {user}@{ip} echo {Marker}",
                "ssh.exe",
                new MultipassRunResult(found ? 0 : 1, found ? Marker : string.Empty, found ? string.Empty : detail, waitMs),
                found
                    ? $"Natives SSH funktioniert — Erfolg nach {waitMs} ms als {user}@{ip}."
                    : $"Natives SSH NICHT erfolgreich ({waitMs} ms gewartet) — siehe StdErr.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static (bool Found, long WaitMs, string Detail) TrySsh(string keyPath, string user, string ip)
    {
        var stopwatch = Stopwatch.StartNew();
        string detail = string.Empty;
        for (var attempt = 1; attempt <= 12; attempt++)
        {
            Thread.Sleep(10_000);
            var ssh = new MultipassCli().Run(
                new[]
                {
                    "-i", keyPath,
                    "-o", "StrictHostKeyChecking=no",
                    "-o", "UserKnownHostsFile=NUL",
                    "-o", "IdentitiesOnly=yes",
                    "-o", "BatchMode=yes",
                    "-o", "ConnectTimeout=10",
                    $"{user}@{ip}",
                    $"echo {Marker}"
                },
                30_000,
                "ssh.exe");
            if (ssh.ExitCode == 0 && ssh.StdOut.Contains(Marker))
            {
                return (true, stopwatch.ElapsedMilliseconds, ssh.StdOut.Trim());
            }

            detail = ssh.StdErr.Trim() + " / " + ssh.StdOut.Trim();
        }

        return (false, stopwatch.ElapsedMilliseconds, detail);
    }
}
