using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using RunnerRunner.Core.Hub;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Grains.Interfaces;
using RunnerRunner.Server.Grains.State;
using RunnerRunner.Server.Providers;
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

    [Fact]
    public async Task GitHubWebhookFleet_WaitsForDisconnectedHostBeforeGeneratingJitConfig()
    {
        await ClearFleetDocumentsAsync();
        var id = OrleansTestIds.Create("fleet-disconnected");
        var hostId = $"{id}-host";
        var profileId = $"{id}-profile";
        var ruleId = $"{id}-rule";
        var credentialId = $"{id}-credential";
        var secret = $"{id}-secret";
        var repo = "octo/fleet";
        var labels = new[] { "self-hosted", "linux", "fleet-docker" };
        var jobId = NextJobId();
        var api = new FakeProviderHttpApi();
        var service = CreateDynamicProvisioningService(api);
        await CreateHostAsync(hostId, HostPlatform.Linux, ExecutionBackend.Docker);
        var profile = await CreateProfileAsync(
            profileId,
            "github-docker",
            RunnerProvider.GitHubActions,
            HostPlatform.Linux,
            ExecutionBackend.Docker,
            labels);
        profile.ProviderCredentialId = credentialId;
        await _store.Update(profile);
        await _grainFactory.GetGrain<IProfileGrain>(profileId).SetProfile(profile);
        await _store.Insert(new ProviderCredential
        {
            Id = credentialId,
            Name = "github",
            Provider = RunnerProvider.GitHubActions,
            GitHubToken = "fake-token",
            GitHubOrg = "octo",
            GitHubRepo = repo
        });
        await CreateWebhookRuleAsync(ruleId, "github docker", RunnerProvider.GitHubActions, secret, repo, profileId, labels, hostId);
        _fixture.HostCommands.SetHostConnected(hostId, connected: false);

        try
        {
            var queued = await ProcessWebhookAsync("github", "queued", jobId, repo, labels, secret);

            await service.ProcessQueuedWebhookEventOnceAsync(queued.EventId!, profileId);

            var queuedEvent = await _store.Get<WebhookEvent>(queued.EventId!);
            Assert.NotNull(queuedEvent);
            Assert.Equal("pending_host_match", queuedEvent.Status);
            Assert.Contains("waiting for it to reconnect", queuedEvent.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Null(queuedEvent.InstanceId);
            Assert.DoesNotContain(_fixture.HostCommands.Commands, command => command.HostId == hostId && command.Kind == HostCommandKind.DeployRunner);
            Assert.Empty(api.Requests);
        }
        finally
        {
            _fixture.HostCommands.SetHostConnected(hostId, connected: true);
        }
    }

    [Theory]
    [InlineData(0, "completed")]
    [InlineData(1, "completed")]
    [InlineData(0, "in_progress")]
    [InlineData(1, "in_progress")]
    public async Task GitHubWebhookFleet_SkipsRecoveryWhenJobAlreadyAdvanced(int retryCount, string jobStatus)
    {
        await ClearFleetDocumentsAsync();
        var id = OrleansTestIds.Create("fleet-retry-done");
        var hostId = $"{id}-host";
        var profileId = $"{id}-profile";
        var secret = $"{id}-secret";
        var repo = "octo/fleet";
        var labels = new[] { "self-hosted", "linux", "fleet-docker" };
        var jobId = NextJobId();
        var api = new FakeProviderHttpApi()
            .RespondJson(
                r => r.RequestUri!.AbsolutePath.EndsWith($"/actions/jobs/{jobId}", StringComparison.Ordinal),
                $$"""{"id":{{jobId}},"status":"{{jobStatus}}","conclusion":null,"runner_name":"other-runner"}""");
        var service = CreateDynamicProvisioningService(api);
        await CreateGitHubFleetWithCredentialAsync(id, hostId, profileId, secret, repo, labels);

        var queued = await ProcessWebhookAsync("github", "queued", jobId, repo, labels, secret);

        // Stale provisioned-event recovery does not increment RetryCount; timeout retries do.
        var evt = (await _store.Get<WebhookEvent>(queued.EventId!))!;
        evt.ScheduleRetry(
            "Previous runner is no longer available",
            DateTime.UtcNow,
            TimeSpan.Zero,
            countAttempt: retryCount > 0);
        evt.InstanceId = null;
        await _store.Update(evt);

        await service.ProcessQueuedWebhookEventOnceAsync(queued.EventId!, profileId);

        var resolved = await _store.Get<WebhookEvent>(queued.EventId!);
        Assert.NotNull(resolved);
        Assert.Equal(jobStatus, resolved.Status);
        Assert.Equal(retryCount, resolved.RetryCount);
        Assert.Equal("other-runner", resolved.AssignedRunnerName);
        Assert.DoesNotContain(_fixture.HostCommands.Commands, c => c.HostId == hostId && c.Kind == HostCommandKind.DeployRunner);
        Assert.DoesNotContain(api.Requests, r => r.PathAndQuery.Contains("generate-jitconfig", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GitHubWebhookFleet_KeepsCompletedEventResolvedWhenRunnerCleanupFails(bool transportFailure)
    {
        await ClearFleetDocumentsAsync();
        var id = OrleansTestIds.Create("fleet-cleanup-failed");
        var hostId = $"{id}-host";
        var oldHostId = $"{id}-old-host";
        var profileId = $"{id}-profile";
        var secret = $"{id}-secret";
        var repo = "octo/fleet";
        var labels = new[] { "self-hosted", "linux", "fleet-docker" };
        var jobId = NextJobId();
        var api = new FakeProviderHttpApi()
            .RespondJson(
                r => r.RequestUri!.AbsolutePath.EndsWith($"/actions/jobs/{jobId}", StringComparison.Ordinal),
                $$"""{"id":{{jobId}},"status":"completed","conclusion":"success","runner_name":"old-runner"}""")
            .RespondJson(
                r => r.RequestUri!.AbsolutePath.EndsWith("/generate-jitconfig", StringComparison.Ordinal),
                """{"encoded_jit_config":"fake-jit"}""");
        var hostCommands = Substitute.For<IHostCommandDispatcher>();
        hostCommands.CanDispatchToHost(Arg.Any<string>())
            .Returns(call => _fixture.HostCommands.CanDispatchToHost(call.Arg<string>()));
        hostCommands.DispatchDeployRunnerAsync(Arg.Any<string>(), Arg.Any<DeployRunnerCommand>())
            .Returns(call => _fixture.HostCommands.DispatchDeployRunnerAsync(call.Arg<string>(), call.Arg<DeployRunnerCommand>()));
        Exception cleanupFailure = transportFailure
            ? new HttpRequestException("Stop command transport failed.")
            : new InvalidOperationException("Old HostWorker is not connected.");
        hostCommands.DispatchStopRunnerAsync(oldHostId, Arg.Any<StopRunnerCommand>())
            .Returns(Task.FromException(cleanupFailure));
        var service = CreateDynamicProvisioningService(api, hostCommands);
        await CreateGitHubFleetWithCredentialAsync(id, hostId, profileId, secret, repo, labels);
        await CreateHostAsync(oldHostId, HostPlatform.Linux, ExecutionBackend.Docker);
        var queued = await ProcessWebhookAsync("github", "queued", jobId, repo, labels, secret);
        var evt = (await _store.Get<WebhookEvent>(queued.EventId!))!;
        evt.ScheduleRetry("Previous runner failed", DateTime.UtcNow, TimeSpan.Zero);
        await _store.Update(evt);
        await _store.Insert(new RunnerInstance
        {
            Id = $"{id}-old-runner",
            HostId = oldHostId,
            ProfileId = profileId,
            RunnerName = "old-runner",
            JobId = jobId.ToString(),
            WebhookEventId = evt.Id,
            ProvisioningMode = "dynamic",
            Status = RunnerInstanceStatus.Failed
        });

        var exception = await Record.ExceptionAsync(
            () => service.ProcessQueuedWebhookEventOnceAsync(evt.Id, profileId));
        Assert.Same(cleanupFailure, exception);
        await service.ProcessQueuedWebhookEventOnceAsync(evt.Id, profileId);

        var resolved = await _store.Get<WebhookEvent>(evt.Id);
        Assert.NotNull(resolved);
        Assert.Equal("completed", resolved.Status);
        Assert.Equal("old-runner", resolved.AssignedRunnerName);
        Assert.NotNull(resolved.ResolvedAt);
        Assert.Null(resolved.NextRetryAt);
        await hostCommands.Received(1).DispatchStopRunnerAsync(oldHostId, Arg.Any<StopRunnerCommand>());
        await hostCommands.DidNotReceive().DispatchDeployRunnerAsync(Arg.Any<string>(), Arg.Any<DeployRunnerCommand>());
        Assert.DoesNotContain(api.Requests, r => r.PathAndQuery.Contains("generate-jitconfig", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("unavailable")]
    [InlineData("timeout")]
    [InlineData("http_error")]
    [InlineData("malformed")]
    public async Task GitHubWebhookFleet_ProvisionsRecoveryWhenJobIsQueuedOrLookupFails(string response)
    {
        await ClearFleetDocumentsAsync();
        var id = OrleansTestIds.Create("fleet-recovery-open");
        var hostId = $"{id}-host";
        var profileId = $"{id}-profile";
        var secret = $"{id}-secret";
        var repo = "octo/fleet";
        var labels = new[] { "self-hosted", "linux", "fleet-docker" };
        var jobId = NextJobId();
        var api = new FakeProviderHttpApi()
            .Respond(
                r => r.RequestUri!.AbsolutePath.EndsWith($"/actions/jobs/{jobId}", StringComparison.Ordinal),
                _ => response switch
                {
                    "queued" => FakeProviderHttpApi.JsonResponse($$"""{"id":{{jobId}},"status":"queued"}"""),
                    "unavailable" => FakeProviderHttpApi.JsonResponse("{}", HttpStatusCode.ServiceUnavailable),
                    "timeout" => throw new TaskCanceledException("GitHub job lookup timed out."),
                    "http_error" => throw new HttpRequestException("GitHub is unreachable."),
                    "malformed" => FakeProviderHttpApi.JsonResponse("{"),
                    _ => throw new ArgumentOutOfRangeException(nameof(response))
                })
            .RespondJson(
                r => r.RequestUri!.AbsolutePath.EndsWith("/generate-jitconfig", StringComparison.Ordinal),
                """{"encoded_jit_config":"fake-jit"}""");
        var service = CreateDynamicProvisioningService(api);
        await CreateGitHubFleetWithCredentialAsync(id, hostId, profileId, secret, repo, labels);
        var queued = await ProcessWebhookAsync("github", "queued", jobId, repo, labels, secret);

        await service.ProcessQueuedWebhookEventOnceAsync(queued.EventId!, profileId);

        var provisioned = await _store.Get<WebhookEvent>(queued.EventId!);
        Assert.NotNull(provisioned);
        Assert.Equal("provisioned", provisioned.Status);
        Assert.Equal(0, provisioned.RetryCount);
        Assert.False(string.IsNullOrWhiteSpace(provisioned.InstanceId));
        FindDeployCommand(hostId, provisioned.InstanceId!);
        Assert.Contains(api.Requests, r => r.PathAndQuery.EndsWith($"/actions/jobs/{jobId}", StringComparison.Ordinal));
        Assert.Contains(api.Requests, r => r.PathAndQuery.Contains("generate-jitconfig", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GitHubPolling_ResolvesOpenEventWhoseRunAlreadyFinished()
    {
        await ClearFleetDocumentsAsync();
        var id = OrleansTestIds.Create("fleet-poll-done");
        var hostId = $"{id}-host";
        var profileId = $"{id}-profile";
        var secret = $"{id}-secret";
        var repo = "octo/fleet";
        var labels = new[] { "self-hosted", "linux", "fleet-docker" };
        var jobId = NextJobId();
        var api = new FakeProviderHttpApi()
            .RespondJson(
                r => r.RequestUri!.AbsolutePath.EndsWith("/actions/runs", StringComparison.Ordinal),
                """{"total_count":0,"workflow_runs":[]}""")
            .RespondJson(
                r => r.RequestUri!.AbsolutePath.EndsWith($"/actions/jobs/{jobId}", StringComparison.Ordinal),
                $$"""{"id":{{jobId}},"status":"completed","conclusion":"failure","runner_name":"other-runner"}""");
        var service = CreateDynamicProvisioningService(api);
        await CreateGitHubFleetWithCredentialAsync(id, hostId, profileId, secret, repo, labels);

        // A queued event that was provisioned, but whose completion webhook never arrived and
        // whose run has since dropped out of the active-run queries.
        var eventId = $"{id}-event";
        await _store.Insert(new WebhookEvent
        {
            Id = eventId,
            Provider = RunnerProvider.GitHubActions.ToString(),
            Action = "queued",
            JobId = jobId.ToString(),
            RunId = (jobId + 10).ToString(),
            Repository = repo,
            Labels = labels.ToList(),
            Status = "provisioned",
            ReceivedAt = DateTime.UtcNow.AddMinutes(-30)
        });

        await service.ReconcileQueuedGitHubJobsAsync(CancellationToken.None);

        var resolved = await _store.Get<WebhookEvent>(eventId);
        Assert.NotNull(resolved);
        Assert.Equal("completed", resolved.Status);
        Assert.Equal("other-runner", resolved.AssignedRunnerName);
        Assert.Contains(api.Requests, r => r.PathAndQuery.EndsWith($"/actions/jobs/{jobId}", StringComparison.Ordinal));
    }

    // Redth/bladeybiscuit, 2026-11-08 15:41 UTC: a push cancelled a run whose two Linux jobs
    // were still queued. Their completed webhooks name no runner, so cleanup stopped "the runner
    // minted for each job" -- runners GitHub had just handed the next run's two jobs. Both died
    // with no logs and were failed ten minutes later.
    [Theory]
    [InlineData("busy")]           // GitHub's runner list already shows it running a job
    [InlineData("busy_on_delete")] // it took the job between our list and our delete
    public async Task CancelledQueuedJob_LeavesItsRunnerRunningWhenGitHubHandedItAnotherJob(string github)
    {
        var fleet = await StartCancelledJobFleetAsync("fleet-cancel-busy", runners =>
        {
            runners.Busy = github == "busy";
            runners.DeleteStatus = github == "busy_on_delete" ? HttpStatusCode.UnprocessableEntity : HttpStatusCode.NoContent;
        });

        await fleet.Service.HandleJobCompletedAsync(fleet.JobId, "cancelled", runnerName: "");

        Assert.Empty(StopsFor(fleet.InstanceId));
        var instance = await _store.Get<RunnerInstance>(fleet.InstanceId);
        Assert.NotNull(instance);
        Assert.Null(instance.JobId);
        Assert.NotNull(instance.ClaimReleasedAt);
        Assert.Equal(github == "busy_on_delete", fleet.Api.Requests.Any(r => r.Method == HttpMethod.Delete));
    }

    [Fact]
    public async Task CancelledQueuedJob_StopsItsIdleRunnerOnlyOnceGitHubHasDeregisteredIt()
    {
        var fleet = await StartCancelledJobFleetAsync("fleet-cancel-idle", _ => { });

        await fleet.Service.HandleJobCompletedAsync(fleet.JobId, "cancelled", runnerName: "");

        Assert.Single(StopsFor(fleet.InstanceId));
        Assert.True(fleet.Runners.DeletedBeforeAnyStop, "the runner was told to stop while GitHub could still hand it a job");
        Assert.Null(await _store.Get<RunnerInstance>(fleet.InstanceId));
    }

    [Fact]
    public async Task CancelledQueuedJob_StopsItsRunnerWhenGitHubNoLongerHasIt()
    {
        var fleet = await StartCancelledJobFleetAsync("fleet-cancel-gone", runners => runners.Listed = false);

        await fleet.Service.HandleJobCompletedAsync(fleet.JobId, "cancelled", runnerName: "");

        Assert.Single(StopsFor(fleet.InstanceId));
        Assert.DoesNotContain(fleet.Api.Requests, r => r.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task CancelledQueuedJob_KeepsItsRunnerWhenGitHubCannotBeAsked()
    {
        var fleet = await StartCancelledJobFleetAsync("fleet-cancel-unknown", runners => runners.ListStatus = HttpStatusCode.BadGateway);

        await fleet.Service.HandleJobCompletedAsync(fleet.JobId, "cancelled", runnerName: "");

        Assert.Empty(StopsFor(fleet.InstanceId));
        var instance = await _store.Get<RunnerInstance>(fleet.InstanceId);
        Assert.NotNull(instance);
        // The claim stays, so RunnerTimeoutService asks GitHub again on its next pass.
        Assert.Equal(fleet.JobId, instance.JobId);
    }

    [Fact]
    public async Task SwappedRunners_EachCompletionStopsOnlyTheRunnerGitHubNamed()
    {
        // Job A's runner ran job B and job B's runner ran job A. A runner GitHub names as having
        // run a completed job is done (a JIT runner takes one job), so no question is asked.
        var fleet = await StartCancelledJobFleetAsync("fleet-swap", runners => runners.Busy = true);
        var otherJobId = NextJobId().ToString();
        var otherInstanceId = $"{fleet.Id}-runner-b";
        await _store.Insert(new RunnerInstance
        {
            Id = otherInstanceId,
            HostId = fleet.HostId,
            ProfileId = fleet.ProfileId,
            RunnerName = $"{fleet.Id}-jit-b",
            JobId = otherJobId,
            ProvisioningMode = "dynamic",
            Status = RunnerInstanceStatus.Running,
            ContainerId = "container-b"
        });

        await fleet.Service.HandleJobCompletedAsync(fleet.JobId, "success", runnerName: $"{fleet.Id}-jit-b");

        Assert.Single(StopsFor(otherInstanceId));
        Assert.Empty(StopsFor(fleet.InstanceId));
        Assert.Null((await _store.Get<RunnerInstance>(fleet.InstanceId))!.JobId);
        // No clearance was asked for: the runner GitHub named is done (only the best-effort
        // deregistration that follows any stop runs).
        Assert.DoesNotContain(fleet.Api.Requests, r => r.PathAndQuery.Contains("&page=", StringComparison.Ordinal));

        await fleet.Service.HandleJobCompletedAsync(otherJobId, "success", runnerName: fleet.RunnerName);

        Assert.Single(StopsFor(fleet.InstanceId));
    }

    private sealed class FakeRunnerRegistrations
    {
        public string Name = "";
        public bool Listed = true;
        public bool Busy;
        public HttpStatusCode ListStatus = HttpStatusCode.OK;
        public HttpStatusCode DeleteStatus = HttpStatusCode.NoContent;
        public bool DeletedBeforeAnyStop;
    }

    private sealed record CancelledJobFleet(
        string Id,
        string HostId,
        string ProfileId,
        string JobId,
        string InstanceId,
        string RunnerName,
        DynamicProvisioningService Service,
        FakeProviderHttpApi Api,
        FakeRunnerRegistrations Runners);

    /// <summary>
    /// A job queued and given a JIT runner, then cancelled while still queued (the webhook
    /// processor records the completed event before the endpoint hands it to cleanup).
    /// </summary>
    private async Task<CancelledJobFleet> StartCancelledJobFleetAsync(string name, Action<FakeRunnerRegistrations> github)
    {
        await ClearFleetDocumentsAsync();
        var id = OrleansTestIds.Create(name);
        var hostId = $"{id}-host";
        var profileId = $"{id}-profile";
        var secret = $"{id}-secret";
        var repo = "octo/fleet";
        var labels = new[] { "self-hosted", "linux", "fleet-docker" };
        var jobId = NextJobId();
        var runners = new FakeRunnerRegistrations();
        github(runners);
        string? instanceId = null;

        var api = new FakeProviderHttpApi()
            .RespondJson(
                r => r.RequestUri!.AbsolutePath.EndsWith("/generate-jitconfig", StringComparison.Ordinal),
                """{"encoded_jit_config":"fake-jit"}""")
            .Respond(
                r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == $"/repos/{repo}/actions/runners",
                _ => runners.ListStatus != HttpStatusCode.OK
                    ? FakeProviderHttpApi.JsonResponse("""{"message":"unavailable"}""", runners.ListStatus)
                    : FakeProviderHttpApi.JsonResponse(JsonSerializer.Serialize(new
                    {
                        total_count = runners.Listed ? 1 : 0,
                        runners = runners.Listed
                            ? new[] { new { id = 4242, name = runners.Name, status = "online", busy = runners.Busy } }
                            : []
                    })))
            .Respond(
                r => r.Method == HttpMethod.Delete && r.RequestUri!.AbsolutePath == $"/repos/{repo}/actions/runners/4242",
                _ =>
                {
                    runners.DeletedBeforeAnyStop = runners.DeleteStatus == HttpStatusCode.NoContent
                        && instanceId != null
                        && StopsFor(instanceId).Count == 0;
                    return FakeProviderHttpApi.JsonResponse("""{"message":"Bad request - Runner is still running a job"}""", runners.DeleteStatus);
                })
            .Respond(_ => FakeProviderHttpApi.JsonResponse("""{"message":"Not Found"}""", HttpStatusCode.NotFound));
        var service = CreateDynamicProvisioningService(api, askGitHubBeforeStopping: true);
        await CreateGitHubFleetWithCredentialAsync(id, hostId, profileId, secret, repo, labels);

        var queued = await ProcessWebhookAsync("github", "queued", jobId, repo, labels, secret);
        await service.ProcessQueuedWebhookEventOnceAsync(queued.EventId!, profileId);
        var provisioned = (await _store.Get<WebhookEvent>(queued.EventId!))!;
        instanceId = provisioned.InstanceId!;
        runners.Name = FindDeployCommand(hostId, instanceId).RunnerName;
        Assert.Equal(jobId.ToString(), (await _store.Get<RunnerInstance>(instanceId))!.JobId);

        await ProcessWebhookAsync("github", "completed", jobId, repo, labels, secret);

        return new CancelledJobFleet(id, hostId, profileId, jobId.ToString(), instanceId, runners.Name, service, api, runners);
    }

    private List<StopRunnerCommand> StopsFor(string instanceId) =>
        _fixture.HostCommands.Commands
            .Where(c => c.Kind == HostCommandKind.StopRunner)
            .Select(c => (StopRunnerCommand)c.Command)
            .Where(c => c.InstanceId == instanceId)
            .ToList();

    private async Task CreateGitHubFleetWithCredentialAsync(
        string id,
        string hostId,
        string profileId,
        string secret,
        string repo,
        string[] labels)
    {
        var ruleId = $"{id}-rule";
        var credentialId = $"{id}-credential";
        await CreateHostAsync(hostId, HostPlatform.Linux, ExecutionBackend.Docker);
        var profile = await CreateProfileAsync(
            profileId,
            "github-docker",
            RunnerProvider.GitHubActions,
            HostPlatform.Linux,
            ExecutionBackend.Docker,
            labels);
        profile.ProviderCredentialId = credentialId;
        await _store.Update(profile);
        await _grainFactory.GetGrain<IProfileGrain>(profileId).SetProfile(profile);
        await _store.Insert(new ProviderCredential
        {
            Id = credentialId,
            Name = "github",
            Provider = RunnerProvider.GitHubActions,
            GitHubToken = "fake-token",
            GitHubOrg = "octo",
            GitHubRepo = repo
        });
        await CreateWebhookRuleAsync(ruleId, "github docker", RunnerProvider.GitHubActions, secret, repo, profileId, labels, hostId);

        var rule = (await _store.Get<ProvisioningRule>(ruleId))!;
        rule.ProviderCredentialId = credentialId;
        await _store.Update(rule);
    }

    private DynamicProvisioningService CreateDynamicProvisioningService(
        FakeProviderHttpApi? api = null,
        IHostCommandDispatcher? hostCommands = null,
        bool askGitHubBeforeStopping = false)
    {
        api ??= new FakeProviderHttpApi(_ => FakeProviderHttpApi.JsonResponse("{}"));
        var auth = new GitHubAuthenticationService(api, NullLogger<GitHubAuthenticationService>.Instance);
        var jit = new JitConfigService(NullLogger<JitConfigService>.Instance, api, auth);
        // The silo's cleanup service has no providers, so it never asks GitHub anything; these
        // tests' own GitHub provider answers from the fake API, as production's does from GitHub.
        var cleanup = askGitHubBeforeStopping
            ? new RunnerRegistrationCleanupService(
                [new GitHubActionsProvider(api, auth, NullLogger<GitHubActionsProvider>.Instance)],
                NullLogger<RunnerRegistrationCleanupService>.Instance)
            : _fixture.Cluster.GetSiloServiceProvider(null!).GetRequiredService<RunnerRegistrationCleanupService>();
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
            hostCommands ?? _fixture.HostCommands,
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
