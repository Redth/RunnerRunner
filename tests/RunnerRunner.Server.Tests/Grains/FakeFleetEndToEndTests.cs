using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RunnerRunner.Core.Hub;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Grains.Interfaces;
using RunnerRunner.Server.Grains.State;
using RunnerRunner.Server.Services;
using RunnerRunner.Server.Tests.TestSupport;
using Shiny.DocumentDb;
using Host = RunnerRunner.Core.Models.Host;

namespace RunnerRunner.Server.Tests.Grains;

[Collection(OrleansClusterCollection.Name)]
public sealed class FakeFleetEndToEndTests
{
    private static long _jobIdSeed = 500_000;

    private readonly OrleansTestClusterFixture _fixture;
    private readonly IGrainFactory _grainFactory;
    private readonly IDocumentStore _store;

    public FakeFleetEndToEndTests(OrleansTestClusterFixture fixture)
    {
        _fixture = fixture;
        _grainFactory = fixture.GrainFactory;
        _store = fixture.DocumentStore;
    }

    [Fact]
    public async Task GitHubDockerWebhookFleet_DispatchesProgressesCompletesAndFreesCapacity()
    {
        await ClearFleetDocumentsAsync();
        var id = OrleansTestIds.Create("fleet-github");
        var hostId = $"{id}-host";
        var profileId = $"{id}-profile";
        var ruleId = $"{id}-rule";
        var secret = $"{id}-secret";
        var repo = "octo/fleet";
        var labels = new[] { "self-hosted", "linux", "fleet-docker" };
        var jobId = NextJobId();
        var service = CreateDynamicProvisioningService();
        var host = await CreateHostAsync(hostId, HostPlatform.Linux, ExecutionBackend.Docker);
        var profile = await CreateProfileAsync(
            profileId,
            "github-docker",
            RunnerProvider.GitHubActions,
            HostPlatform.Linux,
            ExecutionBackend.Docker,
            labels);
        await CreateWebhookRuleAsync(ruleId, "github docker", RunnerProvider.GitHubActions, secret, repo, profileId, labels, hostId);

        var queued = await ProcessWebhookAsync("github", "queued", jobId, repo, labels, secret);

        await service.ProcessQueuedWebhookEventOnceAsync(queued.EventId!, profileId);

        var queuedEvent = await _store.Get<WebhookEvent>(queued.EventId!);
        Assert.NotNull(queuedEvent);
        Assert.Equal("provisioned", queuedEvent.Status);
        Assert.False(string.IsNullOrWhiteSpace(queuedEvent.InstanceId));

        var deploy = FindDeployCommand(hostId, queuedEvent.InstanceId!);
        Assert.Equal(ExecutionBackend.Docker, deploy.Backend);
        Assert.Equal(RunnerProvider.GitHubActions, deploy.Provider);
        Assert.Equal("dynamic", deploy.ProvisioningMode);
        Assert.Equal(1, deploy.BackendCapacityLimit);
        Assert.Contains("fleet-docker", deploy.Labels);
        Assert.Equal(1, GetRunningCount(await host.GetState(), ExecutionBackend.Docker));

        var runner = _grainFactory.GetGrain<IRunnerInstanceGrain>(queuedEvent.InstanceId!);
        var startingState = await runner.GetState();
        Assert.Equal(RunnerInstanceStatus.Starting, startingState.Status);
        Assert.NotNull(startingState.DeployedAt);
        Assert.Contains(startingState.StatusHistory, h => h.Status == RunnerInstanceStatus.Starting);

        var progress = await ProcessWebhookAsync("github", "in_progress", jobId, repo, labels, secret);
        Assert.True(progress.Success);
        Assert.Equal(queuedEvent.InstanceId, progress.InstanceId);

        var runningState = await runner.GetState();
        Assert.Equal(RunnerInstanceStatus.Running, runningState.Status);
        Assert.Equal("Job in progress", runningState.StatusMessage);
        Assert.Equal(1, GetRunningCount(await host.GetState(), ExecutionBackend.Docker));

        var completed = await ProcessWebhookAsync("github", "completed", jobId, repo, labels, secret);
        Assert.True(completed.Success);
        Assert.Equal("completed", completed.Status);
        Assert.Contains(await EventsForJobAsync(jobId), e => e.Action == "completed" && e.Status == "completed");

        await runner.MarkStopped();

        Assert.Equal(0, GetRunningCount(await host.GetState(), ExecutionBackend.Docker));
        var stoppedProjection = await _store.Get<RunnerInstance>(queuedEvent.InstanceId!);
        Assert.NotNull(stoppedProjection);
        Assert.Equal(RunnerInstanceStatus.Stopped, stoppedProjection.Status);
        Assert.Contains(stoppedProjection.StatusHistory, h => h.Status == RunnerInstanceStatus.Stopped);
        Assert.Equal(profile.Id, stoppedProjection.ProfileId);
    }

