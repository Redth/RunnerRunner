using System.Text.Json;
using System.Text.Json.Serialization;
using RunnerRunner.Core.Hub;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Services;
using Shiny.DocumentDb;
using Host = RunnerRunner.Core.Models.Host;

namespace RunnerRunner.Server.Services.HostWorkers;

public sealed class HostWorkerUpdateService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly IDocumentStore _store;
    private readonly IHostCommandDispatcher _dispatcher;
    private readonly ILogger<HostWorkerUpdateService> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private HostWorkerReleaseInfo? _cachedRelease;
    private DateTimeOffset _cacheExpiresAt;

    public HostWorkerUpdateService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        IDocumentStore store,
        IHostCommandDispatcher dispatcher,
        ILogger<HostWorkerUpdateService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _store = store;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    public async Task<HostWorkerReleaseInfo?> GetLatestReleaseAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        if (!forceRefresh && _cachedRelease != null && _cacheExpiresAt > DateTimeOffset.UtcNow)
            return _cachedRelease;

        await _refreshLock.WaitAsync(ct);
        try
        {
            if (!forceRefresh && _cachedRelease != null && _cacheExpiresAt > DateTimeOffset.UtcNow)
                return _cachedRelease;

            _cachedRelease = await FetchLatestReleaseAsync(ct);
            var cacheMinutes = Math.Clamp(_configuration.GetValue("HostWorkerUpdates:CacheMinutes", 30), 1, 24 * 60);
            _cacheExpiresAt = DateTimeOffset.UtcNow.AddMinutes(cacheMinutes);
            return _cachedRelease;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public async Task<HostWorkerUpdateAvailability> GetAvailabilityAsync(Host host, bool forceRefresh = false, CancellationToken ct = default)
    {
        var release = await GetLatestReleaseAsync(forceRefresh, ct);
        if (release == null)
            return HostWorkerUpdateAvailability.Unavailable("Unable to read latest GitHub release.");

        if (!HostWorkerUpdateSelector.TrySelectAsset(host, release, out var asset, out var reason))
            return HostWorkerUpdateAvailability.Unavailable(reason);

        var updateAvailable = HostWorkerUpdateSelector.IsUpdateAvailable(host.AgentVersion, release.Version);
        return new HostWorkerUpdateAvailability(release, asset, updateAvailable, null);
    }

    public async Task RefreshHostUpdateStateAsync(Host host, bool forceRefresh = false, CancellationToken ct = default)
    {
        var availability = await GetAvailabilityAsync(host, forceRefresh, ct);
        host.LastUpdateCheckAt = DateTime.UtcNow;
        host.LatestAvailableVersion = availability.Release?.Version;
        if (!availability.IsAvailable)
        {
            host.UpdateStatus = "Unavailable";
            host.UpdateMessage = availability.UnavailableReason;
        }
        else if (!availability.UpdateAvailable)
        {
            host.UpdateStatus = "Current";
            host.UpdateMessage = $"HostWorker is current at {host.AgentVersion ?? "unknown"}.";
        }
        else
        {
            host.UpdateStatus = "UpdateAvailable";
            host.UpdateMessage = $"HostWorker {availability.Release!.Version} is available.";
        }

        await _store.Update(host);
    }

    public async Task QueueUpdateAsync(string hostId, bool force, CancellationToken ct = default)
    {
        var host = await _store.Get<Host>(hostId)
            ?? throw new InvalidOperationException($"Host '{hostId}' was not found.");

        if (host.AgentStatus != AgentStatus.Online)
            throw new InvalidOperationException($"HostWorker '{host.Label}' must be online before it can be updated.");

        if (!force)
        {
            var activeRunners = (await _store.Query<RunnerInstance>().ToList())
                .Count(instance => instance.HostId == host.Id
                                   && instance.Status is RunnerInstanceStatus.Pending
                                      or RunnerInstanceStatus.Starting
                                      or RunnerInstanceStatus.Running
                                      or RunnerInstanceStatus.Stopping);
            if (activeRunners > 0)
                throw new InvalidOperationException($"HostWorker '{host.Label}' has {activeRunners} active runner(s). Stop them before updating.");
        }

        var availability = await GetAvailabilityAsync(host, forceRefresh: true, ct);
        if (!availability.IsAvailable || availability.Release == null || availability.Asset == null)
            throw new InvalidOperationException(availability.UnavailableReason ?? "No HostWorker update asset is available for this host.");

        if (!force && !availability.UpdateAvailable)
            throw new InvalidOperationException($"HostWorker '{host.Label}' is already current.");

        host.UpdateStatus = "Queued";
        host.UpdateMessage = $"Queued update to {availability.Release.Version}.";
        host.LatestAvailableVersion = availability.Release.Version;
        host.LastUpdateStartedAt = DateTime.UtcNow;
        await _store.Update(host);

        await _dispatcher.DispatchApplyHostWorkerUpdateAsync(host.Id, new HostWorkerUpdateCommand
        {
            TargetVersion = availability.Release.Version,
            AssetName = availability.Asset.Name,
            AssetUrl = availability.Asset.DownloadUrl,
            Sha256 = availability.Asset.Sha256,
            Force = force
        });
    }

    private async Task<HostWorkerReleaseInfo?> FetchLatestReleaseAsync(CancellationToken ct)
    {
        var repository = _configuration["HostWorkerUpdates:Repository"] ?? "Redth/RunnerRunner";
        var requestUrl = $"https://api.github.com/repos/{repository}/releases/latest";
        var client = _httpClientFactory.CreateClient(nameof(HostWorkerUpdateService));
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
        request.Headers.UserAgent.ParseAdd("RunnerRunner");
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var release = await JsonSerializer.DeserializeAsync<GitHubReleaseResponse>(stream, JsonOptions, ct);
        if (release == null || string.IsNullOrWhiteSpace(release.TagName))
            return null;

        var manifestAsset = release.Assets.FirstOrDefault(asset =>
            string.Equals(asset.Name, "release-manifest.json", StringComparison.OrdinalIgnoreCase));
        if (manifestAsset == null || string.IsNullOrWhiteSpace(manifestAsset.BrowserDownloadUrl))
            return null;

        var manifest = await FetchManifestAsync(client, manifestAsset.BrowserDownloadUrl, ct);
        var assets = release.Assets
            .Where(asset => !string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
            .Select(asset => new HostWorkerReleaseAsset(
                asset.Name,
                asset.BrowserDownloadUrl,
                manifest.Assets.FirstOrDefault(a => string.Equals(a.Name, asset.Name, StringComparison.OrdinalIgnoreCase))?.Sha256 ?? ""))
            .ToArray();

        return new HostWorkerReleaseInfo(release.TagName, release.HtmlUrl, release.PublishedAt, assets);
    }

    private static async Task<ReleaseManifest> FetchManifestAsync(HttpClient client, string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("RunnerRunner");
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<ReleaseManifest>(stream, JsonOptions, ct)
               ?? new ReleaseManifest();
    }

    private sealed class GitHubReleaseResponse
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = "";

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedAt { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubReleaseAssetResponse> Assets { get; set; } = [];
    }

    private sealed class GitHubReleaseAssetResponse
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = "";
    }

    private sealed class ReleaseManifest
    {
        public List<ReleaseManifestAsset> Assets { get; set; } = [];
    }

    private sealed class ReleaseManifestAsset
    {
        public string Name { get; set; } = "";
        public string Sha256 { get; set; } = "";
    }
}

