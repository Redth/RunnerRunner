using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NSubstitute;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Providers;
using RunnerRunner.Server.Services;

namespace RunnerRunner.Server.Tests.Providers;

public class GitHubActionsProviderTests
{
    private readonly ILogger<GitHubActionsProvider> _logger = Substitute.For<ILogger<GitHubActionsProvider>>();
    private readonly ILogger<GitHubAuthenticationService> _authLogger = Substitute.For<ILogger<GitHubAuthenticationService>>();

    private GitHubActionsProvider CreateProvider(HttpResponseMessage response)
    {
        var handler = new FakeHttpHandler(response);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));
        return new GitHubActionsProvider(factory, new GitHubAuthenticationService(factory, _authLogger), _logger);
    }

    private GitHubActionsProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var fakeHandler = new FakeHttpHandler(handler);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(fakeHandler));
        return new GitHubActionsProvider(factory, new GitHubAuthenticationService(factory, _authLogger), _logger);
    }

    [Fact]
    public async Task GetRegistrationToken_OrgLevel_UsesOrgEndpoint()
    {
        string? capturedUrl = null;
        var provider = CreateProvider(req =>
        {
            capturedUrl = req.RequestUri?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { token = "test-token-123" }))
            };
        });

        var cred = new ProviderCredential
        {
            Name = "test",
            GitHubOrg = "my-org",
            GitHubToken = "ghp_fake"
        };

        var token = await provider.GetRegistrationTokenAsync(cred);

        Assert.Equal("test-token-123", token);
        Assert.Contains("/orgs/my-org/actions/runners/registration-token", capturedUrl);
    }

    [Fact]
    public async Task GetRegistrationToken_RepoLevel_UsesRepoEndpoint()
    {
        string? capturedUrl = null;
        var provider = CreateProvider(req =>
        {
            capturedUrl = req.RequestUri?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { token = "repo-token" }))
            };
        });

        var cred = new ProviderCredential
        {
            Name = "test",
            GitHubOrg = "my-org",
            GitHubRepo = "my-repo",
            GitHubToken = "ghp_fake"
        };

        var token = await provider.GetRegistrationTokenAsync(cred);

        Assert.Equal("repo-token", token);
        Assert.Contains("/repos/my-org/my-repo/actions/runners/registration-token", capturedUrl);
    }

    [Fact]
    public async Task GetRegistrationToken_FullOwnerRepoValue_DoesNotDuplicateOrg()
    {
        string? capturedUrl = null;
        var provider = CreateProvider(req =>
        {
            capturedUrl = req.RequestUri?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { token = "repo-token" }))
            };
        });

        var cred = new ProviderCredential
        {
            Name = "test",
            GitHubOrg = "ignored-org",
            GitHubRepo = "actual-owner/my-repo",
            GitHubToken = "ghp_fake"
        };

        var token = await provider.GetRegistrationTokenAsync(cred);

        Assert.Equal("repo-token", token);
        Assert.Contains("/repos/actual-owner/my-repo/actions/runners/registration-token", capturedUrl);
        Assert.DoesNotContain("/repos/ignored-org/actual-owner/my-repo", capturedUrl);
    }

    [Fact]
    public async Task GetRegistrationToken_UsesCustomApiUrl()
    {
        string? capturedUrl = null;
        var provider = CreateProvider(req =>
        {
            capturedUrl = req.RequestUri?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { token = "ghe-token" }))
            };
        });

        var cred = new ProviderCredential
        {
            Name = "test",
            GitHubOrg = "my-org",
            GitHubToken = "ghp_fake",
            GitHubApiUrl = "https://github.example.com/api/v3"
        };

        await provider.GetRegistrationTokenAsync(cred);

        Assert.StartsWith("https://github.example.com/api/v3/", capturedUrl);
    }

    [Fact]
    public async Task GetRegistrationToken_GitHubApp_UsesInstallationToken()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var privateKey = rsa.ExportPkcs8PrivateKeyPem();
        string? tokenEndpointAuth = null;
        string? registrationEndpointAuth = null;

        var provider = CreateProvider(req =>
        {
            if (req.RequestUri?.AbsolutePath == "/app/installations/123/access_tokens")
            {
                tokenEndpointAuth = req.Headers.Authorization?.ToString();
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        token = "installation-token",
                        expires_at = DateTimeOffset.UtcNow.AddMinutes(30)
                    }))
                };
            }

            registrationEndpointAuth = req.Headers.Authorization?.ToString();
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { token = "runner-token" }))
            };
        });

        var cred = new ProviderCredential
        {
            Name = "app",
            GitHubOrg = "my-org",
            GitHubAuthType = GitHubAuthType.GitHubApp,
            GitHubAppId = "98765",
            GitHubAppInstallationId = "123",
            GitHubAppPrivateKey = privateKey
        };

        var token = await provider.GetRegistrationTokenAsync(cred);

        Assert.Equal("runner-token", token);
        Assert.StartsWith("Bearer ", tokenEndpointAuth);
        Assert.Equal("Bearer installation-token", registrationEndpointAuth);
    }

    [Fact]
    public async Task GetRegistrationToken_NullToken_Throws()
    {
        var provider = CreateProvider(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { token = (string?)null }))
        });

        var cred = new ProviderCredential
        {
            Name = "test",
            GitHubOrg = "org",
            GitHubToken = "tok"
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetRegistrationTokenAsync(cred));
    }

    [Fact]
    public async Task GetRegistrationToken_HttpError_Throws()
    {
        var provider = CreateProvider(new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var cred = new ProviderCredential
        {
            Name = "test",
            GitHubOrg = "org",
            GitHubToken = "bad-token"
        };

        await Assert.ThrowsAsync<HttpRequestException>(
            () => provider.GetRegistrationTokenAsync(cred));
    }

    [Fact]
    public async Task GetAvailableVersions_ParsesReleasesCorrectly()
    {
        var releasesJson = """
        [
            {
                "tag_name": "v2.311.0",
                "assets": [
                    { "name": "actions-runner-linux-x64-2.311.0.tar.gz", "browser_download_url": "https://example.com/linux-x64" },
                    { "name": "actions-runner-osx-arm64-2.311.0.tar.gz", "browser_download_url": "https://example.com/osx-arm64" },
                    { "name": "actions-runner-win-x64-2.311.0.zip", "browser_download_url": "https://example.com/win-x64" }
                ]
            },
            {
                "tag_name": "v2.310.0",
                "assets": []
            }
        ]
        """;

        var provider = CreateProvider(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(releasesJson)
        });

        var versions = await provider.GetAvailableVersionsAsync();

        Assert.Equal(2, versions.Count);

        var latest = versions[0];
        Assert.Equal("2.311.0", latest.Version);
        Assert.True(latest.IsLatest);
        Assert.Equal(RunnerProvider.GitHubActions, latest.Provider);
        Assert.Equal("https://example.com/linux-x64", latest.DownloadUrlLinuxX64);
        Assert.Equal("https://example.com/osx-arm64", latest.DownloadUrlMacOsArm64);
        Assert.Equal("https://example.com/win-x64", latest.DownloadUrlWindowsX64);

        var older = versions[1];
        Assert.Equal("2.310.0", older.Version);
        Assert.False(older.IsLatest);
    }

    [Fact]
    public async Task RemoveRunner_FindsAndDeletesRunner()
    {
        var callCount = 0;
        string? deleteUrl = null;

        var provider = CreateProvider(req =>
        {
            callCount++;
            if (req.Method == HttpMethod.Get)
            {
                var body = new
                {
                    runners = new[]
                    {
                        new { id = 42, name = "my-runner" },
                        new { id = 43, name = "other-runner" }
                    }
                };
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(body))
                };
            }
            deleteUrl = req.RequestUri?.ToString();
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });

        var cred = new ProviderCredential
        {
            Name = "test",
            GitHubOrg = "org",
            GitHubToken = "tok"
        };

        await provider.RemoveRunnerAsync(cred, "my-runner");

        Assert.Equal(2, callCount); // GET list + DELETE
        Assert.Contains("/42", deleteUrl);
    }

    [Fact]
    public async Task RemoveRunner_NotFound_DoesNotThrow()
    {
        var provider = CreateProvider(req =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { runners = Array.Empty<object>() }))
            };
        });

        var cred = new ProviderCredential
        {
            Name = "test",
            GitHubOrg = "org",
            GitHubToken = "tok"
        };

        // Should not throw
        await provider.RemoveRunnerAsync(cred, "nonexistent-runner");
    }
}

/// <summary>
/// Fake HTTP handler that returns a pre-configured response.
/// </summary>
internal class FakeHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

    public FakeHttpHandler(HttpResponseMessage response)
        : this(_ => response) { }

    public FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        _handler = handler;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        => Task.FromResult(_handler(request));
}
