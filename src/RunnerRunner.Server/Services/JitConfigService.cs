using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RunnerRunner.Core.Models;

namespace RunnerRunner.Server.Services;

public class JitConfigResult
{
	public bool Success { get; set; }
	public string? JitConfig { get; set; }
	public string? RegistrationToken { get; set; }
	public string? Error { get; set; }
}

public class JitConfigService
{
	private readonly ILogger<JitConfigService> _logger;
	private readonly IHttpClientFactory _httpClientFactory;

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
	};

	public JitConfigService(ILogger<JitConfigService> logger, IHttpClientFactory httpClientFactory)
	{
		_logger = logger;
		_httpClientFactory = httpClientFactory;
	}

	public async Task<JitConfigResult> GenerateGitHubJitConfig(
		ProviderCredential credential,
		string runnerName,
		List<string> labels,
		string runnerGroup = "Default",
		string? webhookRepo = null)
	{
		try
		{
			var apiUrl = credential.GitHubApiUrl?.TrimEnd('/') ?? "https://api.github.com";

			// Build list of endpoints to try (repo-level first, then org-level)
			var endpoints = new List<(string url, string scope)>();

			// 1. Use the actual repo from the webhook event (most specific, most likely to work)
			if (!string.IsNullOrEmpty(webhookRepo))
			{
				var parts = webhookRepo.Split('/', 2);
				if (parts.Length == 2)
					endpoints.Add(($"{apiUrl}/repos/{parts[0]}/{parts[1]}/actions/runners/generate-jitconfig", $"repo:{webhookRepo}"));
			}

			// 2. Try credential's configured repo
			if (!string.IsNullOrEmpty(credential.GitHubRepo) && credential.GitHubRepo != webhookRepo)
			{
				var parts = credential.GitHubRepo.Split('/', 2);
				if (parts.Length == 2)
					endpoints.Add(($"{apiUrl}/repos/{parts[0]}/{parts[1]}/actions/runners/generate-jitconfig", $"repo:{credential.GitHubRepo}"));
			}

			// 3. Try org-level
			if (!string.IsNullOrEmpty(credential.GitHubOrg))
			{
				endpoints.Add(($"{apiUrl}/orgs/{credential.GitHubOrg}/actions/runners/generate-jitconfig", $"org:{credential.GitHubOrg}"));
			}

			if (endpoints.Count == 0)
				return new JitConfigResult { Success = false, Error = "No GitHub org, repo, or webhook repo available to generate JIT config." };

			var requestBody = new
			{
				name = runnerName,
				labels = labels.Select(l => l).ToArray(),
				runner_group_id = 1,
				work_folder = "_work"
			};

			using var client = _httpClientFactory.CreateClient();
			client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential.GitHubToken);
			client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RunnerRunner", "1.0"));
			client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

			var json = JsonSerializer.Serialize(requestBody);

			// Try each endpoint — repo-level first, fall back to org-level
			foreach (var (endpoint, scope) in endpoints)
			{
				_logger.LogInformation("Requesting GitHub JIT config for runner '{RunnerName}' at {Endpoint} (scope: {Scope})",
					runnerName, endpoint, scope);

				using var content = new StringContent(json, Encoding.UTF8, "application/json");
				var response = await client.PostAsync(endpoint, content);
				var responseBody = await response.Content.ReadAsStringAsync();

				if (!response.IsSuccessStatusCode)
				{
					_logger.LogWarning("GitHub JIT config request to {Scope} failed with {StatusCode}: {Body}",
						scope, response.StatusCode, responseBody);
					continue; // Try next endpoint
				}

				using var doc = JsonDocument.Parse(responseBody);
				var encodedJitConfig = doc.RootElement.GetProperty("encoded_jit_config").GetString();

				_logger.LogInformation("Successfully generated GitHub JIT config for runner '{RunnerName}' (scope: {Scope})",
					runnerName, scope);

				return new JitConfigResult { Success = true, JitConfig = encodedJitConfig };
			}

			// All endpoints failed
			return new JitConfigResult { Success = false, Error = "GitHub JIT config failed for all endpoints (repo + org). Check token permissions: needs admin:self_hosted_runner scope." };
		}
		catch (HttpRequestException ex)
		{
			_logger.LogError(ex, "HTTP error generating GitHub JIT config for runner '{RunnerName}'", runnerName);
			return new JitConfigResult { Success = false, Error = $"HTTP error: {ex.Message}" };
		}
	}

	public async Task<JitConfigResult> GenerateGiteaJitConfig(
		ProviderCredential credential,
		string runnerName)
	{
		try
		{
			var instanceUrl = credential.GiteaInstanceUrl?.TrimEnd('/')
				?? throw new InvalidOperationException("GiteaInstanceUrl is not configured on the credential.");

			string endpoint;
			if (!string.IsNullOrEmpty(credential.GitHubOrg))
			{
				endpoint = $"{instanceUrl}/api/v1/orgs/{credential.GitHubOrg}/actions/runners/registration-token";
			}
			else if (!string.IsNullOrEmpty(credential.GitHubRepo))
			{
				var parts = credential.GitHubRepo.Split('/', 2);
				if (parts.Length != 2)
					return new JitConfigResult { Success = false, Error = $"Invalid repo format: '{credential.GitHubRepo}'. Expected 'owner/repo'." };

				endpoint = $"{instanceUrl}/api/v1/repos/{parts[0]}/{parts[1]}/actions/runners/registration-token";
			}
			else
			{
				return new JitConfigResult { Success = false, Error = "Neither org nor repo is configured on the credential." };
			}

			using var client = _httpClientFactory.CreateClient();
			client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", credential.GiteaRunnerToken);

			_logger.LogInformation("Requesting Gitea registration token for runner '{RunnerName}' at {Endpoint}", runnerName, endpoint);

			var response = await client.GetAsync(endpoint);
			var responseBody = await response.Content.ReadAsStringAsync();

			if (!response.IsSuccessStatusCode)
			{
				_logger.LogError("Gitea registration token request failed with {StatusCode}: {Body}", response.StatusCode, responseBody);
				return new JitConfigResult { Success = false, Error = $"Gitea API returned {(int)response.StatusCode}: {responseBody}" };
			}

			using var doc = JsonDocument.Parse(responseBody);
			var token = doc.RootElement.GetProperty("token").GetString();

			_logger.LogInformation("Successfully obtained Gitea registration token for runner '{RunnerName}'", runnerName);

			return new JitConfigResult { Success = true, RegistrationToken = token };
		}
		catch (HttpRequestException ex)
		{
			_logger.LogError(ex, "HTTP error generating Gitea registration token for runner '{RunnerName}'", runnerName);
			return new JitConfigResult { Success = false, Error = $"HTTP error: {ex.Message}" };
		}
	}
}
