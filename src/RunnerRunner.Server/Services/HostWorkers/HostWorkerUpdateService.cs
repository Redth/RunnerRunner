using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
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
    private readonly GitHubAuthenticationService _gitHubAuth;
    private readonly IDocumentStore _store;
    private readonly IHostCommandDispatcher _dispatcher;
    private readonly HostWorkerLocalUpdateStore _localUpdateStore;
    private readonly ILogger<HostWorkerUpdateService> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private HostWorkerReleaseInfo? _cachedRelease;
    private DateTimeOffset _cacheExpiresAt;

    public HostWorkerUpdateService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        GitHubAuthenticationService gitHubAuth,
        IDocumentStore store,
        IHostCommandDispatcher dispatcher,
        HostWorkerLocalUpdateStore localUpdateStore,
        ILogger<HostWorkerUpdateService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _gitHubAuth = gitHubAuth;
        _store = store;
        _dispatcher = dispatcher;
        _localUpdateStore = localUpdateStore;
        _logger = logger;
    }

    public async Task<HostWorkerReleaseInfo?> GetLatestReleaseAsync(bool forceRefresh = false, CancellationToken ct = default)
        => await GetReleaseAsync(null, forceRefresh, ct);

    public async Task<HostWorkerReleaseInfo?> GetReleaseAsync(string? version, bool forceRefresh = false, CancellationToken ct = default)
    {
        if (!IsLatestRelease(version))
            return await ResolveGitHubSourceAsync(new HostWorkerUpdateSelection(HostWorkerUpdateSourceKind.Release, version), forceRefresh, ct);

        if (!forceRefresh && _cachedRelease != null && _cacheExpiresAt > DateTimeOffset.UtcNow)
            return _cachedRelease;

        await _refreshLock.WaitAsync(ct);
        try
        {
            if (!forceRefresh && _cachedRelease != null && _cacheExpiresAt > DateTimeOffset.UtcNow)
                return _cachedRelease;

            _cachedRelease = await FetchGitHubReleaseAsync(null, allowNotFound: false, ct);
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
        => await GetAvailabilityAsync(host, HostWorkerUpdateSelection.LatestRelease(), forceRefresh, ct);

    public async Task<HostWorkerUpdateAvailability> GetAvailabilityAsync(
        Host host,
        HostWorkerUpdateSelection selection,
        bool forceRefresh = false,
        CancellationToken ct = default)
    {
        var release = await ResolveReleaseInfoAsync(selection, forceRefresh, ct);
        if (release == null)
            return HostWorkerUpdateAvailability.Unavailable($"Unable to read HostWorker updates from {selection.Source.ToDisplayName()}.");

        if (HostWorkerUpdateSelector.IsContainerized(host))
        {
            if (!HostWorkerUpdateSelector.TrySelectContainerImage(host, release, out var containerImage, out var reason))
                return HostWorkerUpdateAvailability.Unavailable(reason);

            var containerUpdateAvailable = selection.Source == HostWorkerUpdateSourceKind.Release
                ? HostWorkerUpdateSelector.IsUpdateAvailable(host.AgentVersion, release.Version)
                : true;
            return new HostWorkerUpdateAvailability(release, null, containerImage, containerUpdateAvailable, null);
        }

        if (!HostWorkerUpdateSelector.TrySelectAsset(host, release, out var asset, out var assetReason))
            return HostWorkerUpdateAvailability.Unavailable(assetReason);

        if (string.IsNullOrWhiteSpace(asset!.DownloadUrl))
            return HostWorkerUpdateAvailability.Unavailable("HostWorkerUpdates:PublicBaseUrl or a request base URL is required to queue this HostWorker update artifact.");

        var updateAvailable = selection.Source == HostWorkerUpdateSourceKind.Release
            ? HostWorkerUpdateSelector.IsUpdateAvailable(host.AgentVersion, release.Version)
            : true;
        return new HostWorkerUpdateAvailability(release, asset, null, updateAvailable, null);
    }

    public async Task RefreshHostUpdateStateAsync(Host host, bool forceRefresh = false, CancellationToken ct = default)
        => await RefreshHostUpdateStateAsync(host, HostWorkerUpdateSelection.LatestRelease(), forceRefresh, ct);

    public async Task RefreshHostUpdateStateAsync(
        Host host,
        HostWorkerUpdateSelection selection,
        bool forceRefresh = false,
        CancellationToken ct = default)
    {
        var availability = await GetAvailabilityAsync(host, selection, forceRefresh, ct);
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
            host.UpdateMessage = $"HostWorker {availability.Release!.Version} is available from {selection.Source.ToDisplayName()}.";
        }

        await _store.Update(host);
    }

    public async Task QueueUpdateAsync(string hostId, bool force, CancellationToken ct = default)
        => await QueueUpdateAsync(hostId, HostWorkerUpdateSelection.LatestRelease(force), ct);

    public async Task QueueUpdateAsync(string hostId, HostWorkerUpdateSelection selection, CancellationToken ct = default)
    {
        var host = await _store.Get<Host>(hostId)
            ?? throw new InvalidOperationException($"Host '{hostId}' was not found.");

        if (host.AgentStatus != AgentStatus.Online)
            throw new InvalidOperationException($"HostWorker '{host.Label}' must be online before it can be updated.");

        if (!selection.Force)
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

        var availability = await GetAvailabilityAsync(host, selection, forceRefresh: true, ct);
        if (!availability.IsAvailable || availability.Release == null)
            throw new InvalidOperationException(availability.UnavailableReason ?? "No HostWorker update asset is available for this host.");

        if (availability.Asset != null && string.IsNullOrWhiteSpace(availability.Asset.DownloadUrl))
            throw new InvalidOperationException("HostWorkerUpdates:PublicBaseUrl or a request base URL is required to queue this HostWorker update artifact.");

        if (availability.Asset == null && string.IsNullOrWhiteSpace(availability.ContainerImage))
            throw new InvalidOperationException("No HostWorker update asset or container image is available for this host.");

        if (!selection.Force && !selection.AllowNonUpgrade && !availability.UpdateAvailable)
            throw new InvalidOperationException($"HostWorker '{host.Label}' is already current.");

        host.UpdateStatus = "Queued";
        host.UpdateMessage = $"Queued update to {availability.Release.Version} from {selection.Source.ToDisplayName()}.";
        host.LatestAvailableVersion = availability.Release.Version;
        host.LastUpdateStartedAt = DateTime.UtcNow;
        await _store.Update(host);

        await _dispatcher.DispatchApplyHostWorkerUpdateAsync(host.Id, new HostWorkerUpdateCommand
        {
            TargetVersion = availability.Release.Version,
            AssetName = availability.Asset?.Name ?? "",
            AssetUrl = availability.Asset?.DownloadUrl ?? "",
            Sha256 = availability.Asset?.Sha256 ?? "",
            ContainerImage = availability.ContainerImage,
            Force = selection.Force
        });
    }

    public async Task<IReadOnlyList<HostWorkerUpdateVersion>> GetAvailableVersionsAsync(
        HostWorkerUpdateSourceKind source,
        CancellationToken ct = default)
    {
        if (source != HostWorkerUpdateSourceKind.Release)
            return _localUpdateStore.ListVersions(source);

        var release = await GetLatestReleaseAsync(ct: ct);
        return release == null
            ? []
            : [new HostWorkerUpdateVersion(source, release.Version, release.PublishedAt ?? DateTimeOffset.UtcNow, [])];
    }

    private async Task<HostWorkerReleaseInfo?> ResolveReleaseInfoAsync(
        HostWorkerUpdateSelection selection,
        bool forceRefresh,
        CancellationToken ct)
    {
        if (selection.Source == HostWorkerUpdateSourceKind.Release)
            return await ResolveGitHubSourceAsync(selection, forceRefresh, ct);

        var version = _localUpdateStore.GetVersion(selection.Source, selection.Version);
        if (version == null)
            return null;

        var publicBaseUrl = selection.PublicBaseUrl ?? _configuration["HostWorkerUpdates:PublicBaseUrl"];
        var assets = version.Assets
            .Select(asset => new HostWorkerReleaseAsset(
                asset.AssetName,
                string.IsNullOrWhiteSpace(publicBaseUrl) ? "" : _localUpdateStore.BuildDownloadUrl(asset, publicBaseUrl),
                asset.Sha256))
            .ToArray();

        return new HostWorkerReleaseInfo(version.Version, null, version.CreatedAt, assets);
    }

    public async Task ExtractGitHubActionsArtifactAssetAsync(
        long runId,
        string artifactName,
        string assetName,
        string outputPath,
        CancellationToken ct = default)
    {
        if (!string.Equals(artifactName, GetAssetsArtifactName(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unsupported GitHub Actions artifact '{artifactName}'.");

        if (!HostWorkerUpdateSelector.IsHostWorkerAssetName(assetName))
            throw new InvalidOperationException($"Unsupported HostWorker update artifact '{assetName}'.");

        var repository = GetUpdateRepository();
        var artifacts = await FetchActionsArtifactsAsync(runId, repository, ct);
        var artifact = artifacts.FirstOrDefault(x =>
            !x.Expired &&
            string.Equals(x.Name, artifactName, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(x.ArchiveDownloadUrl));
        if (artifact == null)
            throw new FileNotFoundException($"GitHub Actions artifact '{artifactName}' was not found for run {runId}.");

        var archivePath = Path.Combine(Path.GetTempPath(), $"runnerrunner-github-artifact-{Guid.NewGuid():N}.zip");
        try
        {
            await DownloadArtifactArchiveToFileAsync(artifact.ArchiveDownloadUrl, archivePath, ct);
            using var archive = ZipFile.OpenRead(archivePath);
            var entry = archive.Entries.FirstOrDefault(x =>
                string.Equals(Path.GetFileName(x.FullName), assetName, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
                throw new FileNotFoundException($"GitHub Actions artifact '{artifactName}' does not contain '{assetName}'.");

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            entry.ExtractToFile(outputPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(archivePath))
                File.Delete(archivePath);
        }
    }

    private async Task<HostWorkerReleaseInfo?> ResolveGitHubSourceAsync(
        HostWorkerUpdateSelection selection,
        bool forceRefresh,
        CancellationToken ct)
    {
        if (IsLatestRelease(selection.Version))
            return await GetReleaseAsync(selection.Version, forceRefresh, ct);

        var release = await FetchGitHubReleaseAsync(selection.Version, allowNotFound: true, ct);
        return release ?? await FetchGitHubRefArtifactsAsync(selection, ct);
    }

    private async Task<HostWorkerReleaseInfo?> FetchGitHubReleaseAsync(string? version, bool allowNotFound, CancellationToken ct)
    {
        var repository = GetUpdateRepository();
        var requestUrl = IsLatestRelease(version)
            ? $"https://api.github.com/repos/{repository}/releases/latest"
            : $"https://api.github.com/repos/{repository}/releases/tags/{Uri.EscapeDataString(version!.Trim())}";
        var client = _httpClientFactory.CreateClient(nameof(HostWorkerUpdateService));
        using var request = await CreateGitHubRequestAsync(HttpMethod.Get, requestUrl, repository, ct);
        using var response = await client.SendAsync(request, ct);
        if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var release = await JsonSerializer.DeserializeAsync<GitHubReleaseResponse>(stream, JsonOptions, ct);
        if (release == null || string.IsNullOrWhiteSpace(release.TagName))
            return null;

        var manifestAsset = release.Assets.FirstOrDefault(asset =>
            string.Equals(asset.Name, "release-manifest.json", StringComparison.OrdinalIgnoreCase));
        if (manifestAsset == null || string.IsNullOrWhiteSpace(manifestAsset.BrowserDownloadUrl))
            return null;

        var manifest = await FetchManifestAsync(client, manifestAsset.BrowserDownloadUrl, repository, ct);
        var assets = release.Assets
            .Where(asset => !string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
            .Select(asset => new HostWorkerReleaseAsset(
                asset.Name,
                asset.BrowserDownloadUrl,
                manifest.Assets.FirstOrDefault(a => string.Equals(a.Name, asset.Name, StringComparison.OrdinalIgnoreCase))?.Sha256 ?? ""))
            .ToArray();

        return new HostWorkerReleaseInfo(release.TagName, release.HtmlUrl, release.PublishedAt, assets)
        {
            Images = NormalizeImages(manifest.Images)
        };
    }

    private async Task<HostWorkerReleaseInfo?> FetchGitHubRefArtifactsAsync(
        HostWorkerUpdateSelection selection,
        CancellationToken ct)
    {
        var reference = selection.Version?.Trim();
        if (string.IsNullOrWhiteSpace(reference))
            return null;

        var repository = GetUpdateRepository();
        var client = _httpClientFactory.CreateClient(nameof(HostWorkerUpdateService));
        var commit = await FetchCommitAsync(client, repository, reference, ct);
        if (commit == null || string.IsNullOrWhiteSpace(commit.Sha))
            return null;

        var runs = await FetchWorkflowRunsAsync(client, repository, commit.Sha, ct);
        foreach (var run in runs
                     .Where(x => string.Equals(x.Conclusion, "success", StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(x => x.UpdatedAt ?? x.CreatedAt ?? DateTimeOffset.MinValue))
        {
            var artifacts = await FetchActionsArtifactsAsync(run.Id, repository, ct);
            var manifestArtifact = artifacts.FirstOrDefault(x =>
                !x.Expired &&
                string.Equals(x.Name, GetManifestArtifactName(), StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(x.ArchiveDownloadUrl));
            var assetsArtifact = artifacts.FirstOrDefault(x =>
                !x.Expired &&
                string.Equals(x.Name, GetAssetsArtifactName(), StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(x.ArchiveDownloadUrl));

            if (manifestArtifact == null || assetsArtifact == null)
                continue;

            var manifest = await FetchManifestFromArtifactAsync(manifestArtifact.ArchiveDownloadUrl, repository, ct);
            var publicBaseUrl = selection.PublicBaseUrl ?? _configuration["HostWorkerUpdates:PublicBaseUrl"];
            var assets = manifest.Assets
                .Where(asset => HostWorkerUpdateSelector.IsHostWorkerAssetName(asset.Name))
                .Select(asset => new HostWorkerReleaseAsset(
                    asset.Name,
                    string.IsNullOrWhiteSpace(publicBaseUrl)
                        ? ""
                        : BuildGitHubArtifactDownloadUrl(run.Id, assetsArtifact.Name, asset, publicBaseUrl),
                    asset.Sha256))
                .ToArray();

            if (assets.Length == 0 && manifest.Images.Count == 0)
                continue;

            var version = string.IsNullOrWhiteSpace(manifest.GitSha) ? commit.Sha : manifest.GitSha;
            return new HostWorkerReleaseInfo(
                version,
                run.HtmlUrl ?? commit.HtmlUrl,
                run.UpdatedAt ?? run.CreatedAt,
                assets)
            {
                Images = NormalizeImages(manifest.Images)
            };
        }

        return null;
    }

    private static bool IsLatestRelease(string? version)
        => string.IsNullOrWhiteSpace(version)
           || string.Equals(version.Trim(), "latest", StringComparison.OrdinalIgnoreCase);

    private async Task<ReleaseManifest> FetchManifestAsync(HttpClient client, string url, string repository, CancellationToken ct)
    {
        using var request = await CreateGitHubRequestAsync(HttpMethod.Get, url, repository, ct);
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<ReleaseManifest>(stream, JsonOptions, ct)
               ?? new ReleaseManifest();
    }

    private async Task<GitHubCommitResponse?> FetchCommitAsync(
        HttpClient client,
        string repository,
        string reference,
        CancellationToken ct)
    {
        var requestUrl = $"https://api.github.com/repos/{repository}/commits/{Uri.EscapeDataString(reference)}";
        using var request = await CreateGitHubRequestAsync(HttpMethod.Get, requestUrl, repository, ct);
        using var response = await client.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<GitHubCommitResponse>(stream, JsonOptions, ct);
    }

    private async Task<IReadOnlyList<GitHubWorkflowRunResponse>> FetchWorkflowRunsAsync(
        HttpClient client,
        string repository,
        string sha,
        CancellationToken ct)
    {
        var requestUrl = $"https://api.github.com/repos/{repository}/actions/runs?head_sha={Uri.EscapeDataString(sha)}&status=success&per_page=20";
        using var request = await CreateGitHubRequestAsync(HttpMethod.Get, requestUrl, repository, ct);
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var runs = await JsonSerializer.DeserializeAsync<GitHubWorkflowRunsResponse>(stream, JsonOptions, ct);
        return runs?.WorkflowRuns ?? [];
    }

    private async Task<IReadOnlyList<GitHubActionsArtifactResponse>> FetchActionsArtifactsAsync(long runId, string repository, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(nameof(HostWorkerUpdateService));
        var requestUrl = $"https://api.github.com/repos/{repository}/actions/runs/{runId}/artifacts?per_page=100";
        using var request = await CreateGitHubRequestAsync(HttpMethod.Get, requestUrl, repository, ct);
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var artifacts = await JsonSerializer.DeserializeAsync<GitHubActionsArtifactsResponse>(stream, JsonOptions, ct);
        return artifacts?.Artifacts ?? [];
    }

    private async Task<ReleaseManifest> FetchManifestFromArtifactAsync(string archiveDownloadUrl, string repository, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(nameof(HostWorkerUpdateService));
        await using var stream = await DownloadArtifactArchiveAsync(client, archiveDownloadUrl, repository, ct);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var entry = archive.GetEntry("release-manifest.json") ??
                    archive.Entries.FirstOrDefault(x => string.Equals(Path.GetFileName(x.FullName), "release-manifest.json", StringComparison.OrdinalIgnoreCase));
        if (entry == null)
            return new ReleaseManifest();

        await using var manifestStream = entry.Open();
        return await JsonSerializer.DeserializeAsync<ReleaseManifest>(manifestStream, JsonOptions, ct)
               ?? new ReleaseManifest();
    }

    private async Task<MemoryStream> DownloadArtifactArchiveAsync(HttpClient client, string url, string repository, CancellationToken ct)
    {
        using var request = await CreateGitHubRequestAsync(HttpMethod.Get, url, repository, ct);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var memory = new MemoryStream();
        await response.Content.CopyToAsync(memory, ct);
        memory.Position = 0;
        return memory;
    }

    private async Task DownloadArtifactArchiveToFileAsync(string url, string outputPath, CancellationToken ct)
    {
        var repository = GetUpdateRepository();
        var client = _httpClientFactory.CreateClient(nameof(HostWorkerUpdateService));
        using var request = await CreateGitHubRequestAsync(HttpMethod.Get, url, repository, ct);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await input.CopyToAsync(output, ct);
    }

    private static string BuildGitHubArtifactDownloadUrl(
        long runId,
        string artifactName,
        ReleaseManifestAsset asset,
        string publicBaseUrl)
    {
        var baseUri = publicBaseUrl.EndsWith("/", StringComparison.Ordinal) ? publicBaseUrl : publicBaseUrl + "/";
        var relative = $"api/hostworker-updates/github-artifacts/{runId}/{Uri.EscapeDataString(artifactName)}/{Uri.EscapeDataString(asset.Name)}?sha256={Uri.EscapeDataString(asset.Sha256)}";
        return new Uri(new Uri(baseUri), relative).ToString();
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> NormalizeImages(Dictionary<string, List<string>> images)
    {
        var normalized = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in images)
            normalized[key] = value.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        return normalized;
    }

    private string GetManifestArtifactName()
        => _configuration["HostWorkerUpdates:ManifestArtifactName"] ?? "runnerrunner-hostworker-manifest";

    private string GetAssetsArtifactName()
        => _configuration["HostWorkerUpdates:AssetsArtifactName"] ?? "runnerrunner-hostworker-assets";

    private async Task<HttpRequestMessage> CreateGitHubRequestAsync(
        HttpMethod method,
        string url,
        string repository,
        CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.UserAgent.ParseAdd("RunnerRunner");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        var token = await ResolveGitHubTokenAsync(repository, ct);
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        return request;
    }

    private string GetUpdateRepository()
    {
        var repository = _configuration["HostWorkerUpdates:Repository"]?.Trim().Trim('/');
        return string.IsNullOrWhiteSpace(repository) ? "Redth/RunnerRunner" : repository;
    }

    private async Task<string?> ResolveGitHubTokenAsync(string repository, CancellationToken ct)
    {
        var configuredToken = _configuration["HostWorkerUpdates:GitHubToken"];
        if (!string.IsNullOrWhiteSpace(configuredToken))
            return configuredToken.Trim();

        var credential = await ResolveGitHubUpdateCredentialAsync(repository, ct);
        if (credential == null)
            return null;

        _logger.LogDebug(
            "Using GitHub credential {CredentialName} for HostWorker update repository {Repository}",
            credential.Name,
            repository);

        return await _gitHubAuth.GetAccessTokenAsync(credential, repository: repository, ct: ct);
    }

    private async Task<ProviderCredential?> ResolveGitHubUpdateCredentialAsync(string repository, CancellationToken ct)
    {
        var configuredCredentialId = _configuration["HostWorkerUpdates:ProviderCredentialId"];
        if (!string.IsNullOrWhiteSpace(configuredCredentialId))
        {
            var credential = await _store.Get<ProviderCredential>(configuredCredentialId.Trim());
            if (credential == null)
                throw new InvalidOperationException($"HostWorkerUpdates:ProviderCredentialId '{configuredCredentialId}' was not found.");

            if (credential.Provider != RunnerProvider.GitHubActions)
                throw new InvalidOperationException($"HostWorkerUpdates:ProviderCredentialId '{configuredCredentialId}' is not a GitHub Actions credential.");

            if (!GitHubAuthenticationService.HasGitHubApiCredentials(credential, repository: repository))
                throw new InvalidOperationException($"GitHub credential '{credential.Name}' cannot access HostWorker update repository '{repository}'.");

            return credential;
        }

        var credentials = (await _store.Query<ProviderCredential>().ToList())
            .Where(credential => credential.Provider == RunnerProvider.GitHubActions)
            .ToList();

        return credentials.FirstOrDefault(credential =>
                   CredentialTargetsRepository(credential, repository)
                   && GitHubAuthenticationService.HasGitHubApiCredentials(credential, repository: repository))
               ?? credentials.FirstOrDefault(credential =>
                   credential.GitHubAuthType == GitHubAuthType.PersonalAccessToken
                   && string.IsNullOrWhiteSpace(credential.GitHubOrg)
                   && string.IsNullOrWhiteSpace(credential.GitHubRepo)
                   && !string.IsNullOrWhiteSpace(credential.GitHubToken)
                   && GitHubAuthenticationService.HasGitHubApiCredentials(credential, repository: repository));
    }

    private static bool CredentialTargetsRepository(ProviderCredential credential, string repository)
    {
        var normalizedRepository = GitHubCredentialResolver.NormalizeRepository(repository);
        if (string.IsNullOrWhiteSpace(normalizedRepository))
            return false;

        if (GitHubCredentialResolver.ResolveTargetForRepository(credential, normalizedRepository) != null)
            return true;

        var target = GitHubCredentialResolver.ResolveDefaultTarget(credential);
        if (target == null)
            return false;

        if (!string.IsNullOrWhiteSpace(target.Repository))
            return string.Equals(target.Repository, normalizedRepository, StringComparison.OrdinalIgnoreCase);

        var owner = normalizedRepository.Split('/', 2)[0];
        return !string.IsNullOrWhiteSpace(target.Owner)
               && string.Equals(target.Owner, owner, StringComparison.OrdinalIgnoreCase);
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

    private sealed class GitHubCommitResponse
    {
        [JsonPropertyName("sha")]
        public string Sha { get; set; } = "";

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }
    }

    private sealed class GitHubWorkflowRunsResponse
    {
        [JsonPropertyName("workflow_runs")]
        public List<GitHubWorkflowRunResponse> WorkflowRuns { get; set; } = [];
    }

    private sealed class GitHubWorkflowRunResponse
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("conclusion")]
        public string? Conclusion { get; set; }

        [JsonPropertyName("created_at")]
        public DateTimeOffset? CreatedAt { get; set; }

        [JsonPropertyName("updated_at")]
        public DateTimeOffset? UpdatedAt { get; set; }
    }

    private sealed class GitHubActionsArtifactsResponse
    {
        [JsonPropertyName("artifacts")]
        public List<GitHubActionsArtifactResponse> Artifacts { get; set; } = [];
    }

    private sealed class GitHubActionsArtifactResponse
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("archive_download_url")]
        public string ArchiveDownloadUrl { get; set; } = "";

        [JsonPropertyName("expired")]
        public bool Expired { get; set; }
    }

    private sealed class ReleaseManifest
    {
        public string? Version { get; set; }
        public string? GitSha { get; set; }
        public Dictionary<string, List<string>> Images { get; set; } = new(StringComparer.OrdinalIgnoreCase);
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
    IReadOnlyList<HostWorkerReleaseAsset> Assets)
{
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Images { get; init; }
        = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
}

public sealed record HostWorkerReleaseAsset(string Name, string DownloadUrl, string Sha256);

public sealed record HostWorkerUpdateSelection(
    HostWorkerUpdateSourceKind Source,
    string? Version = null,
    bool Force = false,
    bool AllowNonUpgrade = false,
    string? PublicBaseUrl = null)
{
    public static HostWorkerUpdateSelection LatestRelease(bool force = false)
        => new(HostWorkerUpdateSourceKind.Release, Force: force);
}

public sealed record HostWorkerUpdateAvailability(
    HostWorkerReleaseInfo? Release,
    HostWorkerReleaseAsset? Asset,
    string? ContainerImage,
    bool UpdateAvailable,
    string? UnavailableReason)
{
    public bool IsAvailable => Release != null
                               && (Asset != null || !string.IsNullOrWhiteSpace(ContainerImage))
                               && string.IsNullOrWhiteSpace(UnavailableReason);

    public static HostWorkerUpdateAvailability Unavailable(string? reason)
        => new(null, null, null, false, reason ?? "No compatible update is available.");
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

    public static bool TrySelectContainerImage(
        Host host,
        HostWorkerReleaseInfo release,
        out string? image,
        out string? unavailableReason)
    {
        var imageKey = host.Platform == HostPlatform.Windows ? "hostworkerWindows" : "hostworker";
        if (!release.Images.TryGetValue(imageKey, out var images) || images.Count == 0)
        {
            image = null;
            unavailableReason = $"Release {release.Version} does not include a {imageKey} container image.";
            return false;
        }

        image = images.FirstOrDefault(x => x.EndsWith(":" + release.Version, StringComparison.OrdinalIgnoreCase))
                ?? images[0];
        unavailableReason = null;
        return true;
    }

    public static bool IsContainerized(Host host)
        => host.IsContainerized || host.Capabilities.Any(x => string.Equals(x, "container", StringComparison.OrdinalIgnoreCase));

    public static bool IsHostWorkerAssetName(string assetName)
    {
        var fileName = Path.GetFileName(assetName);
        if (!string.Equals(fileName, assetName, StringComparison.Ordinal))
            return false;

        return fileName is "runnerrunner-hostworker-linux-x64.tar.gz"
            or "runnerrunner-hostworker-linux-arm64.tar.gz"
            or "runnerrunner-hostworker-osx-x64.tar.gz"
            or "runnerrunner-hostworker-osx-arm64.tar.gz"
            or "runnerrunner-hostworker-win-x64.zip";
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

    public static string? GetRuntimeIdentifier(HostPlatform platform, string? architecture)
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
