using RunnerRunner.Core.Models;
using RunnerRunner.HostWorker.Services;

namespace RunnerRunner.HostWorker.Tests.Services;

public class HostWorkerConnectionServiceTests
{
    [Fact]
    public void DetectCapabilities_AddsDockerWhenDockerHostIsConfigured()
    {
        var identity = new HostWorkerIdentity("windows-docker", "windows-docker", HostPlatform.Windows, "X64");

        var capabilities = HostWorkerConnectionService.DetectCapabilities(
            identity,
            "npipe://./pipe/docker_engine",
            _ => false,
            _ => false);

        Assert.Contains("native", capabilities);
        Assert.Contains("docker", capabilities);
    }

    [Fact]
    public void DetectCapabilities_DoesNotAddDockerWithoutDockerHint()
    {
        var identity = new HostWorkerIdentity("windows-native", "windows-native", HostPlatform.Windows, "X64");

        var capabilities = HostWorkerConnectionService.DetectCapabilities(
            identity,
            null,
            _ => false,
            _ => false);

        Assert.Contains("native", capabilities);
        Assert.DoesNotContain("docker", capabilities);
    }

    [Fact]
    public void DetectCapabilities_AddsTartOnlyForMacOSHosts()
    {
        var macIdentity = new HostWorkerIdentity("mac", "mac", HostPlatform.MacOS, "Arm64");
        var linuxIdentity = new HostWorkerIdentity("linux", "linux", HostPlatform.Linux, "X64");

        var macCapabilities = HostWorkerConnectionService.DetectCapabilities(
            macIdentity,
            null,
            _ => false,
            command => command == "tart");
        var linuxCapabilities = HostWorkerConnectionService.DetectCapabilities(
            linuxIdentity,
            null,
            _ => false,
            command => command == "tart");

        Assert.Contains("tart", macCapabilities);
        Assert.DoesNotContain("tart", linuxCapabilities);
    }
}
