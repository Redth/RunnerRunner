using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;

namespace RunnerRunner.HostWorker.Tests;

public class HostWorkerIdentityResolverTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyIdentityValues_UseDefaults(string? configuredValue)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HostWorker:HostId"] = configuredValue,
                ["HostWorker:HostName"] = configuredValue,
                ["HostWorker:Architecture"] = configuredValue
            })
            .Build();

        var identity = HostWorkerIdentityResolver.Resolve(configuration);

        Assert.Equal(Environment.MachineName, identity.HostId);
        Assert.Equal(Environment.MachineName, identity.HostName);
        Assert.Equal(RuntimeInformation.OSArchitecture.ToString(), identity.Architecture);
    }

    [Fact]
    public void ConfiguredIdentityValues_AreUsed()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HostWorker:HostId"] = "host-1",
                ["HostWorker:HostName"] = "Linux worker",
                ["HostWorker:Architecture"] = "arm64"
            })
            .Build();

        var identity = HostWorkerIdentityResolver.Resolve(configuration);

        Assert.Equal("host-1", identity.HostId);
        Assert.Equal("Linux worker", identity.HostName);
        Assert.Equal("arm64", identity.Architecture);
    }
}
