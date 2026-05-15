using RunnerRunner.Core.Models;

namespace RunnerRunner.Core.Tests;

public class ModelDefaultsTests
{
    [Fact]
    public void Host_HasGeneratedId()
    {
        var host = new Host { Name = "test-host" };
        Assert.False(string.IsNullOrEmpty(host.Id));
        Assert.True(Guid.TryParse(host.Id, out _));
    }

    [Fact]
    public void Host_DefaultValues()
    {
        var host = new Host { Name = "test" };
        Assert.Equal(AgentStatus.Offline, host.AgentStatus);
        Assert.Empty(host.Capabilities);
        Assert.Empty(host.EnvironmentOverrides);
        Assert.False(host.IsApproved);
        Assert.Null(host.LastHeartbeat);
    }

    [Fact]
    public void RunnerProfile_DefaultValues()
    {
        var profile = new RunnerProfile { Name = "test" };
        Assert.False(string.IsNullOrEmpty(profile.Id));
        Assert.Equal("Default", profile.RunnerGroup);
        Assert.Equal(1, profile.MaxParallelPerHost);
        Assert.False(profile.Ephemeral);
        Assert.Empty(profile.Labels);
        Assert.Empty(profile.EnvironmentVariableSetIds);
        Assert.Empty(profile.EnvironmentOverrides);
        Assert.Empty(profile.ProviderConfig);
        Assert.Null(profile.DockerConfig);
        Assert.Null(profile.TartConfig);
        Assert.Null(profile.RunnerAgentVersion);
    }

    [Fact]
    public void RunnerInstance_DefaultValues()
    {
        var instance = new RunnerInstance { RunnerName = "test-runner" };
        Assert.False(string.IsNullOrEmpty(instance.Id));
        Assert.Equal(RunnerInstanceStatus.Pending, instance.Status);
        Assert.Null(instance.StartedAt);
        Assert.Null(instance.StoppedAt);
        Assert.Null(instance.ContainerId);
        Assert.Null(instance.ErrorMessage);
    }

    [Fact]
    public void EnvironmentVariableSet_DefaultValues()
    {
        var evs = new EnvironmentVariableSet { Name = "test" };
        Assert.False(string.IsNullOrEmpty(evs.Id));
        Assert.Empty(evs.Variables);
        Assert.Equal(0, evs.Priority);
    }

    [Fact]
    public void RunnerAssignment_DefaultValues()
    {
        var assignment = new RunnerAssignment();
        Assert.False(string.IsNullOrEmpty(assignment.Id));
        Assert.Equal(1, assignment.DesiredCount);
        Assert.Equal("", assignment.HostId);
        Assert.Equal("", assignment.ProfileId);
    }

    [Fact]
    public void DockerImageConfig_DefaultValues()
    {
        var config = new DockerImageConfig { RegistryUrl = "ghcr.io", ImageName = "test" };
        Assert.Equal("latest", config.Tag);
        Assert.Equal(PullPolicy.IfNotPresent, config.PullPolicy);
    }

    [Fact]
    public void TartImageConfig_DefaultValues()
    {
        var config = new TartImageConfig { RegistryUrl = "ghcr.io", ImageName = "test" };
        Assert.Equal("latest", config.Tag);
        Assert.Null(config.CpuCount);
        Assert.Null(config.MemorySizeGb);
        Assert.Null(config.DiskSizeGb);
        Assert.Empty(config.SharedDirs);
    }

    [Fact]
    public void ProviderCredential_DefaultValues()
    {
        var cred = new ProviderCredential { Name = "test" };
        Assert.False(string.IsNullOrEmpty(cred.Id));
        Assert.Null(cred.GitHubOrg);
        Assert.Null(cred.GitHubToken);
        Assert.Equal(GitHubAuthType.PersonalAccessToken, cred.GitHubAuthType);
        Assert.Null(cred.GitHubAppId);
        Assert.Null(cred.GiteaInstanceUrl);
        Assert.Null(cred.AzDoOrgUrl);
    }

    [Fact]
    public void AuditLogEntry_DefaultTimestamp()
    {
        var before = DateTime.UtcNow;
        var entry = new AuditLogEntry { Action = "test", EntityType = "test" };
        Assert.True(entry.Timestamp >= before);
        Assert.True(entry.Timestamp <= DateTime.UtcNow);
    }

    [Fact]
    public void WebhookEvent_ScheduleRetry_TracksAttemptsAndNextRetry()
    {
        var now = new DateTime(2026, 4, 13, 19, 30, 0, DateTimeKind.Utc);
        var evt = new WebhookEvent { Action = "queued", Status = "pending" };

        evt.ScheduleRetry("Waiting for a matching host", now, TimeSpan.FromSeconds(30));

        Assert.Equal("pending", evt.Status);
        Assert.Equal("Waiting for a matching host", evt.Error);
        Assert.Equal(1, evt.RetryCount);
        Assert.Equal(now, evt.LastAttemptAt);
        Assert.Equal(now.AddSeconds(30), evt.NextRetryAt);
        Assert.Null(evt.ExpiresAt);
    }

