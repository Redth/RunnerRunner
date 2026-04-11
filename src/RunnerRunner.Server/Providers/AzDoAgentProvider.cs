using System.Net.Http.Headers;
using System.Text.Json;
using RunnerRunner.Core.Interfaces;
using RunnerRunner.Core.Models;

namespace RunnerRunner.Server.Providers;

/// <summary>
/// Azure DevOps agent provider. Handles agent registration via the AzDO REST API
/// and version discovery from the AzDO agent feed.
/// </summary>
public class AzDoAgentProvider : IRunnerProviderPlugin
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<AzDoAgentProvider> _logger;

    public RunnerProvider Provider => RunnerProvider.AzureDevOps;

    public AzDoAgentProvider(IHttpClientFactory httpFactory, ILogger<AzDoAgentProvider> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<string> GetRegistrationTokenAsync(ProviderCredential credential, CancellationToken ct = default)
    {
        // AzDO uses PATs directly for agent registration (via config.cmd --token).
        // Unlike GitHub, there's no short-lived registration token endpoint.
        if (string.IsNullOrEmpty(credential.AzDoPat))
            throw new InvalidOperationException("Azure DevOps PAT is not configured");

        _logger.LogInformation("Using AzDO PAT for agent registration at {OrgUrl}", credential.AzDoOrgUrl);
        return credential.AzDoPat;
    }

    public async Task RemoveRunnerAsync(ProviderCredential credential, string runnerName, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(credential.AzDoOrgUrl) || string.IsNullOrEmpty(credential.AzDoPat))
            return;

        var client = _httpFactory.CreateClient();
        var orgUrl = credential.AzDoOrgUrl.TrimEnd('/');
        var poolName = credential.AzDoPoolName ?? "Default";

        // Get pool ID
        using var poolRequest = new HttpRequestMessage(HttpMethod.Get,
            $"{orgUrl}/_apis/distributedtask/pools?poolName={poolName}&api-version=7.1");
        poolRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($":{credential.AzDoPat}")));

        var poolResponse = await client.SendAsync(poolRequest, ct);
        if (!poolResponse.IsSuccessStatusCode) return;

        var poolJson = await poolResponse.Content.ReadFromJsonAsync<JsonElement>(ct);
        var pools = poolJson.GetProperty("value").EnumerateArray().ToList();
        if (pools.Count == 0) return;

        var poolId = pools[0].GetProperty("id").GetInt32();

        // Get agents in pool
        using var agentRequest = new HttpRequestMessage(HttpMethod.Get,
            $"{orgUrl}/_apis/distributedtask/pools/{poolId}/agents?agentName={runnerName}&api-version=7.1");
        agentRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($":{credential.AzDoPat}")));

        var agentResponse = await client.SendAsync(agentRequest, ct);
        if (!agentResponse.IsSuccessStatusCode) return;

        var agentJson = await agentResponse.Content.ReadFromJsonAsync<JsonElement>(ct);
        var agents = agentJson.GetProperty("value").EnumerateArray().ToList();

        foreach (var agent in agents)
        {
            var agentId = agent.GetProperty("id").GetInt32();
            using var deleteRequest = new HttpRequestMessage(HttpMethod.Delete,
                $"{orgUrl}/_apis/distributedtask/pools/{poolId}/agents/{agentId}?api-version=7.1");
            deleteRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($":{credential.AzDoPat}")));

            await client.SendAsync(deleteRequest, ct);
            _logger.LogInformation("AzDO agent {Name} (id: {Id}) removed", runnerName, agentId);
        }
    }

    public async Task<List<RunnerAgentVersion>> GetAvailableVersionsAsync(CancellationToken ct = default)
    {
        var versions = new List<RunnerAgentVersion>();

        try
        {
            var client = _httpFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://api.github.com/repos/microsoft/azure-pipelines-agent/releases?per_page=10");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("RunnerRunner", "1.0"));

            var response = await client.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var releases = await response.Content.ReadFromJsonAsync<JsonElement[]>(ct) ?? [];
            for (var i = 0; i < releases.Length; i++)
            {
                var release = releases[i];
                var tagName = release.GetProperty("tag_name").GetString()?.TrimStart('v') ?? "";
                if (string.IsNullOrEmpty(tagName)) continue;

                var version = new RunnerAgentVersion
                {
                    Provider = RunnerProvider.AzureDevOps,
                    Version = tagName,
                    IsLatest = i == 0
                };

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
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch AzDO agent versions");
        }

        return versions;
    }
}
