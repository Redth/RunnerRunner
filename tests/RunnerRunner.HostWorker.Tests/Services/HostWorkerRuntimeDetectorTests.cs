using RunnerRunner.HostWorker.Services;

namespace RunnerRunner.HostWorker.Tests.Services;

public class HostWorkerRuntimeDetectorTests
{
    [Fact]
    public void IsContainer_DetectsDotnetContainerEnvironment()
    {
        var detected = HostWorkerRuntimeDetector.IsContainer(
            key => key == "DOTNET_RUNNING_IN_CONTAINER" ? "true" : null,
            _ => false);

        Assert.True(detected);
    }

    [Fact]
    public void TryDetectContainerId_UsesDockerHostname()
    {
        var detected = HostWorkerRuntimeDetector.TryDetectContainerId(
            key => key == "HOSTNAME" ? "abcdef123456" : null,
            () => null,
            "host-machine");

        Assert.Equal("abcdef123456", detected);
    }

    [Fact]
    public void TryDetectContainerId_UsesCgroupContainerId()
    {
        const string containerId = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        var detected = HostWorkerRuntimeDetector.TryDetectContainerId(
            _ => null,
            () => $"0::/docker/{containerId}",
            "host-machine");

        Assert.Equal(containerId, detected);
    }
}
