using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using RunnerRunner.Server.Services.Admission;
using RunnerRunner.Core.Hub;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Services;
using RunnerRunner.Server.Tests.TestSupport;

namespace RunnerRunner.Server.Tests.Services;

public class ReconciliationServiceTests
{
    [Fact]
    public async Task SnapshotOmissionRetainsReservationAndDoesNotDeleteUnknownNativeResources()
    {
        var store = TestDocumentStore.Create();
        await store.Insert(new RunnerRunner.Core.Models.Host { Id = "host-1", Name = "host-1" });
        await store.Insert(new RunnerInstance
        {
            Id = "owned", HostId = "host-1", ProfileId = "profile-1", RunnerName = "owned-runner",
            ContainerId = "owned-container", Status = RunnerInstanceStatus.Running, StartedAt = DateTime.UtcNow.AddHours(-1)
        });
        var admission = Substitute.For<IResourceAdmission>();
        admission.GetReservationAsync("owned", Arg.Any<CancellationToken>()).Returns(
            new ResourceReservation("owned", "host-1", ExecutionBackend.Docker, new(2,4096,64), "owned-container", false));
        var commands = new RecordingHostCommandDispatcher();
        using var services = new ServiceCollection().AddSingleton(store).AddSingleton(admission)
            .AddSingleton(new RunnerRegistrationCleanupService([], admission, NullLogger<RunnerRegistrationCleanupService>.Instance))
            .BuildServiceProvider();
        using var service = new ReconciliationService(NullLogger<ReconciliationService>.Instance, services, commands);
        await service.ProcessReconciliationAsync(new ReconciliationReport
        {
            HostId = "host-1", Runners = [new DiscoveredRunnerInfo
            {
                InstanceId = "unknown", RunnerName = "rr-unknown", VmName = "rr-unknown", Backend = ExecutionBackend.Tart, IsRunning = true
            }]
        });
        var kept = await store.Get<RunnerInstance>("owned");
        Assert.NotNull(kept);
        Assert.Equal(RunnerInstanceStatus.Stopping, kept.Status);
        Assert.Null(kept.StoppedAt);
        Assert.Equal("owned-container", kept.ContainerId);
        Assert.Empty(commands.Commands);
    }

    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void IsWithinDeployGracePeriod_PendingInstanceUnderTwoMinutes_IsWithinGrace()
    {
        var referenceTime = Now.AddSeconds(-90);

        Assert.True(ReconciliationService.IsWithinDeployGracePeriod(RunnerInstanceStatus.Pending, referenceTime, Now));
    }

    [Fact]
    public void IsWithinDeployGracePeriod_PendingInstanceOverTwoMinutes_IsNotWithinGrace()
    {
        var referenceTime = Now.AddMinutes(-3);

        Assert.False(ReconciliationService.IsWithinDeployGracePeriod(RunnerInstanceStatus.Pending, referenceTime, Now));
    }

    [Fact]
    public void IsWithinDeployGracePeriod_StartingInstanceUnderFiveMinutes_IsWithinGrace()
    {
        // Regression: a native macOS deploy was observed live taking ~3.5 minutes under host
        // load. A Starting instance at that age must still be within grace, or reconciliation
        // spawns a duplicate runner for a deploy that was never actually dead.
        var referenceTime = Now.AddMinutes(-3.5);

        Assert.True(ReconciliationService.IsWithinDeployGracePeriod(RunnerInstanceStatus.Starting, referenceTime, Now));
    }

    [Fact]
    public void IsWithinDeployGracePeriod_StartingInstanceOverFiveMinutes_IsNotWithinGrace()
    {
        var referenceTime = Now.AddMinutes(-6);

        Assert.False(ReconciliationService.IsWithinDeployGracePeriod(RunnerInstanceStatus.Starting, referenceTime, Now));
    }

    [Fact]
    public void IsWithinDeployGracePeriod_RunningInstance_NeverWithinGrace()
    {
        // A runner that was confirmed Running and then vanished from the host report has
        // genuinely crashed — no grace period should apply, regardless of age.
        var referenceTime = Now.AddSeconds(-1);

        Assert.False(ReconciliationService.IsWithinDeployGracePeriod(RunnerInstanceStatus.Running, referenceTime, Now));
    }

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
    public void TryPrepareDynamicWebhookRetry_RequeuesProvisionedQueuedEvent()
    {
        var clock = new TestClock();
        var scenario = new RunnerScenarioBuilder(clock)
            .WithIds("dynamic-retry")
            .WithDynamicWebhook(jobId: "job-3")
            .Build();

        var changed = ReconciliationService.TryPrepareDynamicWebhookRetry(
            scenario.Instance,
            scenario.WebhookEvent,
            clock.UtcNow,
            "Runner exited on the host before the queued job started");

        Assert.True(changed);
        Assert.Equal("pending", scenario.WebhookEvent.Status);
        Assert.Null(scenario.WebhookEvent.InstanceId);
    }

    [Fact]
    public void TryPrepareDynamicWebhookRetry_IgnoresStaticRunnerEvenWithLinkedQueuedEvent()
    {
        var clock = new TestClock();
        var scenario = new RunnerScenarioBuilder(clock)
            .WithIds("static-retry")
            .WithDynamicWebhook(jobId: "job-static")
            .Build();
        scenario.Instance.ProvisioningMode = "static";

        var changed = ReconciliationService.TryPrepareDynamicWebhookRetry(
            scenario.Instance,
            scenario.WebhookEvent,
            clock.UtcNow,
            "Static runner should not recycle webhook work");

        Assert.False(changed);
        Assert.Equal("provisioned", scenario.WebhookEvent.Status);
        Assert.Equal(scenario.Instance.Id, scenario.WebhookEvent.InstanceId);
    }
}
