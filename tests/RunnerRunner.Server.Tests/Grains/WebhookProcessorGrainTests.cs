using RunnerRunner.Core.Models;
using RunnerRunner.Server.Grains;

namespace RunnerRunner.Server.Tests.Grains;

public class WebhookProcessorGrainTests
{
    [Fact]
    public void MarkQueuedEventsInProgress_UpdatesLinkedQueuedEvent()
    {
        var now = DateTime.UtcNow;
        var queuedEvent = new WebhookEvent
        {
            Id = "evt-1",
            Action = "queued",
            Status = "provisioned",
            JobId = "job-1",
            NextRetryAt = now.AddMinutes(1)
        };
        var instance = new RunnerInstance
        {
            Id = "inst-1",
            RunnerName = "runner-1",
            ProfileId = "profile-1",
            ProvisioningMode = "dynamic",
            JobId = "job-1",
            WebhookEventId = queuedEvent.Id
        };

        var updated = WebhookProcessorGrain.MarkQueuedEventsInProgress([queuedEvent], [instance], now);

        Assert.Single(updated);
        Assert.Same(queuedEvent, updated[0]);
        Assert.Equal("in_progress", queuedEvent.Status);
        Assert.Equal("inst-1", queuedEvent.InstanceId);
        Assert.Equal("profile-1", queuedEvent.MatchedProfileId);
        Assert.Equal(now, queuedEvent.UpdatedAt);
        Assert.Null(queuedEvent.NextRetryAt);
        Assert.Null(queuedEvent.ResolvedAt);
    }

    [Fact]
    public void MarkQueuedEventsInProgress_DoesNotRegressCompletedEvent()
    {
        var now = DateTime.UtcNow;
        var queuedEvent = new WebhookEvent
        {
            Id = "evt-1",
            Action = "queued",
            Status = "completed",
            JobId = "job-1",
            InstanceId = "inst-1"
        };
        var instance = new RunnerInstance
        {
            Id = "inst-1",
            RunnerName = "runner-1",
            ProfileId = "profile-1",
            ProvisioningMode = "dynamic",
            JobId = "job-1",
            WebhookEventId = queuedEvent.Id
        };

        var updated = WebhookProcessorGrain.MarkQueuedEventsInProgress([queuedEvent], [instance], now);

        Assert.Empty(updated);
        Assert.Equal("completed", queuedEvent.Status);
        Assert.Equal("inst-1", queuedEvent.InstanceId);
    }

    [Fact]
    public void ComputeRebindDecision_ReturnsRebindWhenGitHubSwappedRunners()
    {
        // Scenario: we dispatched two JIT runners with the same labels.
        // Server intent: A→jobX, B→jobY. GitHub bound A→jobY, B→jobX.
        // First in_progress webhook arrives for jobX with runner_name=B.
        // We must rebind B from jobY to jobX (and update its WebhookEventId).
        var jobXEvent = new WebhookEvent { Id = "evt-x", Action = "queued", Status = "provisioned", JobId = "jobX", ReceivedAt = DateTime.UtcNow.AddMinutes(-1) };
        var jobYEvent = new WebhookEvent { Id = "evt-y", Action = "queued", Status = "provisioned", JobId = "jobY", ReceivedAt = DateTime.UtcNow };

        var instanceA = new RunnerInstance { Id = "A", RunnerName = "runner-A", ProvisioningMode = "dynamic", JobId = "jobX", WebhookEventId = "evt-x" };
        var instanceB = new RunnerInstance { Id = "B", RunnerName = "runner-B", ProvisioningMode = "dynamic", JobId = "jobY", WebhookEventId = "evt-y" };

        var decision = WebhookProcessorGrain.ComputeRebindDecision(
            [instanceA, instanceB], [jobXEvent, jobYEvent], runnerName: "runner-B", jobId: "jobX");

        Assert.NotNull(decision);
        Assert.Same(instanceB, decision!.Instance);
        Assert.Equal("evt-x", decision.NewWebhookEventId);
    }

    [Fact]
    public void ComputeRebindDecision_ReturnsNullWhenBindingAlreadyCorrect()
    {
        var instance = new RunnerInstance { Id = "A", RunnerName = "runner-A", ProvisioningMode = "dynamic", JobId = "jobX" };
        var decision = WebhookProcessorGrain.ComputeRebindDecision(
            [instance], [], runnerName: "runner-A", jobId: "jobX");

        Assert.Null(decision);
    }

    [Fact]
    public void ComputeRebindDecision_ReturnsNullWhenNoMatchingInstance()
    {
        var instance = new RunnerInstance { Id = "A", RunnerName = "runner-A", ProvisioningMode = "dynamic", JobId = "jobX" };
        var decision = WebhookProcessorGrain.ComputeRebindDecision(
            [instance], [], runnerName: "ghost-runner", jobId: "jobX");

        Assert.Null(decision);
    }

    [Fact]
    public void ComputeRebindDecision_IgnoresStaticInstances()
    {
        var instance = new RunnerInstance { Id = "A", RunnerName = "runner-A", ProvisioningMode = "static", JobId = "jobOld" };
        var decision = WebhookProcessorGrain.ComputeRebindDecision(
            [instance], [], runnerName: "runner-A", jobId: "jobX");

        Assert.Null(decision);
    }

    [Fact]
    public void ComputeRebindDecision_ReturnsNullWhenRunnerNameIsEmpty()
    {
        // GitHub omits runner_name on queued payloads — we must not rebind.
        var instance = new RunnerInstance { Id = "A", RunnerName = "runner-A", ProvisioningMode = "dynamic", JobId = "jobX" };
        var decision = WebhookProcessorGrain.ComputeRebindDecision(
            [instance], [], runnerName: null, jobId: "jobY");

        Assert.Null(decision);
    }

    [Fact]
    public void ComputeRebindDecision_PrefersOpenQueuedEventOverTerminal()
    {
        // If a stale terminal queued event exists for the same jobId (e.g. a
        // prior timed_out event being requeued), we should pick the live one.
        var stale = new WebhookEvent { Id = "evt-old", Action = "queued", Status = "timed_out", JobId = "jobX", ReceivedAt = DateTime.UtcNow.AddMinutes(-30) };
        var live = new WebhookEvent { Id = "evt-new", Action = "queued", Status = "pending", JobId = "jobX", ReceivedAt = DateTime.UtcNow };
        var instance = new RunnerInstance { Id = "B", RunnerName = "runner-B", ProvisioningMode = "dynamic", JobId = "jobY" };

        var decision = WebhookProcessorGrain.ComputeRebindDecision(
            [instance], [stale, live], runnerName: "runner-B", jobId: "jobX");

        Assert.NotNull(decision);
        Assert.Equal("evt-new", decision!.NewWebhookEventId);
    }
}