    [Fact]
    public async Task GiteaTartWebhookFleet_ReplacesCrashAndFreesCapacityAfterFailure()
    {
        await ClearFleetDocumentsAsync();
        var id = OrleansTestIds.Create("fleet-gitea");
        var hostId = $"{id}-host";
        var profileId = $"{id}-profile";
        var ruleId = $"{id}-rule";
        var secret = $"{id}-secret";
        var repo = "team/fleet";
        var labels = new[] { "self-hosted", "macos", "fleet-tart" };
        var jobId = NextJobId();
        var service = CreateDynamicProvisioningService();
        var host = await CreateHostAsync(hostId, HostPlatform.MacOS, ExecutionBackend.Tart);
        await CreateProfileAsync(
            profileId,
            "gitea-tart",
            RunnerProvider.GiteaActions,
            HostPlatform.MacOS,
            ExecutionBackend.Tart,
            labels);
        await CreateWebhookRuleAsync(ruleId, "gitea tart", RunnerProvider.GiteaActions, secret, repo, profileId, labels, hostId);

        var queued = await ProcessWebhookAsync("gitea", "queued", jobId, repo, labels, secret);

        await service.ProcessQueuedWebhookEventOnceAsync(queued.EventId!, profileId);

        var firstEvent = await _store.Get<WebhookEvent>(queued.EventId!);
        Assert.NotNull(firstEvent);
        Assert.False(string.IsNullOrWhiteSpace(firstEvent.InstanceId));
        var firstInstanceId = firstEvent.InstanceId!;
        var firstDeploy = FindDeployCommand(hostId, firstInstanceId);
        Assert.Equal(ExecutionBackend.Tart, firstDeploy.Backend);
        Assert.Equal(RunnerProvider.GiteaActions, firstDeploy.Provider);
        Assert.Equal(1, firstDeploy.BackendCapacityLimit);

        var firstRunner = _grainFactory.GetGrain<IRunnerInstanceGrain>(firstInstanceId);
        await firstRunner.MarkRunning(vmName: "fake-tart-vm", statusMessage: "Provider accepted job");
        Assert.Equal(1, GetRunningCount(await host.GetState(), ExecutionBackend.Tart));

        await firstRunner.MarkCrashed("fake Tart VM exited");

        Assert.Equal(0, GetRunningCount(await host.GetState(), ExecutionBackend.Tart));
        var crashedProjection = await _store.Get<RunnerInstance>(firstInstanceId);
        Assert.NotNull(crashedProjection);
        Assert.Equal(RunnerInstanceStatus.Crashed, crashedProjection.Status);
        Assert.Contains(crashedProjection.StatusHistory, h => h.Status == RunnerInstanceStatus.Crashed);

        await service.ProcessQueuedWebhookEventOnceAsync(queued.EventId!, profileId);

        var replacementEvent = await _store.Get<WebhookEvent>(queued.EventId!);
        Assert.NotNull(replacementEvent);
        Assert.False(string.IsNullOrWhiteSpace(replacementEvent.InstanceId));
        var replacementId = replacementEvent.InstanceId!;
        Assert.NotEqual(firstInstanceId, replacementId);
        FindDeployCommand(hostId, replacementId);
        Assert.Equal(1, GetRunningCount(await host.GetState(), ExecutionBackend.Tart));

        var replacementRunner = _grainFactory.GetGrain<IRunnerInstanceGrain>(replacementId);
        await replacementRunner.MarkFailed("fake provider rejected replacement");

        Assert.Equal(0, GetRunningCount(await host.GetState(), ExecutionBackend.Tart));
        var failedProjection = await _store.Get<RunnerInstance>(replacementId);
        Assert.NotNull(failedProjection);
        Assert.Equal(RunnerInstanceStatus.Failed, failedProjection.Status);
        Assert.Contains(failedProjection.StatusHistory, h => h.Status == RunnerInstanceStatus.Failed);
    }

