using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using RunnerRunner.Core.Interfaces;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Providers;
using RunnerRunner.Server.Services;

namespace RunnerRunner.Server.Tests.Services;

/// <summary>
/// A rule-owned runner target has no stored <see cref="RunnerProfile"/>: provisioning builds
/// one from the rule. Cleanup used to look only for a stored profile, so for these runners it
/// skipped GitHub entirely -- an idle runner told to stop stayed registered, and GitHub could
/// hand it a job during the 30 seconds its container took to stop.
/// </summary>
public class RunnerReleaseForRunnerTargetsTests
{
    [Fact]
    public async Task ReleaseIdleRunner_AsksGitHubAboutARunnerTargetsRunner()
    {
        var (store, instance) = await RunnerTargetInstanceAsync();
        var (cleanup, provider) = Cleanup(RunnerReleaseOutcome.Busy);

        var outcome = await cleanup.ReleaseIdleRunnerAsync(store, instance);

        Assert.Equal(RunnerReleaseOutcome.Busy, outcome);
        await ((IIdleRunnerReleaser)provider).Received(1).ReleaseIdleRunnerAsync(
            Arg.Is<ProviderCredential>(c => c.GitHubRepo == "Redth/bladeybiscuit"),
            "Redth/bladeybiscuit",
            instance.RunnerName,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryRemoveRunner_DeregistersARunnerTargetsRunner()
    {
        var (store, instance) = await RunnerTargetInstanceAsync();
        var (cleanup, provider) = Cleanup(RunnerReleaseOutcome.Deregistered);

        await cleanup.TryRemoveRunnerAsync(store, instance);

        await provider.Received(1).RemoveRunnerAsync(Arg.Any<ProviderCredential>(), instance.RunnerName, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveProfile_FindsTheTargetThroughTheWebhookEventsRule()
    {
        var (store, instance) = await RunnerTargetInstanceAsync();
        instance.ProvisioningRuleId = null;
        instance.RunnerDefinitionId = null;

        var profile = await RunnerRegistrationCleanupService.ResolveProfileAsync(store, instance);

        Assert.NotNull(profile);
        Assert.Equal("cred-1", profile.ProviderCredentialId);
        Assert.Equal(RunnerProvider.GitHubActions, profile.Provider);
    }

    private static async Task<(Shiny.DocumentDb.IDocumentStore Store, RunnerInstance Instance)> RunnerTargetInstanceAsync()
    {
        var store = TestDocumentStore.Create();
        await store.Insert(new ProviderCredential
        {
            Id = "cred-1",
            Name = "github",
            Provider = RunnerProvider.GitHubActions,
            GitHubToken = "fake-token",
            GitHubOrg = "Redth"
        });
        await store.Insert(new ProvisioningRule
        {
            Id = "rule-1",
            Name = "maui",
            Type = ProvisioningType.Webhook,
            Enabled = true,
            Provider = RunnerProvider.GitHubActions,
            ProviderCredentialId = "cred-1",
            RunnerDefinitions =
            [
                new RunnerDefinition { Id = "target-linux", Name = "linux-maui-docker" }
            ]
        });
        await store.Insert(new WebhookEvent
        {
            Id = "evt-1",
            BindingId = "rule-1",
            Provider = RunnerProvider.GitHubActions.ToString(),
            Action = "queued",
            Status = "completed",
            Repository = "Redth/bladeybiscuit",
            JobId = "128778460275"
        });

        var instance = new RunnerInstance
        {
            Id = "inst-1",
            HostId = "linux-host",
            ProfileId = "target-linux",
            ProvisioningRuleId = "rule-1",
            RunnerDefinitionId = "target-linux",
            RunnerName = "linux-maui-docker-jit-cc99f338",
            ProvisioningMode = "dynamic",
            WebhookEventId = "evt-1",
            JobId = "128778460275",
            Status = RunnerInstanceStatus.Running
        };
        await store.Insert(instance);
        return (store, instance);
    }

    private static (RunnerRegistrationCleanupService Cleanup, IRunnerProviderPlugin Provider) Cleanup(RunnerReleaseOutcome github)
    {
        var provider = Substitute.For<IRunnerProviderPlugin, IIdleRunnerReleaser>();
        provider.Provider.Returns(RunnerProvider.GitHubActions);
        ((IIdleRunnerReleaser)provider)
            .ReleaseIdleRunnerAsync(Arg.Any<ProviderCredential>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(github);
        return (new RunnerRegistrationCleanupService([provider], NullLogger<RunnerRegistrationCleanupService>.Instance), provider);
    }
}