public sealed record HostWorkerReleaseInfo(
    string Version,
    string? ReleaseUrl,
    DateTimeOffset? PublishedAt,
    IReadOnlyList<HostWorkerReleaseAsset> Assets);

public sealed record HostWorkerReleaseAsset(string Name, string DownloadUrl, string Sha256);

public sealed record HostWorkerUpdateAvailability(
    HostWorkerReleaseInfo? Release,
    HostWorkerReleaseAsset? Asset,
    bool UpdateAvailable,
    string? UnavailableReason)
{
    public bool IsAvailable => Release != null && Asset != null && string.IsNullOrWhiteSpace(UnavailableReason);

    public static HostWorkerUpdateAvailability Unavailable(string? reason)
        => new(null, null, false, reason ?? "No compatible update is available.");
}

public static class HostWorkerUpdateSelector
{
    public static bool TrySelectAsset(
        Host host,
        HostWorkerReleaseInfo release,
        out HostWorkerReleaseAsset? asset,
        out string? unavailableReason)
    {
        var rid = GetRuntimeIdentifier(host.Platform, host.Architecture);
        if (rid == null)
        {
            asset = null;
            unavailableReason = $"No HostWorker release asset is defined for {host.Platform} {host.Architecture}.";
            return false;
        }

        var expectedName = host.Platform == HostPlatform.Windows
            ? $"runnerrunner-hostworker-{rid}.zip"
            : $"runnerrunner-hostworker-{rid}.tar.gz";
        asset = release.Assets.FirstOrDefault(a => string.Equals(a.Name, expectedName, StringComparison.OrdinalIgnoreCase));
        if (asset == null)
        {
            unavailableReason = $"Release {release.Version} does not include {expectedName}.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(asset.Sha256))
        {
            unavailableReason = $"Release {release.Version} is missing a checksum for {expectedName}.";
            return false;
        }

        unavailableReason = null;
        return true;
    }

    public static bool IsUpdateAvailable(string? currentVersion, string latestVersion)
    {
        if (string.IsNullOrWhiteSpace(currentVersion))
            return true;

        var current = NormalizeVersion(currentVersion);
        var latest = NormalizeVersion(latestVersion);
        if (Version.TryParse(current, out var currentParsed) && Version.TryParse(latest, out var latestParsed))
            return latestParsed > currentParsed;

        return !string.Equals(current, latest, StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetRuntimeIdentifier(HostPlatform platform, string? architecture)
    {
        var arch = (architecture ?? "").ToLowerInvariant();
        return platform switch
        {
            HostPlatform.Linux when arch.Contains("arm") || arch.Contains("aarch64") => "linux-arm64",
            HostPlatform.Linux => "linux-x64",
            HostPlatform.MacOS when arch.Contains("arm") || arch.Contains("aarch64") => "osx-arm64",
            HostPlatform.MacOS => "osx-x64",
            HostPlatform.Windows => "win-x64",
            _ => null
        };
    }

    private static string NormalizeVersion(string version)
    {
        var normalized = version.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
            normalized = normalized[1..];

        var metadataIndex = normalized.IndexOfAny(['+', '-']);
        if (metadataIndex >= 0)
            normalized = normalized[..metadataIndex];

        var parts = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            1 => normalized + ".0.0",
            2 => normalized + ".0",
            _ => normalized
        };
    }
}
