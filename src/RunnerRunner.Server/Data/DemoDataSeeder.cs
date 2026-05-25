using RunnerRunner.Core.HostWorkers;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Services.HostWorkers;
using RunnerRunner.Server.Services.Logs;
using Shiny.DocumentDb;
using Host = RunnerRunner.Core.Models.Host;

namespace RunnerRunner.Server.Data;

public sealed class DemoDataSeeder
{
    private const string Prefix = "demo-";

    private readonly IDocumentStore _store;
    private readonly ObservedLogStore _serverLogs;
    private readonly HostWorkerLogCache _hostLogs;

    public DemoDataSeeder(
        IDocumentStore store,
        ObservedLogStore serverLogs,
        HostWorkerLogCache hostLogs)
    {
        _store = store;
        _serverLogs = serverLogs;
        _hostLogs = hostLogs;
    }

    public async Task<DemoDataSeedResult> SeedAsync()
    {
        await ClearAsync();

        var now = DateTime.UtcNow;
        var credentials = BuildCredentials(now);
        var registries = BuildRegistries(now);
        var envSets = BuildEnvironmentSets(now);
        var hosts = BuildHosts(now);
        var profiles = BuildProfiles(now, credentials, registries, envSets);
        var rules = BuildProvisioningRules(now, profiles, credentials);
        var events = BuildWebhookEvents(now, profiles, rules);
        var instances = BuildRunnerInstances(now, hosts, profiles, rules, events);
        var assignments = BuildAssignments(now, hosts, profiles);
        var images = BuildImages(now, hosts);

        await InsertAll(credentials);
        await InsertAll(registries);
        await InsertAll(envSets);
        await InsertAll(hosts);
        await InsertAll(profiles);
        await InsertAll(rules);
        await InsertAll(events);
        await InsertAll(instances);
        await InsertAll(assignments);
        await InsertAll(images);

        SeedLogs(now, hosts, instances);

        return new DemoDataSeedResult(
            Hosts: hosts.Count,
            Runners: instances.Count,
            Jobs: events.Count + instances.Count,
            Images: images.Count,
            WebhookEvents: events.Count,
            Profiles: profiles.Count,
            ProvisioningRules: rules.Count);
    }

    public async Task ClearAsync()
    {
        await RemoveDemo<AgentImage>();
        await RemoveDemo<RunnerInstance>();
        await RemoveDemo<WebhookEvent>();
        await RemoveDemo<RunnerAssignment>();
        await RemoveDemo<ProvisioningRule>();
        await RemoveDemo<RunnerProfile>();
        await RemoveDemo<EnvironmentVariableSet>();
        await RemoveDemo<RegistryCredential>();
        await RemoveDemo<ProviderCredential>();
        await RemoveDemo<Host>();
    }

    private async Task InsertAll<T>(IEnumerable<T> documents)
        where T : class
    {
        foreach (var document in documents)
            await _store.Insert(document);
    }

