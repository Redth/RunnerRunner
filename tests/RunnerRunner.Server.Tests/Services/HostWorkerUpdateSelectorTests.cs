using RunnerRunner.Core.Models;
using RunnerRunner.Server.Services.HostWorkers;
using Host = RunnerRunner.Core.Models.Host;

namespace RunnerRunner.Server.Tests.Services;

public class HostWorkerUpdateSelectorTests
{
    [Fact]
    public void TrySelectAsset_SelectsMacArm64Asset()
    {
        var release = new HostWorkerReleaseInfo(
            "v1.2.3",
            null,
            DateTimeOffset.UnixEpoch,
            [
                new HostWorkerReleaseAsset("runnerrunner-hostworker-osx-arm64.tar.gz", "https://example.test/osx", "abc"),
                new HostWorkerReleaseAsset("runnerrunner-hostworker-win-x64.zip", "https://example.test/win", "def")
            ]);
        var host = new Host
        {
            Name = "mac",
            Platform = HostPlatform.MacOS,
            Architecture = "Arm64"
        };

        var selected = HostWorkerUpdateSelector.TrySelectAsset(host, release, out var asset, out var reason);

        Assert.True(selected);
        Assert.Null(reason);
        Assert.Equal("runnerrunner-hostworker-osx-arm64.tar.gz", asset?.Name);
    }

    [Theory]
    [InlineData("v1.2.2", "v1.2.3", true)]
    [InlineData("1.2.3", "v1.2.3", false)]
    [InlineData("1.2.3+build.5", "v1.2.3", false)]
    [InlineData(null, "v1.2.3", true)]
    public void IsUpdateAvailable_NormalizesReleaseTags(string? current, string latest, bool expected)
        => Assert.Equal(expected, HostWorkerUpdateSelector.IsUpdateAvailable(current, latest));

    [Fact]
    public void TrySelectAsset_RequiresChecksum()
    {
        var release = new HostWorkerReleaseInfo(
            "v1.2.3",
            null,
            DateTimeOffset.UnixEpoch,
            [new HostWorkerReleaseAsset("runnerrunner-hostworker-linux-x64.tar.gz", "https://example.test/linux", "")]);
        var host = new Host
        {
            Name = "linux",
            Platform = HostPlatform.Linux,
            Architecture = "x64"
        };

        var selected = HostWorkerUpdateSelector.TrySelectAsset(host, release, out _, out var reason);

        Assert.False(selected);
        Assert.Contains("checksum", reason);
    }
}
