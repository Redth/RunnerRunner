using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NSubstitute;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Providers;
using RunnerRunner.Server.Tests.TestSupport;

namespace RunnerRunner.Server.Tests.Providers;

public class AzDoAgentProviderTests
{
    private readonly ILogger<AzDoAgentProvider> _logger = Substitute.For<ILogger<AzDoAgentProvider>>();

    private AzDoAgentProvider CreateProvider()
    {
        return new AzDoAgentProvider(new FakeProviderHttpApi(), _logger);
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

        var api = new FakeProviderHttpApi(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(releases))
        });
        var provider = new AzDoAgentProvider(api, _logger);

        var versions = await provider.GetAvailableVersionsAsync();

        Assert.Single(versions);
        Assert.Equal("3.232.0", versions[0].Version);
        Assert.True(versions[0].IsLatest);
        Assert.Equal(RunnerProvider.AzureDevOps, versions[0].Provider);
        Assert.Equal("https://example.com/linux-x64", versions[0].DownloadUrlLinuxX64);
        Assert.Equal("https://example.com/osx-arm64", versions[0].DownloadUrlMacOsArm64);
    }

    [Fact]
    public async Task GetAvailableVersions_MalformedJson_ReturnsEmptyList()
    {
        var provider = new AzDoAgentProvider(
            new FakeProviderHttpApi(FakeProviderHttpApi.JsonResponse("""{"unexpected":"shape"}""")),
            _logger);

        var versions = await provider.GetAvailableVersionsAsync();

        Assert.Empty(versions);
    }

    [Fact]
    public async Task RemoveRunner_NoCreds_DoesNotThrow()
    {
        var provider = CreateProvider();
        var cred = new ProviderCredential { Name = "test" };

        // Missing AzDoOrgUrl/AzDoPat → early return
        await provider.RemoveRunnerAsync(cred, "runner-1");
    }

    [Fact]
    public async Task RemoveRunner_FindsPoolAndAgentThenDeletesWithBasicAuth()
    {
        var api = new FakeProviderHttpApi()
            .RespondJson(
                req => req.Method == HttpMethod.Get
                    && (req.RequestUri?.PathAndQuery.Contains("/pools?poolName=mac-pool", StringComparison.Ordinal) ?? false),
                """
                {
                  "value": [
                    { "id": 7, "name": "mac-pool" }
                  ]
                }
                """)
            .RespondJson(
                req => req.Method == HttpMethod.Get
                    && (req.RequestUri?.PathAndQuery.Contains("/pools/7/agents", StringComparison.Ordinal) ?? false),
                """
                {
                  "value": [
                    { "id": 42, "name": "runner-1" }
                  ]
                }
                """)
            .Respond(
                req => req.Method == HttpMethod.Delete,
                _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var provider = new AzDoAgentProvider(api, _logger);

        await provider.RemoveRunnerAsync(new ProviderCredential
        {
            Name = "azdo",
            AzDoOrgUrl = "https://dev.azure.com/octo-org/",
            AzDoPoolName = "mac-pool",
            AzDoPat = "azdo-pat"
        }, "runner-1");

        Assert.Equal(3, api.Requests.Count);
        Assert.Equal("/octo-org/_apis/distributedtask/pools?poolName=mac-pool&api-version=7.1", api.Requests[0].PathAndQuery);
        Assert.Equal("/octo-org/_apis/distributedtask/pools/7/agents?agentName=runner-1&api-version=7.1", api.Requests[1].PathAndQuery);
        Assert.Equal("/octo-org/_apis/distributedtask/pools/7/agents/42?api-version=7.1", api.Requests[2].PathAndQuery);

        var expectedToken = Convert.ToBase64String(Encoding.ASCII.GetBytes(":azdo-pat"));
        Assert.All(api.Requests, request =>
        {
            Assert.Equal("Basic", request.Authorization?.Scheme);
            Assert.Equal(expectedToken, request.Authorization?.Parameter);
        });
    }

    [Fact]
    public async Task RemoveRunner_PoolLookupFailure_DoesNotQueryAgents()
    {
        var api = new FakeProviderHttpApi(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var provider = new AzDoAgentProvider(api, _logger);

        await provider.RemoveRunnerAsync(new ProviderCredential
        {
            Name = "azdo",
            AzDoOrgUrl = "https://dev.azure.com/octo-org",
            AzDoPoolName = "default",
            AzDoPat = "bad-pat"
        }, "runner-1");

        var request = Assert.Single(api.Requests);
        Assert.Contains("/_apis/distributedtask/pools?poolName=default", request.PathAndQuery);
    }
}
