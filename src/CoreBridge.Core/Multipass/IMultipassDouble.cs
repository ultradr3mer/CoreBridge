namespace CoreBridge.Core.Multipass;

/// <summary>
/// Markiert einen <see cref="IMultipassCli"/>, der kein echtes Multipass aufruft
/// (<see cref="ReplayMultipassCli"/>, <see cref="SimulatedMultipassCli"/>).
/// Solche Clients werden nicht protokolliert — ihre Antworten sind keine
/// Aufnahme echten Verhaltens — und <c>multipass-status</c> meldet
/// <see cref="BinaryLabel"/> statt eines Binary-Pfads.
/// </summary>
public interface IMultipassDouble
{
    /// <summary>Pseudo-Pfad für <c>multipass-status</c>, z. B. <c>simulation://multipass</c>.</summary>
    string BinaryLabel { get; }
}