    private DynamicProvisioningService CreateDynamicProvisioningService()
    {
        var api = new FakeProviderHttpApi(_ => FakeProviderHttpApi.JsonResponse("{}"));
        var auth = new GitHubAuthenticationService(api, NullLogger<GitHubAuthenticationService>.Instance);
        var jit = new JitConfigService(NullLogger<JitConfigService>.Instance, api, auth);
        var cleanup = _fixture.Cluster.GetSiloServiceProvider(null!).GetRequiredService<RunnerRegistrationCleanupService>();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DynamicProvisioning:PendingRetrySeconds"] = "5",
                ["DynamicProvisioning:PendingTimeoutMinutes"] = "10",
                ["DynamicProvisioning:GitHubPollSeconds"] = "3600"
            })
            .Build();

        return new DynamicProvisioningService(
            NullLogger<DynamicProvisioningService>.Instance,
            configuration,
            _fixture.Cluster.GetSiloServiceProvider(null!),
            jit,
            cleanup,
            api,
            auth,
            _fixture.HostCommands,
            _grainFactory);
    }

    private async Task<IHostGrain> CreateHostAsync(string hostId, HostPlatform platform, ExecutionBackend backend)
    {
        var host = _grainFactory.GetGrain<IHostGrain>(hostId);
        await host.Register($"{hostId}-name", platform, platform == HostPlatform.MacOS ? "arm64" : "x64", "test-agent");
        await host.SetResourceLimits(
            maxDocker: backend == ExecutionBackend.Docker ? 1 : 0,
            maxTart: backend == ExecutionBackend.Tart ? 1 : 0,
            maxNative: backend == ExecutionBackend.Native ? 1 : 0);

        var document = await _store.Get<Host>(hostId) ?? new Host { Id = hostId, Name = $"{hostId}-name" };
        document.Name = $"{hostId}-name";
        document.Platform = platform;
        document.Architecture = platform == HostPlatform.MacOS ? "arm64" : "x64";
        document.AgentStatus = AgentStatus.Online;
        document.IsApproved = true;
        document.Capabilities = [backend.ToString().ToLowerInvariant()];
        document.Labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["os"] = platform.ToString().ToLowerInvariant(),
            ["pool"] = hostId,
            [backend.ToString().ToLowerInvariant()] = "true"
        };
        document.MaxDockerContainers = backend == ExecutionBackend.Docker ? 1 : 0;
        document.MaxTartVMs = backend == ExecutionBackend.Tart ? 1 : 0;
        document.MaxNativeProcesses = backend == ExecutionBackend.Native ? 1 : 0;
        document.UpdatedAt = DateTime.UtcNow;

        if (await _store.Get<Host>(hostId) is null)
            await _store.Insert(document);
        else
            await _store.Update(document);

        return host;
    }

    private async Task<RunnerProfile> CreateProfileAsync(
        string profileId,
        string name,
        RunnerProvider provider,
        HostPlatform platform,
        ExecutionBackend backend,
        IEnumerable<string> labels)
    {
        var profile = new RunnerProfile
        {
            Id = profileId,
            Name = $"{name}-{profileId[^6..]}",
            Provider = provider,
            RunnerAgentVersion = "test-version",
            RequiredHostPlatform = platform,
            ExecutionBackend = backend,
            Labels = labels.ToList(),
            MaxParallelPerHost = 1,
            EmitMetadataLabels = false,
            EmitJobStartedBanner = false
        };

        await _grainFactory.GetGrain<IProfileGrain>(profileId).SetProfile(profile);
        await _store.Insert(profile);
        return profile;
    }

    private async Task CreateWebhookRuleAsync(
        string ruleId,
        string name,
        RunnerProvider provider,
        string secret,
        string repo,
        string profileId,
        IEnumerable<string> labels,
        string hostId)
    {
        await _store.Insert(new ProvisioningRule
        {
            Id = ruleId,
            Name = name,
            Type = ProvisioningType.Webhook,
            Enabled = true,
            Provider = provider,
            WebhookSecret = secret,
            AllowedRepos = [repo],
            RequiredHostLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["pool"] = hostId
            },
            MaxConcurrent = 1,
            LabelMappings =
            [
                new LabelProfileMapping
                {
                    ProfileId = profileId,
                    RequiredLabels = labels.ToList()
                }
            ]
        });
    }

    private async Task<WebhookProcessResult> ProcessWebhookAsync(
        string providerKey,
        string action,
        long jobId,
        string repo,
        IReadOnlyCollection<string> labels,
        string secret)
    {
        var body = BuildWorkflowJobPayload(action, jobId, jobId + 10, repo, labels);
        var result = await _grainFactory
            .GetGrain<IWebhookProcessorGrain>(NextJobId())
            .ProcessWebhook(providerKey, body, Encoding.UTF8.GetBytes(body), Sign(body, secret, providerKey));

        Assert.True(result.Success);
        Assert.Equal(action == "queued" ? "provisioned" : action, result.Status);
        return result;
    }

    private DeployRunnerCommand FindDeployCommand(string hostId, string instanceId)
    {
        var commands = _fixture.HostCommands.Commands
            .Where(command => command.HostId == hostId && command.Kind == HostCommandKind.DeployRunner)
            .Select(command => (DeployRunnerCommand)command.Command)
            .Where(command => command.InstanceId == instanceId)
            .ToList();

        return Assert.Single(commands);
    }

    private async Task<List<WebhookEvent>> EventsForJobAsync(long jobId)
    {
        var job = jobId.ToString();
        return (await _store.Query<WebhookEvent>().ToList())
            .Where(e => e.JobId == job)
            .ToList();
    }

    private async Task ClearFleetDocumentsAsync()
    {
        await RemoveAllAsync<WebhookEvent>();
        await RemoveAllAsync<RunnerInstance>();
        await RemoveAllAsync<ProvisioningRule>();
        await RemoveAllAsync<RunnerProfile>();
        await RemoveAllAsync<Host>();
        await EnsureFleetTablesAsync();
    }

    private async Task RemoveAllAsync<T>() where T : class
    {
        IReadOnlyList<T> items;
        try
        {
            items = (await _store.Query<T>().ToList()).ToList();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1 && ex.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        foreach (var item in items)
        {
            var id = (string?)typeof(T).GetProperty("Id")?.GetValue(item)
                ?? throw new InvalidOperationException($"{typeof(T).Name} does not expose an Id property.");
            await _store.Remove<T>(id);
        }
    }

    private async Task EnsureFleetTablesAsync()
    {
        const string runnerId = "__fleet_table_runner__";
        await _store.Insert(new RunnerInstance
        {
            Id = runnerId,
            RunnerName = runnerId
        });
        await _store.Remove<RunnerInstance>(runnerId);

        const string envSetId = "__fleet_table_env__";
        await _store.Insert(new EnvironmentVariableSet
        {
            Id = envSetId,
            Name = envSetId
        });
        await _store.Remove<EnvironmentVariableSet>(envSetId);
    }

    private static int GetRunningCount(HostGrainState state, ExecutionBackend backend) =>
        backend switch
        {
            ExecutionBackend.Docker => state.RunningDockerContainers,
            ExecutionBackend.Tart => state.RunningTartVMs,
            ExecutionBackend.Native => state.RunningNativeProcesses,
            _ => 0
        };

    private static string BuildWorkflowJobPayload(
        string action,
        long jobId,
        long runId,
        string repository,
        IReadOnlyCollection<string> labels)
        => JsonSerializer.Serialize(new
        {
            action,
            workflow_job = new
            {
                id = jobId,
                run_id = runId,
                workflow_name = "CI",
                labels
            },
            repository = new
            {
                full_name = repository
            }
        });

    private static string Sign(string body, string secret, string providerKey)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var signature = Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(body)));
        return providerKey == "github" ? $"sha256={signature}" : signature;
    }

    private static long NextJobId() => Interlocked.Increment(ref _jobIdSeed);
}
