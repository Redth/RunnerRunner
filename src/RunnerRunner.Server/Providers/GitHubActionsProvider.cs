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
    internal sealed record GitHubRunnerRegistration(long Id, string Name, string Status, bool Busy);

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
        var normalizedRepo = NormalizeRepo(credential.GitHubRepo, credential.GitHubOrg);

        string endpoint;
        if (string.IsNullOrEmpty(normalizedRepo))
        {
            endpoint = $"{apiUrl}/orgs/{credential.GitHubOrg}/actions/runners/registration-token";
            _logger.LogInformation("Requesting org-level registration token for {Org}", credential.GitHubOrg);
        }
        else
        {
            var parts = normalizedRepo.Split('/', 2);
            endpoint = $"{apiUrl}/repos/{parts[0]}/{parts[1]}/actions/runners/registration-token";
            _logger.LogInformation("Requesting repo-level registration token for {Repo}", normalizedRepo);
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
        var registrations = await ListRunnerRegistrationsAsync(credential, ct);

        foreach (var runner in registrations)
        {
            if (string.Equals(runner.Name, runnerName, StringComparison.OrdinalIgnoreCase))
            {
                await DeleteRunnerAsync(credential, runner.Id, ct);
                _logger.LogInformation("Runner {RunnerName} (id: {RunnerId}) removed from GitHub", runnerName, runner.Id);
                return;
            }
        }

        _logger.LogInformation("Runner {RunnerName} was not found during GitHub cleanup; it may have already deregistered itself", runnerName);
    }

    internal async Task<int> RemoveOfflineDynamicRunnersAsync(
        ProviderCredential credential,
        ISet<string> protectedRunnerNames,
        CancellationToken ct = default)
    {
        var registrations = await ListRunnerRegistrationsAsync(credential, ct);
        var removed = 0;

        foreach (var runner in registrations)
        {
            if (!IsDynamicRunnerName(runner.Name)
                || runner.Busy
                || string.Equals(runner.Status, "online", StringComparison.OrdinalIgnoreCase)
                || protectedRunnerNames.Contains(runner.Name))
            {
                continue;
            }

            await DeleteRunnerAsync(credential, runner.Id, ct);
            removed++;

            _logger.LogInformation(
                "Removed stale offline GitHub runner registration {RunnerName} (id: {RunnerId})",
                runner.Name,
                runner.Id);
        }

        return removed;
    }

    internal async Task<IReadOnlyList<GitHubRunnerRegistration>> ListRunnerRegistrationsAsync(
        ProviderCredential credential,
        CancellationToken ct = default)
    {
        var listEndpoint = BuildRunnersEndpoint(credential);

        using var listRequest = CreateGitHubRequest(HttpMethod.Get, $"{listEndpoint}?per_page=100", credential.GitHubToken ?? "");
        var listResponse = await _httpFactory.CreateClient().SendAsync(listRequest, ct);
        listResponse.EnsureSuccessStatusCode();

        var json = await listResponse.Content.ReadFromJsonAsync<JsonElement>(ct);
        var runners = new List<GitHubRunnerRegistration>();

        foreach (var runner in json.GetProperty("runners").EnumerateArray())
        {
            var id = runner.GetProperty("id").GetInt64();
            var name = runner.GetProperty("name").GetString() ?? "";
            var status = runner.TryGetProperty("status", out var statusElement)
                ? statusElement.GetString() ?? ""
                : "";
            var busy = runner.TryGetProperty("busy", out var busyElement) && busyElement.ValueKind is JsonValueKind.True;

            runners.Add(new GitHubRunnerRegistration(id, name, status, busy));
        }

        return runners;
    }

    internal static bool IsDynamicRunnerName(string? runnerName) =>
        !string.IsNullOrWhiteSpace(runnerName)
        && runnerName.Contains("-jit-", StringComparison.OrdinalIgnoreCase);

    private async Task DeleteRunnerAsync(ProviderCredential credential, long runnerId, CancellationToken ct)
    {
        var deleteEndpoint = $"{BuildRunnersEndpoint(credential)}/{runnerId}";
        using var deleteRequest = CreateGitHubRequest(HttpMethod.Delete, deleteEndpoint, credential.GitHubToken ?? "");
        var deleteResponse = await _httpFactory.CreateClient().SendAsync(deleteRequest, ct);
        deleteResponse.EnsureSuccessStatusCode();
    }

    private static string BuildRunnersEndpoint(ProviderCredential credential)
    {
        var apiUrl = credential.GitHubApiUrl?.TrimEnd('/') ?? "https://api.github.com";
        var normalizedRepo = NormalizeRepo(credential.GitHubRepo, credential.GitHubOrg);

        if (string.IsNullOrEmpty(normalizedRepo))
            return $"{apiUrl}/orgs/{credential.GitHubOrg}/actions/runners";

        var parts = normalizedRepo.Split('/', 2);
        return $"{apiUrl}/repos/{parts[0]}/{parts[1]}/actions/runners";
    }

    private static HttpRequestMessage CreateGitHubRequest(HttpMethod method, string endpoint, string token)
    {
        var request = new HttpRequestMessage(method, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("RunnerRunner", "1.0"));
        return request;
    }

    private static string? NormalizeRepo(string? repo, string? org)
    {
        if (string.IsNullOrWhiteSpace(repo))
            return null;

        var trimmed = repo.Trim().Trim('/');
        if (trimmed.Contains('/'))
            return trimmed;

        return string.IsNullOrWhiteSpace(org) ? null : $"{org.Trim().Trim('/')}/{trimmed}";
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
