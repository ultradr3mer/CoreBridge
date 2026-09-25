using System.Text.RegularExpressions;

namespace CoreBridge.Core.Vm;

/// <summary>
/// Entfernt das Log-Rauschen der gRPC/absl-Bibliotheken aus multipass-stderr
/// (z. B. "WARNING: All log messages before absl::InitializeLog() ..." und
/// "E0000 00:00:... init.cc:229] ..."), damit nur die eigentliche Meldung übrig bleibt.
/// </summary>
public static partial class MultipassStdErr
{
    public static string Clean(string stdErr)
    {
        var lines = stdErr.Replace("\r\n", "\n")
            .Split('\n')
            .Where(line => !string.IsNullOrWhiteSpace(line) && !NoiseLine().IsMatch(line));
        return string.Join(Environment.NewLine, lines).Trim();
    }

    [GeneratedRegex(@"^(WARNING: All log messages before absl::InitializeLog\(\)|[EWIF]\d{4} [\d:.]+\s+\d+ \S+:\d+\])")]
    private static partial Regex NoiseLine();
}