    [Fact]
    public void WebhookEvent_MarkResolved_AndTimeoutHelpersBehaveAsExpected()
    {
        var now = new DateTime(2026, 4, 13, 19, 30, 0, DateTimeKind.Utc);
        var evt = new WebhookEvent
        {
            Action = "queued",
            Status = "pending",
            NextRetryAt = now,
            ExpiresAt = now.AddMinutes(5)
        };

        Assert.False(evt.HasExpired(now));
        Assert.True(evt.IsRetryCandidate(now));

        evt.MarkResolved("completed", now, "instance-123");

        Assert.Equal("completed", evt.Status);
        Assert.Equal("instance-123", evt.InstanceId);
        Assert.Equal(now, evt.ResolvedAt);
        Assert.False(evt.IsRetryCandidate(now.AddMinutes(1)));
    }

    [Fact]
    public void WebhookEvent_OpenEndedQueueWait_DoesNotExpireLocally()
    {
        var now = new DateTime(2026, 4, 13, 19, 30, 0, DateTimeKind.Utc);
        var evt = new WebhookEvent
        {
            Action = "queued",
            Status = "pending_capacity",
            ReceivedAt = now.AddHours(-3),
            ExpiresAt = now.AddMinutes(-1),
            NextRetryAt = now.AddSeconds(-5)
        };

        evt.EnsureLifecycleWindow(now, TimeSpan.FromMinutes(10));

        Assert.True(evt.IsRetryCandidate(now));
        Assert.False(evt.HasExpired(now));
        Assert.Null(evt.ExpiresAt);
    }

    [Fact]
    public void WebhookEvent_SetProgress_UpdatesDisplayStateWithoutResolving()
    {
        var now = new DateTime(2026, 4, 13, 19, 30, 0, DateTimeKind.Utc);
        var evt = new WebhookEvent { Action = "queued", Status = "pending" };

        evt.SetProgress("dispatching", "Sending runner to host", now, now.AddSeconds(15));

        Assert.Equal("dispatching", evt.Status);
        Assert.Equal("Sending runner to host", evt.Error);
        Assert.Equal(now, evt.UpdatedAt);
        Assert.Equal(now, evt.LastAttemptAt);
        Assert.Equal(now.AddSeconds(15), evt.NextRetryAt);
        Assert.Null(evt.ResolvedAt);
    }

    [Fact]
    public void LabelProfileMapping_SupportsWildcardMatching()
    {
        var mapping = new LabelProfileMapping
        {
            RequiredLabels = ["self-hosted", "mac*"],
            ProfileId = "profile-1"
        };

        Assert.True(mapping.Matches(["self-hosted", "macOS", "x64"]));
        Assert.True(mapping.Matches(["self-hosted", "mac", "arm64"]));
        Assert.False(mapping.Matches(["self-hosted", "linux"]));
    }

    [Fact]
    public void ProvisioningRule_ResolveWebhookProfileId_RequiresExplicitMapping()
    {
        var rule = new ProvisioningRule
        {
            Name = "Webhook",
            Type = ProvisioningType.Webhook,
            DefaultProfileId = "legacy-default",
            ProfileId = "legacy-direct",
            LabelMappings =
            [
                new LabelProfileMapping
                {
                    RequiredLabels = ["mac*"],
                    ProfileId = "mac-profile",
                    Priority = 10
                },
                new LabelProfileMapping
                {
                    RequiredLabels = ["*"],
                    ProfileId = "catch-all",
                    Priority = 1
                }
            ]
        };

        Assert.Equal("mac-profile", rule.ResolveWebhookProfileId(["self-hosted", "macOS"]));
        Assert.Equal("catch-all", rule.ResolveWebhookProfileId(["self-hosted", "linux"]));

        rule.LabelMappings.Clear();
        Assert.Null(rule.ResolveWebhookProfileId(["self-hosted", "linux"]));
    }

    [Fact]
    public void RunnerAgentVersion_DefaultValues()
    {
        var version = new RunnerAgentVersion { Version = "2.300.0" };
        Assert.False(version.IsLatest);
        Assert.Null(version.DownloadUrlLinuxX64);
    }

    [Fact]
    public void UniqueIds_AcrossInstances()
    {
        var ids = Enumerable.Range(0, 100)
            .Select(_ => new Host { Name = "test" }.Id)
            .ToHashSet();
        Assert.Equal(100, ids.Count);
    }
}
