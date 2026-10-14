using Docker.DotNet.Models;
using RunnerRunner.HostWorker.Services;

namespace RunnerRunner.HostWorker.Tests.Services;

public class HostWorkerSelfUpdaterTests
{
    [Theory]
    [InlineData("https://ci.example.test/api/hostworker-updates/github-artifacts/1/assets/worker.tar.gz?sha256=abc", true)]
    [InlineData("https://ci.example.test/prefix/api/hostworker-updates/artifacts/local/worker.tar.gz?sha256=abc", true)]
    [InlineData("https://github.com/redth/RunnerRunner/releases/download/v1/worker.tar.gz", false)]
    [InlineData("not a url", false)]
    public void IsRunnerRunnerUpdateAssetUrl_MatchesOnlyHostedUpdateEndpoints(string url, bool expected)
        => Assert.Equal(expected, HostWorkerSelfUpdater.IsRunnerRunnerUpdateAssetUrl(url));

    [Theory]
    [InlineData("bridge")]
    [InlineData("default")]
    public void SplitNetworks_SeparatesNetworksConnectedAfterCreation(string networkMode)
    {
        var container = CreateContainer(networkMode, "bridge", "runnerrunner_default");

        var (primary, additional) = HostWorkerSelfUpdater.SplitNetworks(container);

        Assert.Equal("bridge", primary?.Key);
        Assert.Equal(["runnerrunner_default"], additional.Select(n => n.Key));
    }

    [Fact]
    public void SplitNetworks_ForComposeContainer_UsesTheComposeNetworkAsPrimary()
    {
        var container = CreateContainer("runnerrunner_default", "runnerrunner_default", "monitoring");

        var (primary, additional) = HostWorkerSelfUpdater.SplitNetworks(container);

        Assert.Equal("runnerrunner_default", primary?.Key);
        Assert.Equal(["monitoring"], additional.Select(n => n.Key));
    }

    [Fact]
    public void SplitNetworks_WhenNetworkModeIsANetworkId_MatchesThePrimaryById()
    {
        var container = CreateContainer("bbb0868d9a3f", "runnerrunner_default", "bridge");
        container.NetworkSettings.Networks["runnerrunner_default"].NetworkID = "bbb0868d9a3f0123456789";

        var (primary, additional) = HostWorkerSelfUpdater.SplitNetworks(container);

        Assert.Equal("runnerrunner_default", primary?.Key);
        Assert.Equal(["bridge"], additional.Select(n => n.Key));
    }

    [Theory]
    [InlineData("host")]
    [InlineData("none")]
    [InlineData("container:abc123")]
    public void SplitNetworks_ForExclusiveNetworkModes_ReturnsNothing(string networkMode)
    {
        var container = CreateContainer(networkMode, networkMode, "runnerrunner_default");

        var (primary, additional) = HostWorkerSelfUpdater.SplitNetworks(container);

        Assert.Null(primary);
        Assert.Empty(additional);
    }

    [Fact]
    public void FilterNetworkAliases_DropsOnlyTheOldContainersShortId()
    {
        var aliases = HostWorkerSelfUpdater.FilterNetworkAliases(
            "runnerrunner_default", ["host-worker", "0d21f7e1ddfd", "0d"], "0d21f7e1ddfdb0a09674ab4222410232");

        Assert.Equal(["host-worker", "0d"], aliases);
    }

    [Fact]
    public void FilterNetworkAliases_WhenOnlyTheShortIdRemains_ReturnsNull()
        => Assert.Null(HostWorkerSelfUpdater.FilterNetworkAliases("runnerrunner_default", ["0d21f7e1ddfd"], "0d21f7e1ddfdb0a09674ab4222410232"));

    [Fact]
    public void FilterNetworkAliases_OnDefaultBridge_ReturnsNull()
        => Assert.Null(HostWorkerSelfUpdater.FilterNetworkAliases("bridge", ["host-worker"], "0d21f7e1ddfdb0a09674ab4222410232"));

    [Theory]
    [InlineData("runnerrunner-host-worker", "runnerrunner-host-worker")]
    [InlineData("runnerrunner-host-worker-replaced", "runnerrunner-host-worker")]
    [InlineData(null, "runnerrunner-host-worker")]
    [InlineData("-replaced", "-replaced")]
    public void ResolveCanonicalContainerName_StripsTheRetiredSuffix(string? currentName, string expected)
        => Assert.Equal(expected, HostWorkerSelfUpdater.ResolveCanonicalContainerName(currentName));

    [Fact]
    public void BuildRetiredContainerName_IsDeterministicAndWithinDockerNameLimit()
    {
        Assert.Equal("runnerrunner-host-worker-replaced", HostWorkerSelfUpdater.BuildRetiredContainerName("runnerrunner-host-worker"));
        Assert.True(HostWorkerSelfUpdater.BuildRetiredContainerName(new string('a', 80)).Length <= 63);
    }

    private static ContainerInspectResponse CreateContainer(string networkMode, params string[] networks) => new()
    {
        ID = "0d21f7e1ddfdb0a09674ab4222410232",
        HostConfig = new HostConfig { NetworkMode = networkMode },
        NetworkSettings = new NetworkSettings
        {
            Networks = networks.ToDictionary(n => n, _ => new EndpointSettings())
        }
    };
}
