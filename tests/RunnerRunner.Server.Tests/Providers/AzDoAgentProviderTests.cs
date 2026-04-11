using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NSubstitute;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Providers;

namespace RunnerRunner.Server.Tests.Providers;

public class AzDoAgentProviderTests
{
    private readonly ILogger<AzDoAgentProvider> _logger = Substitute.For<ILogger<AzDoAgentProvider>>();

    private AzDoAgentProvider CreateProvider()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());
        return new AzDoAgentProvider(factory, _logger);
    }

    [Fact]
    public async Task GetRegistrationToken_ReturnsPat()
    {
        var provider = CreateProvider();
        var cred = new ProviderCredential
        {
            Name = "test",
            AzDoPat = "my-azdo-pat",
            AzDoOrgUrl = "https://dev.azure.com/myorg"
        };

        var token = await provider.GetRegistrationTokenAsync(cred);
        Assert.Equal("my-azdo-pat", token);
    }

    [Fact]
    public async Task GetRegistrationToken_NoPat_Throws()
    {
        var provider = CreateProvider();
        var cred = new ProviderCredential { Name = "test" };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetRegistrationTokenAsync(cred));
    }

    [Fact]
    public void Provider_ReturnsAzureDevOps()
    {
        var provider = CreateProvider();
        Assert.Equal(RunnerProvider.AzureDevOps, provider.Provider);
    }

    [Fact]
    public async Task GetAvailableVersions_ParsesGitHubReleases()
    {
        var releases = new[]
        {
            new
            {
                tag_name = "v3.232.0",
                assets = new[]
                {
                    new { name = "vsts-agent-linux-x64-3.232.0.tar.gz", browser_download_url = "https://example.com/linux-x64" },
                    new { name = "vsts-agent-osx-arm64-3.232.0.tar.gz", browser_download_url = "https://example.com/osx-arm64" }
                }
            }
        };

        var handler = new FakeHttpHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(releases))
        });
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));
        var provider = new AzDoAgentProvider(factory, _logger);

        var versions = await provider.GetAvailableVersionsAsync();

        Assert.Single(versions);
        Assert.Equal("3.232.0", versions[0].Version);
        Assert.True(versions[0].IsLatest);
        Assert.Equal(RunnerProvider.AzureDevOps, versions[0].Provider);
        Assert.Equal("https://example.com/linux-x64", versions[0].DownloadUrlLinuxX64);
        Assert.Equal("https://example.com/osx-arm64", versions[0].DownloadUrlMacOsArm64);
    }

    [Fact]
    public async Task RemoveRunner_NoCreds_DoesNotThrow()
    {
        var provider = CreateProvider();
        var cred = new ProviderCredential { Name = "test" };

        // Missing AzDoOrgUrl/AzDoPat → early return
        await provider.RemoveRunnerAsync(cred, "runner-1");
    }
}
