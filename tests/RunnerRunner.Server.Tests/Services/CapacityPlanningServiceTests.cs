using RunnerRunner.Core.Models;
using RunnerRunner.Server.Services;
using Host = RunnerRunner.Core.Models.Host;

namespace RunnerRunner.Server.Tests.Services;

public class CapacityPlanningServiceTests
{
    [Fact]
    public void EvaluateRuleCapacity_ShowsWhenProfilePoolIsTighterThanRuleLimit()
    {
        var hostA = new Host { Name = "linux-a", Platform = HostPlatform.Linux, MaxDockerContainers = 2 };
        var hostB = new Host { Name = "linux-b", Platform = HostPlatform.Linux, MaxDockerContainers = 1 };
        var profile = new RunnerProfile
        {
            Name = "docker-linux",
            RequiredHostPlatform = HostPlatform.Linux,
            ExecutionBackend = ExecutionBackend.Docker,
            MaxParallelPerHost = 1
        };
        var rule = new ProvisioningRule
        {
            Name = "gh-webhook",
            Type = ProvisioningType.Webhook,
            Provider = RunnerProvider.GitHubActions,
            MaxConcurrent = 10,
            LabelMappings =
            [
                new LabelProfileMapping
                {
                    ProfileId = profile.Id,
                    RequiredLabels = ["ubuntu"]
                }
            ]
        };

        var activeInstances = new[]
        {
            new RunnerInstance
            {
                RunnerName = "runner-a",
                HostId = hostA.Id,
                ProfileId = profile.Id,
                ProvisioningMode = "dynamic",
                WebhookEventId = "evt-a",
                Status = RunnerInstanceStatus.Running
            },
            new RunnerInstance
            {
                RunnerName = "runner-b",
                HostId = hostB.Id,
                ProfileId = profile.Id,
                ProvisioningMode = "dynamic",
                WebhookEventId = "evt-b",
                Status = RunnerInstanceStatus.Running
            }
        };

        var events = new[]
        {
            new WebhookEvent { Id = "evt-a", BindingId = rule.Id, Provider = "GitHubActions", Action = "queued", Repository = "org/repo" },
            new WebhookEvent { Id = "evt-b", BindingId = rule.Id, Provider = "GitHubActions", Action = "queued", Repository = "org/repo" }
        };

        var result = CapacityPlanningService.EvaluateRuleCapacity(
            rule,
            [hostA, hostB],
            new Dictionary<string, RunnerProfile> { [profile.Id] = profile },
            activeInstances,
            events);

        Assert.Equal(10, result.ConfiguredLimit);
        Assert.Equal(2, result.ActiveCount);
        Assert.Equal(8, result.RemainingSlots);
        Assert.Single(result.MappedProfiles);
        Assert.Equal(2, result.MappedProfiles[0].EffectivePoolLimit);
        Assert.Equal(0, result.MappedProfiles[0].AvailableNow);
    }

