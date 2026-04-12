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
		string runnerGroup = "Default")
	{
		try
		{
			var apiUrl = credential.GitHubApiUrl?.TrimEnd('/') ?? "https://api.github.com";

			string endpoint;
			if (!string.IsNullOrEmpty(credential.GitHubOrg))
			{
				endpoint = $"{apiUrl}/orgs/{credential.GitHubOrg}/actions/runners/generate-jitconfig";
			}
			else if (!string.IsNullOrEmpty(credential.GitHubRepo))
			{
				var parts = credential.GitHubRepo.Split('/', 2);
				if (parts.Length != 2)
					return new JitConfigResult { Success = false, Error = $"Invalid repo format: '{credential.GitHubRepo}'. Expected 'owner/repo'." };

				endpoint = $"{apiUrl}/repos/{parts[0]}/{parts[1]}/actions/runners/generate-jitconfig";
			}
			else
			{
				return new JitConfigResult { Success = false, Error = "Neither GitHubOrg nor GitHubRepo is configured on the credential." };
			}

			var requestBody = new
			{
				name = runnerName,
				labels,
				runner_group_id = 1,
				work_folder = "_work"
			};

			using var client = _httpClientFactory.CreateClient();
			client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential.GitHubToken);
			client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RunnerRunner", "1.0"));

			var json = JsonSerializer.Serialize(requestBody);
			using var content = new StringContent(json, Encoding.UTF8, "application/json");

			_logger.LogInformation("Requesting GitHub JIT config for runner '{RunnerName}' at {Endpoint}", runnerName, endpoint);

			var response = await client.PostAsync(endpoint, content);
			var responseBody = await response.Content.ReadAsStringAsync();

			if (!response.IsSuccessStatusCode)
			{
				_logger.LogError("GitHub JIT config request failed with {StatusCode}: {Body}", response.StatusCode, responseBody);
				return new JitConfigResult { Success = false, Error = $"GitHub API returned {(int)response.StatusCode}: {responseBody}" };
			}

			using var doc = JsonDocument.Parse(responseBody);
			var encodedJitConfig = doc.RootElement.GetProperty("encoded_jit_config").GetString();

			_logger.LogInformation("Successfully generated GitHub JIT config for runner '{RunnerName}'", runnerName);

			return new JitConfigResult { Success = true, JitConfig = encodedJitConfig };
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
