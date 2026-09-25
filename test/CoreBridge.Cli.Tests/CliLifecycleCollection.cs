namespace CoreBridge.Cli.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CliLifecycleCollection : ICollectionFixture<CliLifecycleFixture>
{
    public const string Name = "CLI integration";
}
