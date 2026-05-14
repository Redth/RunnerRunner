using Microsoft.Extensions.Logging;
using NSubstitute;
using RunnerRunner.Core.Interfaces;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Services;

namespace RunnerRunner.Server.Tests.Services;

public class RunnerRegistrationCleanupServiceTests
{
    [Fact]
    public async Task TryRemoveRunnerAsync_UsesWebhookRepositoryScopeForGitHubCleanup()
    {
        var store = TestDocumentStore.Create();

        var credential = new ProviderCredential
        {
            Id = "cred-1",
            Name = "github",
            Provider = RunnerProvider.GitHubActions,
            GitHubOrg = "redth",
            GitHubToken = "ghp_test"
        };
        await store.Insert(credential);

        var profile = new RunnerProfile
        {
            Id = "profile-1",
            Name = "linux-jit",
            Provider = RunnerProvider.GitHubActions,
            ProviderCredentialId = credential.Id
        };
        await store.Insert(profile);

        await store.Insert(new WebhookEvent
        {
            Id = "evt-1",
            Repository = "poolmath/PoolMath",
            JobId = "job-1",
            Provider = RunnerProvider.GitHubActions.ToString()
        });

        var instance = new RunnerInstance
        {
            Id = "inst-1",
            RunnerName = "linux-jit-1234",
            ProfileId = profile.Id,
            WebhookEventId = "evt-1",
            JobId = "job-1",
            ProvisioningMode = "dynamic"
        };

        var provider = Substitute.For<IRunnerProviderPlugin>();
        provider.Provider.Returns(RunnerProvider.GitHubActions);

        var service = new RunnerRegistrationCleanupService(
            [provider],
            Substitute.For<ILogger<RunnerRegistrationCleanupService>>());

        await service.TryRemoveRunnerAsync(store, instance);

        await provider.Received(1).RemoveRunnerAsync(
            Arg.Is<ProviderCredential>(c =>
                c.GitHubOrg == "poolmath"
                && c.GitHubRepo == "poolmath/PoolMath"),
            instance.RunnerName,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ScopeGitHubCredentialToRepository_PreservesGitHubAppInstallationFromWebhook()
    {
        var credential = new ProviderCredential
        {
            Name = "github-app",
            Provider = RunnerProvider.GitHubActions,
            GitHubOrg = "redth",
            GitHubAuthType = GitHubAuthType.GitHubApp,
            GitHubAppId = "98765",
            GitHubAppInstallationId = "default-installation",
            GitHubAppPrivateKey = "private-key",
            GitHubAppWebhookSecret = "secret"
        };

        var scoped = RunnerRegistrationCleanupService.ScopeGitHubCredentialToRepository(
            credential,
            "poolmath/PoolMath",
            "webhook-installation");

        Assert.Equal("poolmath", scoped.GitHubOrg);
        Assert.Equal("poolmath/PoolMath", scoped.GitHubRepo);
        Assert.Equal("webhook-installation", scoped.GitHubAppInstallationId);
        Assert.Equal("98765", scoped.GitHubAppId);
        Assert.Equal("private-key", scoped.GitHubAppPrivateKey);
        Assert.Equal("secret", scoped.GitHubAppWebhookSecret);
    }

    [Fact]
    public void ScopeGitHubCredentialToRepository_PreservesCredentialWhenRepositoryMissing()
    {
        var credential = new ProviderCredential
        {
            Name = "github",
            Provider = RunnerProvider.GitHubActions,
            GitHubOrg = "redth",
            GitHubRepo = "RunnerRunner"
        };

        var scoped = RunnerRegistrationCleanupService.ScopeGitHubCredentialToRepository(credential, null);

        Assert.Equal("redth", scoped.GitHubOrg);
        Assert.Equal("RunnerRunner", scoped.GitHubRepo);
    }

    [Fact]
    public void BuildGitHubSweepScopes_IncludesRecentReposForMatchingOrg()
    {
        var credential = new ProviderCredential
        {
            Name = "github",
            Provider = RunnerProvider.GitHubActions,
            GitHubOrg = "Redth",
            GitHubToken = "ghp_test"
        };

        var scopes = RunnerRegistrationCleanupService.BuildGitHubSweepScopes(
                [credential],
                ["Redth/ailoha", "PoolMath/PoolMath"])
            .ToList();

        Assert.Contains(scopes, s => s.GitHubOrg == "Redth" && string.IsNullOrWhiteSpace(s.GitHubRepo));
        Assert.Contains(scopes, s => s.GitHubRepo == "Redth/ailoha");
        Assert.DoesNotContain(scopes, s => s.GitHubRepo == "PoolMath/PoolMath");
    }

    [Fact]
    public void BuildGitHubProtectedRunnerNames_KeepsManagedNamesEvenWhenNotActive()
    {
        var names = RunnerRegistrationCleanupService.BuildGitHubProtectedRunnerNames([
            new RunnerInstance
            {
                RunnerName = "MAUI-Linux-jit-c1e37630",
                HostId = "host-1",
                ProvisioningMode = "dynamic",
                Status = RunnerInstanceStatus.Crashed,
                ManagedByRunnerRunner = true
            },
            new RunnerInstance
            {
                RunnerName = "external-runner",
                HostId = "",
                Status = RunnerInstanceStatus.Running,
                ManagedByRunnerRunner = false
            }
        ]);

        Assert.Contains("MAUI-Linux-jit-c1e37630", names);
        Assert.DoesNotContain("external-runner", names);
    }
}
