using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using RunnerRunner.Core.Interfaces;
using RunnerRunner.Core.Models;

namespace RunnerRunner.Server.Providers;

/// <summary>
/// GitHub Actions runner provider. Handles registration token generation,
/// runner deregistration, and version discovery via GitHub API.
/// </summary>
public class GitHubActionsProvider : IRunnerProviderPlugin
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<GitHubActionsProvider> _logger;

    public RunnerProvider Provider => RunnerProvider.GitHubActions;

    public GitHubActionsProvider(IHttpClientFactory httpFactory, ILogger<GitHubActionsProvider> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<string> GetRegistrationTokenAsync(ProviderCredential credential, CancellationToken ct = default)
    {
        var apiUrl = credential.GitHubApiUrl?.TrimEnd('/') ?? "https://api.github.com";

        string endpoint;
        if (string.IsNullOrEmpty(credential.GitHubRepo))
        {
            endpoint = $"{apiUrl}/orgs/{credential.GitHubOrg}/actions/runners/registration-token";
            _logger.LogInformation("Requesting org-level registration token for {Org}", credential.GitHubOrg);
        }
        else
        {
            endpoint = $"{apiUrl}/repos/{credential.GitHubOrg}/{credential.GitHubRepo}/actions/runners/registration-token";
            _logger.LogInformation("Requesting repo-level registration token for {Org}/{Repo}",
                credential.GitHubOrg, credential.GitHubRepo);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.GitHubToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("RunnerRunner", "1.0"));

        var response = await _httpFactory.CreateClient().SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var token = json.GetProperty("token").GetString()
            ?? throw new InvalidOperationException("Registration token was null in response");

        _logger.LogInformation("Registration token obtained (expires in ~60 minutes)");
        return token;
    }

    public async Task RemoveRunnerAsync(ProviderCredential credential, string runnerName, CancellationToken ct = default)
    {
        var apiUrl = credential.GitHubApiUrl?.TrimEnd('/') ?? "https://api.github.com";

        string listEndpoint;
        if (string.IsNullOrEmpty(credential.GitHubRepo))
            listEndpoint = $"{apiUrl}/orgs/{credential.GitHubOrg}/actions/runners";
        else
            listEndpoint = $"{apiUrl}/repos/{credential.GitHubOrg}/{credential.GitHubRepo}/actions/runners";

        using var listRequest = new HttpRequestMessage(HttpMethod.Get, listEndpoint);
        listRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.GitHubToken);
        listRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        listRequest.Headers.UserAgent.Add(new ProductInfoHeaderValue("RunnerRunner", "1.0"));

        var listResponse = await _httpFactory.CreateClient().SendAsync(listRequest, ct);
        listResponse.EnsureSuccessStatusCode();

        var json = await listResponse.Content.ReadFromJsonAsync<JsonElement>(ct);
        var runners = json.GetProperty("runners").EnumerateArray();

        foreach (var runner in runners)
        {
            if (runner.GetProperty("name").GetString() == runnerName)
            {
                var runnerId = runner.GetProperty("id").GetInt64();
                var deleteEndpoint = $"{listEndpoint}/{runnerId}";

                using var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, deleteEndpoint);
                deleteRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.GitHubToken);
                deleteRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                deleteRequest.Headers.UserAgent.Add(new ProductInfoHeaderValue("RunnerRunner", "1.0"));

                var deleteResponse = await _httpFactory.CreateClient().SendAsync(deleteRequest, ct);
                deleteResponse.EnsureSuccessStatusCode();

                _logger.LogInformation("Runner {RunnerName} (id: {RunnerId}) removed from GitHub", runnerName, runnerId);
                return;
            }
        }

        _logger.LogWarning("Runner {RunnerName} not found in GitHub, may have already been removed", runnerName);
    }

    public async Task<List<RunnerAgentVersion>> GetAvailableVersionsAsync(CancellationToken ct = default)
    {
        var versions = new List<RunnerAgentVersion>();

        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://api.github.com/repos/actions/runner/releases?per_page=10");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("RunnerRunner", "1.0"));

        var response = await _httpFactory.CreateClient().SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var releases = await response.Content.ReadFromJsonAsync<JsonElement[]>(ct) ?? [];

        for (var i = 0; i < releases.Length; i++)
        {
            var release = releases[i];
            var tagName = release.GetProperty("tag_name").GetString()?.TrimStart('v') ?? "";
            if (string.IsNullOrEmpty(tagName)) continue;

            var version = new RunnerAgentVersion
            {
                Provider = RunnerProvider.GitHubActions,
                Version = tagName,
                IsLatest = i == 0
            };

            // Parse download URLs from assets
            if (release.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? "";
                    var url = asset.GetProperty("browser_download_url").GetString();

                    if (name.Contains("linux-x64")) version.DownloadUrlLinuxX64 = url;
                    else if (name.Contains("linux-arm64")) version.DownloadUrlLinuxArm64 = url;
                    else if (name.Contains("osx-x64")) version.DownloadUrlMacOsX64 = url;
                    else if (name.Contains("osx-arm64")) version.DownloadUrlMacOsArm64 = url;
                    else if (name.Contains("win-x64")) version.DownloadUrlWindowsX64 = url;
                    else if (name.Contains("win-arm64")) version.DownloadUrlWindowsArm64 = url;
                }
            }

            versions.Add(version);
        }

        _logger.LogInformation("Discovered {Count} GitHub Actions runner versions", versions.Count);
        return versions;
    }
}
