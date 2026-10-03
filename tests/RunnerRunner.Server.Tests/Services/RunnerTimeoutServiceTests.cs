using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RunnerRunner.Core.Hub;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Services;
using RunnerRunner.Server.Tests.TestSupport;
using Host = RunnerRunner.Core.Models.Host;

namespace RunnerRunner.Server.Tests.Services;

public class RunnerTimeoutServiceTests
{
    [Fact]
    public void PrepareLinkedEventRetry_ResetsProvisionedQueuedEvent()
    {
        var now = new TestClock().UtcNow;
        var evt = new WebhookEvent
        {
            Id = "evt-1",
            Action = "queued",
            Status = "provisioned",
            InstanceId = "inst-1",
            ReceivedAt = now.AddMinutes(-2),
            UpdatedAt = now.AddMinutes(-1)
        };

        var changed = RunnerTimeoutService.PrepareLinkedEventRetry(
            evt,
            now,
            "Runner came online but never picked up the queued job");

        Assert.True(changed);
        Assert.Equal("pending", evt.Status);
        Assert.Null(evt.InstanceId);
        Assert.Null(evt.ResolvedAt);
        Assert.NotNull(evt.NextRetryAt);
        Assert.Contains("never picked up", evt.Error);
    }

    [Fact]
    public void PrepareLinkedEventRetry_SkipsInProgressEvent()
    {
        var now = new TestClock().UtcNow;
        var evt = new WebhookEvent
        {
            Id = "evt-2",
            Action = "queued",
            Status = "in_progress",
            InstanceId = "inst-2",
            ReceivedAt = now.AddMinutes(-2),
            UpdatedAt = now.AddMinutes(-1)
        };

        var changed = RunnerTimeoutService.PrepareLinkedEventRetry(
            evt,
            now,
            "Runner failed");

        Assert.False(changed);
        Assert.Equal("in_progress", evt.Status);
        Assert.Equal("inst-2", evt.InstanceId);
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("timed_out")]
    [InlineData("rejected")]
    [InlineData("ignored")]
    public void PrepareLinkedEventRetry_SkipsTerminalQueuedEvents(string terminalStatus)
    {
        var clock = new TestClock();
        var evt = new WebhookEvent
        {
            Id = $"evt-{terminalStatus}",
            Action = "queued",
            Status = terminalStatus,
            InstanceId = "inst-terminal",
            ResolvedAt = clock.UtcNow.AddMinutes(-1),
            NextRetryAt = null
        };

        var changed = RunnerTimeoutService.PrepareLinkedEventRetry(
            evt,
            clock.UtcNow,
            "Runner failed after provider already resolved the job");

        Assert.False(changed);
        Assert.Equal(terminalStatus, evt.Status);
        Assert.Equal("inst-terminal", evt.InstanceId);
        Assert.Null(evt.NextRetryAt);
    }

    [Fact]
    public async Task ScanForTimeouts_RequeuesPendingDynamicRunnerAndRemovesInstance()
    {
        var store = TestDocumentStore.Create();
        var hostCommands = new RecordingHostCommandDispatcher();
        using var services = new ServiceCollection()
            .AddSingleton(store)
            .BuildServiceProvider();

        var now = DateTime.UtcNow;
        var host = new Host
        {
            Id = "host-timeout",
            Name = "host-timeout",
            Platform = HostPlatform.Linux,
            AgentStatus = AgentStatus.Online
        };
        var evt = new WebhookEvent
        {
            Id = "evt-timeout",
            Action = "queued",
            Status = "provisioned",
            InstanceId = "inst-timeout",
            JobId = "job-timeout",
            ReceivedAt = now.AddMinutes(-20),
            UpdatedAt = now.AddMinutes(-19)
        };
        var instance = new RunnerInstance
        {
            Id = "inst-timeout",
            HostId = host.Id,
            ProfileId = "profile-timeout",
            RunnerName = "dynamic-runner",
            ProvisioningMode = "dynamic",
            WebhookEventId = evt.Id,
            JobId = evt.JobId,
            Status = RunnerInstanceStatus.Pending,
            CreatedAt = now.AddMinutes(-10),
            DeployedAt = now.AddMinutes(-5),
            ContainerId = "container-timeout"
        };

        await store.Insert(host);
        await store.Insert(evt);
        await store.Insert(instance);

        var service = new RunnerTimeoutService(
            NullLogger<RunnerTimeoutService>.Instance,
            services,
            hostCommands,
            new ConfigurationBuilder().Build());

        await InvokeScanForTimeoutsAsync(service);

        var requeuedEvent = await store.Get<WebhookEvent>(evt.Id);
        Assert.NotNull(requeuedEvent);
        Assert.Equal("pending", requeuedEvent!.Status);
        Assert.Null(requeuedEvent.InstanceId);
        Assert.Null(requeuedEvent.ResolvedAt);
        Assert.Contains("deployment timed out", requeuedEvent.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await store.Get<RunnerInstance>(instance.Id));

        var stop = hostCommands.SingleCommand<StopRunnerCommand>(HostCommandKind.StopRunner);
        Assert.Equal(instance.Id, stop.InstanceId);
        Assert.Equal(instance.ContainerId, stop.InstanceHandle);
    }

