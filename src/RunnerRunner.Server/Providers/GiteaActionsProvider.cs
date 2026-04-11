using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using RunnerRunner.Core.Interfaces;
using RunnerRunner.Core.Models;

namespace RunnerRunner.Server.Providers;

/// <summary>
/// Gitea Actions runner provider. Handles runner registration using act_runner
/// and version discovery from Gitea's release feed.
/// </summary>
public class GiteaActionsProvider : IRunnerProviderPlugin
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<GiteaActionsProvider> _logger;

    public RunnerProvider Provider => RunnerProvider.GiteaActions;

    public GiteaActionsProvider(IHttpClientFactory httpFactory, ILogger<GiteaActionsProvider> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<string> GetRegistrationTokenAsync(ProviderCredential credential, CancellationToken ct = default)
    {
        // Gitea uses pre-generated runner tokens, not short-lived registration tokens like GitHub.
        // The token is configured directly in the Gitea admin UI and passed to act_runner.
        // We return it as-is since it's already a registration token.
        if (string.IsNullOrEmpty(credential.GiteaRunnerToken))
            throw new InvalidOperationException("Gitea runner token is not configured");

        _logger.LogInformation("Using configured Gitea runner token for {Url}", credential.GiteaInstanceUrl);
        return credential.GiteaRunnerToken;
    }

    public Task RemoveRunnerAsync(ProviderCredential credential, string runnerName, CancellationToken ct = default)
    {
        // Gitea act_runner handles its own deregistration on graceful shutdown.
        // No API call needed — the runner removes itself when stopped.
        _logger.LogInformation("Gitea runner {RunnerName} will self-deregister on shutdown", runnerName);
        return Task.CompletedTask;
    }

    public async Task<List<RunnerAgentVersion>> GetAvailableVersionsAsync(CancellationToken ct = default)
    {
        var versions = new List<RunnerAgentVersion>();

        try
        {
            var client = _httpFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://gitea.com/gitea/act_runner/releases?type=json&limit=10");
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("RunnerRunner", "1.0"));

            var response = await client.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                // Fall back to GitHub mirror
                using var ghRequest = new HttpRequestMessage(HttpMethod.Get,
                    "https://api.github.com/repos/nektos/act/releases?per_page=10");
                ghRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                ghRequest.Headers.UserAgent.Add(new ProductInfoHeaderValue("RunnerRunner", "1.0"));
                response = await client.SendAsync(ghRequest, ct);
            }

            if (response.IsSuccessStatusCode)
            {
                var releases = await response.Content.ReadFromJsonAsync<JsonElement[]>(ct) ?? [];
                for (var i = 0; i < releases.Length; i++)
                {
                    var release = releases[i];
                    var tagName = release.GetProperty("tag_name").GetString()?.TrimStart('v') ?? "";
                    if (string.IsNullOrEmpty(tagName)) continue;

                    versions.Add(new RunnerAgentVersion
                    {
                        Provider = RunnerProvider.GiteaActions,
                        Version = tagName,
                        IsLatest = i == 0
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch Gitea runner versions");
        }

        return versions;
    }
}
