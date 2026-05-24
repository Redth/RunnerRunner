using System.Security.Cryptography;
using System.Text;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Grains.Interfaces;
using RunnerRunner.Server.Grains.State;
using RunnerRunner.Server.Tests.TestSupport;

namespace RunnerRunner.Server.Tests.Grains;

[Collection(OrleansClusterCollection.Name)]
public sealed class GrainStateTests
{
    private readonly OrleansTestClusterFixture _fixture;
    private readonly IGrainFactory _grainFactory;

    public GrainStateTests(OrleansTestClusterFixture fixture)
    {
        _fixture = fixture;
        _grainFactory = fixture.GrainFactory;
    }

    [Fact]
    public async Task HostGrain_Register_StoresOnlineHostStateWithDefaultLabels()
    {
        var hostId = OrleansTestIds.Create("host");
        var host = _grainFactory.GetGrain<IHostGrain>(hostId);

        await host.Register("mac-studio", HostPlatform.MacOS, "arm64", "1.0.0");

        var state = await host.GetState();
        Assert.Equal("mac-studio", state.Name);
        Assert.Equal(HostPlatform.MacOS, state.Platform);
        Assert.Equal("arm64", state.Architecture);
        Assert.Equal("1.0.0", state.AgentVersion);
        Assert.Equal(AgentStatus.Online, state.Status);
        Assert.Equal("macos", state.Labels["os"]);
        Assert.Equal("arm64", state.Labels["arch"]);
        Assert.NotNull(state.LastHeartbeat);
    }

    [Fact]
    public async Task HostGrain_CapacityCountersRespectBackendLimitsAndNeverUnderflow()
    {
        var host = _grainFactory.GetGrain<IHostGrain>(OrleansTestIds.Create("host"));
        await host.Register("linux-builder", HostPlatform.Linux, "x64", "1.0.0");
        await host.SetResourceLimits(maxDocker: 2, maxTart: 1, maxNative: 1);

        Assert.True(await host.CanAcceptRunner(ExecutionBackend.Docker));

        await host.IncrementRunningCount(ExecutionBackend.Docker);
        await host.IncrementRunningCount(ExecutionBackend.Docker);

        Assert.False(await host.CanAcceptRunner(ExecutionBackend.Docker));
        Assert.True(await host.CanAcceptRunner(ExecutionBackend.Tart));

        await host.DecrementRunningCount(ExecutionBackend.Docker);
        await host.DecrementRunningCount(ExecutionBackend.Docker);
        await host.DecrementRunningCount(ExecutionBackend.Docker);

        var state = await host.GetState();
        Assert.Equal(0, state.RunningDockerContainers);
        Assert.True(await host.CanAcceptRunner(ExecutionBackend.Docker));
    }

    [Fact]
    public async Task SchedulerGrain_SelectHost_ChoosesOnlineHostWithMatchingLabelsAndCapacity()
    {
        var hostId = OrleansTestIds.Create("linux-host");
        var scheduler = _grainFactory.GetGrain<ISchedulerGrain>(CreatePositiveLong());
        var host = _grainFactory.GetGrain<IHostGrain>(hostId);
        await host.Register("linux-builder", HostPlatform.Linux, "x64", "1.0.0");
        await host.UpdateLabels(new Dictionary<string, string>
        {
            ["pool"] = "default",
            ["builder"] = "true"
        });
        await scheduler.RegisterHost(hostId);

        var selected = await scheduler.SelectHost(
            new Dictionary<string, string> { ["builder"] = "true" },
            ExecutionBackend.Docker);

        Assert.Equal(hostId, selected);
    }

