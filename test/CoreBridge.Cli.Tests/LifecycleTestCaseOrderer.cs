using Xunit.Abstractions;
using Xunit.Sdk;

namespace CoreBridge.Cli.Tests;

/// <summary>
/// Reihenfolge der nummerierten Lifecycle-Stufen ist Teil des Testvertrags.
/// </summary>
public sealed class LifecycleTestCaseOrderer : ITestCaseOrderer
{
    public IEnumerable<TTestCase> OrderTestCases<TTestCase>(IEnumerable<TTestCase> testCases)
        where TTestCase : ITestCase
    {
        return testCases.OrderBy(testCase => testCase.TestMethod.Method.Name, StringComparer.Ordinal);
    }
}
