using Microsoft.Extensions.Logging;
using NSubstitute;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Providers;

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
}