    [Fact]
    public async Task SchedulerGrain_SelectHost_ReturnsNullWhenUnregisteredNoCapacityOrLabelsMissing()
    {
        var hostId = OrleansTestIds.Create("linux-host");
        var scheduler = _grainFactory.GetGrain<ISchedulerGrain>(CreatePositiveLong());

        Assert.Null(await scheduler.SelectHost(
            new Dictionary<string, string> { ["builder"] = "true" },
            ExecutionBackend.Docker));

        var host = _grainFactory.GetGrain<IHostGrain>(hostId);
        await host.Register("linux-builder", HostPlatform.Linux, "x64", "1.0.0");
        await host.UpdateLabels(new Dictionary<string, string> { ["builder"] = "false" });
        await scheduler.RegisterHost(hostId);

        Assert.Null(await scheduler.SelectHost(
            new Dictionary<string, string> { ["builder"] = "true" },
            ExecutionBackend.Docker));

        await host.UpdateLabels(new Dictionary<string, string> { ["builder"] = "true" });
        await host.SetResourceLimits(maxDocker: 0, maxTart: 0, maxNative: 0);

        Assert.Null(await scheduler.SelectHost(
            new Dictionary<string, string> { ["builder"] = "true" },
            ExecutionBackend.Docker));

        await host.SetResourceLimits(maxDocker: 1, maxTart: 0, maxNative: 0);

        Assert.Equal(hostId, await scheduler.SelectHost(
            new Dictionary<string, string> { ["builder"] = "true" },
            ExecutionBackend.Docker));

        await scheduler.UnregisterHost(hostId);

        Assert.Null(await scheduler.SelectHost(
            new Dictionary<string, string> { ["builder"] = "true" },
            ExecutionBackend.Docker));
    }

    [Fact]
    public async Task HostGroupGrain_ConfigAndMembership_AreStoredAndDeduplicated()
    {
        var group = _grainFactory.GetGrain<IHostGroupGrain>(OrleansTestIds.Create("group"));
        var hostA = OrleansTestIds.Create("host-a");
        var hostB = OrleansTestIds.Create("host-b");

        await group.SetConfig("linux-pool", "Linux builders", new Dictionary<string, string>
        {
            ["pool"] = "linux",
            ["zone"] = "west"
        });
        await group.AddHost(hostA);
        await group.AddHost(hostA);
        await group.AddHost(hostB);
        await group.RemoveHost(hostA);

        var state = await group.GetState();
        Assert.Equal("linux-pool", state.Name);
        Assert.Equal("Linux builders", state.Description);
        Assert.Equal("linux", state.SharedLabels["pool"]);
        Assert.Equal("west", state.SharedLabels["zone"]);
        Assert.Equal([hostB], state.HostIds);
        Assert.Equal([hostB], await group.GetHostIds());
    }

    [Fact]
    public async Task ProvisioningRuleGrain_EnableDisable_PersistsConfigAndControlsReconcile()
    {
        var rule = _grainFactory.GetGrain<IProvisioningRuleGrain>(OrleansTestIds.Create("rule"));
        var profileId = OrleansTestIds.Create("profile");

        await rule.SetConfig(new ProvisioningRuleConfig
        {
            Name = "Webhook Linux",
            Description = "Dynamic webhook runners",
            ProfileId = profileId,
            Type = ProvisioningType.Webhook,
            Enabled = true,
            MaxConcurrent = 2,
            RequiredHostLabels = new Dictionary<string, string>
            {
                ["pool"] = "linux"
            }
        });

        var configured = await rule.GetState();
        Assert.Equal("Webhook Linux", configured.Config.Name);
        Assert.True(configured.Config.Enabled);
        Assert.Equal(ProvisioningType.Webhook, configured.Config.Type);
        Assert.Equal("linux", configured.Config.RequiredHostLabels["pool"]);

        await rule.Disable();
        await rule.Reconcile();

        var disabled = await rule.GetState();
        Assert.False(disabled.Config.Enabled);
        Assert.Null(disabled.LastReconciliation);

        await rule.Enable();
        await rule.Reconcile();

        var enabled = await rule.GetState();
        Assert.True(enabled.Config.Enabled);
        Assert.NotNull(enabled.LastReconciliation);
    }

