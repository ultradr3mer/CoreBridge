using CoreBridge.Core.Multipass;
using CoreBridge.Core.Vm;

namespace CoreBridge.Core.Tests;

public class MultipassVmListTests
{
    [Fact]
    public void ParseLiestNamenStateUndIpv4()
    {
        const string json = """
            {
              "list": [
                { "name": "cbx-t03-a", "state": "Deleted", "ipv4": [ ], "release": "Core 26", "os": "Ubuntu" },
                { "name": "cbx-t08", "state": "Running", "ipv4": [ "172.30.85.131", "172.30.91.14" ], "release": "Core 26" }
              ]
            }
            """;

        var vms = MultipassVmList.Parse(json);

        Assert.Equal(2, vms.Count);
        Assert.Equal("cbx-t03-a", vms[0].Name);
        Assert.Equal("Deleted", vms[0].State);
        Assert.True(vms[0].IsDeleted);
        Assert.Empty(vms[0].Ipv4);
        Assert.Equal("cbx-t08", vms[1].Name);
        Assert.False(vms[1].IsDeleted);
        Assert.Equal(new[] { "172.30.85.131", "172.30.91.14" }, vms[1].Ipv4);
    }

    [Fact]
    public void ParseOhneListeLiefertLeereMenge()
    {
        Assert.Empty(MultipassVmList.Parse("""{ "errors": [ ], "list": [ ] }"""));
        Assert.Empty(MultipassVmList.Parse("""{ "unbekannt": 1 }"""));
    }

    [Fact]
    public void ParseAkzeptiertIpv4AlsEinfachenString()
    {
        const string json = """
            { "list": [ { "name": "x", "state": "Running", "ipv4": "10.0.0.5" } ] }
            """;

        var vms = MultipassVmList.Parse(json);

        Assert.Single(vms);
        Assert.Equal("10.0.0.5", Assert.Single(vms[0].Ipv4));
    }

    [Theory]
    [InlineData("Running", "N/A", true)]
    [InlineData("Running", "", true)]
    [InlineData("Running", "10.0.0.5", false)]
    [InlineData("Starting", "N/A", true)]
    [InlineData("Unknown", "", true)]
    [InlineData("Stopped", "N/A", false)]
    public void NeedsForceStop_UebergangOderLaufendOhneIpv4(string state, string ipv4, bool expected)
    {
        var vm = new VmInfo("x", state, ipv4.Length == 0 ? [] : [ipv4]);

        Assert.Equal(expected, vm.NeedsForceStop);
        Assert.Equal(state == "Running" && expected, vm.IsUnreachable);
    }
}
