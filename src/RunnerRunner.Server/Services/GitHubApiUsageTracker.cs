using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;

namespace RunnerRunner.Server.Services;

public enum GitHubApiRequestKind
{
    Rest,
    GraphQL
}

public sealed record GitHubRateLimitSnapshot(
    int? Limit,
    int? Remaining,
    int? Used,
    DateTimeOffset? ResetAt,
    string? Resource);

public sealed record GitHubApiUsageSummary(
    string Method,
    GitHubApiRequestKind Kind,
    string Endpoint,
    string Scope,
    string Category,
    long RequestCount,
    long SuccessCount,
    long NotModifiedCount,
    long FailureCount,
    long RateLimitedCount,
    int? LastStatusCode,
    DateTimeOffset LastObservedAt,
    TimeSpan LastDuration,
    GitHubRateLimitSnapshot LastRateLimit);

public sealed class GitHubApiUsageSnapshot
{
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset CapturedAt { get; init; }
    public required IReadOnlyList<GitHubApiUsageSummary> Summaries { get; init; }

    public long TotalRequests => Summaries.Sum(s => s.RequestCount);
    public long RestRequests => Summaries.Where(s => s.Kind == GitHubApiRequestKind.Rest).Sum(s => s.RequestCount);
    public long GraphQLRequests => Summaries.Where(s => s.Kind == GitHubApiRequestKind.GraphQL).Sum(s => s.RequestCount);
    public long RateLimitedRequests => Summaries.Sum(s => s.RateLimitedCount);
    public long FailedRequests => Summaries.Sum(s => s.FailureCount);

    public GitHubRateLimitSnapshot? MostConstrainedRateLimit => Summaries
        .Select(s => s.LastRateLimit)
        .Where(r => r.Remaining.HasValue)
        .OrderBy(r => r.Remaining)
        .FirstOrDefault();
}

public sealed class GitHubApiUsageTracker
{
    private sealed class UsageBucket
    {
        private readonly object gate = new();

        public UsageBucket(
            string method,
            GitHubApiRequestKind kind,
            string endpoint,
            string scope,
            string category)
        {
            Method = method;
            Kind = kind;
            Endpoint = endpoint;
            Scope = scope;
            Category = category;
        }

        public string Method { get; }
        public GitHubApiRequestKind Kind { get; }
        public string Endpoint { get; }
        public string Scope { get; }
        public string Category { get; }
        public long RequestCount { get; private set; }
        public long SuccessCount { get; private set; }
        public long NotModifiedCount { get; private set; }
        public long FailureCount { get; private set; }
        public long RateLimitedCount { get; private set; }
        public int? LastStatusCode { get; private set; }
        public DateTimeOffset LastObservedAt { get; private set; }
        public TimeSpan LastDuration { get; private set; }
        public GitHubRateLimitSnapshot LastRateLimit { get; private set; } = new(null, null, null, null, null);

        public void Record(HttpStatusCode? statusCode, TimeSpan duration, GitHubRateLimitSnapshot rateLimit)
        {
            lock (gate)
            {
                RequestCount++;
                LastStatusCode = statusCode.HasValue ? (int)statusCode.Value : null;
                LastObservedAt = DateTimeOffset.UtcNow;
                LastDuration = duration;
                LastRateLimit = rateLimit;

                if (statusCode == HttpStatusCode.NotModified)
                    NotModifiedCount++;
                else if (statusCode.HasValue && (int)statusCode.Value is >= 200 and <= 399)
                    SuccessCount++;
                else
                    FailureCount++;

                if (statusCode is HttpStatusCode.TooManyRequests
                    || (statusCode == HttpStatusCode.Forbidden && rateLimit.Remaining == 0))
                {
                    RateLimitedCount++;
                }
            }
        }

        public GitHubApiUsageSummary ToSummary()
        {
            lock (gate)
            {
                return new GitHubApiUsageSummary(
                    Method,
                    Kind,
                    Endpoint,
                    Scope,
                    Category,
                    RequestCount,
                    SuccessCount,
                    NotModifiedCount,
                    FailureCount,
                    RateLimitedCount,
                    LastStatusCode,
                    LastObservedAt,
                    LastDuration,
                    LastRateLimit);
            }
        }
    }

