using RunnerRunner.Server.Services.HostWorkers;
using Host = RunnerRunner.Core.Models.Host;

namespace RunnerRunner.Server.Tests.Services;

public class HostConnectionMetadataTests
{
    [Theory]
    [InlineData("ipv4:192.168.1.21:53100", "192.168.1.21")]
    [InlineData("ipv6:[::ffff:192.168.1.22]:53100", "192.168.1.22")]
    [InlineData("10.0.0.8:443", "10.0.0.8")]
    [InlineData("10.0.0.9", "10.0.0.9")]
    public void ExtractPeerAddress_ParsesGrpcPeerFormats(string peer, string expected)
    {
        var parsed = HostConnectionMetadata.ExtractPeerAddress(peer);

        Assert.Equal(expected, parsed);
    }

    [Fact]
    public void ResolveSshTargetAddress_PrefersPersistedTargetThenRemoteThenReported()
    {
        var host = new Host
        {
            Name = "mac-mini",
            LastRemoteIpAddress = "192.168.1.21",
            ReportedIpAddresses = ["10.0.0.5"]
        };

        Assert.Equal("192.168.1.21", HostConnectionMetadata.ResolveSshTargetAddress(host));

        host.SshTargetAddress = "mac-mini.local";

        Assert.Equal("mac-mini.local", HostConnectionMetadata.ResolveSshTargetAddress(host));
    }

    [Fact]
    public void Apply_PreservesExistingAddressesWhenConnectionDoesNotReportAny()
    {
        var host = new Host
        {
            Name = "mac-mini",
            LastRemoteEndpoint = "ipv4:192.168.1.21:5000",
            LastRemoteIpAddress = "192.168.1.21",
            ReportedIpAddresses = ["10.0.0.5"]
        };

        HostConnectionMetadata.Apply(host, null, null);

        Assert.Equal("ipv4:192.168.1.21:5000", host.LastRemoteEndpoint);
        Assert.Equal("192.168.1.21", host.LastRemoteIpAddress);
        Assert.Equal(["10.0.0.5"], host.ReportedIpAddresses);
    }
}
