using RunnerRunner.Core.Models;
using RunnerRunner.Server.Services;

namespace RunnerRunner.Server.Tests.Services;

public class GitHubCredentialResolverTests
{
    [Fact]
    public void ResolveDefaultTarget_FallsBackToFirstInstallationWhenNoDefaultIsMarked()
    {
        var credential = new ProviderCredential
        {
            Name = "github-app",
            Provider = RunnerProvider.GitHubActions,
            GitHubAuthType = GitHubAuthType.GitHubApp,
            GitHubAppInstallations =
            [
                new GitHubAppInstallation
                {
                    Owner = "Redth",
                    InstallationId = "111"
                },
                new GitHubAppInstallation
                {
                    Owner = "PoolMath",
                    InstallationId = "222"
                }
            ]
        };

        var target = GitHubCredentialResolver.ResolveDefaultTarget(credential);

        Assert.NotNull(target);
        Assert.Equal("Redth", target.Owner);
        Assert.Equal("111", target.InstallationId);
    }

    [Fact]
    public void ResolveInstallationId_UsesRepoSpecificTargetBeforeDefault()
    {
        var credential = new ProviderCredential
        {
            Name = "github-app",
            Provider = RunnerProvider.GitHubActions,
            GitHubAuthType = GitHubAuthType.GitHubApp,
            GitHubAppInstallations =
            [
                new GitHubAppInstallation
                {
                    Owner = "Redth",
                    InstallationId = "111",
                    IsDefault = true
                },
                new GitHubAppInstallation
                {
                    Owner = "PoolMath",
                    Repository = "PoolMath/PoolMath",
                    InstallationId = "222"
                }
            ]
        };

        var installationId = GitHubCredentialResolver.ResolveInstallationId(
            credential,
            repository: "PoolMath/PoolMath");

        Assert.Equal("222", installationId);
    }
}
