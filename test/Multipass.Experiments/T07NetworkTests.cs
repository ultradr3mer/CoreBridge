using CoreBridge.Core.Multipass;
using CoreBridge.Core.Vm;

namespace Multipass.Experiments;

[Collection("MultipassExperiments")]
public class T07NetworkTests : MultipassExperiment
{
    private const string Vm = "cbx-t03-b";

    [MultipassFact]
    public void VmHatIpGatewayUndInternetzugang()
    {
        var vm = MultipassVms.List().SingleOrDefault(v => v.Name == Vm);
        Assert.NotNull(vm);
        Assert.False(vm.Ipv4.Count == 0, $"VM {Vm} hat keine IPv4 (multipass list)");
        ExperimentLog.Append(
            "T07",
            "IP aus `list`",
            "list --format json (Auszug)",
            MultipassBinaryLocator.LocateOrNull(),
            new MultipassRunResult(0, $"ipv4: {string.Join(", ", vm.Ipv4)}; state: {vm.State}", string.Empty, 0),
            $"IPv4: [{string.Join(", ", vm.Ipv4)}], State: {vm.State ?? "unbekannt"}");

        var route = RunExperiment(
            "T07",
            "Routing — /proc/net/route",
            new[] { "exec", Vm, "--", "/bin/sh", "-c", "cat /proc/net/route" },
            60_000,
            null);
        Assert.Equal(0, route.ExitCode);
        var gateway = ParseGateway(route.StdOut);

        RunExperiment(
            "T07",
            "DNS — /etc/resolv.conf",
            new[] { "exec", Vm, "--", "/bin/sh", "-c", "cat /etc/resolv.conf" },
            60_000,
            null);

        var tools = RunExperiment(
            "T07",
            "Verfügbare Netzwerk-Tools",
            new[] { "exec", Vm, "--", "/bin/sh", "-c", "for t in ping ping4 curl wget nc; do command -v $t >/dev/null 2>&1 && echo $t=ja || echo $t=nein; done" },
            60_000,
            r => $"Gateway: {gateway ?? "unbekannt"} — Dauer: {r.DurationMs} ms");
        Assert.Equal(0, tools.ExitCode);

        if (gateway is not null && tools.StdOut.Contains("ping=ja", StringComparison.Ordinal))
        {
            RunExperiment(
                "T07",
                $"Gateway-Ping ({gateway})",
                new[] { "exec", Vm, "--", "/bin/sh", "-c", $"ping -c 2 {gateway}" },
                60_000,
                null);
        }

        if (tools.StdOut.Contains("wget=ja", StringComparison.Ordinal))
        {
            RunExperiment(
                "T07",
                "Internet — wget example.com",
                new[] { "exec", Vm, "--", "/bin/sh", "-c", "wget -T 10 -O - http://example.com" },
                90_000,
                null);
        }
        else if (tools.StdOut.Contains("curl=ja", StringComparison.Ordinal))
        {
            RunExperiment(
                "T07",
                "Internet — curl example.com",
                new[] { "exec", Vm, "--", "/bin/sh", "-c", "curl -m 10 http://example.com" },
                90_000,
                null);
        }

        if (tools.StdOut.Contains("nc=ja", StringComparison.Ordinal))
        {
            RunExperiment(
                "T07",
                "Internet — TCP-Check via nc (example.com:80)",
                new[] { "exec", Vm, "--", "/bin/sh", "-c", "nc -w 5 example.com 80 </dev/null >/dev/null 2>&1 && echo tcp-ok || echo tcp-fail" },
                90_000,
                null);
        }
    }

    internal static string? ParseGateway(string routeOutput)
    {
        foreach (var line in routeOutput.Split('\n'))
        {
            var columns = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length > 2 && columns[1] == "00000000" && columns[2] != "00000000")
            {
                var hex = columns[2].PadLeft(8, '0');
                var bytes = new byte[4];
                for (var i = 0; i < 4; i++)
                {
                    bytes[i] = Convert.ToByte(hex.Substring((3 - i) * 2, 2), 16);
                }

                return string.Join(".", bytes);
            }
        }

        return null;
    }
}
