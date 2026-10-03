namespace CoreBridge.Cli.Tests;

/// <summary>
/// Echter und simulierte Lifecycles in einer Sammlung, damit sie nie parallel laufen:
/// <see cref="Program.Run"/> setzt den statischen <c>InitStore</c> auf den Store des Aufrufs.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CliLifecycleCollection : ICollectionFixture<CliLifecycleFixture>, ICollectionFixture<SimulatedCliLifecycleFixture>, ICollectionFixture<SimulatedBinaryCliLifecycleFixture>
{
    public const string Name = "CLI integration";
}