    private async Task RemoveDemo<T>()
        where T : class
    {
        var items = await _store.Query<T>().ToList();
        foreach (var item in items)
        {
            var id = item.GetType().GetProperty("Id")?.GetValue(item)?.ToString();
            if (!string.IsNullOrWhiteSpace(id) && id.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                await _store.Remove<T>(id);
        }
    }

    private static List<ProviderCredential> BuildCredentials(DateTime now) =>
    [
        new()
        {
            Id = "demo-credential-github",
            Name = "Demo GitHub App",
            Provider = RunnerProvider.GitHubActions,
            GitHubOrg = "redth-labs",
            GitHubRepo = "runner-ops",
            GitHubServerUrl = "https://github.com",
            GitHubApiUrl = "https://api.github.com",
            GitHubAuthType = GitHubAuthType.GitHubApp,
            GitHubAppId = "123456",
            GitHubAppInstallationId = "987654",
            GitHubToken = "demo-token-not-real",
            CreatedAt = now.AddDays(-15),
            UpdatedAt = now.AddMinutes(-30)
        },
        new()
        {
            Id = "demo-credential-azdo",
            Name = "Demo Azure DevOps",
            Provider = RunnerProvider.AzureDevOps,
            AzDoOrgUrl = "https://dev.azure.com/redth-labs",
            AzDoProjectName = "Runner Platform",
            AzDoPoolName = "RunnerRunner Demo",
            AzDoPat = "demo-pat-not-real",
            CreatedAt = now.AddDays(-11),
            UpdatedAt = now.AddHours(-3)
        },
        new()
        {
            Id = "demo-credential-gitea",
            Name = "Demo Gitea",
            Provider = RunnerProvider.GiteaActions,
            GiteaInstanceUrl = "https://git.demo.internal",
            GiteaRunnerToken = "demo-gitea-token-not-real",
            CreatedAt = now.AddDays(-7),
            UpdatedAt = now.AddHours(-4)
        }
    ];

    private static List<RegistryCredential> BuildRegistries(DateTime now) =>
    [
        new()
        {
            Id = "demo-registry-ghcr",
            Name = "GitHub Container Registry",
            RegistryUrl = "ghcr.io",
            RegistryType = RegistryType.Docker,
            DefaultNamespace = "redth-labs",
            Username = "runner-bot",
            Password = "demo-registry-token-not-real",
            IsDefault = true,
            CreatedAt = now.AddDays(-18),
            UpdatedAt = now.AddHours(-1)
        },
        new()
        {
            Id = "demo-registry-tart",
            Name = "Tart OCI Images",
            RegistryUrl = "ghcr.io",
            RegistryType = RegistryType.Tart,
            DefaultNamespace = "cirruslabs",
            IsDefault = false,
            CreatedAt = now.AddDays(-10),
            UpdatedAt = now.AddHours(-2)
        }
    ];

    private static List<EnvironmentVariableSet> BuildEnvironmentSets(DateTime now) =>
    [
        new()
        {
            Id = "demo-env-common",
            Name = "Common CI Defaults",
            Description = "Shared tracing, cache, and provider settings for demo runners.",
            Priority = 100,
            Variables = new Dictionary<string, string>
            {
                ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
                ["NUGET_XMLDOC_MODE"] = "skip",
                ["RUNNERRUNNER_ENV"] = "demo",
                ["RR_GITHUB_TOKEN"] = "$RR_GITHUB_TOKEN"
            },
            SecretKeys = ["RR_GITHUB_TOKEN"],
            CreatedAt = now.AddDays(-20),
            UpdatedAt = now.AddHours(-2)
        },
        new()
        {
            Id = "demo-env-mobile",
            Name = "Mobile Build Toolchain",
            Description = "Xcode, Android, and signing hints for macOS runners.",
            Priority = 200,
            Variables = new Dictionary<string, string>
            {
                ["ANDROID_HOME"] = "/Users/runner/Library/Android/sdk",
                ["DEVELOPER_DIR"] = "/Applications/Xcode_16.4.app/Contents/Developer",
                ["MATCH_KEYCHAIN_NAME"] = "demo-signing.keychain"
            },
            SecretKeys = ["MATCH_KEYCHAIN_PASSWORD"],
            CreatedAt = now.AddDays(-13),
            UpdatedAt = now.AddHours(-5)
        }
    ];

    private static List<Host> BuildHosts(DateTime now) =>
    [
        new()
        {
            Id = "demo-host-linux-01",
            Name = "rr-linux-01.demo.internal",
            DisplayName = "Linux Build Farm 01",
            Platform = HostPlatform.Linux,
            AgentStatus = AgentStatus.Online,
            LastHeartbeat = now.AddSeconds(-18),
            AgentVersion = "1.0.0-demo.42",
            LatestAvailableVersion = "1.0.0-demo.44",
            UpdateStatus = "update_available",
            UpdateMessage = "2 demo builds behind",
            OsVersion = "Ubuntu 24.04.2 LTS",
            Architecture = "x64",
            IsContainerized = false,
            Capabilities = ["docker", "linux", "x64", "node20", "dotnet10"],
            Labels = new Dictionary<string, string>
            {
                ["os"] = "linux",
                ["arch"] = "x64",
                ["pool"] = "linux-build",
                ["docker"] = "true"
            },
            MaxDockerContainers = 8,
            MaxTartVMs = 0,
            MaxNativeProcesses = 4,
            GroupId = "linux-build",
            IsApproved = true,
            CreatedAt = now.AddDays(-21),
            UpdatedAt = now.AddSeconds(-18)
        },
        new()
        {
            Id = "demo-host-linux-gpu",
            Name = "rr-linux-gpu-01.demo.internal",
            DisplayName = "GPU Linux Worker",
            Platform = HostPlatform.Linux,
            AgentStatus = AgentStatus.Connecting,
            LastHeartbeat = now.AddMinutes(-4),
            AgentVersion = "1.0.0-demo.44",
            LatestAvailableVersion = "1.0.0-demo.44",
            UpdateStatus = "current",
            OsVersion = "Ubuntu 24.04.2 LTS",
            Architecture = "arm64",
            IsContainerized = true,
            ContainerImage = "ghcr.io/redth-labs/hostworker:demo",
            ContainerId = "a8f7e2c4d901",
            Capabilities = ["docker", "linux", "arm64", "gpu"],
            Labels = new Dictionary<string, string>
            {
                ["os"] = "linux",
                ["arch"] = "arm64",
                ["pool"] = "linux-gpu",
                ["gpu"] = "nvidia-l4"
            },
            MaxDockerContainers = 3,
            MaxTartVMs = 0,
            MaxNativeProcesses = 2,
            GroupId = "linux-gpu",
            IsApproved = true,
            CreatedAt = now.AddDays(-8),
            UpdatedAt = now.AddMinutes(-4)
        },
        new()
        {
            Id = "demo-host-macos-01",
            Name = "rr-mac-mini-01.local",
            DisplayName = "Mac Mini M4",
            Platform = HostPlatform.MacOS,
            AgentStatus = AgentStatus.Online,
            LastHeartbeat = now.AddSeconds(-9),
            AgentVersion = "1.0.0-demo.44",
            LatestAvailableVersion = "1.0.0-demo.44",
            UpdateStatus = "current",
            OsVersion = "macOS 15.5",
            Architecture = "arm64",
            Capabilities = ["tart", "macos", "arm64", "xcode16", "ios"],
            Labels = new Dictionary<string, string>
            {
                ["os"] = "macos",
                ["arch"] = "arm64",
                ["pool"] = "macos-arm64",
                ["xcode"] = "16.4"
            },
            MaxDockerContainers = 0,
            MaxTartVMs = 3,
            MaxNativeProcesses = 2,
            ObservedRunningTartVMs = 1,
            ObservedResourceUsageAt = now.AddSeconds(-30),
            GroupId = "macos-arm64",
            IsApproved = true,
            CreatedAt = now.AddDays(-12),
            UpdatedAt = now.AddSeconds(-9)
        },
        new()
        {
            Id = "demo-host-windows-01",
            Name = "rr-win-01.demo.internal",
            DisplayName = "Windows Native 01",
            Platform = HostPlatform.Windows,
            AgentStatus = AgentStatus.Offline,
            LastHeartbeat = now.AddMinutes(-38),
            AgentVersion = "1.0.0-demo.41",
            LatestAvailableVersion = "1.0.0-demo.44",
            UpdateStatus = "offline",
            UpdateMessage = "Host has missed recent heartbeats.",
            OsVersion = "Windows Server 2025",
            Architecture = "x64",
            Capabilities = ["windows", "x64", "native", "msbuild"],
            Labels = new Dictionary<string, string>
            {
                ["os"] = "windows",
                ["arch"] = "x64",
                ["pool"] = "windows-native"
            },
            MaxDockerContainers = 0,
            MaxTartVMs = 0,
            MaxNativeProcesses = 5,
            GroupId = "windows-native",
            IsApproved = true,
            CreatedAt = now.AddDays(-16),
            UpdatedAt = now.AddMinutes(-38)
        }
    ];

    private static List<RunnerProfile> BuildProfiles(
        DateTime now,
        IReadOnlyList<ProviderCredential> credentials,
        IReadOnlyList<RegistryCredential> registries,
        IReadOnlyList<EnvironmentVariableSet> envSets)
    {
        var githubCredentialId = credentials.Single(x => x.Id == "demo-credential-github").Id;
        var azdoCredentialId = credentials.Single(x => x.Id == "demo-credential-azdo").Id;
        var ghcrId = registries.Single(x => x.Id == "demo-registry-ghcr").Id;

        return
        [
            new()
            {
                Id = "demo-profile-linux-docker",
                Name = "Linux Docker Large",
                Description = "Default high-throughput Linux container runner.",
                Provider = RunnerProvider.GitHubActions,
                ProviderCredentialId = githubCredentialId,
                RequiredHostPlatform = HostPlatform.Linux,
                ExecutionBackend = ExecutionBackend.Docker,
                DockerConfig = new DockerImageConfig
                {
                    RegistryUrl = "ghcr.io",
                    ImageName = "redth-labs/runner-dotnet",
                    Tag = "jammy-dotnet10",
                    CredentialId = ghcrId
                },
                EnvironmentVariableSetIds = [envSets.Single(x => x.Id == "demo-env-common").Id],
                Labels = ["self-hosted", "linux", "x64", "docker", "rr-demo"],
                RunnerGroup = "RunnerRunner Demo",
                Ephemeral = true,
                MaxParallelPerHost = 3,
                AllowWebhookImageTagOverride = true,
                CreatedAt = now.AddDays(-20),
                UpdatedAt = now.AddHours(-2)
            },
            new()
            {
                Id = "demo-profile-macos-tart",
                Name = "macOS Tart Xcode",
                Description = "Ephemeral macOS VM runners for Apple builds.",
                Provider = RunnerProvider.GitHubActions,
                ProviderCredentialId = githubCredentialId,
                RequiredHostPlatform = HostPlatform.MacOS,
                ExecutionBackend = ExecutionBackend.Tart,
                TartConfig = new TartImageConfig
                {
                    RegistryUrl = "ghcr.io",
                    ImageName = "cirruslabs/macos-runner",
                    Tag = "sonoma-xcode16.4",
                    CpuCount = 6,
                    MemorySizeGb = 14,
                    DiskSizeGb = 80,
                    Display = "1024x768"
                },
                EnvironmentVariableSetIds =
                [
                    envSets.Single(x => x.Id == "demo-env-common").Id,
                    envSets.Single(x => x.Id == "demo-env-mobile").Id
                ],
                Labels = ["self-hosted", "macos", "arm64", "xcode16", "rr-demo"],
                RunnerGroup = "Apple Builds",
                Ephemeral = true,
                MaxParallelPerHost = 2,
                AllowWebhookImageTagOverride = true,
                CreatedAt = now.AddDays(-12),
                UpdatedAt = now.AddHours(-1)
            },
            new()
            {
                Id = "demo-profile-windows-native",
                Name = "Windows Native MSBuild",
                Description = "Native Windows runner for installer and signing jobs.",
                Provider = RunnerProvider.AzureDevOps,
                ProviderCredentialId = azdoCredentialId,
                RequiredHostPlatform = HostPlatform.Windows,
                ExecutionBackend = ExecutionBackend.Native,
                Labels = ["self-hosted", "windows", "x64", "msbuild", "rr-demo"],
                RunnerGroup = "Windows",
                Ephemeral = false,
                MaxParallelPerHost = 2,
                CreatedAt = now.AddDays(-16),
                UpdatedAt = now.AddHours(-8)
            }
        ];
    }

    private static List<ProvisioningRule> BuildProvisioningRules(
        DateTime now,
        IReadOnlyList<RunnerProfile> profiles,
        IReadOnlyList<ProviderCredential> credentials)
    {
        var linuxProfile = profiles.Single(x => x.Id == "demo-profile-linux-docker");
        var macProfile = profiles.Single(x => x.Id == "demo-profile-macos-tart");
        var windowsProfile = profiles.Single(x => x.Id == "demo-profile-windows-native");
        var githubCredentialId = credentials.Single(x => x.Id == "demo-credential-github").Id;

        return
        [
            new()
            {
                Id = "demo-rule-linux-warm-pool",
                Name = "Linux Warm Pool",
                Description = "Keep a small pool of Docker runners ready for common CI.",
                Type = ProvisioningType.Static,
                ProfileId = linuxProfile.Id,
                DesiredCount = 3,
                TargetGroupId = "linux-build",
                RequiredHostLabels = new Dictionary<string, string> { ["pool"] = "linux-build" },
                Enabled = true,
                CreatedAt = now.AddDays(-19),
                UpdatedAt = now.AddHours(-1)
            },
            new()
            {
                Id = "demo-rule-macos-scale-set",
                Name = "macOS Scale Set",
                Description = "Burst macOS VM capacity for mobile builds.",
                Type = ProvisioningType.ScaleSet,
                ProfileId = macProfile.Id,
                MinReady = 1,
                MaxInstances = 5,
                ScaleDownDelaySeconds = 900,
                TargetGroupId = "macos-arm64",
                RequiredHostLabels = new Dictionary<string, string> { ["pool"] = "macos-arm64" },
                Enabled = true,
                CreatedAt = now.AddDays(-12),
                UpdatedAt = now.AddHours(-3)
            },
            new()
            {
                Id = "demo-rule-github-jit",
                Name = "GitHub JIT Jobs",
                Description = "Route workflow_job webhooks to Docker or Tart profiles by labels.",
                Type = ProvisioningType.Webhook,
                ProfileId = linuxProfile.Id,
                DefaultProfileId = linuxProfile.Id,
                Provider = RunnerProvider.GitHubActions,
                ProviderCredentialId = githubCredentialId,
                WebhookSecret = "demo-webhook-secret-not-real",
                AllowedOrgs = ["redth-labs"],
                AllowedRepos = ["redth-labs/runner-ops", "redth-labs/mobile-app"],
                LabelMappings =
                [
                    new() { RequiredLabels = ["self-hosted", "macos", "xcode16"], ProfileId = macProfile.Id, Priority = 100 },
                    new() { RequiredLabels = ["self-hosted", "linux"], ProfileId = linuxProfile.Id, Priority = 50 }
                ],
                MaxConcurrent = 6,
                CooldownSeconds = 10,
                Enabled = true,
                CreatedAt = now.AddDays(-10),
                UpdatedAt = now.AddMinutes(-45)
            },
            new()
            {
                Id = "demo-rule-windows-maintenance",
                Name = "Windows Maintenance",
                Description = "Disabled maintenance rule to show off warning/disabled UI states.",
                Type = ProvisioningType.Static,
                ProfileId = windowsProfile.Id,
                DesiredCount = 1,
                TargetGroupId = "windows-native",
                RequiredHostLabels = new Dictionary<string, string> { ["pool"] = "windows-native" },
                Enabled = false,
                CreatedAt = now.AddDays(-14),
                UpdatedAt = now.AddDays(-1)
            }
        ];
    }

    private static List<WebhookEvent> BuildWebhookEvents(
        DateTime now,
        IReadOnlyList<RunnerProfile> profiles,
        IReadOnlyList<ProvisioningRule> rules)
    {
        var linux = profiles.Single(x => x.Id == "demo-profile-linux-docker");
        var mac = profiles.Single(x => x.Id == "demo-profile-macos-tart");
        var jitRule = rules.Single(x => x.Id == "demo-rule-github-jit");
        var macRule = rules.Single(x => x.Id == "demo-rule-macos-scale-set");

        return
        [
            new()
            {
                Id = "demo-event-api-tests",
                BindingId = jitRule.Id,
                Provider = "GitHubActions",
                Action = "queued",
                JobId = "927341001",
                RunId = "88100231",
                Repository = "redth-labs/runner-ops",
                WorkflowName = "CI / API Tests",
                Labels = ["self-hosted", "linux", "x64", "rr-image-tag=jammy-dotnet10"],
                MatchedProfileId = linux.Id,
                MatchedProfileName = linux.Name,
                InstanceId = "demo-runner-api-tests",
                ImageTagOverride = "jammy-dotnet10",
                Status = "in_progress",
                ReceivedAt = now.AddMinutes(-24),
                UpdatedAt = now.AddMinutes(-21),
                LastAttemptAt = now.AddMinutes(-22),
                NextRetryAt = null,
                ExpiresAt = null
            },
            new()
            {
                Id = "demo-event-mobile-build",
                BindingId = jitRule.Id,
                Provider = "GitHubActions",
                Action = "queued",
                JobId = "927341118",
                RunId = "88100302",
                Repository = "redth-labs/mobile-app",
                WorkflowName = "iOS Release",
                Labels = ["self-hosted", "macos", "arm64", "xcode16"],
                MatchedProfileId = mac.Id,
                MatchedProfileName = mac.Name,
                InstanceId = "demo-runner-ios-release",
                Status = "provisioned",
                ReceivedAt = now.AddMinutes(-15),
                UpdatedAt = now.AddMinutes(-12),
                LastAttemptAt = now.AddMinutes(-12),
                NextRetryAt = null,
                ExpiresAt = null
            },
            new()
            {
                Id = "demo-event-linux-capacity",
                BindingId = jitRule.Id,
                Provider = "GitHubActions",
                Action = "queued",
                JobId = "927341304",
                RunId = "88100416",
                Repository = "redth-labs/runner-ops",
                WorkflowName = "Container Matrix",
                Labels = ["self-hosted", "linux", "docker"],
                MatchedProfileId = linux.Id,
                MatchedProfileName = linux.Name,
                Status = "pending_capacity",
                Error = "Linux Docker Large is at per-host parallelism limit on Linux Build Farm 01.",
                ReceivedAt = now.AddMinutes(-8),
                UpdatedAt = now.AddMinutes(-2),
                LastAttemptAt = now.AddMinutes(-2),
                NextRetryAt = now.AddHours(2),
                RetryCount = 2
            },
            new()
            {
                Id = "demo-event-no-host",
                BindingId = macRule.Id,
                Provider = "GitHubActions",
                Action = "queued",
                JobId = "927341455",
                RunId = "88100501",
                Repository = "redth-labs/mobile-app",
                WorkflowName = "macOS UI Tests",
                Labels = ["self-hosted", "macos", "xcode16", "gpu"],
                MatchedProfileId = mac.Id,
                MatchedProfileName = mac.Name,
                Status = "pending_host_match",
                Error = "No online host currently satisfies label gpu for the macOS profile.",
                ReceivedAt = now.AddMinutes(-5),
                UpdatedAt = now.AddMinutes(-1),
                LastAttemptAt = now.AddMinutes(-1),
                NextRetryAt = now.AddHours(2),
                RetryCount = 1
            },
            new()
            {
                Id = "demo-event-completed",
                BindingId = jitRule.Id,
                Provider = "GitHubActions",
                Action = "completed",
                JobId = "927340904",
                RunId = "88099888",
                Repository = "redth-labs/runner-ops",
                WorkflowName = "CI / Lint",
                Labels = ["self-hosted", "linux", "x64"],
                MatchedProfileId = linux.Id,
                MatchedProfileName = linux.Name,
                InstanceId = "demo-runner-lint-completed",
                Status = "completed",
                ReceivedAt = now.AddHours(-1).AddMinutes(-12),
                UpdatedAt = now.AddHours(-1).AddMinutes(-4),
                ResolvedAt = now.AddHours(-1).AddMinutes(-4),
                NextRetryAt = null,
                ExpiresAt = null
            },
            new()
            {
                Id = "demo-event-ignored-scope",
                Provider = "GitHubActions",
                Action = "queued",
                JobId = "927340455",
                RunId = "88099123",
                Repository = "unknown/fork",
                WorkflowName = "Pull Request",
                Labels = ["self-hosted", "linux"],
                Status = WebhookEvent.StatusIgnoredScope,
                Error = "Repository/org is not handled by any enabled webhook rule.",
                ReceivedAt = now.AddHours(-2),
                UpdatedAt = now.AddHours(-2).AddMinutes(1),
                ResolvedAt = now.AddHours(-2).AddMinutes(1),
                NextRetryAt = null,
                ExpiresAt = null
            }
        ];
    }

    private static List<RunnerInstance> BuildRunnerInstances(
        DateTime now,
        IReadOnlyList<Host> hosts,
        IReadOnlyList<RunnerProfile> profiles,
        IReadOnlyList<ProvisioningRule> rules,
        IReadOnlyList<WebhookEvent> events)
    {
        var linux = profiles.Single(x => x.Id == "demo-profile-linux-docker");
        var mac = profiles.Single(x => x.Id == "demo-profile-macos-tart");
        var windows = profiles.Single(x => x.Id == "demo-profile-windows-native");
        var linuxHost = hosts.Single(x => x.Id == "demo-host-linux-01");
        var gpuHost = hosts.Single(x => x.Id == "demo-host-linux-gpu");
        var macHost = hosts.Single(x => x.Id == "demo-host-macos-01");
        var windowsHost = hosts.Single(x => x.Id == "demo-host-windows-01");
        var linuxRule = rules.Single(x => x.Id == "demo-rule-linux-warm-pool");
        var jitRule = rules.Single(x => x.Id == "demo-rule-github-jit");
        var macRule = rules.Single(x => x.Id == "demo-rule-macos-scale-set");
        var windowsRule = rules.Single(x => x.Id == "demo-rule-windows-maintenance");

        return
        [
            BuildInstance(
                "demo-runner-api-tests",
                "rr-linux-api-tests-9f3a",
                linuxHost.Id,
                linux.Id,
                RunnerInstanceStatus.Running,
                "dynamic",
                now.AddMinutes(-23),
                "demo-event-api-tests",
                jitRule.Id,
                "927341001",
                containerId: "rr-api-tests-9f3a",
                statusMessage: "Connected to GitHub; executing test shard 3/8."),
            BuildInstance(
                "demo-runner-linux-static-1",
                "rr-linux-warm-01",
                linuxHost.Id,
                linux.Id,
                RunnerInstanceStatus.Running,
                "static",
                now.AddHours(-3),
                provisioningRuleId: linuxRule.Id,
                containerId: "rr-warm-01",
                statusMessage: "Idle and ready for queued jobs."),
            BuildInstance(
                "demo-runner-linux-static-2",
                "rr-linux-warm-02",
                linuxHost.Id,
                linux.Id,
                RunnerInstanceStatus.Starting,
                "static",
                now.AddMinutes(-4),
                provisioningRuleId: linuxRule.Id,
                containerId: "rr-warm-02",
                statusMessage: "Pulling runner image ghcr.io/redth-labs/runner-dotnet:jammy-dotnet10."),
            BuildInstance(
                "demo-runner-gpu-smoke",
                "rr-gpu-smoke-7c1e",
                gpuHost.Id,
                linux.Id,
                RunnerInstanceStatus.Pending,
                "dynamic",
                now.AddMinutes(-3),
                provisioningRuleId: jitRule.Id,
                jobId: "927341512",
                containerId: "rr-gpu-smoke-7c1e",
                statusMessage: "Waiting for HostWorker reconnect."),
            BuildInstance(
                "demo-runner-ios-release",
                "rr-macos-ios-release-b87d",
                macHost.Id,
                mac.Id,
                RunnerInstanceStatus.Starting,
                "dynamic",
                now.AddMinutes(-13),
                "demo-event-mobile-build",
                macRule.Id,
                "927341118",
                vmName: "rr-ios-release-b87d",
                statusMessage: "Booting Tart VM and preparing Xcode cache."),
            BuildInstance(
                "demo-runner-macos-ready",
                "rr-macos-ready-01",
                macHost.Id,
                mac.Id,
                RunnerInstanceStatus.Running,
                "static",
                now.AddHours(-1).AddMinutes(-8),
                provisioningRuleId: macRule.Id,
                vmName: "rr-macos-ready-01",
                statusMessage: "Registered and idle."),
            BuildInstance(
                "demo-runner-windows-signing",
                "rr-win-signing-01",
                windowsHost.Id,
                windows.Id,
                RunnerInstanceStatus.Failed,
                "static",
                now.AddMinutes(-47),
                provisioningRuleId: windowsRule.Id,
                processId: 44128,
                statusMessage: "Host went offline during job startup.",
                errorMessage: "Lost HostWorker heartbeat before runner registration completed."),
            BuildInstance(
                "demo-runner-lint-completed",
                "rr-linux-lint-2d4c",
                linuxHost.Id,
                linux.Id,
                RunnerInstanceStatus.Stopped,
                "dynamic",
                now.AddHours(-1).AddMinutes(-10),
                "demo-event-completed",
                jitRule.Id,
                "927340904",
                containerId: "rr-lint-2d4c",
                stoppedAt: now.AddHours(-1).AddMinutes(-4),
                statusMessage: "Runner removed after successful ephemeral job.")
        ];
    }

    private static RunnerInstance BuildInstance(
        string id,
        string name,
        string hostId,
        string profileId,
        RunnerInstanceStatus status,
        string mode,
        DateTime createdAt,
        string? webhookEventId = null,
        string? provisioningRuleId = null,
        string? jobId = null,
        string? containerId = null,
        string? vmName = null,
        int? processId = null,
        DateTime? stoppedAt = null,
        string? statusMessage = null,
        string? errorMessage = null)
    {
        DateTime? startedAt = status is RunnerInstanceStatus.Running or RunnerInstanceStatus.Stopping or RunnerInstanceStatus.Stopped
            ? createdAt.AddMinutes(1)
            : null;

        return new RunnerInstance
        {
            Id = id,
            RunnerName = name,
            HostId = hostId,
            ProfileId = profileId,
            Status = status,
            ProvisioningMode = mode,
            WebhookEventId = webhookEventId,
            ProvisioningRuleId = provisioningRuleId,
            JobId = jobId,
            ContainerId = containerId,
            VmName = vmName,
            ProcessId = processId,
            ManagedByRunnerRunner = true,
            CreatedAt = createdAt,
            DeployedAt = createdAt.AddSeconds(45),
            StartedAt = startedAt,
            StoppedAt = stoppedAt,
            LastHealthCheck = status is RunnerInstanceStatus.Running or RunnerInstanceStatus.Starting
                ? DateTime.UtcNow.AddSeconds(-Random.Shared.Next(12, 90))
                : null,
            StatusMessage = statusMessage,
            ErrorMessage = errorMessage,
            StatusHistory =
            [
                new() { Timestamp = createdAt, Status = RunnerInstanceStatus.Pending, Source = "webhook", StatusMessage = "Instance record created." },
                new() { Timestamp = createdAt.AddSeconds(35), Status = RunnerInstanceStatus.Starting, Source = "grain_call", StatusMessage = "Dispatching runner startup command." },
                new() { Timestamp = stoppedAt ?? createdAt.AddMinutes(1), Status = status, Source = status is RunnerInstanceStatus.Failed ? "health_check" : "hostworker", StatusMessage = statusMessage }
            ]
        };
    }

    private static List<RunnerAssignment> BuildAssignments(
        DateTime now,
        IReadOnlyList<Host> hosts,
        IReadOnlyList<RunnerProfile> profiles)
    {
        var linuxHost = hosts.Single(x => x.Id == "demo-host-linux-01");
        var macHost = hosts.Single(x => x.Id == "demo-host-macos-01");
        var linux = profiles.Single(x => x.Id == "demo-profile-linux-docker");
        var mac = profiles.Single(x => x.Id == "demo-profile-macos-tart");

        return
        [
            new()
            {
                Id = "demo-assignment-linux",
                HostId = linuxHost.Id,
                ProfileId = linux.Id,
                DesiredCount = 3,
                CreatedAt = now.AddDays(-19),
                UpdatedAt = now.AddHours(-1)
            },
            new()
            {
                Id = "demo-assignment-macos",
                HostId = macHost.Id,
                ProfileId = mac.Id,
                DesiredCount = 1,
                CreatedAt = now.AddDays(-12),
                UpdatedAt = now.AddHours(-2)
            }
        ];
    }

    private static List<AgentImage> BuildImages(DateTime now, IReadOnlyList<Host> hosts)
    {
        var linuxHost = hosts.Single(x => x.Id == "demo-host-linux-01");
        var gpuHost = hosts.Single(x => x.Id == "demo-host-linux-gpu");
        var macHost = hosts.Single(x => x.Id == "demo-host-macos-01");

        return
        [
            new()
            {
                Id = "demo-image-linux-dotnet",
                HostId = linuxHost.Id,
                ImageType = ImageType.Docker,
                Repository = "ghcr.io/redth-labs/runner-dotnet",
                Tag = "jammy-dotnet10",
                ImageId = "sha256:5f7ed3a2c901ddc4",
                SizeBytes = 2_980_000_000,
                ImageCreatedAt = now.AddDays(-4),
                LastReportedAt = now.AddMinutes(-6)
            },
            new()
            {
                Id = "demo-image-linux-node",
                HostId = linuxHost.Id,
                ImageType = ImageType.Docker,
                Repository = "ghcr.io/redth-labs/runner-node",
                Tag = "node22-bookworm",
                ImageId = "sha256:7ad18e9b3140ac12",
                SizeBytes = 1_720_000_000,
                ImageCreatedAt = now.AddDays(-6),
                LastReportedAt = now.AddMinutes(-6)
            },
            new()
            {
                Id = "demo-image-gpu-cuda",
                HostId = gpuHost.Id,
                ImageType = ImageType.Docker,
                Repository = "nvidia/cuda",
                Tag = "12.6.2-runtime-ubuntu24.04",
                ImageId = "sha256:c2a54128d7b300ff",
                SizeBytes = 5_860_000_000,
                ImageCreatedAt = now.AddDays(-9),
                LastReportedAt = now.AddMinutes(-18)
            },
            new()
            {
                Id = "demo-image-macos-sonoma",
                HostId = macHost.Id,
                ImageType = ImageType.Tart,
                Repository = "ghcr.io/cirruslabs/macos-runner",
                Tag = "sonoma-xcode16.4",
                ImageId = "oci:macos-sonoma-xcode16.4",
                SizeBytes = 42_400_000_000,
                ImageCreatedAt = now.AddDays(-3),
                LastReportedAt = now.AddMinutes(-4)
            },
            new()
            {
                Id = "demo-image-macos-sequoia",
                HostId = macHost.Id,
                ImageType = ImageType.Tart,
                Repository = "ghcr.io/cirruslabs/macos-runner",
                Tag = "sequoia-xcode26-beta",
                ImageId = "oci:macos-sequoia-xcode26-beta",
                SizeBytes = 45_100_000_000,
                ImageCreatedAt = now.AddDays(-1),
                LastReportedAt = now.AddMinutes(-4)
            }
        ];
    }

    private void SeedLogs(DateTime now, IReadOnlyList<Host> hosts, IReadOnlyList<RunnerInstance> instances)
    {
        AddServerLog(now.AddMinutes(-24), ObservedLogLevel.Information, "RunnerRunner.Server.Services.DynamicProvisioningService", "Accepted demo webhook event demo-event-api-tests.");
        AddServerLog(now.AddMinutes(-23), ObservedLogLevel.Information, "RunnerRunner.Server.Grains.ProvisioningRuleGrain", "Matched GitHub JIT Jobs -> Linux Docker Large.");
        AddServerLog(now.AddMinutes(-12), ObservedLogLevel.Warning, "RunnerRunner.Server.Services.CapacityPlanningService", "macOS Scale Set is nearing Tart VM capacity: 2/3 active.");
        AddServerLog(now.AddMinutes(-8), ObservedLogLevel.Warning, "RunnerRunner.Server.Services.DynamicProvisioningService", "Queued demo-event-linux-capacity while profile limit recovers.");
        AddServerLog(now.AddMinutes(-1), ObservedLogLevel.Information, "RunnerRunner.Server.Services.StreamSubscriptionService", "Demo stream snapshot refreshed.");

        var linux = hosts.Single(x => x.Id == "demo-host-linux-01");
        var mac = hosts.Single(x => x.Id == "demo-host-macos-01");
        var apiRunner = instances.Single(x => x.Id == "demo-runner-api-tests");
        var iosRunner = instances.Single(x => x.Id == "demo-runner-ios-release");

        AddHostFrame(linux, "demo-host-linux-worker", ObservedLogSourceType.Host, ObservedLogStreamKind.Worker, null, now.AddMinutes(-23), 0, "HostWorker connected; 8 Docker slots available.\n");
        AddHostFrame(linux, "demo-runner-api-tests", ObservedLogSourceType.Runner, ObservedLogStreamKind.Stdout, apiRunner.Id, now.AddMinutes(-22), 1, "Pulling ghcr.io/redth-labs/runner-dotnet:jammy-dotnet10...\n");
        AddHostFrame(linux, "demo-runner-api-tests", ObservedLogSourceType.Runner, ObservedLogStreamKind.Stdout, apiRunner.Id, now.AddMinutes(-21), 2, "Runner rr-linux-api-tests-9f3a registered with labels: self-hosted, linux, x64, docker.\n");
        AddHostFrame(linux, "demo-runner-api-tests", ObservedLogSourceType.Runner, ObservedLogStreamKind.Stdout, apiRunner.Id, now.AddMinutes(-6), 3, "dotnet test completed 418 tests; 0 failed; 2 skipped.\n");
        AddHostFrame(mac, "demo-host-macos-worker", ObservedLogSourceType.Host, ObservedLogStreamKind.Worker, null, now.AddMinutes(-13), 0, "Preparing Tart VM rr-ios-release-b87d from sonoma-xcode16.4.\n");
        AddHostFrame(mac, "demo-runner-ios-release", ObservedLogSourceType.Runner, ObservedLogStreamKind.Stdout, iosRunner.Id, now.AddMinutes(-11), 1, "Booted VM; warming DerivedData and restoring Swift packages.\n");
    }

    private void AddServerLog(DateTime timestamp, ObservedLogLevel level, string category, string message)
    {
        _serverLogs.Add(new ObservedLogEntry
        {
            Id = $"{Prefix}log-{Guid.NewGuid():N}",
            Timestamp = timestamp,
            SourceType = ObservedLogSourceType.Server,
            SourceId = "server",
            SourceName = "Server",
            StreamKind = ObservedLogStreamKind.Application,
            Category = category,
            Level = level,
            Message = message,
            RenderedMessage = message
        });
    }

    private void AddHostFrame(
        Host host,
        string streamId,
        ObservedLogSourceType sourceType,
        ObservedLogStreamKind streamKind,
        string? runnerInstanceId,
        DateTime timestamp,
        long offset,
        string text)
    {
        _hostLogs.Ingest(host.Id, new HostWorkerLogFrame
        {
            StreamId = streamId,
            StreamKind = streamKind.ToString(),
            SourceType = sourceType.ToString(),
            SourceName = runnerInstanceId is null ? host.Label : streamId,
            RunnerInstanceId = runnerInstanceId,
            Category = runnerInstanceId is null ? "HostWorker" : "Runner",
            Level = ObservedLogLevel.Information.ToString(),
            Backend = runnerInstanceId is null ? null : ExecutionBackend.Docker.ToString(),
            Provider = RunnerProvider.GitHubActions.ToString(),
            Offset = offset,
            Text = text,
            Timestamp = timestamp
        });
    }
}

public sealed record DemoDataSeedResult(
    int Hosts,
    int Runners,
    int Jobs,
    int Images,
    int WebhookEvents,
    int Profiles,
    int ProvisioningRules);
