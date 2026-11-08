using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using RunnerRunner.Core.Hub;
using RunnerRunner.Core.Interfaces;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Providers;
using RunnerRunner.Server.Services;
using RunnerRunner.Server.Tests.TestSupport;
using Host = RunnerRunner.Core.Models.Host;

namespace RunnerRunner.Server.Tests.Services;

/// <summary>
/// RunnerTimeoutService's passes stop runners that nothing proves are done with a job of
/// their own. Each must wait for GitHub to release the runner, or leave it running.
/// </summary>
public class RunnerTimeoutStopClearanceTests
{
    [Theory]
    [InlineData(RunnerReleaseOutcome.Busy)]
    [InlineData(RunnerReleaseOutcome.Unverified)]
    public async Task ResolvedEventPass_LeavesARunnerGitHubWontReleaseRunning(RunnerReleaseOutcome github)
    {
        // The job the runner was minted for was cancelled while queued (resolved, no runner
        // named), and the runner still holds its claim because the in_progress webhook for the
        // job GitHub really gave it hasn't been processed yet.
        var scenario = await ScenarioAsync(github, eventStatus: "completed", startedMinutesAgo: 1);

        await scenario.ScanAsync();

        Assert.Empty(scenario.Stops());
        var instance = await scenario.Store.Get<RunnerInstance>(scenario.Instance.Id);
        Assert.NotNull(instance);
        Assert.NotEqual(RunnerInstanceStatus.Failed, instance.Status);
        if (github == RunnerReleaseOutcome.Busy)
        {
            Assert.Null(instance.JobId);
            Assert.NotNull(instance.ClaimReleasedAt);
        }
        else
        {
            Assert.Equal(scenario.Instance.JobId, instance.JobId);
        }
    }

    [Theory]
    [InlineData(RunnerReleaseOutcome.Deregistered)]
    [InlineData(RunnerReleaseOutcome.NotRegistered)]
    public async Task ResolvedEventPass_StopsARunnerGitHubReleased(RunnerReleaseOutcome github)
    {
        var scenario = await ScenarioAsync(github, eventStatus: "completed", startedMinutesAgo: 1);

        await scenario.ScanAsync();

        var stop = Assert.Single(scenario.Stops());
        Assert.Equal(scenario.Instance.Id, stop.InstanceId);
        Assert.Null(await scenario.Store.Get<RunnerInstance>(scenario.Instance.Id));
    }

    [Fact]
    public async Task PickupTimeout_LeavesABusyRunnerRunningAndFindsItsJobAnotherRunner()
    {
        // "Never picked up its queued job" after 10 minutes, but GitHub says it's running one.
        var scenario = await ScenarioAsync(RunnerReleaseOutcome.Busy, eventStatus: "provisioned", startedMinutesAgo: 11);

        await scenario.ScanAsync();

        Assert.Empty(scenario.Stops());
        var evt = await scenario.Store.Get<WebhookEvent>(scenario.Event.Id);
        Assert.NotNull(evt);
        Assert.Equal("pending", evt.Status);
        Assert.Null((await scenario.Store.Get<RunnerInstance>(scenario.Instance.Id))!.JobId);
    }

    [Fact]
    public async Task RuntimeBackstop_LeavesARunnerGitHubCallsBusyRunning()
    {
        var scenario = await ScenarioAsync(RunnerReleaseOutcome.Busy, eventStatus: "in_progress", startedMinutesAgo: 7 * 60);

        await scenario.ScanAsync();

        Assert.Empty(scenario.Stops());
        Assert.NotNull(await scenario.Store.Get<RunnerInstance>(scenario.Instance.Id));
    }

    [Fact]
    public async Task StartingTimeout_StopsARunnerOnlyAfterGitHubReleasesIt()
    {
        var scenario = await ScenarioAsync(
            RunnerReleaseOutcome.Deregistered,
            eventStatus: "provisioned",
            startedMinutesAgo: 6,
            status: RunnerInstanceStatus.Starting);

        await scenario.ScanAsync();

        Assert.Single(scenario.Stops());
        await scenario.Releaser.Received(1).ReleaseIdleRunnerAsync(
            Arg.Any<ProviderCredential>(), "octo/fleet", scenario.Instance.RunnerName, Arg.Any<CancellationToken>());
    }

