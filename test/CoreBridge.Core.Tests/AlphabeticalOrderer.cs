using Xunit.Abstractions;
using Xunit.Sdk;

namespace CoreBridge.Core.Tests;

/// <summary>
/// Testfälle innerhalb einer Klasse alphabetisch nach Methodenname sortieren
/// (xUnit garantiert ohne Orderer keine Reihenfolge). Konvention: _0_, _1_, ...
/// </summary>
public sealed class AlphabeticalOrderer : ITestCaseOrderer
{
    public IEnumerable<TTestCase> OrderTestCases<TTestCase>(IEnumerable<TTestCase> testCases)
        where TTestCase : ITestCase
    {
        return testCases.OrderBy(testCase => testCase.TestMethod.Method.Name);
    }
}
