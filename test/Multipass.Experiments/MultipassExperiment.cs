using CoreBridge.Core.Multipass;
using CoreBridge.Core.Vm;

namespace Multipass.Experiments;

public abstract class MultipassExperiment
{
    protected static MultipassRunResult RunExperiment(
        string id,
        string title,
        IReadOnlyList<string> args,
        int timeoutMs = 60_000,
        Func<MultipassRunResult, string?>? observe = null)
    {
        var result = new MultipassCli().Run(args, timeoutMs);
        ExperimentLog.Append(
            id,
            title,
            string.Join(' ', args),
            MultipassBinaryLocator.LocateOrNull(),
            result,
            observe?.Invoke(result));
        return result;
    }
}
