using System.Net;
using Microsoft.Extensions.Logging;
using NSubstitute;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Providers;
using RunnerRunner.Server.Tests.TestSupport;

namespace RunnerRunner.Server.Tests.Providers;

public class GiteaActionsProviderTests
{
    private readonly ILogger<GiteaActionsProvider> _logger = Substitute.For<ILogger<GiteaActionsProvider>>();

    private GiteaActionsProvider CreateProvider()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());
        return new GiteaActionsProvider(factory, _logger);
    }

    [Fact]
    public async Task GetRegistrationToken_ReturnsConfiguredToken()
    {
        var provider = CreateProvider();
        var cred = new ProviderCredential
        {
            Name = "test",
            GiteaRunnerToken = "my-gitea-token-123",
            GiteaInstanceUrl = "https://gitea.example.com"
        };

        var token = await provider.GetRegistrationTokenAsync(cred);
        Assert.Equal("my-gitea-token-123", token);
    }

    [Fact]
    public async Task GetRegistrationToken_MissingToken_Throws()
    {
        var provider = CreateProvider();
        var cred = new ProviderCredential
        {
            Name = "test",
            GiteaInstanceUrl = "https://gitea.example.com"
            // No token
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetRegistrationTokenAsync(cred));
    }

    [Fact]
    public void Provider_ReturnsGiteaActions()
    {
        var provider = CreateProvider();
        Assert.Equal(RunnerProvider.GiteaActions, provider.Provider);
    }

    [Fact]
    public async Task RemoveRunner_DoesNotThrow()
    {
        var provider = CreateProvider();
        var cred = new ProviderCredential { Name = "test" };

        // Gitea runners self-deregister, so this is a no-op
        await provider.RemoveRunnerAsync(cred, "any-runner");
    }

    [Fact]
    public async Task GetAvailableVersions_ParsesPrimaryGiteaReleaseFeed()
    {
        var api = new FakeProviderHttpApi()
            .RespondJson(
                req => req.RequestUri?.Host == "gitea.com",
                """
                [
                  { "tag_name": "v0.2.11" },
                  { "tag_name": "v0.2.10" }
                ]
                """);
        var provider = new GiteaActionsProvider(api, _logger);

        var versions = await provider.GetAvailableVersionsAsync();

        Assert.Equal(2, versions.Count);
        Assert.Equal("0.2.11", versions[0].Version);
        Assert.True(versions[0].IsLatest);
        Assert.Equal(RunnerProvider.GiteaActions, versions[0].Provider);
        Assert.False(versions[1].IsLatest);

        var request = Assert.Single(api.Requests);
        Assert.Equal("/api/v1/repos/gitea/act_runner/releases?limit=10", request.PathAndQuery);
        Assert.Contains("RunnerRunner/1.0", request.UserAgent);
    }

    [Fact]
    public async Task GetAvailableVersions_FallsBackToGitHubMirrorWhenGiteaFails()
    {
        var api = new FakeProviderHttpApi()
            .Respond(
                req => req.RequestUri?.Host == "gitea.com",
                _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))
            .RespondJson(
                req => req.RequestUri?.Host == "api.github.com",
                """
                [
                  { "tag_name": "v0.2.9" }
                ]
                """);
        var provider = new GiteaActionsProvider(api, _logger);

        var versions = await provider.GetAvailableVersionsAsync();

        var version = Assert.Single(versions);
        Assert.Equal("0.2.9", version.Version);
        Assert.Equal(2, api.Requests.Count);
        Assert.Equal("/repos/gitea/act_runner/releases?per_page=10", api.Requests[1].PathAndQuery);
    }

    [Fact]
    public async Task GetAvailableVersions_MalformedJson_ReturnsEmptyList()
    {
        var provider = new GiteaActionsProvider(
            new FakeProviderHttpApi(FakeProviderHttpApi.JsonResponse("""{"unexpected":"shape"}""")),
            _logger);

        var versions = await provider.GetAvailableVersionsAsync();

        Assert.Empty(versions);
    }
}