    [Fact]
    public void AnalyzeHostSelection_BlocksWhenProfilePerHostLimitIsReached()
    {
        var host = new Host { Name = "mac-mini", Platform = HostPlatform.MacOS, MaxTartVMs = 3 };
        var profile = new RunnerProfile
        {
            Name = "macos-jit",
            RequiredHostPlatform = HostPlatform.MacOS,
            ExecutionBackend = ExecutionBackend.Tart,
            MaxParallelPerHost = 1
        };

        var instances = new[]
        {
            new RunnerInstance
            {
                RunnerName = "macos-jit-1",
                HostId = host.Id,
                ProfileId = profile.Id,
                Status = RunnerInstanceStatus.Running
            }
        };

        var analysis = CapacityPlanningService.AnalyzeHostSelection(
            profile,
            null,
            [host],
            new Dictionary<string, RunnerProfile> { [profile.Id] = profile },
            instances);

        Assert.True(analysis.CapacityBlocked);
        Assert.Equal(CapacityBlockerKind.Profile, analysis.BlockedBy);
        Assert.Contains("per-host", analysis.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnalyzeHostSelection_BlocksWhenObservedTartCapacityIsReached()
    {
        var host = new Host
        {
            Name = "mac-mini",
            Platform = HostPlatform.MacOS,
            MaxTartVMs = 2,
            ObservedRunningTartVMs = 2,
            ObservedResourceUsageAt = DateTime.UtcNow
        };
        var profile = new RunnerProfile
        {
            Name = "macos-tart",
            RequiredHostPlatform = HostPlatform.MacOS,
            ExecutionBackend = ExecutionBackend.Tart,
            MaxParallelPerHost = 2
        };

        var analysis = CapacityPlanningService.AnalyzeHostSelection(
            profile,
            null,
            [host],
            new Dictionary<string, RunnerProfile> { [profile.Id] = profile },
            []);

        Assert.True(analysis.CapacityBlocked);
        Assert.Equal(CapacityBlockerKind.Host, analysis.BlockedBy);
        Assert.Single(analysis.Candidates);
        Assert.Equal(2, analysis.Candidates[0].BackendCapacity.Used);
        Assert.Equal(0, analysis.Candidates[0].BackendCapacity.Remaining);
    }

    [Fact]
    public void AnalyzeHostSelection_RespectsTargetHostAndHostCapacity()
    {
        var hostA = new Host { Name = "linux-a", Platform = HostPlatform.Linux, MaxDockerContainers = 5 };
        var hostB = new Host { Name = "linux-b", Platform = HostPlatform.Linux, MaxDockerContainers = 1 };
        var profile = new RunnerProfile
        {
            Name = "docker-jit",
            RequiredHostPlatform = HostPlatform.Linux,
            ExecutionBackend = ExecutionBackend.Docker,
            MaxParallelPerHost = 3
        };
        var rule = new ProvisioningRule
        {
            Name = "targeted",
            Type = ProvisioningType.Webhook,
            TargetHostId = hostB.Id,
            MaxConcurrent = 5,
            LabelMappings = [new LabelProfileMapping { ProfileId = profile.Id, RequiredLabels = ["ubuntu"] }]
        };

        var instances = new[]
        {
            new RunnerInstance
            {
                RunnerName = "docker-jit-1",
                HostId = hostB.Id,
                ProfileId = profile.Id,
                Status = RunnerInstanceStatus.Running
            }
        };

        var analysis = CapacityPlanningService.AnalyzeHostSelection(
            profile,
            rule,
            [hostA, hostB],
            new Dictionary<string, RunnerProfile> { [profile.Id] = profile },
            instances);

        Assert.True(analysis.CapacityBlocked);
        Assert.Equal(CapacityBlockerKind.Host, analysis.BlockedBy);
        Assert.All(analysis.Candidates, c => Assert.Equal(hostB.Id, c.HostId));
    }

    [Fact]
    public void AnalyzeHostSelection_SkipsWindowsHostsWhenDockerIsInLinuxMode()
    {
        var incompatibleHost = new Host
        {
            Name = "win-linux-mode",
            Platform = HostPlatform.Windows,
            MaxDockerContainers = 4,
            Labels = { ["docker"] = "true", ["docker_os"] = "linux" }
        };

        var compatibleHost = new Host
        {
            Name = "win-windows-mode",
            Platform = HostPlatform.Windows,
            MaxDockerContainers = 4,
            Labels = { ["docker"] = "true", ["docker_os"] = "windows" }
        };

        var profile = new RunnerProfile
        {
            Name = "windows-docker",
            RequiredHostPlatform = HostPlatform.Windows,
            ExecutionBackend = ExecutionBackend.Docker,
            MaxParallelPerHost = 2
        };

        var analysis = CapacityPlanningService.AnalyzeHostSelection(
            profile,
            null,
            [incompatibleHost, compatibleHost],
            new Dictionary<string, RunnerProfile> { [profile.Id] = profile },
            []);

        Assert.False(analysis.CapacityBlocked);
        Assert.NotNull(analysis.SelectedHost);
        Assert.Equal(compatibleHost.Id, analysis.SelectedHost!.Id);
        Assert.Single(analysis.Candidates);
    }

    [Fact]
    public void IsCapacityConsuming_IgnoresLegacyUnmanagedStaticRunnerEntries()
    {
        var unmanaged = new RunnerInstance
        {
            RunnerName = "macos-ci",
            HostId = "host-1",
            ProfileId = "profile-1",
            ProvisioningMode = "static",
            Status = RunnerInstanceStatus.Running
        };

        var managed = new RunnerInstance
        {
            RunnerName = "MacOS-Native-jit-1234",
            HostId = "host-1",
            ProfileId = "profile-1",
            ProvisioningMode = "dynamic",
            Status = RunnerInstanceStatus.Running,
            WebhookEventId = "evt-1"
        };

        Assert.False(CapacityPlanningService.IsRunnerRunnerManaged(unmanaged));
        Assert.False(CapacityPlanningService.IsCapacityConsuming(unmanaged));
        Assert.True(CapacityPlanningService.IsRunnerRunnerManaged(managed));
        Assert.True(CapacityPlanningService.IsCapacityConsuming(managed));
    }

    [Fact]
    public void ExplainEvent_ShowsProvisioningRuleLimit()
    {
        var host = new Host { Name = "linux-a", Platform = HostPlatform.Linux, MaxDockerContainers = 10 };
        var profile = new RunnerProfile
        {
            Name = "docker-jit",
            RequiredHostPlatform = HostPlatform.Linux,
            ExecutionBackend = ExecutionBackend.Docker,
            MaxParallelPerHost = 2
        };
        var rule = new ProvisioningRule
        {
            Id = "rule-1",
            Name = "github",
            Type = ProvisioningType.Webhook,
            Provider = RunnerProvider.GitHubActions,
            MaxConcurrent = 1,
            LabelMappings = [new LabelProfileMapping { ProfileId = profile.Id, RequiredLabels = ["ubuntu"] }]
        };
        var evt = new WebhookEvent
        {
            Id = "evt-1",
            BindingId = rule.Id,
            Provider = "GitHubActions",
            Action = "queued",
            Repository = "org/repo",
            Labels = ["ubuntu"],
            Status = "pending_capacity",
            MatchedProfileId = profile.Id
        };

        var running = new RunnerInstance
        {
            RunnerName = "active",
            HostId = host.Id,
            ProfileId = profile.Id,
            ProvisioningMode = "dynamic",
            WebhookEventId = "evt-active",
            Status = RunnerInstanceStatus.Running
        };

        var result = CapacityPlanningService.ExplainEvent(
            evt,
            [host],
            new Dictionary<string, RunnerProfile> { [profile.Id] = profile },
            new Dictionary<string, ProvisioningRule> { [rule.Id] = rule },
            [running],
            [evt, new WebhookEvent { Id = "evt-active", BindingId = rule.Id, Provider = "GitHubActions", Action = "queued", Repository = "org/repo", Labels = ["ubuntu"] }]);

        Assert.Equal(CapacityBlockerKind.ProvisioningRule, result.BlockedBy);
        Assert.Contains("1/1", result.Summary, StringComparison.OrdinalIgnoreCase);
    }
}
