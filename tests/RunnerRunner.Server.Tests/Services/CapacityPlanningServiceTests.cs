using RunnerRunner.Core.Models;
using RunnerRunner.Server.Services;
using Host = RunnerRunner.Core.Models.Host;

namespace RunnerRunner.Server.Tests.Services;

public class CapacityPlanningServiceTests
{
    [Fact]
    public void EvaluateRuleCapacity_UsesHostBackendPoolWithoutProfilePerHostLimit()
    {
        var hostA = new Host { Name = "linux-a", Platform = HostPlatform.Linux, MaxDockerContainers = 2, Capabilities = ["docker"] };
        var hostB = new Host { Name = "linux-b", Platform = HostPlatform.Linux, MaxDockerContainers = 1, Capabilities = ["docker"] };
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
        Assert.Single(result.MappedRunners);
        Assert.Equal(3, result.MappedRunners[0].EffectivePoolLimit);
        Assert.Equal(1, result.MappedRunners[0].AvailableNow);
    }

    [Fact]
    public void EvaluateRuleCapacity_TreatsZeroWebhookMaxConcurrentAsUnlimited()
    {
        var host = new Host { Name = "linux-a", Platform = HostPlatform.Linux, MaxDockerContainers = 1, Capabilities = ["docker"] };
        var profile = new RunnerProfile
        {
            Name = "docker-linux",
            RequiredHostPlatform = HostPlatform.Linux,
            ExecutionBackend = ExecutionBackend.Docker
        };
        var rule = new ProvisioningRule
        {
            Name = "gh-webhook",
            Type = ProvisioningType.Webhook,
            Provider = RunnerProvider.GitHubActions,
            MaxConcurrent = 0,
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
                HostId = host.Id,
                ProfileId = profile.Id,
                ProvisioningMode = "dynamic",
                WebhookEventId = "evt-a",
                Status = RunnerInstanceStatus.Running
            }
        };
        var events = new[]
        {
            new WebhookEvent { Id = "evt-a", BindingId = rule.Id, Provider = "GitHubActions", Action = "queued", Repository = "org/repo" }
        };

        var result = CapacityPlanningService.EvaluateRuleCapacity(
            rule,
            [host],
            new Dictionary<string, RunnerProfile> { [profile.Id] = profile },
            activeInstances,
            events);

