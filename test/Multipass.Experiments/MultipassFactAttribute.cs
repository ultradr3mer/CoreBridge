using CoreBridge.Core.Multipass;
using CoreBridge.Core.Vm;

namespace Multipass.Experiments;

public sealed class MultipassFactAttribute : FactAttribute
{
    public MultipassFactAttribute()
    {
        if (!MultipassBinaryLocator.IsInstalled())
        {
            Skip = "Multipass ist nicht installiert (Env CORE_BRIDGE_MULTIPASS_EXE, PATH und Standardpfad geprüft).";
        }
    }
}
