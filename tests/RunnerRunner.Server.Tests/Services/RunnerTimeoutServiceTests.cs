using RunnerRunner.Core.Models;
using RunnerRunner.Server.Services;

namespace RunnerRunner.Server.Tests.Services;

public class RunnerTimeoutServiceTests
{
    [Fact]
    public void PrepareLinkedEventRetry_ResetsProvisionedQueuedEvent()
    {
        var now = DateTime.UtcNow;
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
        var now = DateTime.UtcNow;
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

    [Fact]
    public void GetEffectiveEventStatus_UsesRecordedInProgressEvent()
    {
        var linkedEvent = new WebhookEvent
        {
            Id = "evt-queued",
            Action = "queued",
            Status = "provisioned",
            JobId = "job-1"
        };
        var eventsForJob = new[]
        {
            linkedEvent,
            new WebhookEvent
            {
                Id = "evt-progress",
                Action = "in_progress",
                Status = "in_progress",
                JobId = "job-1"
            }
        };

        var status = RunnerTimeoutService.GetEffectiveEventStatus(linkedEvent, eventsForJob);

        Assert.Equal("in_progress", status);
    }

    [Fact]
    public void GetEffectiveEventStatus_UsesRecordedTerminalEventBeforeInProgress()
    {
        var linkedEvent = new WebhookEvent
        {
            Id = "evt-queued",
            Action = "queued",
            Status = "provisioned",
            JobId = "job-1"
        };
        var eventsForJob = new[]
        {
            linkedEvent,
            new WebhookEvent
            {
                Id = "evt-completed",
                Action = "completed",
                Status = "completed",
                JobId = "job-1"
            },
            new WebhookEvent
            {
                Id = "evt-progress",
                Action = "in_progress",
                Status = "in_progress",
                JobId = "job-1"
            }
        };

        var status = RunnerTimeoutService.GetEffectiveEventStatus(linkedEvent, eventsForJob);

        Assert.Equal("completed", status);
    }
}