        Assert.True(result.IsUnlimited);
        Assert.Equal(0, result.ConfiguredLimit);
        Assert.Equal(int.MaxValue, result.RemainingSlots);
    }

    [Fact]
    public void BuildSnapshot_IncludesRuleOwnedRunnerDefinitions()
    {
        var host = new Host { Name = "mac-mini", Platform = HostPlatform.MacOS, MaxTartVMs = 2, Capabilities = ["tart"] };
        var rule = new ProvisioningRule
        {
            Name = "mac webhook",
            Type = ProvisioningType.Webhook,
            Provider = RunnerProvider.GitHubActions,
            MaxConcurrent = 5,
            RunnerDefinitions =
            [
                new RunnerDefinition
                {
                    Id = "runner-macos",
                    Name = "macOS Tart",
                    RequiredHostPlatform = HostPlatform.MacOS,
                    ExecutionBackend = ExecutionBackend.Tart,
                    Matchers = [new RunnerLabelMatcher { RequiredLabels = ["mac*"] }]
                }
            ]
        };

        var snapshot = CapacityPlanningService.BuildSnapshot([host], [], [rule], [], []);

        Assert.True(snapshot.Profiles.ContainsKey("runner-macos"));
        var ruleView = snapshot.Rules[rule.Id];
        var mapped = Assert.Single(ruleView.MappedRunners);
        Assert.Equal("macOS Tart", mapped.RunnerName);
        Assert.Equal(2, mapped.EffectivePoolLimit);
        Assert.Equal(2, mapped.AvailableNow);
    }

    [Fact]
    public void AnalyzeHostSelection_IgnoresLegacyProfilePerHostLimitWhenBackendHasCapacity()
    {
        var host = new Host { Name = "mac-mini", Platform = HostPlatform.MacOS, MaxTartVMs = 3, Capabilities = ["tart"] };
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

        Assert.False(analysis.CapacityBlocked);
        Assert.NotNull(analysis.SelectedHost);
        Assert.Equal(host.Id, analysis.SelectedHost!.Id);
    }

    [Fact]
    public void AnalyzeHostSelection_BlocksWhenObservedTartCapacityIsReached()
    {
        var host = new Host
        {
            Name = "mac-mini",
            Platform = HostPlatform.MacOS,
            MaxTartVMs = 2,
            Capabilities = ["tart"],
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
        var hostA = new Host { Name = "linux-a", Platform = HostPlatform.Linux, MaxDockerContainers = 5, Capabilities = ["docker"] };
        var hostB = new Host { Name = "linux-b", Platform = HostPlatform.Linux, MaxDockerContainers = 1, Capabilities = ["docker"] };
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
    public void AnalyzeHostSelection_RespectsTargetSpecificHostCapabilities()
    {
        var plainHost = new Host
        {
            Name = "windows-build",
            Platform = HostPlatform.Windows,
            MaxNativeProcesses = 2,
            Capabilities = ["native", "windows"]
        };
        var uiHost = new Host
        {
            Name = "windows-ui",
            Platform = HostPlatform.Windows,
            MaxNativeProcesses = 2,
            Capabilities = ["native", "windows", "windows-ui"]
        };
        var profile = new RunnerProfile
        {
            Name = "windows-ui-tests",
            RequiredHostPlatform = HostPlatform.Windows,
            ExecutionBackend = ExecutionBackend.Native,
            RequiredHostCapabilities = ["windows-ui"]
        };

        var analysis = CapacityPlanningService.AnalyzeHostSelection(
            profile,
            null,
            [plainHost, uiHost],
            new Dictionary<string, RunnerProfile> { [profile.Id] = profile },
            []);

        Assert.False(analysis.CapacityBlocked);
        Assert.NotNull(analysis.SelectedHost);
        Assert.Equal(uiHost.Id, analysis.SelectedHost!.Id);
        var candidate = Assert.Single(analysis.Candidates);
        Assert.Equal(uiHost.Id, candidate.HostId);
    }

    [Fact]
    public void AnalyzeHostSelection_MatchesHostLabelKeysCaseInsensitively()
    {
        var host = new Host
        {
            Name = "mac-mini",
            Platform = HostPlatform.MacOS,
            MaxNativeProcesses = 2,
            Labels = { ["Pool"] = "Mac-Native" }
        };
        var profile = new RunnerProfile
        {
            Name = "macos-native",
            RequiredHostPlatform = HostPlatform.MacOS,
            ExecutionBackend = ExecutionBackend.Native
        };
        var rule = new ProvisioningRule
        {
            Name = "targeted",
            Type = ProvisioningType.Webhook,
            RequiredHostLabels = new Dictionary<string, string>
            {
                ["pool"] = "mac-native"
            }
        };

        var analysis = CapacityPlanningService.AnalyzeHostSelection(
            profile,
            rule,
            [host],
            new Dictionary<string, RunnerProfile> { [profile.Id] = profile },
            []);

        Assert.False(analysis.CapacityBlocked);
        Assert.NotNull(analysis.SelectedHost);
        Assert.Equal(host.Id, analysis.SelectedHost!.Id);
    }

    [Fact]
    public void AnalyzeHostSelection_TreatsPlatformAndArchitectureAsHostCapabilities()
    {
        var host = new Host
        {
            Name = "mac-mini",
            Platform = HostPlatform.MacOS,
            Architecture = "ARM64",
            MaxNativeProcesses = 2
        };
        var profile = new RunnerProfile
        {
            Name = "macos-native",
            RequiredHostPlatform = HostPlatform.MacOS,
            ExecutionBackend = ExecutionBackend.Native,
            RequiredHostCapabilities = ["macos", "native", "arm64"]
        };

        var analysis = CapacityPlanningService.AnalyzeHostSelection(
            profile,
            null,
            [host],
            new Dictionary<string, RunnerProfile> { [profile.Id] = profile },
            []);

        Assert.False(analysis.CapacityBlocked);
        Assert.NotNull(analysis.SelectedHost);
        Assert.Equal(host.Id, analysis.SelectedHost!.Id);
    }

    [Fact]
    public void BuildSnapshot_UsesRunnerDefinitionHostRoutingForCapacity()
    {
        var defaultHost = new Host
        {
            Name = "linux-default",
            Platform = HostPlatform.Linux,
            GroupId = "default",
            MaxDockerContainers = 2
        };
        var gpuHost = new Host
        {
            Name = "linux-gpu",
            Platform = HostPlatform.Linux,
            GroupId = "gpu",
            MaxDockerContainers = 3,
            Capabilities = ["docker", "gpu"]
        };
        var rule = new ProvisioningRule
        {
            Name = "linux webhook",
            Type = ProvisioningType.Webhook,
            Provider = RunnerProvider.GitHubActions,
            MaxConcurrent = 5,
            RunnerDefinitions =
            [
                new RunnerDefinition
                {
                    Id = "runner-gpu",
                    Name = "GPU Linux",
                    RequiredHostPlatform = HostPlatform.Linux,
                    ExecutionBackend = ExecutionBackend.Docker,
                    TargetGroupId = "gpu",
                    RequiredHostCapabilities = ["gpu"]
                }
            ]
        };

        var snapshot = CapacityPlanningService.BuildSnapshot([defaultHost, gpuHost], [], [rule], [], []);

        var mapped = Assert.Single(snapshot.Rules[rule.Id].MappedRunners);
        Assert.Equal(1, mapped.MatchingHosts);
        Assert.Equal(3, mapped.EffectivePoolLimit);
    }

    [Fact]
    public void AnalyzeHostSelection_SkipsWindowsHostsWhenDockerIsInLinuxMode()
    {
        var incompatibleHost = new Host
        {
            Name = "win-linux-mode",
            Platform = HostPlatform.Windows,
            MaxDockerContainers = 4,
            Capabilities = ["docker"],
            Labels = { ["docker"] = "true", ["docker_os"] = "linux" }
        };

        var compatibleHost = new Host
        {
            Name = "win-windows-mode",
            Platform = HostPlatform.Windows,
            MaxDockerContainers = 4,
            Capabilities = ["docker"],
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
    public void AnalyzeHostSelection_AllowsLinuxDockerTargetsOnMacDockerHosts()
    {
        var host = new Host
        {
            Name = "mac-docker",
            Platform = HostPlatform.MacOS,
            AgentStatus = AgentStatus.Online,
            MaxDockerContainers = 2,
            Capabilities = ["docker"]
        };
        var profile = new RunnerProfile
        {
            Name = "linux-docker",
            RequiredHostPlatform = HostPlatform.Linux,
            ExecutionBackend = ExecutionBackend.Docker
        };

        var analysis = CapacityPlanningService.AnalyzeHostSelection(
            profile,
            null,
            [host],
            new Dictionary<string, RunnerProfile> { [profile.Id] = profile },
            [],
            requireDispatchReadiness: true);

        Assert.False(analysis.CapacityBlocked);
        Assert.NotNull(analysis.SelectedHost);
        Assert.Equal(host.Id, analysis.SelectedHost!.Id);
    }

    [Fact]
    public void AnalyzeHostSelection_ExplainsMissingBackendCapabilityWhenDispatching()
    {
        var host = new Host
        {
            Name = "linux-native-only",
            Platform = HostPlatform.Linux,
            AgentStatus = AgentStatus.Online,
            MaxDockerContainers = 2,
            Capabilities = ["native"]
        };
        var profile = new RunnerProfile
        {
            Name = "linux-docker",
            RequiredHostPlatform = HostPlatform.Linux,
            ExecutionBackend = ExecutionBackend.Docker
        };

        var analysis = CapacityPlanningService.AnalyzeHostSelection(
            profile,
            null,
            [host],
            new Dictionary<string, RunnerProfile> { [profile.Id] = profile },
            [],
            requireDispatchReadiness: true);

        Assert.False(analysis.CapacityBlocked);
        Assert.Null(analysis.SelectedHost);
        Assert.Contains("Missing 'docker' capability", analysis.Reason);
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
        var host = new Host { Name = "linux-a", Platform = HostPlatform.Linux, MaxDockerContainers = 10, Capabilities = ["docker"] };
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

    [Fact]
    public void HasEarlierQueuedWorkAhead_BlocksNewerEventInSameCapacityLaneAcrossRules()
    {
        var profileA = CreateProfile("linux-a", HostPlatform.Linux, ExecutionBackend.Docker);
        var profileB = CreateProfile("linux-b", HostPlatform.Linux, ExecutionBackend.Docker);
        var ruleA = CreateWebhookRule("rule-a", profileA.Id, ["ubuntu"]);
        var ruleB = CreateWebhookRule("rule-b", profileB.Id, ["linux"]);
        var now = new DateTime(2026, 5, 18, 12, 0, 0, DateTimeKind.Utc);
        var earlier = CreateQueuedEvent("evt-a", ruleA.Id, "job-a", now, ["ubuntu"], profileA.Id);
        var current = CreateQueuedEvent("evt-b", ruleB.Id, "job-b", now.AddSeconds(1), ["linux"], profileB.Id);

        var result = CapacityPlanningService.HasEarlierQueuedWorkAhead(
            current,
            ruleB,
            profileB,
            [earlier, current],
            new Dictionary<string, ProvisioningRule> { [ruleA.Id] = ruleA, [ruleB.Id] = ruleB },
            new Dictionary<string, RunnerProfile> { [profileA.Id] = profileA, [profileB.Id] = profileB });

        Assert.True(result);
    }

    [Fact]
    public void HasEarlierQueuedWorkAhead_IgnoresEarlierEventInDifferentCapacityLane()
    {
        var dockerProfile = CreateProfile("linux-docker", HostPlatform.Linux, ExecutionBackend.Docker);
        var tartProfile = CreateProfile("mac-tart", HostPlatform.MacOS, ExecutionBackend.Tart);
        var dockerRule = CreateWebhookRule("rule-docker", dockerProfile.Id, ["ubuntu"]);
        var tartRule = CreateWebhookRule("rule-tart", tartProfile.Id, ["macos"]);
        var now = new DateTime(2026, 5, 18, 12, 0, 0, DateTimeKind.Utc);
        var earlier = CreateQueuedEvent("evt-a", dockerRule.Id, "job-a", now, ["ubuntu"], dockerProfile.Id);
        var current = CreateQueuedEvent("evt-b", tartRule.Id, "job-b", now.AddSeconds(1), ["macos"], tartProfile.Id);

        var result = CapacityPlanningService.HasEarlierQueuedWorkAhead(
            current,
            tartRule,
            tartProfile,
            [earlier, current],
            new Dictionary<string, ProvisioningRule> { [dockerRule.Id] = dockerRule, [tartRule.Id] = tartRule },
            new Dictionary<string, RunnerProfile> { [dockerProfile.Id] = dockerProfile, [tartProfile.Id] = tartProfile });

        Assert.False(result);
    }

    [Fact]
    public void ExplainEvent_ShowsFifoWhenOlderSameLaneEventIsStillQueued()
    {
        var host = new Host { Name = "linux-a", Platform = HostPlatform.Linux, MaxDockerContainers = 10, Capabilities = ["docker"] };
        var profile = CreateProfile("linux-docker", HostPlatform.Linux, ExecutionBackend.Docker);
        var rule = CreateWebhookRule("rule-1", profile.Id, ["ubuntu"]);
        var now = new DateTime(2026, 5, 18, 12, 0, 0, DateTimeKind.Utc);
        var earlier = CreateQueuedEvent("evt-a", rule.Id, "job-a", now, ["ubuntu"], profile.Id);
        var current = CreateQueuedEvent("evt-b", rule.Id, "job-b", now.AddSeconds(1), ["ubuntu"], profile.Id);

        var result = CapacityPlanningService.ExplainEvent(
            current,
            [host],
            new Dictionary<string, RunnerProfile> { [profile.Id] = profile },
            new Dictionary<string, ProvisioningRule> { [rule.Id] = rule },
            [],
            [earlier, current]);

        Assert.Equal(CapacityBlockerKind.Fifo, result.BlockedBy);
        Assert.Contains("older queued work", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnalyzeHostSelection_SelectsLeastLoadedReadyHost()
    {
        var hostA = new Host { Name = "linux-a", Platform = HostPlatform.Linux, MaxDockerContainers = 3, Capabilities = ["docker"] };
        var hostB = new Host { Name = "linux-b", Platform = HostPlatform.Linux, MaxDockerContainers = 3, Capabilities = ["docker"] };
        var profile = CreateProfile("linux-docker", HostPlatform.Linux, ExecutionBackend.Docker, maxParallelPerHost: 3);
        var activeOnHostA = new RunnerInstance
        {
            RunnerName = "active-a",
            HostId = hostA.Id,
            ProfileId = profile.Id,
            Status = RunnerInstanceStatus.Running,
            ManagedByRunnerRunner = true
        };

        var analysis = CapacityPlanningService.AnalyzeHostSelection(
            profile,
            null,
            [hostA, hostB],
            new Dictionary<string, RunnerProfile> { [profile.Id] = profile },
            [activeOnHostA]);

        Assert.False(analysis.CapacityBlocked);
        Assert.NotNull(analysis.SelectedHost);
        Assert.Equal(hostB.Id, analysis.SelectedHost!.Id);
    }

    [Fact]
    public void EvaluateRuleCapacity_StaticRuleCountsOnlyHostsMatchingRuleFilters()
    {
        var targetHost = new Host { Name = "target", Platform = HostPlatform.Linux, GroupId = "pool-a", MaxDockerContainers = 2, Capabilities = ["docker"] };
        var otherHost = new Host { Name = "other", Platform = HostPlatform.Linux, GroupId = "pool-b", MaxDockerContainers = 2, Capabilities = ["docker"] };
        var profile = CreateProfile("linux-docker", HostPlatform.Linux, ExecutionBackend.Docker);
        var rule = new ProvisioningRule
        {
            Name = "static-pool-a",
            Type = ProvisioningType.Static,
            ProfileId = profile.Id,
            TargetGroupId = "pool-a",
            DesiredCount = 2
        };
        var matchingInstance = CreateActiveInstance(targetHost.Id, profile.Id, "matching");
        var nonMatchingInstance = CreateActiveInstance(otherHost.Id, profile.Id, "other-pool");

        var result = CapacityPlanningService.EvaluateRuleCapacity(
            rule,
            [targetHost, otherHost],
            new Dictionary<string, RunnerProfile> { [profile.Id] = profile },
            [matchingInstance, nonMatchingInstance],
            []);

        Assert.Equal(2, result.ConfiguredLimit);
        Assert.Equal(1, result.ActiveCount);
        Assert.Equal(1, result.RemainingSlots);
        Assert.Single(result.MappedRunners);
        Assert.Equal(1, result.MappedRunners[0].MatchingHosts);
    }

    [Theory]
    [InlineData(ExecutionBackend.Docker, HostPlatform.Linux)]
    [InlineData(ExecutionBackend.Tart, HostPlatform.MacOS)]
    [InlineData(ExecutionBackend.Native, HostPlatform.Linux)]
    public void AnalyzeHostSelection_BlocksWhenBackendCapacityIsExhausted(
        ExecutionBackend backend,
        HostPlatform platform)
    {
        var host = CreateHostWithBackendLimit("capacity-host", platform, backend, limit: 1);
        var profile = CreateProfile($"{backend}-profile", platform, backend, maxParallelPerHost: 3);
        var activeInstance = CreateActiveInstance(host.Id, profile.Id, "active");

        var analysis = CapacityPlanningService.AnalyzeHostSelection(
            profile,
            null,
            [host],
            new Dictionary<string, RunnerProfile> { [profile.Id] = profile },
            [activeInstance]);

        Assert.True(analysis.CapacityBlocked);
        Assert.Equal(CapacityBlockerKind.Host, analysis.BlockedBy);
        var candidate = Assert.Single(analysis.Candidates);
        Assert.Equal(1, candidate.BackendCapacity.Used);
        Assert.Equal(1, candidate.BackendCapacity.Limit);
        Assert.Equal(0, candidate.BackendCapacity.Remaining);
        Assert.Contains(backend.ToString(), candidate.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExplainEvent_ShowsStoredConfigurationErrorForPendingConfig()
    {
        var evt = new WebhookEvent
        {
            Id = "evt-config",
            Action = "queued",
            Status = "pending_config",
            Error = "Credential 'cred-missing' is missing and will be retried automatically"
        };

        var result = CapacityPlanningService.ExplainEvent(
            evt,
            [],
            new Dictionary<string, RunnerProfile>(),
            new Dictionary<string, ProvisioningRule>(),
            [],
            [evt]);

        Assert.Equal(CapacityBlockerKind.Configuration, result.BlockedBy);
        Assert.Equal(evt.Error, result.Summary);
    }

    [Fact]
    public void ExplainEvent_ShowsMissingRuleWhenRepositoryNoLongerMatches()
    {
        var profile = CreateProfile("linux-docker", HostPlatform.Linux, ExecutionBackend.Docker);
        var rule = CreateWebhookRule("rule-other", profile.Id, ["ubuntu"]);
        rule.AllowedRepos = ["other/repo"];
        var evt = CreateQueuedEvent("evt-1", rule.Id, "job-1", new DateTime(2026, 5, 18, 12, 0, 0, DateTimeKind.Utc), ["ubuntu"], profile.Id);

        var result = CapacityPlanningService.ExplainEvent(
            evt,
            [],
            new Dictionary<string, RunnerProfile> { [profile.Id] = profile },
            new Dictionary<string, ProvisioningRule> { [rule.Id] = rule },
            [],
            [evt]);

        Assert.Equal(CapacityBlockerKind.Matching, result.BlockedBy);
        Assert.Contains("No provisioning rule", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("org/repo", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExplainEvent_ShowsMissingLabelMappingWhenProfileCannotBeResolved()
    {
        var profile = CreateProfile("linux-docker", HostPlatform.Linux, ExecutionBackend.Docker);
        var rule = CreateWebhookRule("rule-1", profile.Id, ["windows"]);
        var evt = CreateQueuedEvent("evt-1", rule.Id, "job-1", new DateTime(2026, 5, 18, 12, 0, 0, DateTimeKind.Utc), ["ubuntu"], profile.Id);

        var result = CapacityPlanningService.ExplainEvent(
            evt,
            [],
            new Dictionary<string, RunnerProfile> { [profile.Id] = profile },
            new Dictionary<string, ProvisioningRule> { [rule.Id] = rule },
            [],
            [evt]);

        Assert.Equal(CapacityBlockerKind.Matching, result.BlockedBy);
        Assert.Contains("No current label mapping", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ubuntu", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    private static RunnerProfile CreateProfile(
        string name,
        HostPlatform platform,
        ExecutionBackend backend,
        int maxParallelPerHost = 1)
        => new()
        {
            Name = name,
            RequiredHostPlatform = platform,
            ExecutionBackend = backend,
            MaxParallelPerHost = maxParallelPerHost
        };

    private static ProvisioningRule CreateWebhookRule(string id, string profileId, List<string> labels)
        => new()
        {
            Id = id,
            Name = id,
            Type = ProvisioningType.Webhook,
            Provider = RunnerProvider.GitHubActions,
            MaxConcurrent = 10,
            LabelMappings =
            [
                new LabelProfileMapping
                {
                    ProfileId = profileId,
                    RequiredLabels = labels
                }
            ]
        };

    private static WebhookEvent CreateQueuedEvent(
        string id,
        string ruleId,
        string jobId,
        DateTime receivedAt,
        List<string> labels,
        string profileId)
        => new()
        {
            Id = id,
            BindingId = ruleId,
            Provider = RunnerProvider.GitHubActions.ToString(),
            Action = "queued",
            JobId = jobId,
            Repository = "org/repo",
            Labels = labels,
            Status = "pending_capacity",
            ReceivedAt = receivedAt,
            MatchedProfileId = profileId
        };

    private static RunnerInstance CreateActiveInstance(string hostId, string profileId, string name)
        => new()
        {
            RunnerName = name,
            HostId = hostId,
            ProfileId = profileId,
            Status = RunnerInstanceStatus.Running,
            ManagedByRunnerRunner = true
        };

    private static Host CreateHostWithBackendLimit(
        string name,
        HostPlatform platform,
        ExecutionBackend backend,
        int limit)
    {
        var host = new Host { Name = name, Platform = platform };

        switch (backend)
        {
            case ExecutionBackend.Docker:
                host.MaxDockerContainers = limit;
                host.Capabilities.Add("docker");
                break;
            case ExecutionBackend.Tart:
                host.MaxTartVMs = limit;
                host.Capabilities.Add("tart");
                break;
            default:
                host.MaxNativeProcesses = limit;
                host.Capabilities.Add("native");
                break;
        }

        return host;
    }
}