    [Theory]
    [InlineData("no_match", 25, true)]
    [InlineData("pending_config", 23, false)]
    [InlineData("completed", 8 * 24, true)]
    [InlineData("timed_out", 6 * 24, false)]
    [InlineData("ignored", 3 * 24, true)]
    [InlineData("rejected", 24, false)]
    [InlineData("provisioned", 8 * 24, true)]
    [InlineData("pending", 30 * 24, false)]
    public void ShouldRemoveWebhookEvent_UsesStatusSpecificRetention(
        string status,
        int ageHours,
        bool expected)
    {
        var now = new TestClock().UtcNow;
        var evt = new WebhookEvent
        {
            Id = $"evt-{status}",
            Action = "queued",
            Status = status,
            ReceivedAt = now.AddHours(-ageHours)
        };

        Assert.Equal(expected, InvokeShouldRemoveWebhookEvent(evt, now));
    }

    private static Task InvokeScanForTimeoutsAsync(RunnerTimeoutService service)
    {
        var method = typeof(RunnerTimeoutService).GetMethod(
            "ScanForTimeoutsAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);
        return (Task)method!.Invoke(service, [CancellationToken.None])!;
    }

    private static bool InvokeShouldRemoveWebhookEvent(WebhookEvent evt, DateTime now)
    {
        var method = typeof(RunnerTimeoutService).GetMethod(
            "ShouldRemoveWebhookEvent",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        return (bool)method!.Invoke(null, [evt, now])!;
    }

    // Regression: run 35545917495. Runner Macos-jit-6d85e2e1 was minted for the iOS job
    // 106183357904, but GitHub handed it to build 106183357880. Because the linked iOS
    // event never reached "in_progress", the pickup timeout recycled the runner 10 minutes
    // in -- killing a build mid-step and deleting its workspace.
    [Fact]
    public void ClassifyLinkedJob_DifferentWhenGitHubAssignedAnotherJob()
    {
        var instance = new RunnerInstance { Id = "inst-1", RunnerName = "Macos-jit-6d85e2e1", JobId = "106183357888" };
        var linkedEvent = new WebhookEvent { Id = "evt-1", JobId = "106183357904", Status = "provisioned" };

        Assert.Equal(
            RunnerTimeoutService.LinkedJobBinding.Different,
            RunnerTimeoutService.ClassifyLinkedJob(instance, linkedEvent));
    }

    [Fact]
    public void ClassifyLinkedJob_SameWhenRunnerStillHoldsThatClaim()
    {
        var instance = new RunnerInstance { Id = "inst-1", RunnerName = "Macos-jit-6d85e2e1", JobId = "106183357904" };
        var linkedEvent = new WebhookEvent { Id = "evt-1", JobId = "106183357904", Status = "provisioned" };

        Assert.Equal(
            RunnerTimeoutService.LinkedJobBinding.Same,
            RunnerTimeoutService.ClassifyLinkedJob(instance, linkedEvent));
    }

    [Fact]
    public void ClassifyLinkedJob_UnknownWhenThereIsNothingToCompare()
    {
        // A released claim must classify as Unknown, never as "idle". Observed in
        // production: the webhook processor cleared Macos-jit-dbd36386's stale claim after
        // GitHub gave job 110937915464 to a different runner, the completion path correctly
        // declined to stop it, and the event-resolved reaper then stopped it anyway seven
        // seconds later because a null JobId could not prove adoption. Absence of a claim is
        // not evidence that the runner is doing nothing.
        var linkedEvent = new WebhookEvent { Id = "evt-1", JobId = "110937915464", Status = "completed" };

        Assert.Equal(
            RunnerTimeoutService.LinkedJobBinding.Unknown,
            RunnerTimeoutService.ClassifyLinkedJob(
                new RunnerInstance { Id = "inst-1", RunnerName = "Macos-jit-dbd36386", JobId = null },
                linkedEvent));
        Assert.Equal(
            RunnerTimeoutService.LinkedJobBinding.Unknown,
            RunnerTimeoutService.ClassifyLinkedJob(
                new RunnerInstance { Id = "inst-2", RunnerName = "Macos-jit-dbd36386", JobId = "" },
                linkedEvent));
        Assert.Equal(
            RunnerTimeoutService.LinkedJobBinding.Unknown,
            RunnerTimeoutService.ClassifyLinkedJob(
                new RunnerInstance { Id = "inst-3", RunnerName = "Macos-jit-dbd36386", JobId = "110937915464" },
                null));
        Assert.Equal(
            RunnerTimeoutService.LinkedJobBinding.Unknown,
            RunnerTimeoutService.ClassifyLinkedJob(
                new RunnerInstance { Id = "inst-4", RunnerName = "Macos-jit-dbd36386", JobId = "110937915464" },
                new WebhookEvent { Id = "evt-2", JobId = "" }));
    }

    [Fact]
    public void ClassifyLinkedJob_PrefersTheRunnerTheProviderActuallyAssigned()
    {
        // The stale claim still names this job because the runner's own in_progress
        // webhook has not arrived yet. Comparing job IDs alone would say "Same" and stop a
        // runner that is mid-build; the assigned runner name settles it.
        var linkedEvent = new WebhookEvent
        {
            Id = "evt-1",
            JobId = "110937915464",
            Status = "completed",
            AssignedRunnerName = "Macos-jit-7e8b7047"
        };

        Assert.Equal(
            RunnerTimeoutService.LinkedJobBinding.Different,
            RunnerTimeoutService.ClassifyLinkedJob(
                new RunnerInstance { Id = "inst-1", RunnerName = "Macos-jit-dbd36386", JobId = "110937915464" },
                linkedEvent));

        Assert.Equal(
            RunnerTimeoutService.LinkedJobBinding.Same,
            RunnerTimeoutService.ClassifyLinkedJob(
                new RunnerInstance { Id = "inst-2", RunnerName = "macos-jit-7E8B7047", JobId = null },
                linkedEvent));
    }
}
