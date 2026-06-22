using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Services;
using RunnerRunner.Server.Tests.TestSupport;

namespace RunnerRunner.Server.Tests.Services;

public class GitHubAuthenticationServiceTests
{
    [Fact]
    public async Task GetAccessTokenAsync_CachesGitHubAppInstallationToken()
    {
        var requests = 0;
        var api = new FakeProviderHttpApi(_ =>
        {
            requests++;
            return FakeProviderHttpApi.JsonResponse($$"""
            {
              "token": "installation-token-{{requests}}",
              "expires_at": "{{DateTimeOffset.UtcNow.AddMinutes(60):O}}"
            }
            """, HttpStatusCode.Created);
        });
        var service = new GitHubAuthenticationService(api, NullLogger<GitHubAuthenticationService>.Instance);
        var credential = CreateGitHubAppCredential();

        var first = await service.GetAccessTokenAsync(credential);
        var second = await service.GetAccessTokenAsync(credential);

        Assert.Equal("installation-token-1", first);
        Assert.Equal(first, second);
        Assert.Single(api.Requests);
        Assert.Equal("/app/installations/123/access_tokens", api.Requests[0].PathAndQuery);
    }

    [Fact]
    public async Task GetAccessTokenAsync_RefreshesGitHubAppInstallationTokenNearExpiry()
    {
        var requests = 0;
        var api = new FakeProviderHttpApi(_ =>
        {
            requests++;
            var expiresAt = requests == 1
                ? DateTimeOffset.UtcNow.AddMinutes(2)
                : DateTimeOffset.UtcNow.AddMinutes(60);
            return FakeProviderHttpApi.JsonResponse($$"""
            {
              "token": "installation-token-{{requests}}",
              "expires_at": "{{expiresAt:O}}"
            }
            """, HttpStatusCode.Created);
        });
        var service = new GitHubAuthenticationService(api, NullLogger<GitHubAuthenticationService>.Instance);
        var credential = CreateGitHubAppCredential();

        var first = await service.GetAccessTokenAsync(credential);
        var second = await service.GetAccessTokenAsync(credential);

        Assert.Equal("installation-token-1", first);
        Assert.Equal("installation-token-2", second);
        Assert.Equal(2, api.Requests.Count);
    }

    private static ProviderCredential CreateGitHubAppCredential()
    {
        using var rsa = RSA.Create(2048);
        return new ProviderCredential
        {
            Name = "github-app",
            Provider = RunnerProvider.GitHubActions,
            GitHubAuthType = GitHubAuthType.GitHubApp,
            GitHubAppId = "98765",
            GitHubAppPrivateKey = rsa.ExportRSAPrivateKeyPem(),
            GitHubAppInstallationId = "123"
        };
    }
}
