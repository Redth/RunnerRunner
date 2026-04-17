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
			using var client = _httpClientFactory.CreateClient();
			client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential.GitHubToken);
			client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RunnerRunner", "1.0"));
			client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

			// Build list of endpoints to try (repo-level first, then org-level)
			var endpoints = new List<(string url, string scope)>();
			var triedScopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var errors = new List<string>();

			void AddEndpoint(string url, string scope)
			{
				if (triedScopes.Add(scope))
					endpoints.Add((url, scope));
			}

			// 1. Use the actual repo from the webhook event (most specific, most likely to work)
			var normalizedWebhookRepo = NormalizeRepo(webhookRepo, credential.GitHubOrg);
			if (!string.IsNullOrEmpty(normalizedWebhookRepo))
			{
				var parts = normalizedWebhookRepo.Split('/', 2);
				if (parts.Length == 2)
				{
					AddEndpoint($"{apiUrl}/repos/{parts[0]}/{parts[1]}/actions/runners/generate-jitconfig", $"repo:{normalizedWebhookRepo}");
					AddEndpoint($"{apiUrl}/orgs/{parts[0]}/actions/runners/generate-jitconfig", $"org:{parts[0]}");
				}
			}

			// 2. Try credential's configured repo
			var normalizedCredentialRepo = NormalizeRepo(credential.GitHubRepo, credential.GitHubOrg);
			if (!string.IsNullOrEmpty(normalizedCredentialRepo)
				&& !string.Equals(normalizedCredentialRepo, normalizedWebhookRepo, StringComparison.OrdinalIgnoreCase))
			{
				var parts = normalizedCredentialRepo.Split('/', 2);
				if (parts.Length == 2)
					AddEndpoint($"{apiUrl}/repos/{parts[0]}/{parts[1]}/actions/runners/generate-jitconfig", $"repo:{normalizedCredentialRepo}");
			}

			// 3. Try org-level
			if (!string.IsNullOrEmpty(credential.GitHubOrg))
			{
				AddEndpoint($"{apiUrl}/orgs/{credential.GitHubOrg}/actions/runners/generate-jitconfig", $"org:{credential.GitHubOrg}");
			}

			if (endpoints.Count == 0)
				return new JitConfigResult { Success = false, Error = "No GitHub org, repo, or webhook repo available to generate JIT config." };

			var resolvedRunnerGroupId = await ResolveRunnerGroupIdAsync(client, apiUrl, credential, runnerGroup);
			var effectiveRunnerGroupId = resolvedRunnerGroupId ?? 1;
			var requestBody = new
			{
				Name = runnerName,
				Labels = labels.ToArray(),
				RunnerGroupId = effectiveRunnerGroupId,
				WorkFolder = "_work"
			};

			var json = JsonSerializer.Serialize(requestBody, JsonOptions);
			_logger.LogInformation(
				"Generating GitHub JIT config for runner '{RunnerName}' with group id {RunnerGroupId} and labels [{Labels}]",
				runnerName, effectiveRunnerGroupId, string.Join(", ", labels));

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
					errors.Add($"{scope} -> {(int)response.StatusCode}: {SummarizeError(responseBody)}");
					continue; // Try next endpoint
				}

				using var doc = JsonDocument.Parse(responseBody);
				var encodedJitConfig = doc.RootElement.GetProperty("encoded_jit_config").GetString();

				_logger.LogInformation("Successfully generated GitHub JIT config for runner '{RunnerName}' (scope: {Scope})",
					runnerName, scope);

				return new JitConfigResult { Success = true, JitConfig = encodedJitConfig };
			}

			// All endpoints failed
			return new JitConfigResult
			{
				Success = false,
				Error = $"GitHub JIT config failed for all endpoints. {string.Join(" | ", errors)}"
			};
		}
		catch (HttpRequestException ex)
		{
			_logger.LogError(ex, "HTTP error generating GitHub JIT config for runner '{RunnerName}'", runnerName);
			return new JitConfigResult { Success = false, Error = $"HTTP error: {ex.Message}" };
		}
	}

	private async Task<long?> ResolveRunnerGroupIdAsync(HttpClient client, string apiUrl, ProviderCredential credential, string? runnerGroup)
	{
		if (string.IsNullOrWhiteSpace(runnerGroup) || string.Equals(runnerGroup, "Default", StringComparison.OrdinalIgnoreCase))
			return null;

		if (string.IsNullOrWhiteSpace(credential.GitHubOrg))
		{
			_logger.LogWarning("Runner group '{RunnerGroup}' requested, but no GitHub org is configured; using GitHub default group", runnerGroup);
			return null;
		}

		var endpoint = $"{apiUrl}/orgs/{credential.GitHubOrg}/actions/runner-groups";
		var response = await client.GetAsync(endpoint);
		var body = await response.Content.ReadAsStringAsync();

		if (!response.IsSuccessStatusCode)
		{
			_logger.LogWarning("Unable to resolve GitHub runner group '{RunnerGroup}' from {Endpoint}: {StatusCode} {Body}",
				runnerGroup, endpoint, response.StatusCode, body);
			return null;
		}

		using var doc = JsonDocument.Parse(body);
		if (!doc.RootElement.TryGetProperty("runner_groups", out var groups))
			return null;

		foreach (var group in groups.EnumerateArray())
		{
			var name = group.GetProperty("name").GetString();
			if (!string.Equals(name, runnerGroup, StringComparison.OrdinalIgnoreCase))
				continue;

			return group.GetProperty("id").GetInt64();
		}

		_logger.LogWarning("GitHub runner group '{RunnerGroup}' was not found in org '{Org}'; using GitHub default group",
			runnerGroup, credential.GitHubOrg);
		return null;
	}

	private static string SummarizeError(string? responseBody)
	{
		if (string.IsNullOrWhiteSpace(responseBody))
			return "No response body";

		try
		{
			using var doc = JsonDocument.Parse(responseBody);
			var root = doc.RootElement;
			var message = root.TryGetProperty("message", out var messageEl) ? messageEl.GetString() : null;
			return string.IsNullOrWhiteSpace(message) ? responseBody : message.Replace('\n', ' ').Trim();
		}
		catch (JsonException)
		{
			return responseBody.Length > 300 ? responseBody[..300] : responseBody;
		}
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
