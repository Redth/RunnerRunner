using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using RunnerRunner.Core.Models;

namespace RunnerRunner.Server.Services;

public interface IRegistryCatalogService
{
    Task<RegistrySearchResponse> SearchRepositoriesAsync(RegistryCredential registry, string query, CancellationToken ct = default);
    Task<RegistryTagResponse> GetTagsAsync(RegistryCredential registry, string repository, CancellationToken ct = default);
}

public sealed partial class RegistryCatalogService : IRegistryCatalogService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<RegistryCatalogService> _logger;

    public RegistryCatalogService(
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        ILogger<RegistryCatalogService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger;
    }

    public async Task<RegistrySearchResponse> SearchRepositoriesAsync(RegistryCredential registry, string query, CancellationToken ct = default)
    {
        query = (query ?? "").Trim();
        if (string.IsNullOrWhiteSpace(query) && !(IsGhcrRegistry(registry.RegistryUrl) && !string.IsNullOrWhiteSpace(GetGhcrDefaultOwner(registry))))
        {
            return new RegistrySearchResponse
            {
                Warning = "Enter a repository name or partial path to search."
            };
        }

        var cacheKey = $"registry-search:{registry.Id}:{query.ToLowerInvariant()}";
        if (_cache.TryGetValue(cacheKey, out RegistrySearchResponse? cached) && cached != null)
            return cached.Clone(fromCache: true);

        RegistrySearchResponse response;
        try
        {
            response = IsGhcrRegistry(registry.RegistryUrl)
                ? await SearchGhcrAsync(registry, query, ct)
                : IsDockerHubRegistry(registry.RegistryUrl)
                ? await SearchDockerHubAsync(registry, query, ct)
                : await SearchGenericCatalogAsync(registry, query, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Registry search failed for {Registry}", registry.RegistryUrl);
            response = new RegistrySearchResponse
            {
                Warning = $"Search failed: {ex.Message}"
            };
        }

        _cache.Set(cacheKey, response.Clone(fromCache: false), TimeSpan.FromMinutes(5));
        return response;
    }

    public async Task<RegistryTagResponse> GetTagsAsync(RegistryCredential registry, string repository, CancellationToken ct = default)
    {
        repository = (repository ?? "").Trim().Trim('/');
        if (string.IsNullOrWhiteSpace(repository))
        {
            return new RegistryTagResponse
            {
                Warning = "Choose a repository first."
            };
        }

        var cacheKey = $"registry-tags:{registry.Id}:{repository.ToLowerInvariant()}";
        if (_cache.TryGetValue(cacheKey, out RegistryTagResponse? cached) && cached != null)
            return cached.Clone(fromCache: true);

        RegistryTagResponse response;
        try
        {
            response = IsGhcrRegistry(registry.RegistryUrl)
                ? await GetGhcrTagsAsync(registry, repository, ct)
                : IsDockerHubRegistry(registry.RegistryUrl)
                ? await GetDockerHubTagsAsync(registry, repository, ct)
                : await GetGenericTagsAsync(registry, repository, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tag lookup failed for {Registry}/{Repository}", registry.RegistryUrl, repository);
            response = new RegistryTagResponse
            {
                Repository = repository,
                Warning = $"Tag lookup failed: {ex.Message}"
            };
        }

        _cache.Set(cacheKey, response.Clone(fromCache: false), TimeSpan.FromMinutes(5));
        return response;
    }

    private async Task<RegistrySearchResponse> SearchDockerHubAsync(RegistryCredential registry, string query, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient();
        var url = $"https://hub.docker.com/v2/search/repositories/?page_size=100&query={Uri.EscapeDataString(query)}";

        using var response = await client.GetAsync(url, ct);
        var payload = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            return new RegistrySearchResponse
            {
                Warning = $"Docker Hub search failed: {(int)response.StatusCode} {response.ReasonPhrase}"
            };
        }

        using var doc = JsonDocument.Parse(payload);
        var results = new List<RegistryRepositoryResult>();
        if (doc.RootElement.TryGetProperty("results", out var items))
        {
            foreach (var item in items.EnumerateArray())
            {
                var repoName =
                    TryGetString(item, "repo_name") ??
                    TryGetString(item, "name") ??
                    "";

                if (string.IsNullOrWhiteSpace(repoName))
                    continue;

                var description =
                    TryGetString(item, "short_description") ??
                    TryGetString(item, "description");

                int? stars = null;
                if (item.TryGetProperty("star_count", out var starProp) && starProp.TryGetInt32(out var starCount))
                    stars = starCount;

                results.Add(new RegistryRepositoryResult
                {
                    Name = repoName,
                    Description = description,
                    StarCount = stars
                });
            }
        }

        if (results.Count == 0 && LooksLikeRepositoryPath(query))
        {
            results.Add(new RegistryRepositoryResult
            {
                Name = NormalizeRepositoryQuery(query),
                Description = "Manual repository path from query text"
            });
        }

        return new RegistrySearchResponse
        {
            Results = results,
            InfoMessage = results.Count == 0 ? "No repositories matched that search." : null
        };
    }

    private async Task<RegistrySearchResponse> SearchGenericCatalogAsync(RegistryCredential registry, string query, CancellationToken ct)
    {
        var catalogUri = BuildRegistryUri(registry.RegistryUrl, "/v2/_catalog?n=250");
        var (doc, error) = await TryGetJsonAsync(registry, catalogUri, "registry:catalog:*", ct);

        var results = new List<RegistryRepositoryResult>();
        if (doc != null &&
            doc.RootElement.TryGetProperty("repositories", out var repositories) &&
            repositories.ValueKind == JsonValueKind.Array)
        {
            results = repositories
                .EnumerateArray()
                .Select(x => x.GetString() ?? "")
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Where(x => x.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Take(100)
                .Select(x => new RegistryRepositoryResult { Name = x })
                .ToList();
        }

        if (results.Count == 0 && LooksLikeRepositoryPath(query))
        {
            results.Add(new RegistryRepositoryResult
            {
                Name = NormalizeRepositoryQuery(query),
                Description = "Manual repository path (catalog search unavailable or returned no matches)"
            });
        }

        return new RegistrySearchResponse
        {
            Results = results,
            Warning = results.Count == 0 ? error : null,
            InfoMessage = results.Count == 0 && string.IsNullOrWhiteSpace(error)
                ? "No repositories matched that search."
                : null
        };
    }

    private async Task<RegistrySearchResponse> SearchGhcrAsync(RegistryCredential registry, string query, CancellationToken ct)
    {
        var (owner, filter, explicitOwner) = ParseGhcrSearch(registry, query);
        if (string.IsNullOrWhiteSpace(owner))
        {
            return new RegistrySearchResponse
            {
                Warning = "GHCR search is owner-scoped. Search like 'owner/image' or set a Default Namespace / Owner on this registry."
            };
        }

        var packagesResult = await ListGhcrPackagesAsync(registry, owner, ct);
        if (!packagesResult.Packages.Any())
        {
            return new RegistrySearchResponse
            {
                Warning = packagesResult.Warning,
                InfoMessage = string.IsNullOrWhiteSpace(packagesResult.Warning)
                    ? $"No container images were found under '{owner}'."
                    : null
            };
        }

        var results = packagesResult.Packages
            .Where(p =>
                string.IsNullOrWhiteSpace(filter) ||
                p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                $"{owner}/{p.Name}".Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Select(p => new RegistryRepositoryResult
            {
                Name = $"{owner}/{p.Name}",
                Description = string.Join(" · ", new[]
                {
                    string.IsNullOrWhiteSpace(p.Description) ? null : p.Description,
                    p.Visibility,
                    p.VersionCount is > 0 ? $"{p.VersionCount} version(s)" : null
                }.Where(x => !string.IsNullOrWhiteSpace(x))),
                StarCount = null
            })
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new RegistrySearchResponse
        {
            Results = results,
            Warning = results.Count == 0 ? packagesResult.Warning : null,
            InfoMessage = results.Count == 0
                ? explicitOwner
                    ? $"No GHCR images matched '{filter}' under '{owner}'."
                    : $"No GHCR images matched '{filter}'. Try 'owner/image' or set a default owner."
                : $"Showing GitHub container packages for '{owner}'."
        };
    }

    private async Task<RegistryTagResponse> GetGhcrTagsAsync(RegistryCredential registry, string repository, CancellationToken ct)
    {
        var (owner, packageName) = ParseGhcrRepository(registry, repository);
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(packageName))
        {
            return new RegistryTagResponse
            {
                Repository = repository,
                Warning = "GHCR tag lookup needs a full 'owner/image' path or a configured default namespace."
            };
        }

        var versionsResult = await GetGhcrPackageVersionsAsync(registry, owner, packageName, ct);
        var tagsByName = new Dictionary<string, RegistryTagResult>(StringComparer.OrdinalIgnoreCase);

        foreach (var version in versionsResult.Versions)
        {
            foreach (var tag in version.Tags.Where(t => !string.IsNullOrWhiteSpace(t)))
            {
                if (!tagsByName.TryGetValue(tag, out var existing) || (version.UpdatedAt ?? DateTimeOffset.MinValue) > (existing.UpdatedAt ?? DateTimeOffset.MinValue))
                {
                    tagsByName[tag] = new RegistryTagResult
                    {
                        Name = tag,
                        UpdatedAt = version.UpdatedAt
                    };
                }
            }
        }

        if (tagsByName.Count == 0)
        {
            var fallback = await GetGenericTagsAsync(registry, $"{owner}/{packageName}", ct);
            fallback.Repository = $"{owner}/{packageName}";
            if (fallback.Tags.Any())
                return fallback;

            return new RegistryTagResponse
            {
                Repository = $"{owner}/{packageName}",
                Warning = versionsResult.Warning ?? fallback.Warning,
                InfoMessage = "No GHCR tags were found for that package."
            };
        }

        return new RegistryTagResponse
        {
            Repository = $"{owner}/{packageName}",
            Tags = SortTags(tagsByName.Values.ToList()),
            Warning = versionsResult.Warning
        };
    }

    private async Task<RegistryTagResponse> GetDockerHubTagsAsync(RegistryCredential registry, string repository, CancellationToken ct)
    {
        var normalizedRepo = NormalizeDockerHubRepository(repository);
        var encodedRepo = EncodeRepositoryPath(normalizedRepo);
        var client = _httpClientFactory.CreateClient();
        var url = $"https://hub.docker.com/v2/repositories/{encodedRepo}/tags?page_size=100";

        using var response = await client.GetAsync(url, ct);
        var payload = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            return new RegistryTagResponse
            {
                Repository = normalizedRepo,
                Warning = $"Docker Hub tag lookup failed: {(int)response.StatusCode} {response.ReasonPhrase}"
            };
        }

        using var doc = JsonDocument.Parse(payload);
        var tags = new List<RegistryTagResult>();

        if (doc.RootElement.TryGetProperty("results", out var items))
        {
            foreach (var item in items.EnumerateArray())
            {
                var tagName = TryGetString(item, "name") ?? "";
                if (string.IsNullOrWhiteSpace(tagName))
                    continue;

                long? size = null;
                if (item.TryGetProperty("full_size", out var sizeProp) && sizeProp.TryGetInt64(out var fullSize))
                    size = fullSize;

                DateTimeOffset? updatedAt = null;
                if (item.TryGetProperty("tag_last_pushed", out var pushedProp) &&
                    pushedProp.ValueKind == JsonValueKind.String &&
                    DateTimeOffset.TryParse(pushedProp.GetString(), out var pushed))
                {
                    updatedAt = pushed;
                }

                tags.Add(new RegistryTagResult
                {
                    Name = tagName,
                    SizeBytes = size,
                    UpdatedAt = updatedAt
                });
            }
        }

        return new RegistryTagResponse
        {
            Repository = normalizedRepo,
            Tags = SortTags(tags)
        };
    }

    private async Task<RegistryTagResponse> GetGenericTagsAsync(RegistryCredential registry, string repository, CancellationToken ct)
    {
        var repo = NormalizeRepositoryQuery(repository);
        var tagsUri = BuildRegistryUri(registry.RegistryUrl, $"/v2/{EncodeRepositoryPath(repo)}/tags/list");
        var (doc, error) = await TryGetJsonAsync(registry, tagsUri, $"repository:{repo}:pull", ct);

        var tags = new List<RegistryTagResult>();
        if (doc != null &&
            doc.RootElement.TryGetProperty("tags", out var items) &&
            items.ValueKind == JsonValueKind.Array)
        {
            tags = items.EnumerateArray()
                .Select(x => x.GetString() ?? "")
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => new RegistryTagResult { Name = x })
                .ToList();
        }

        return new RegistryTagResponse
        {
            Repository = repo,
            Tags = SortTags(tags),
            Warning = tags.Count == 0 ? error : null,
            InfoMessage = tags.Count == 0 && string.IsNullOrWhiteSpace(error)
                ? "No tags were reported for that repository."
                : null
        };
    }

    private async Task<(JsonDocument? Document, string? Error)> TryGetJsonAsync(
        RegistryCredential registry,
        Uri uri,
        string? scope,
        CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient();

        using var initialRequest = CreateRequest(uri, registry, bearerToken: null);
        using var initialResponse = await client.SendAsync(initialRequest, ct);

        if (initialResponse.IsSuccessStatusCode)
        {
            var content = await initialResponse.Content.ReadAsStringAsync(ct);
            return (JsonDocument.Parse(content), null);
        }

        if (initialResponse.StatusCode == HttpStatusCode.Unauthorized)
        {
            var token = await TryGetBearerTokenAsync(client, initialResponse, registry, scope, ct);
            if (!string.IsNullOrWhiteSpace(token))
            {
                using var retryRequest = CreateRequest(uri, registry, token);
                using var retryResponse = await client.SendAsync(retryRequest, ct);

                if (retryResponse.IsSuccessStatusCode)
                {
                    var content = await retryResponse.Content.ReadAsStringAsync(ct);
                    return (JsonDocument.Parse(content), null);
                }

                var retryError = await retryResponse.Content.ReadAsStringAsync(ct);
                return (null, BuildErrorMessage(retryResponse, retryError));
            }
        }

        var error = await initialResponse.Content.ReadAsStringAsync(ct);
        return (null, BuildErrorMessage(initialResponse, error));
    }

    private static HttpRequestMessage CreateRequest(Uri uri, RegistryCredential registry, string? bearerToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }
        else if (!string.IsNullOrWhiteSpace(registry.Username) && !string.IsNullOrWhiteSpace(registry.Password))
        {
            var basicValue = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{registry.Username}:{registry.Password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicValue);
        }

        return request;
    }

    private async Task<string?> TryGetBearerTokenAsync(
        HttpClient client,
        HttpResponseMessage challengeResponse,
        RegistryCredential registry,
        string? scope,
        CancellationToken ct)
    {
        var challenge = challengeResponse.Headers.WwwAuthenticate.FirstOrDefault();
        var parameterText = challenge?.Parameter;
        if (!string.Equals(challenge?.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(parameterText))
        {
            return null;
        }

        var parts = AuthRegex()
            .Matches(parameterText)
            .ToDictionary(
                m => m.Groups[1].Value,
                m => m.Groups[2].Value,
                StringComparer.OrdinalIgnoreCase);

        if (!parts.TryGetValue("realm", out var realm) || string.IsNullOrWhiteSpace(realm))
            return null;

        var queryParts = new List<string>();
        if (parts.TryGetValue("service", out var service) && !string.IsNullOrWhiteSpace(service))
            queryParts.Add($"service={Uri.EscapeDataString(service)}");
        if (!string.IsNullOrWhiteSpace(scope))
            queryParts.Add($"scope={Uri.EscapeDataString(scope)}");

        var tokenUri = queryParts.Count == 0
            ? realm
            : $"{realm}?{string.Join("&", queryParts)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, tokenUri);
        if (!string.IsNullOrWhiteSpace(registry.Username) && !string.IsNullOrWhiteSpace(registry.Password))
        {
            var basicValue = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{registry.Username}:{registry.Password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicValue);
        }

        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return null;

        var payload = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(payload);
        return TryGetString(doc.RootElement, "token") ?? TryGetString(doc.RootElement, "access_token");
    }

    private async Task<GhcrPackagesResult> ListGhcrPackagesAsync(RegistryCredential registry, string owner, CancellationToken ct)
    {
        var orgUri = BuildGitHubApiUri($"/orgs/{Uri.EscapeDataString(owner)}/packages?package_type=container&per_page=100");
        var (orgDoc, orgError) = await TryGetGitHubJsonAsync(registry, orgUri, ct);
        if (orgDoc != null)
            return new GhcrPackagesResult(ParseGhcrPackages(orgDoc), orgError);

        var userUri = BuildGitHubApiUri($"/users/{Uri.EscapeDataString(owner)}/packages?package_type=container&per_page=100");
        var (userDoc, userError) = await TryGetGitHubJsonAsync(registry, userUri, ct);
        if (userDoc != null)
            return new GhcrPackagesResult(ParseGhcrPackages(userDoc), userError);

        return new GhcrPackagesResult([], userError ?? orgError ?? $"Could not load GHCR packages for '{owner}'.");
    }

    private async Task<GhcrPackageVersionsResult> GetGhcrPackageVersionsAsync(RegistryCredential registry, string owner, string packageName, CancellationToken ct)
    {
        var encodedPackageName = Uri.EscapeDataString(packageName);

        var orgUri = BuildGitHubApiUri($"/orgs/{Uri.EscapeDataString(owner)}/packages/container/{encodedPackageName}/versions?per_page=100");
        var (orgDoc, orgError) = await TryGetGitHubJsonAsync(registry, orgUri, ct);
        if (orgDoc != null)
            return new GhcrPackageVersionsResult(ParseGhcrVersions(orgDoc), orgError);

        var userUri = BuildGitHubApiUri($"/users/{Uri.EscapeDataString(owner)}/packages/container/{encodedPackageName}/versions?per_page=100");
        var (userDoc, userError) = await TryGetGitHubJsonAsync(registry, userUri, ct);
        if (userDoc != null)
            return new GhcrPackageVersionsResult(ParseGhcrVersions(userDoc), userError);

        return new GhcrPackageVersionsResult([], userError ?? orgError ?? $"Could not load GHCR versions for '{owner}/{packageName}'.");
    }

    private async Task<(JsonDocument? Document, string? Error)> TryGetGitHubJsonAsync(
        RegistryCredential registry,
        Uri uri,
        CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient();
        using var request = CreateGitHubApiRequest(uri, registry);
        using var response = await client.SendAsync(request, ct);
        var payload = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            return (null, BuildErrorMessage(response, payload));

        return (JsonDocument.Parse(payload), null);
    }

    private static HttpRequestMessage CreateGitHubApiRequest(Uri uri, RegistryCredential registry)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.UserAgent.ParseAdd("RunnerRunner");

        if (!string.IsNullOrWhiteSpace(registry.Password))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", registry.Password);

        return request;
    }

    private static Uri BuildGitHubApiUri(string path) =>
        new($"https://api.github.com{path}");

    private static (string? Owner, string Filter, bool ExplicitOwner) ParseGhcrSearch(RegistryCredential registry, string query)
    {
        var normalized = NormalizeRepositoryQuery(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return (GetGhcrDefaultOwner(registry), "", false);

        var slashIndex = normalized.IndexOf('/');
        if (slashIndex > 0)
        {
            var owner = normalized[..slashIndex];
            var filter = normalized[(slashIndex + 1)..];
            return (owner, filter, true);
        }

        return (GetGhcrDefaultOwner(registry), normalized, false);
    }

    private static (string? Owner, string? PackageName) ParseGhcrRepository(RegistryCredential registry, string repository)
    {
        var normalized = NormalizeRepositoryQuery(repository);
        var slashIndex = normalized.IndexOf('/');
        if (slashIndex > 0)
            return (normalized[..slashIndex], normalized[(slashIndex + 1)..]);

        var defaultOwner = GetGhcrDefaultOwner(registry);
        return string.IsNullOrWhiteSpace(defaultOwner) ? (null, normalized) : (defaultOwner, normalized);
    }

    private static string? GetGhcrDefaultOwner(RegistryCredential registry) =>
        string.IsNullOrWhiteSpace(registry.DefaultNamespace)
            ? registry.Username?.Trim()
            : registry.DefaultNamespace.Trim();

    private static List<GhcrPackage> ParseGhcrPackages(JsonDocument doc)
    {
        var results = new List<GhcrPackage>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return results;

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var name = TryGetString(item, "name");
            if (string.IsNullOrWhiteSpace(name))
                continue;

            int? versionCount = null;
            if (item.TryGetProperty("version_count", out var versionCountProp) && versionCountProp.TryGetInt32(out var vc))
                versionCount = vc;

            results.Add(new GhcrPackage(
                name,
                TryGetString(item, "visibility"),
                TryGetString(item, "html_url") ?? TryGetString(item, "url"),
                TryGetString(item, "description"),
                versionCount));
        }

        return results;
    }

    private static List<GhcrPackageVersion> ParseGhcrVersions(JsonDocument doc)
    {
        var results = new List<GhcrPackageVersion>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return results;

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            DateTimeOffset? updatedAt = null;
            if (item.TryGetProperty("updated_at", out var updatedProp) &&
                updatedProp.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(updatedProp.GetString(), out var parsed))
            {
                updatedAt = parsed;
            }

            var tags = new List<string>();
            if (item.TryGetProperty("metadata", out var metadata) &&
                metadata.ValueKind == JsonValueKind.Object &&
                metadata.TryGetProperty("container", out var container) &&
                container.ValueKind == JsonValueKind.Object &&
                container.TryGetProperty("tags", out var tagsProp) &&
                tagsProp.ValueKind == JsonValueKind.Array)
            {
                tags = tagsProp.EnumerateArray()
                    .Select(t => t.GetString() ?? "")
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            if (!tags.Any())
            {
                var name = TryGetString(item, "name");
                if (!string.IsNullOrWhiteSpace(name) && !name.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    tags.Add(name);
            }

            results.Add(new GhcrPackageVersion(tags, updatedAt));
        }

        return results;
    }

    private static string BuildErrorMessage(HttpResponseMessage response, string body)
    {
        var summary = $"{(int)response.StatusCode} {response.ReasonPhrase}";
        if (string.IsNullOrWhiteSpace(body))
            return $"Registry request failed: {summary}";

        var trimmed = body.Length > 200 ? body[..200] + "..." : body;
        return $"Registry request failed: {summary} - {trimmed}";
    }

    private static Uri BuildRegistryUri(string registryUrl, string path)
    {
        var baseUrl = registryUrl.Trim();
        if (!baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            baseUrl = $"https://{baseUrl}";
        }

        if (!baseUrl.EndsWith('/'))
            baseUrl += "/";

        return new Uri(new Uri(baseUrl), path.TrimStart('/'));
    }

    private static bool IsDockerHubRegistry(string registryUrl)
    {
        var value = (registryUrl ?? "").ToLowerInvariant();
        return value.Contains("docker.io") ||
               value.Contains("registry-1.docker.io") ||
               value.Contains("hub.docker.com");
    }

    private static bool IsGhcrRegistry(string registryUrl)
    {
        var value = (registryUrl ?? "").ToLowerInvariant();
        return value.Contains("ghcr.io") || value.Contains("github container registry");
    }

    private static string NormalizeDockerHubRepository(string repository)
    {
        var repo = NormalizeRepositoryQuery(repository);
        if (!repo.Contains('/'))
            repo = $"library/{repo}";
        return repo;
    }

    private static string NormalizeRepositoryQuery(string repository) =>
        repository.Trim().Trim('/').Replace("\\", "/");

    private static bool LooksLikeRepositoryPath(string query) =>
        query.Contains('/') || query.Contains('-') || query.Contains('_');

    private static string EncodeRepositoryPath(string repository) =>
        string.Join("/", repository.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));

    private static List<RegistryTagResult> SortTags(List<RegistryTagResult> tags) =>
        tags
            .OrderByDescending(x => string.Equals(x.Name, "latest", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(x => x.UpdatedAt ?? DateTimeOffset.MinValue)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string? TryGetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    [GeneratedRegex("(\\w+)=\"([^\"]*)\"")]
    private static partial Regex AuthRegex();

    private sealed record GhcrPackage(string Name, string? Visibility, string? Url, string? Description, int? VersionCount);
    private sealed record GhcrPackageVersion(List<string> Tags, DateTimeOffset? UpdatedAt);
    private sealed record GhcrPackagesResult(List<GhcrPackage> Packages, string? Warning);
    private sealed record GhcrPackageVersionsResult(List<GhcrPackageVersion> Versions, string? Warning);
}

public sealed class RegistrySearchResponse
{
    public List<RegistryRepositoryResult> Results { get; set; } = [];
    public string? Warning { get; set; }
    public string? InfoMessage { get; set; }
    public bool FromCache { get; set; }

    public RegistrySearchResponse Clone(bool fromCache) => new()
    {
        Results = Results.Select(x => new RegistryRepositoryResult
        {
            Name = x.Name,
            Description = x.Description,
            StarCount = x.StarCount
        }).ToList(),
        Warning = Warning,
        InfoMessage = InfoMessage,
        FromCache = fromCache
    };
}

public sealed class RegistryRepositoryResult
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public int? StarCount { get; set; }
}

public sealed class RegistryTagResponse
{
    public string Repository { get; set; } = "";
    public List<RegistryTagResult> Tags { get; set; } = [];
    public string? Warning { get; set; }
    public string? InfoMessage { get; set; }
    public bool FromCache { get; set; }

    public RegistryTagResponse Clone(bool fromCache) => new()
    {
        Repository = Repository,
        Tags = Tags.Select(x => new RegistryTagResult
        {
            Name = x.Name,
            SizeBytes = x.SizeBytes,
            UpdatedAt = x.UpdatedAt
        }).ToList(),
        Warning = Warning,
        InfoMessage = InfoMessage,
        FromCache = fromCache
    };
}

public sealed class RegistryTagResult
{
    public string Name { get; set; } = "";
    public long? SizeBytes { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
