using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RunnerRunner.Core.Models;

namespace RunnerRunner.Server.Services;

public class GitHubAuthenticationService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GitHubAuthenticationService> _logger;

    public GitHubAuthenticationService(
        IHttpClientFactory httpClientFactory,
        ILogger<GitHubAuthenticationService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public static bool HasGitHubApiCredentials(
        ProviderCredential credential,
        string? installationId = null,
        string? repository = null)
    {
        if (credential.GitHubAuthType == GitHubAuthType.GitHubApp)
        {
            return !string.IsNullOrWhiteSpace(credential.GitHubAppId)
                && !string.IsNullOrWhiteSpace(credential.GitHubAppPrivateKey)
                && !string.IsNullOrWhiteSpace(ResolveGitHubAppInstallationId(credential, installationId, repository));
        }

        return !string.IsNullOrWhiteSpace(credential.GitHubToken);
    }

    public static bool IsGitHubAppCredential(ProviderCredential? credential) =>
        credential?.GitHubAuthType == GitHubAuthType.GitHubApp;

    public async Task<HttpRequestMessage> CreateRequestAsync(
        HttpMethod method,
        string endpoint,
        ProviderCredential credential,
        string? installationId = null,
        string? repository = null,
        CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(method, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            await GetAccessTokenAsync(credential, installationId, repository, ct));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("RunnerRunner", "1.0"));
        return request;
    }

    public async Task ConfigureClientAsync(
        HttpClient client,
        ProviderCredential credential,
        string? installationId = null,
        string? repository = null,
        CancellationToken ct = default)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            await GetAccessTokenAsync(credential, installationId, repository, ct));
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RunnerRunner", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public async Task<string> GetAccessTokenAsync(
        ProviderCredential credential,
        string? installationId = null,
        string? repository = null,
        CancellationToken ct = default)
    {
        if (credential.GitHubAuthType != GitHubAuthType.GitHubApp)
        {
            return credential.GitHubToken
                ?? throw new InvalidOperationException($"GitHub credential '{credential.Name}' does not have a PAT configured.");
        }

        var resolvedInstallationId = ResolveGitHubAppInstallationId(credential, installationId, repository)
            ?? throw new InvalidOperationException($"GitHub App credential '{credential.Name}' does not have an installation ID.");

        var appJwt = CreateAppJwt(credential);
        var apiUrl = credential.GitHubApiUrl?.TrimEnd('/') ?? "https://api.github.com";
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{apiUrl}/app/installations/{resolvedInstallationId}/access_tokens");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", appJwt);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("RunnerRunner", "1.0"));

        var response = await _httpClientFactory.CreateClient().SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(body);
        var token = json.RootElement.GetProperty("token").GetString();
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("GitHub App installation token response did not include a token.");

        _logger.LogDebug(
            "Generated GitHub App installation token for credential {CredentialName} installation {InstallationId}",
            credential.Name,
            resolvedInstallationId);

        return token;
    }

    private static string CreateAppJwt(ProviderCredential credential)
    {
        if (string.IsNullOrWhiteSpace(credential.GitHubAppId))
            throw new InvalidOperationException($"GitHub App credential '{credential.Name}' does not have an app ID.");

        if (string.IsNullOrWhiteSpace(credential.GitHubAppPrivateKey))
            throw new InvalidOperationException($"GitHub App credential '{credential.Name}' does not have a private key.");

        var now = DateTimeOffset.UtcNow;
        var header = Base64UrlEncode(Encoding.UTF8.GetBytes("""{"alg":"RS256","typ":"JWT"}"""));
        var payloadJson = JsonSerializer.Serialize(new
        {
            iat = now.AddSeconds(-60).ToUnixTimeSeconds(),
            exp = now.AddMinutes(9).ToUnixTimeSeconds(),
            iss = credential.GitHubAppId.Trim()
        });
        var payload = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));
        var signingInput = $"{header}.{payload}";

        using var rsa = RSA.Create();
        rsa.ImportFromPem(NormalizePem(credential.GitHubAppPrivateKey).AsSpan());
        var signature = rsa.SignData(
            Encoding.ASCII.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        return $"{signingInput}.{Base64UrlEncode(signature)}";
    }

    public static string? ResolveGitHubAppInstallationId(
        ProviderCredential credential,
        string? installationId = null,
        string? repository = null) =>
        GitHubCredentialResolver.ResolveInstallationId(credential, installationId, repository);

    private static string NormalizePem(string pem) =>
        pem.Replace("\\n", "\n", StringComparison.Ordinal).Trim();

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
