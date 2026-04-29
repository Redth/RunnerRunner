using RunnerRunner.Core.Hub;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Services;

namespace RunnerRunner.Server.Tests.Services;

public class ReconciliationServiceTests
{
    [Fact]
    public void MatchesRunner_ReturnsTrue_ForVmNameMatch()
    {
        var instance = new RunnerInstance
        {
            Id = "inst-1",
            HostId = "host-1",
            ProfileId = "profile-1",
            RunnerName = "macos-test-jit-0a79fbcf",
            VmName = "rr-macos-test-jit-0a79fbcf"
        };

        var discovered = new DiscoveredRunnerInfo
        {
            RunnerName = "macos-test-jit-0a79fbcf",
            VmName = "rr-macos-test-jit-0a79fbcf",
            Backend = ExecutionBackend.Tart,
            Status = "stopped"
        };

        Assert.True(ReconciliationService.MatchesRunner(instance, discovered));
    }

    [Fact]
    public void MatchesRunner_FallsBackToRunnerName()
    {
        var instance = new RunnerInstance
        {
            Id = "inst-2",
            HostId = "host-1",
            ProfileId = "profile-1",
            RunnerName = "macos-test-jit-32c9f714"
        };

        var discovered = new DiscoveredRunnerInfo
        {
            RunnerName = "macos-test-jit-32c9f714",
            Backend = ExecutionBackend.Tart,
            Status = "stopped"
        };

        Assert.True(ReconciliationService.MatchesRunner(instance, discovered));
    }

    [Fact]
    public void IsRunnerStillActive_ReturnsFalse_ForExitedNativeRunner()
    {
        var discovered = new DiscoveredRunnerInfo
        {
            RunnerName = "MacOS-Native-jit-e98a2f6f",
            ProcessId = 12345,
            Backend = ExecutionBackend.Native,
            IsRunning = false,
            Status = "exited"
        };

        Assert.False(ReconciliationService.IsRunnerStillActive(discovered));
    }

    [Fact]
    public void ResolveProfileForOrphan_MatchesByJitNamePrefix()
    {
        var profiles = new List<RunnerProfile>
        {
            new() { Id = "p1", Name = "MacOS-Native" },
            new() { Id = "p2", Name = "Ailoha-ReactNative-Linux" },
        };

        var discovered = new DiscoveredRunnerInfo
        {
            RunnerName = "MacOS-Native-jit-5494609f",
            Backend = ExecutionBackend.Native,
            IsRunning = true,
            Status = "running"
        };

        var profile = ReconciliationService.ResolveProfileForOrphan(discovered, profiles);
        Assert.NotNull(profile);
        Assert.Equal("p1", profile!.Id);
    }

    [Fact]
    public void ResolveProfileForOrphan_ReturnsNull_WhenNameDoesNotContainJitMarker()
    {
        var profiles = new List<RunnerProfile> { new() { Id = "p1", Name = "MacOS-Native" } };
        var discovered = new DiscoveredRunnerInfo
        {
            RunnerName = "manually-named-runner",
            Backend = ExecutionBackend.Native,
            Status = "running"
        };

        Assert.Null(ReconciliationService.ResolveProfileForOrphan(discovered, profiles));
    }

    [Fact]
    public void ResolveProfileForOrphan_ReturnsNull_WhenProfileMissing()
    {
        var profiles = new List<RunnerProfile> { new() { Id = "p1", Name = "OtherProfile" } };
        var discovered = new DiscoveredRunnerInfo
        {
            RunnerName = "DeletedProfile-jit-abc12345",
            Backend = ExecutionBackend.Native,
            Status = "running"
        };

        Assert.Null(ReconciliationService.ResolveProfileForOrphan(discovered, profiles));
    }

    [Fact]
    public void TryPrepareDynamicWebhookRetry_RequeuesProvisionedQueuedEvent()
    {
        var instance = new RunnerInstance
        {
            Id = "inst-3",
            HostId = "host-1",
            ProfileId = "profile-1",
            RunnerName = "MAUI-Linux-jit-f28ae31b",
            ProvisioningMode = "dynamic"
        };

        var now = DateTime.UtcNow;
        var linkedEvent = new WebhookEvent
        {
            Id = "evt-3",
            Action = "queued",
            Status = "provisioned",
            InstanceId = instance.Id,
            ReceivedAt = now.AddMinutes(-1)
        };

        var changed = ReconciliationService.TryPrepareDynamicWebhookRetry(
            instance,
            linkedEvent,
            now,
            "Runner exited on the host before the queued job started");

        Assert.True(changed);
        Assert.Equal("pending", linkedEvent.Status);
        Assert.Null(linkedEvent.InstanceId);
    }
}