    [Fact]
    public async Task ProfileGrain_ComposeEnvironmentVariables_AppliesEnvSetsThenProfileOverrides()
    {
        var profileId = OrleansTestIds.Create("profile");
        var profile = _grainFactory.GetGrain<IProfileGrain>(profileId);

        await profile.SetProfile(new RunnerProfile
        {
            Id = profileId,
            Name = "linux-profile",
            EnvironmentOverrides =
            {
                ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
                ["CONFIGURATION"] = "Release"
            }
        });

        var env = await profile.ComposeEnvironmentVariables();

        Assert.Equal("1", env["DOTNET_CLI_TELEMETRY_OPTOUT"]);
        Assert.Equal("Release", env["CONFIGURATION"]);
    }

    [Fact]
    public async Task RunnerInstanceGrain_TransitionsPersistStatusHistoryAndHostCapacity()
    {
        var hostId = OrleansTestIds.Create("host");
        var profileId = OrleansTestIds.Create("profile");
        var instanceId = OrleansTestIds.Create("runner");

        var host = _grainFactory.GetGrain<IHostGrain>(hostId);
        await host.Register("linux-builder", HostPlatform.Linux, "x64", "1.0.0");

        var profile = _grainFactory.GetGrain<IProfileGrain>(profileId);
        await profile.SetProfile(new RunnerProfile
        {
            Id = profileId,
            Name = "linux-profile",
            ExecutionBackend = ExecutionBackend.Docker
        });

        var runner = _grainFactory.GetGrain<IRunnerInstanceGrain>(instanceId);
        await runner.Initialize(hostId, profileId, "linux-runner-1", "static");
        await runner.MarkStarting("Deploying");
        await runner.MarkRunning(containerId: "container-1", statusMessage: "Running");

        var state = await runner.GetState();
        Assert.Equal(RunnerInstanceStatus.Running, state.Status);
        Assert.Equal("container-1", state.ContainerId);
        Assert.Equal("Running", state.StatusMessage);
        Assert.Contains(state.StatusHistory, entry => entry.Status == RunnerInstanceStatus.Pending);
        Assert.Contains(state.StatusHistory, entry => entry.Status == RunnerInstanceStatus.Starting);
        Assert.Contains(state.StatusHistory, entry => entry.Status == RunnerInstanceStatus.Running);

        var hostState = await host.GetState();
        Assert.Equal(1, hostState.RunningDockerContainers);

        await runner.MarkStopped();

        hostState = await host.GetState();
        Assert.Equal(0, hostState.RunningDockerContainers);
        Assert.Equal(RunnerInstanceStatus.Stopped, (await runner.GetState()).Status);
    }

    [Fact]
    public async Task RunnerInstanceGrain_MarkFailedAndStopped_PersistFailureAndReleaseCapacityOnce()
    {
        var hostId = OrleansTestIds.Create("host");
        var profileId = OrleansTestIds.Create("profile");
        var instanceId = OrleansTestIds.Create("runner");

        var host = _grainFactory.GetGrain<IHostGrain>(hostId);
        await host.Register("linux-builder", HostPlatform.Linux, "x64", "1.0.0");

        var profile = _grainFactory.GetGrain<IProfileGrain>(profileId);
        await profile.SetProfile(new RunnerProfile
        {
            Id = profileId,
            Name = "linux-profile",
            ExecutionBackend = ExecutionBackend.Docker
        });

        var runner = _grainFactory.GetGrain<IRunnerInstanceGrain>(instanceId);
        await runner.Initialize(hostId, profileId, "linux-runner-failing", "static");
        await runner.MarkStarting("Deploying");

        Assert.Equal(1, (await host.GetState()).RunningDockerContainers);

        await runner.MarkFailed("Deploy command failed");

        var failed = await runner.GetState();
        Assert.Equal(RunnerInstanceStatus.Failed, failed.Status);
        Assert.Equal("Deploy command failed", failed.ErrorMessage);
        Assert.Contains(failed.StatusHistory, entry =>
            entry.Status == RunnerInstanceStatus.Failed
            && entry.StatusMessage == "Deploy command failed");
        Assert.Equal(0, (await host.GetState()).RunningDockerContainers);

        await runner.MarkStopped();

        var stopped = await runner.GetState();
        Assert.Equal(RunnerInstanceStatus.Stopped, stopped.Status);
        Assert.NotNull(stopped.StoppedAt);
        Assert.Contains(stopped.StatusHistory, entry => entry.Status == RunnerInstanceStatus.Stopped);
        Assert.Equal(0, (await host.GetState()).RunningDockerContainers);
    }

