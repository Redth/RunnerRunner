using RunnerRunner.Core.Models;
using RunnerRunner.Server.Services;

namespace RunnerRunner.Server.Tests.Services;

public class ProviderNavigationLinksTests
{
    [Fact]
    public void BuildJobUrl_CreatesGitHubJobLink()
    {
        var evt = new WebhookEvent
        {
            Provider = RunnerProvider.GitHubActions.ToString(),
            Repository = "octo-org/octo-repo",
            RunId = "12345",
            JobId = "67890"
        };

        var url = ProviderNavigationLinks.BuildJobUrl(evt, null);

        Assert.Equal("https://github.com/octo-org/octo-repo/actions/runs/12345/job/67890", url);
    }

    [Fact]
    public void BuildJobUrl_CreatesGiteaJobLinkWhenInstanceUrlExists()
    {
        var evt = new WebhookEvent
        {
            Provider = RunnerProvider.GiteaActions.ToString(),
            Repository = "team/repo",
            RunId = "12",
            JobId = "34"
        };
        var credential = new ProviderCredential
        {
            Name = "gitea",
            Provider = RunnerProvider.GiteaActions,
            GiteaInstanceUrl = "https://gitea.example.test/"
        };

        var url = ProviderNavigationLinks.BuildJobUrl(evt, credential);

        Assert.Equal("https://gitea.example.test/team/repo/actions/runs/12/jobs/34", url);
    }

    [Fact]
    public void BuildJobUrl_CreatesAzureDevOpsJobLogLink()
    {
        var evt = new WebhookEvent
        {
            Provider = RunnerProvider.AzureDevOps.ToString(),
            RunId = "123",
            JobId = "job-abc"
        };
        var credential = new ProviderCredential
        {
            Name = "azdo",
            Provider = RunnerProvider.AzureDevOps,
            AzDoOrgUrl = "https://dev.azure.com/example",
            AzDoProjectName = "Project One"
        };

        var url = ProviderNavigationLinks.BuildJobUrl(evt, credential);

        Assert.Equal("https://dev.azure.com/example/Project%20One/_build/results?buildId=123&view=logs&j=job-abc", url);
    }

    [Fact]
    public void BuildRunnerPageUrl_CreatesGitHubRepoRunnerSettingsLink()
    {
        var profile = new RunnerProfile
        {
            Name = "linux",
            Provider = RunnerProvider.GitHubActions
        };
        var credential = new ProviderCredential
        {
            Name = "github",
            Provider = RunnerProvider.GitHubActions,
            GitHubRepo = "octo-org/octo-repo"
        };

        var url = ProviderNavigationLinks.BuildRunnerPageUrl(profile, credential);

        Assert.Equal("https://github.com/octo-org/octo-repo/settings/actions/runners", url);
    }

    [Fact]
    public void BuildRunnerPageUrl_CreatesAzureDevOpsAgentQueuesLink()
    {
        var profile = new RunnerProfile
        {
            Name = "windows",
            Provider = RunnerProvider.AzureDevOps,
            RunnerGroup = "Default"
        };
        var credential = new ProviderCredential
        {
            Name = "azdo",
            Provider = RunnerProvider.AzureDevOps,
            AzDoOrgUrl = "https://dev.azure.com/example",
            AzDoProjectName = "Project One",
            AzDoPoolName = "Hosted Pool"
        };

        var url = ProviderNavigationLinks.BuildRunnerPageUrl(profile, credential);

        Assert.Equal("https://dev.azure.com/example/Project%20One/_settings/agentqueues?poolName=Hosted%20Pool", url);
    }
}