    private static readonly Regex ShaRegex = new("^[a-fA-F0-9]{40}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> QueryKeysToKeep = new(StringComparer.OrdinalIgnoreCase)
    {
        "package_type",
        "per_page",
        "status"
    };

    private readonly ConcurrentDictionary<string, UsageBucket> buckets = new(StringComparer.Ordinal);
    private readonly DateTimeOffset startedAt = DateTimeOffset.UtcNow;

    public void Record(HttpRequestMessage request, HttpResponseMessage? response, TimeSpan duration)
    {
        if (!TryCreateUsageKey(request, out var key))
            return;

        var bucket = buckets.GetOrAdd(
            key.DictionaryKey,
            _ => new UsageBucket(key.Method, key.Kind, key.Endpoint, key.Scope, key.Category));

        bucket.Record(response?.StatusCode, duration, ReadRateLimit(response));
    }

    public GitHubApiUsageSnapshot GetSnapshot() => new()
    {
        StartedAt = startedAt,
        CapturedAt = DateTimeOffset.UtcNow,
        Summaries = buckets.Values
            .Select(b => b.ToSummary())
            .OrderByDescending(s => s.LastObservedAt)
            .ThenBy(s => s.Method, StringComparer.Ordinal)
            .ThenBy(s => s.Endpoint, StringComparer.Ordinal)
            .ToList()
    };

    internal static bool TryCreateUsageKey(HttpRequestMessage request, out GitHubApiUsageKey key)
    {
        key = default;
        var uri = request.RequestUri;
        if (!IsGitHubApiUri(uri, out var kind))
            return false;

        var endpoint = NormalizeEndpoint(uri!, out var scope);
        var method = request.Method.Method.ToUpperInvariant();
        var category = CategorizeEndpoint(endpoint);
        key = new GitHubApiUsageKey(
            $"{method}|{kind}|{scope}|{endpoint}",
            method,
            kind,
            endpoint,
            scope,
            category);
        return true;
    }

    private static bool IsGitHubApiUri(Uri? uri, out GitHubApiRequestKind kind)
    {
        kind = GitHubApiRequestKind.Rest;
        if (uri == null || !uri.IsAbsoluteUri)
            return false;

        var path = uri.AbsolutePath;
        var isGraphQL = path.Equals("/graphql", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/graphql", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/graphql", StringComparison.OrdinalIgnoreCase);

        if (isGraphQL)
        {
            kind = GitHubApiRequestKind.GraphQL;
            return true;
        }

        if (uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
            return true;

        return path.Equals("/api/v3", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/v3/", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeEndpoint(Uri uri, out string scope)
    {
        scope = "global";
        var segments = uri.AbsolutePath
            .Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();

        if (segments.Length == 0)
            return "/";

        var apiPrefixLength = segments.Length >= 2
            && segments[0].Equals("api", StringComparison.OrdinalIgnoreCase)
            && segments[1].Equals("v3", StringComparison.OrdinalIgnoreCase)
                ? 2
                : 0;

        var normalized = segments.ToArray();
        if (segments.Length > apiPrefixLength)
        {
            var root = segments[apiPrefixLength];
            if (root.Equals("repos", StringComparison.OrdinalIgnoreCase) && segments.Length > apiPrefixLength + 2)
            {
                scope = $"repo:{segments[apiPrefixLength + 1]}/{segments[apiPrefixLength + 2]}";
                normalized[apiPrefixLength + 1] = "{owner}";
                normalized[apiPrefixLength + 2] = "{repo}";
            }
            else if (root.Equals("orgs", StringComparison.OrdinalIgnoreCase) && segments.Length > apiPrefixLength + 1)
            {
                scope = $"org:{segments[apiPrefixLength + 1]}";
                normalized[apiPrefixLength + 1] = "{org}";
            }
            else if (root.Equals("app", StringComparison.OrdinalIgnoreCase)
                && segments.Length > apiPrefixLength + 2
                && segments[apiPrefixLength + 1].Equals("installations", StringComparison.OrdinalIgnoreCase))
            {
                scope = $"installation:{segments[apiPrefixLength + 2]}";
                normalized[apiPrefixLength + 2] = "{installation}";
            }
        }

        for (var i = apiPrefixLength; i < normalized.Length; i++)
            normalized[i] = NormalizeSegment(normalized[i]);

        var path = "/" + string.Join('/', normalized);
        var query = NormalizeQuery(uri.Query);
        return string.IsNullOrEmpty(query) ? path : $"{path}?{query}";
    }

    private static string NormalizeSegment(string segment)
    {
        if (long.TryParse(segment, out _))
            return "{id}";

        if (Guid.TryParse(segment, out _))
            return "{id}";

        return ShaRegex.IsMatch(segment) ? "{sha}" : segment;
    }

    private static string NormalizeQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "";

        var pairs = query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Select(parts => new
            {
                Key = Uri.UnescapeDataString(parts[0]),
                Value = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : ""
            })
            .Where(pair => QueryKeysToKeep.Contains(pair.Key))
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => $"{pair.Key}={NormalizeQueryValue(pair.Value)}")
            .ToList();

        return pairs.Count == 0 ? "" : string.Join('&', pairs);
    }

    private static string NormalizeQueryValue(string value) =>
        long.TryParse(value, out _) || ShaRegex.IsMatch(value) ? "{value}" : value;

    private static string CategorizeEndpoint(string endpoint)
    {
        if (endpoint.Contains("/graphql", StringComparison.OrdinalIgnoreCase))
            return "GraphQL";
        if (endpoint.Contains("/app/installations/", StringComparison.OrdinalIgnoreCase))
            return "GitHub App auth";
        if (endpoint.Contains("/actions/runners/generate-jitconfig", StringComparison.OrdinalIgnoreCase))
            return "JIT config";
        if (endpoint.Contains("/actions/runners", StringComparison.OrdinalIgnoreCase))
            return "Runner registration";
        if (endpoint.Contains("/actions/runs", StringComparison.OrdinalIgnoreCase))
            return "Workflow/job reconciliation";
        if (endpoint.Contains("/runner-groups", StringComparison.OrdinalIgnoreCase))
            return "Runner groups";
        if (endpoint.Contains("/packages", StringComparison.OrdinalIgnoreCase))
            return "Registry catalog";
        if (endpoint.Contains("/releases", StringComparison.OrdinalIgnoreCase))
            return "Version/update discovery";
        if (endpoint.Contains("/compare/", StringComparison.OrdinalIgnoreCase)
            || endpoint.Contains("/commits/", StringComparison.OrdinalIgnoreCase))
        {
            return "Update validation";
        }

        return "GitHub API";
    }

    private static GitHubRateLimitSnapshot ReadRateLimit(HttpResponseMessage? response) => new(
        ReadRateLimitInt(response, "x-ratelimit-limit"),
        ReadRateLimitInt(response, "x-ratelimit-remaining"),
        ReadRateLimitInt(response, "x-ratelimit-used"),
        ReadRateLimitReset(response),
        ReadHeader(response, "x-ratelimit-resource"));

    private static int? ReadRateLimitInt(HttpResponseMessage? response, string name) =>
        int.TryParse(ReadHeader(response, name), out var value) ? value : null;

    private static DateTimeOffset? ReadRateLimitReset(HttpResponseMessage? response) =>
        long.TryParse(ReadHeader(response, "x-ratelimit-reset"), out var unixSeconds)
            ? DateTimeOffset.FromUnixTimeSeconds(unixSeconds)
            : null;

    private static string? ReadHeader(HttpResponseMessage? response, string name)
    {
        if (response?.Headers.TryGetValues(name, out var values) != true)
            return null;

        return values?.FirstOrDefault();
    }

    internal readonly record struct GitHubApiUsageKey(
        string DictionaryKey,
        string Method,
        GitHubApiRequestKind Kind,
        string Endpoint,
        string Scope,
        string Category);
}