    [Fact]
    public async Task WebhookProcessorGrain_InProgressWebhook_UpdatesMatchingDynamicRunnerGrain()
    {
        const string secret = "github-secret";
        var hostId = OrleansTestIds.Create("host");
        var profileId = OrleansTestIds.Create("profile");
        var instanceId = OrleansTestIds.Create("runner");
        var ruleId = OrleansTestIds.Create("rule");
        var jobNumber = CreatePositiveLong();
        var jobId = jobNumber.ToString();

        var host = _grainFactory.GetGrain<IHostGrain>(hostId);
        await host.Register("linux-builder", HostPlatform.Linux, "x64", "1.0.0");

        var profile = _grainFactory.GetGrain<IProfileGrain>(profileId);
        await profile.SetProfile(new RunnerProfile
        {
            Id = profileId,
            Name = "linux-profile",
            Provider = RunnerProvider.GitHubActions,
            RequiredHostPlatform = HostPlatform.Linux,
            ExecutionBackend = ExecutionBackend.Docker,
            Labels = ["self-hosted", "linux"]
        });

        var runner = _grainFactory.GetGrain<IRunnerInstanceGrain>(instanceId);
        await runner.Initialize(hostId, profileId, "linux-dynamic-runner", "dynamic", jobId);

        await _fixture.DocumentStore.Insert(new ProvisioningRule
        {
            Id = ruleId,
            Name = "Webhook Linux",
            ProfileId = profileId,
            Type = ProvisioningType.Webhook,
            Enabled = true,
            Provider = RunnerProvider.GitHubActions,
            WebhookSecret = secret,
            AllowedRepos = ["repo"]
        });

        var body = $$"""
        {
          "action": "in_progress",
          "workflow_job": {
            "id": {{jobNumber}},
            "run_id": {{jobNumber + 1}},
            "labels": ["self-hosted", "linux"],
            "workflow_name": "CI"
          },
          "repository": {
            "full_name": "octo/repo"
          }
        }
        """;
        var signature = "sha256=" + ComputeSignature(body, secret);
        var processor = _grainFactory.GetGrain<IWebhookProcessorGrain>(CreatePositiveLong());

        var result = await processor.ProcessWebhook("github", body, Encoding.UTF8.GetBytes(body), signature);

        Assert.True(result.Success);
        Assert.Equal("in_progress", result.Status);
        Assert.Equal(instanceId, result.InstanceId);

        var runnerState = await runner.GetState();
        Assert.Equal(RunnerInstanceStatus.Running, runnerState.Status);
        Assert.Equal("Job in progress", runnerState.StatusMessage);
        Assert.Equal(1, (await host.GetState()).RunningDockerContainers);

        var events = (await _fixture.DocumentStore.Query<WebhookEvent>().ToList()).ToList();
        Assert.Contains(events, evt =>
            evt.JobId == jobId
            && evt.Status == "in_progress"
            && evt.InstanceId == instanceId);
    }

    private static long CreatePositiveLong()
    {
        var value = BitConverter.ToInt64(Guid.NewGuid().ToByteArray());
        return value == long.MinValue ? 1 : Math.Abs(value);
    }

    private static string ComputeSignature(string body, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(body)));
    }
}