    private static async Task<Scenario> ScenarioAsync(
        RunnerReleaseOutcome github,
        string eventStatus,
        int startedMinutesAgo,
        RunnerInstanceStatus status = RunnerInstanceStatus.Running)
    {
        var store = TestDocumentStore.Create();
        var now = DateTime.UtcNow;
        var host = new Host { Id = "host-1", Name = "linux-host", Platform = HostPlatform.Linux, AgentStatus = AgentStatus.Online };
        var credential = new ProviderCredential
        {
            Id = "cred-1",
            Name = "github",
            Provider = RunnerProvider.GitHubActions,
            GitHubToken = "fake-token",
            GitHubOrg = "octo"
        };
        var profile = new RunnerProfile
        {
            Id = "profile-1",
            Name = "linux-docker",
            Provider = RunnerProvider.GitHubActions,
            ProviderCredentialId = credential.Id
        };
        var evt = new WebhookEvent
        {
            Id = "evt-1",
            Provider = RunnerProvider.GitHubActions.ToString(),
            Action = "queued",
            Status = eventStatus,
            Repository = "octo/fleet",
            JobId = "job-1",
            InstanceId = "inst-1",
            ReceivedAt = now.AddMinutes(-startedMinutesAgo - 1),
            ResolvedAt = eventStatus is "completed" or "in_progress" ? now.AddSeconds(-30) : null
        };
        var instance = new RunnerInstance
        {
            Id = "inst-1",
            HostId = host.Id,
            ProfileId = profile.Id,
            RunnerName = "linux-docker-jit-cc99f338",
            ProvisioningMode = "dynamic",
            WebhookEventId = evt.Id,
            JobId = evt.JobId,
            Status = status,
            CreatedAt = now.AddMinutes(-startedMinutesAgo - 1),
            DeployedAt = now.AddMinutes(-startedMinutesAgo),
            StartedAt = now.AddMinutes(-startedMinutesAgo),
            LastHealthCheck = now,
            ContainerId = "container-1"
        };
        await store.Insert(host);
        await store.Insert(credential);
        await store.Insert(profile);
        await store.Insert(evt);
        await store.Insert(instance);

        var provider = Substitute.For<IRunnerProviderPlugin, IIdleRunnerReleaser>();
        provider.Provider.Returns(RunnerProvider.GitHubActions);
        var releaser = (IIdleRunnerReleaser)provider;
        releaser.ReleaseIdleRunnerAsync(Arg.Any<ProviderCredential>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(github == RunnerReleaseOutcome.Unverified
                ? Task.FromException<RunnerReleaseOutcome>(new HttpRequestException("GitHub is unreachable"))
                : Task.FromResult(github));

        var hostCommands = new RecordingHostCommandDispatcher();
        var services = new ServiceCollection()
            .AddSingleton(store)
            .AddSingleton(new RunnerRegistrationCleanupService([provider], NullLogger<RunnerRegistrationCleanupService>.Instance))
            .BuildServiceProvider();
        var service = new RunnerTimeoutService(
            NullLogger<RunnerTimeoutService>.Instance,
            services,
            hostCommands,
            new ConfigurationBuilder().Build());

        return new Scenario(store, evt, instance, service, hostCommands, releaser);
    }

    private sealed record Scenario(
        Shiny.DocumentDb.IDocumentStore Store,
        WebhookEvent Event,
        RunnerInstance Instance,
        RunnerTimeoutService Service,
        RecordingHostCommandDispatcher HostCommands,
        IIdleRunnerReleaser Releaser)
    {
        public Task ScanAsync()
        {
            var method = typeof(RunnerTimeoutService).GetMethod(
                "ScanForTimeoutsAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            return (Task)method.Invoke(Service, [CancellationToken.None])!;
        }

        public List<StopRunnerCommand> Stops() =>
            HostCommands.Commands
                .Where(c => c.Kind == HostCommandKind.StopRunner)
                .Select(c => (StopRunnerCommand)c.Command)
                .ToList();
    }
}
