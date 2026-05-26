using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using Orleans;
using RunnerRunner.Core.Hub;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Grains.Interfaces;
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
    private readonly IGrainFactory _grainFactory;
    private readonly HostWorkerLocalUpdateStore _localUpdateStore;
    private readonly LongRunningTaskService _tasks;
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
        IGrainFactory grainFactory,
        HostWorkerLocalUpdateStore localUpdateStore,
        LongRunningTaskService tasks,
        ILogger<HostWorkerUpdateService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _gitHubAuth = gitHubAuth;
        _store = store;
        _dispatcher = dispatcher;
        _grainFactory = grainFactory;
        _localUpdateStore = localUpdateStore;
        _tasks = tasks;
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
                ? await IsUpdateAvailableAsync(host.AgentVersion, host.AgentCommitSha, release, selection.Source, ct)
                : true;
            return new HostWorkerUpdateAvailability(release, null, containerImage, containerUpdateAvailable, null);
        }

        if (!HostWorkerUpdateSelector.TrySelectAsset(host, release, out var asset, out var assetReason))
            return HostWorkerUpdateAvailability.Unavailable(assetReason);

        if (string.IsNullOrWhiteSpace(asset!.DownloadUrl))
            return HostWorkerUpdateAvailability.Unavailable("HostWorkerUpdates:PublicBaseUrl or a request base URL is required to queue this HostWorker update artifact.");

        var updateAvailable = selection.Source == HostWorkerUpdateSourceKind.Release
            ? await IsUpdateAvailableAsync(host.AgentVersion, host.AgentCommitSha, release, selection.Source, ct)
            : true;
        return new HostWorkerUpdateAvailability(release, asset, null, updateAvailable, null);
    }

    public async Task<bool> IsUpdateAvailableAsync(
        string? currentVersion,
        string? currentCommitSha,
        HostWorkerReleaseInfo release,
        HostWorkerUpdateSourceKind source = HostWorkerUpdateSourceKind.Release,
        CancellationToken ct = default)
    {
        if (source != HostWorkerUpdateSourceKind.Release)
            return true;

        var currentSha = HostWorkerUpdateSelector.ResolveCommitSha(currentCommitSha, currentVersion);
        var targetSha = HostWorkerUpdateSelector.NormalizeCommitSha(release.CommitSha);
        if (!string.IsNullOrWhiteSpace(currentSha) && !string.IsNullOrWhiteSpace(targetSha))
        {
            if (HostWorkerUpdateSelector.CommitShaEquals(currentSha, targetSha))
                return false;

            var comparison = await FetchCommitComparisonAsync(currentSha, targetSha, ct);
            if (comparison != null)
                return comparison.IsTargetNewerOrDifferent;

            return true;
        }

        return HostWorkerUpdateSelector.IsUpdateAvailable(currentVersion, release.Version);
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
        host.LatestAvailableCommitSha = availability.Release?.CommitSha;
        if (!availability.IsAvailable)
        {
            host.UpdateStatus = "Unavailable";
            host.UpdateMessage = availability.UnavailableReason;
        }
        else if (!availability.UpdateAvailable)
        {
            host.UpdateStatus = "Current";
            host.UpdateMessage = $"HostWorker is current at {HostWorkerUpdateSelector.FormatVersionWithCommit(host.AgentVersion, host.AgentCommitSha)}.";
        }
        else
        {
            host.UpdateStatus = "UpdateAvailable";
            host.UpdateMessage = $"HostWorker {HostWorkerUpdateSelector.FormatVersionWithCommit(availability.Release!.Version, availability.Release.CommitSha)} is available from {selection.Source.ToDisplayName()}.";
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

        var availability = await GetAvailabilityAsync(host, selection, forceRefresh: true, ct);
        if (!availability.IsAvailable || availability.Release == null)
            throw new InvalidOperationException(availability.UnavailableReason ?? "No HostWorker update asset is available for this host.");

        if (availability.Asset != null && string.IsNullOrWhiteSpace(availability.Asset.DownloadUrl))
            throw new InvalidOperationException("HostWorkerUpdates:PublicBaseUrl or a request base URL is required to queue this HostWorker update artifact.");

        if (availability.Asset == null && string.IsNullOrWhiteSpace(availability.ContainerImage))
            throw new InvalidOperationException("No HostWorker update asset or container image is available for this host.");

        if (!selection.Force && !selection.AllowNonUpgrade && !availability.UpdateAvailable)
            throw new InvalidOperationException($"HostWorker '{host.Label}' is already current.");

        if (!selection.Force)
        {
            var activeRunners = await GetActiveHostRunnersAsync(host.Id);
            if (activeRunners.Count > 0)
            {
                await QueueDrainedUpdateAsync(host, selection, availability, activeRunners, ct);
                return;
            }
        }

        await DispatchUpdateAsync(host, selection, availability, isPendingDrain: false, ct);
    }

    public async Task ProcessPendingDrainedUpdatesAsync(CancellationToken ct = default)
    {
        var hosts = (await _store.Query<Host>().ToList())
            .Where(host => host.IsDraining
                           && HasPendingUpdate(host)
                           && host.PendingHostWorkerUpdateDispatchedAt == null)
            .OrderBy(host => host.PendingHostWorkerUpdateQueuedAt ?? host.LastUpdateStartedAt ?? host.UpdatedAt)
            .ToList();

        foreach (var host in hosts)
        {
            ct.ThrowIfCancellationRequested();
            await ProcessPendingDrainedUpdateAsync(host.Id, ct);
        }
    }

    public async Task<bool> ProcessPendingDrainedUpdateAsync(string hostId, CancellationToken ct = default)
    {
        var host = await _store.Get<Host>(hostId);
        if (host == null || !host.IsDraining || !HasPendingUpdate(host) || host.PendingHostWorkerUpdateDispatchedAt != null)
            return false;

        if (host.AgentStatus != AgentStatus.Online)
        {
            host.UpdateStatus = "Draining";
            host.UpdateMessage = "Waiting for HostWorker to reconnect before applying drained update.";
            await _store.Update(host);
            _tasks.UpdateHostWorkerUpdate(
                host,
                host.UpdateStatus,
                host.UpdateMessage,
                host.LatestAvailableVersion,
                host.LatestAvailableCommitSha);
            return false;
        }

        var activeRunners = await GetActiveHostRunnersAsync(host.Id);
        if (activeRunners.Count > 0)
        {
            host.UpdateStatus = "Draining";
            host.UpdateMessage = $"Draining {activeRunners.Count} active runner(s) before updating HostWorker.";
            await _store.Update(host);
            _tasks.UpdateHostWorkerUpdate(
                host,
                host.UpdateStatus,
                host.UpdateMessage,
                host.LatestAvailableVersion,
                host.LatestAvailableCommitSha);
            await StopDrainableHostRunnersAsync(host.Id, activeRunners, ct);
            return false;
        }

        var selection = CreatePendingSelection(host);
        var availability = await GetAvailabilityAsync(host, selection, forceRefresh: true, ct);
        if (!availability.IsAvailable || availability.Release == null)
        {
            await FailPendingDrainedUpdateAsync(
                host,
                availability.UnavailableReason ?? "No HostWorker update asset is available for this host.",
                ct);
            return false;
        }

        if (availability.Asset != null && string.IsNullOrWhiteSpace(availability.Asset.DownloadUrl))
        {
            await FailPendingDrainedUpdateAsync(
                host,
                "HostWorkerUpdates:PublicBaseUrl or a request base URL is required to queue this HostWorker update artifact.",
                ct);
            return false;
        }

        if (availability.Asset == null && string.IsNullOrWhiteSpace(availability.ContainerImage))
        {
            await FailPendingDrainedUpdateAsync(host, "No HostWorker update asset or container image is available for this host.", ct);
            return false;
        }

        if (!selection.AllowNonUpgrade && !availability.UpdateAvailable)
        {
            host.UpdateStatus = "Current";
            host.UpdateMessage = $"HostWorker is current at {HostWorkerUpdateSelector.FormatVersionWithCommit(host.AgentVersion, host.AgentCommitSha)}.";
            host.LastUpdateCompletedAt = DateTime.UtcNow;
            await SetHostDrainingAsync(host, false);
            ClearPendingUpdate(host);
            await _store.Update(host);
            _tasks.MarkHostWorkerUpdateSucceeded(host, host.UpdateMessage);
            return false;
        }

        try
        {
            await DispatchUpdateAsync(host, selection, availability, isPendingDrain: true, ct);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Drained HostWorker update dispatch failed for host {HostId}", host.Id);
            host.PendingHostWorkerUpdateDispatchedAt = null;
            host.UpdateStatus = "Draining";
            host.UpdateMessage = $"Drained but failed to dispatch update: {ex.Message}";
            await _store.Update(host);
            _tasks.UpdateHostWorkerUpdate(
                host,
                host.UpdateStatus,
                host.UpdateMessage,
                host.LatestAvailableVersion,
                host.LatestAvailableCommitSha);
            return false;
        }
    }

    private async Task QueueDrainedUpdateAsync(
        Host host,
        HostWorkerUpdateSelection selection,
        HostWorkerUpdateAvailability availability,
        IReadOnlyCollection<RunnerInstance> activeRunners,
        CancellationToken ct)
    {
        await SetHostDrainingAsync(host, true);
        host.UpdateStatus = "Draining";
        host.UpdateMessage = $"Draining {activeRunners.Count} active runner(s) before updating to {HostWorkerUpdateSelector.FormatVersionWithCommit(availability.Release!.Version, availability.Release.CommitSha)} from {selection.Source.ToDisplayName()}.";
        host.LatestAvailableVersion = availability.Release.Version;
        host.LatestAvailableCommitSha = availability.Release.CommitSha;
        host.LastUpdateStartedAt = DateTime.UtcNow;
        host.PendingHostWorkerUpdateSource = selection.Source.ToSourceId();
        host.PendingHostWorkerUpdateVersion = selection.Version;
        host.PendingHostWorkerUpdateAllowNonUpgrade = selection.AllowNonUpgrade;
        host.PendingHostWorkerUpdatePublicBaseUrl = selection.PublicBaseUrl;
        host.PendingHostWorkerUpdateQueuedAt = DateTime.UtcNow;
        host.PendingHostWorkerUpdateDispatchedAt = null;
        await _store.Update(host);
        _tasks.TrackHostWorkerUpdate(
            host,
            availability.Release.Version,
            availability.Release.CommitSha,
            host.UpdateMessage,
            host.UpdateStatus);

        await StopDrainableHostRunnersAsync(host.Id, activeRunners, ct);
    }

    private async Task DispatchUpdateAsync(
        Host host,
        HostWorkerUpdateSelection selection,
        HostWorkerUpdateAvailability availability,
        bool isPendingDrain,
        CancellationToken ct)
    {
        host.UpdateStatus = "Queued";
        host.UpdateMessage = isPendingDrain
            ? $"Drained; queued update to {HostWorkerUpdateSelector.FormatVersionWithCommit(availability.Release!.Version, availability.Release.CommitSha)} from {selection.Source.ToDisplayName()}."
            : $"Queued update to {HostWorkerUpdateSelector.FormatVersionWithCommit(availability.Release!.Version, availability.Release.CommitSha)} from {selection.Source.ToDisplayName()}.";
        host.LatestAvailableVersion = availability.Release.Version;
        host.LatestAvailableCommitSha = availability.Release.CommitSha;
        host.LastUpdateStartedAt = DateTime.UtcNow;
        if (isPendingDrain)
            host.PendingHostWorkerUpdateDispatchedAt = DateTime.UtcNow;
        await _store.Update(host);
        _tasks.TrackHostWorkerUpdate(
            host,
            availability.Release.Version,
            availability.Release.CommitSha,
            host.UpdateMessage);

        try
        {
            await _dispatcher.DispatchApplyHostWorkerUpdateAsync(host.Id, CreateUpdateCommand(selection, availability));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (!isPendingDrain)
            {
                host.UpdateStatus = "Failed";
                host.UpdateMessage = $"Failed to dispatch update: {ex.Message}";
                host.LastUpdateCompletedAt = DateTime.UtcNow;
                await _store.Update(host);
                _tasks.MarkHostWorkerUpdateFailed(host, host.UpdateMessage);
            }

            throw;
        }
    }

    private static HostWorkerUpdateCommand CreateUpdateCommand(
        HostWorkerUpdateSelection selection,
        HostWorkerUpdateAvailability availability)
        => new()
        {
            TargetVersion = availability.Release!.Version,
            AssetName = availability.Asset?.Name ?? "",
            AssetUrl = availability.Asset?.DownloadUrl ?? "",
            Sha256 = availability.Asset?.Sha256 ?? "",
            ContainerImage = availability.ContainerImage,
            TargetCommitSha = availability.Release.CommitSha,
            Force = selection.Force
        };

    private async Task<IReadOnlyList<RunnerInstance>> GetActiveHostRunnersAsync(string hostId)
        => (await _store.Query<RunnerInstance>().ToList())
            .Where(instance => string.Equals(instance.HostId, hostId, StringComparison.OrdinalIgnoreCase)
                               && instance.Status is RunnerInstanceStatus.Pending
                                  or RunnerInstanceStatus.Starting
                                  or RunnerInstanceStatus.Running
                                  or RunnerInstanceStatus.Stopping)
            .ToList();

    private async Task StopDrainableHostRunnersAsync(
        string hostId,
        IReadOnlyCollection<RunnerInstance> activeRunners,
        CancellationToken ct)
    {
        foreach (var instance in activeRunners.Where(ShouldStopRunnerForDrain))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var runnerGrain = _grainFactory.GetGrain<IRunnerInstanceGrain>(instance.Id);
                await runnerGrain.MarkStopping();

                await _dispatcher.DispatchStopRunnerAsync(hostId, new StopRunnerCommand
                {
                    InstanceId = instance.Id,
                    InstanceHandle = instance.ContainerId ?? instance.VmName ?? instance.ProcessId?.ToString()
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to stop runner {RunnerInstanceId} while draining host {HostId}", instance.Id, hostId);
            }
        }
    }

    internal static bool ShouldStopRunnerForDrain(RunnerInstance instance)
    {
        if (instance.Status is not (RunnerInstanceStatus.Pending or RunnerInstanceStatus.Starting or RunnerInstanceStatus.Running))
            return false;

        if (string.Equals(instance.ProvisioningMode, "dynamic", StringComparison.OrdinalIgnoreCase)
            || string.Equals(instance.ProvisioningMode, "webhook", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(instance.JobId);
        }

        return true;
    }

    private static bool HasPendingUpdate(Host host)
        => !string.IsNullOrWhiteSpace(host.PendingHostWorkerUpdateSource);

    private static HostWorkerUpdateSelection CreatePendingSelection(Host host)
    {
        if (!HostWorkerUpdateSourceKinds.TryParse(host.PendingHostWorkerUpdateSource, out var source))
            throw new InvalidOperationException($"Unsupported pending HostWorker update source '{host.PendingHostWorkerUpdateSource}'.");

        return new HostWorkerUpdateSelection(
            source,
            host.PendingHostWorkerUpdateVersion,
            Force: false,
            AllowNonUpgrade: host.PendingHostWorkerUpdateAllowNonUpgrade,
            PublicBaseUrl: host.PendingHostWorkerUpdatePublicBaseUrl);
    }

    private async Task FailPendingDrainedUpdateAsync(Host host, string message, CancellationToken ct)
    {
        await SetHostDrainingAsync(host, false);
        ClearPendingUpdate(host);
        host.UpdateStatus = "Failed";
        host.UpdateMessage = message;
        host.LastUpdateCompletedAt = DateTime.UtcNow;
        await _store.Update(host);
        _tasks.MarkHostWorkerUpdateFailed(host, message);
    }

    private async Task SetHostDrainingAsync(Host host, bool isDraining)
    {
        host.IsDraining = isDraining;
        var hostGrain = _grainFactory.GetGrain<IHostGrain>(host.Id);
        await hostGrain.SetDraining(isDraining);
    }

    private static void ClearPendingUpdate(Host host)
    {
        host.IsDraining = false;
        host.PendingHostWorkerUpdateSource = null;
        host.PendingHostWorkerUpdateVersion = null;
        host.PendingHostWorkerUpdateAllowNonUpgrade = false;
        host.PendingHostWorkerUpdatePublicBaseUrl = null;
        host.PendingHostWorkerUpdateQueuedAt = null;
        host.PendingHostWorkerUpdateDispatchedAt = null;
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

        var publicBaseUrl = ResolveArtifactPublicBaseUrl(selection.PublicBaseUrl);
        var assets = version.Assets
            .Select(asset => new HostWorkerReleaseAsset(
                asset.AssetName,
                string.IsNullOrWhiteSpace(publicBaseUrl) ? "" : _localUpdateStore.BuildDownloadUrl(asset, publicBaseUrl),
                asset.Sha256))
            .ToArray();

        return new HostWorkerReleaseInfo(version.Version, null, version.CreatedAt, assets)
        {
            CommitSha = HostWorkerUpdateSelector.ExtractCommitSha(version.Version)
        };
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
            CommitSha = HostWorkerUpdateSelector.NormalizeCommitSha(manifest.GitSha),
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
            var publicBaseUrl = ResolveArtifactPublicBaseUrl(selection.PublicBaseUrl);
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
                CommitSha = HostWorkerUpdateSelector.NormalizeCommitSha(version),
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

    private async Task<GitHubCommitComparisonResponse?> FetchCommitComparisonAsync(
        string currentCommitSha,
        string targetCommitSha,
        CancellationToken ct)
    {
        var repository = GetUpdateRepository();
        var client = _httpClientFactory.CreateClient(nameof(HostWorkerUpdateService));
        var requestUrl =
            $"https://api.github.com/repos/{repository}/compare/{Uri.EscapeDataString(currentCommitSha)}...{Uri.EscapeDataString(targetCommitSha)}";
        using var request = await CreateGitHubRequestAsync(HttpMethod.Get, requestUrl, repository, ct);
        using var response = await client.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<GitHubCommitComparisonResponse>(stream, JsonOptions, ct);
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

    private string? ResolveArtifactPublicBaseUrl(string? requestBaseUrl)
    {
        var configured = _configuration["HostWorkerUpdates:PublicBaseUrl"];
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.Trim();

        return string.IsNullOrWhiteSpace(requestBaseUrl) ? null : requestBaseUrl.Trim();
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

    private sealed class GitHubCommitComparisonResponse
    {
        [JsonPropertyName("status")]
        public string Status { get; set; } = "";

        [JsonPropertyName("ahead_by")]
        public int AheadBy { get; set; }

        public bool IsTargetNewerOrDifferent
            => Status switch
            {
                "identical" => false,
                "behind" => false,
                "ahead" => AheadBy > 0,
                "diverged" => true,
                _ => AheadBy > 0
            };
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
    public string? CommitSha { get; init; }

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
    private static readonly Regex CommitShaPattern = new(
        @"(?<![0-9a-f])([0-9a-f]{7,64})(?![0-9a-f])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
        return TrySelectImage(imageKey, release, out image, out unavailableReason);
    }

    public static bool TrySelectImage(
        string imageKey,
        HostWorkerReleaseInfo release,
        out string? image,
        out string? unavailableReason)
    {
        if (!release.Images.TryGetValue(imageKey, out var images) || images.Count == 0)
        {
            image = null;
            unavailableReason = $"Release {release.Version} does not include a {imageKey} image.";
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

    public static bool IsUpdateAvailable(
        string? currentVersion,
        string? currentCommitSha,
        string? latestVersion,
        string? latestCommitSha)
    {
        var currentSha = ResolveCommitSha(currentCommitSha, currentVersion);
        var targetSha = ResolveCommitSha(latestCommitSha, latestVersion);
        if (!string.IsNullOrWhiteSpace(currentSha) && !string.IsNullOrWhiteSpace(targetSha))
            return !CommitShaEquals(currentSha, targetSha);

        if (string.IsNullOrWhiteSpace(latestVersion))
            return false;

        return IsUpdateAvailable(currentVersion, latestVersion);
    }

    public static bool IsUpdateAvailable(string? currentVersion, string? currentCommitSha, HostWorkerReleaseInfo release)
        => IsUpdateAvailable(currentVersion, currentCommitSha, release.Version, release.CommitSha);

    public static string? ResolveCommitSha(params string?[] values)
    {
        foreach (var value in values)
        {
            var commitSha = ExtractCommitSha(value);
            if (!string.IsNullOrWhiteSpace(commitSha))
                return commitSha;
        }

        return null;
    }

    public static string? ExtractCommitSha(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        foreach (Match match in CommitShaPattern.Matches(value.Trim()))
        {
            var candidate = NormalizeCommitSha(match.Value);
            if (!string.IsNullOrWhiteSpace(candidate))
                return candidate;
        }

        return null;
    }

    public static string? NormalizeCommitSha(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = value.Trim().Trim('\'').ToLowerInvariant();
        return LooksLikeCommitSha(normalized) ? normalized : null;
    }

    public static bool CommitShaEquals(string? left, string? right)
    {
        var normalizedLeft = NormalizeCommitSha(left);
        var normalizedRight = NormalizeCommitSha(right);
        if (string.IsNullOrWhiteSpace(normalizedLeft) || string.IsNullOrWhiteSpace(normalizedRight))
            return false;

        return normalizedLeft.Length <= normalizedRight.Length
            ? normalizedRight.StartsWith(normalizedLeft, StringComparison.OrdinalIgnoreCase)
            : normalizedLeft.StartsWith(normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

    public static string FormatVersionWithCommit(string? version, string? commitSha)
    {
        var formattedVersion = FormatDisplayVersion(version);
        var formattedCommit = FormatCommitSha(commitSha);
        if (string.IsNullOrWhiteSpace(formattedCommit))
            return formattedVersion;

        if (string.Equals(formattedVersion, "unknown", StringComparison.OrdinalIgnoreCase))
            return formattedCommit;

        return ExtractCommitSha(formattedVersion) != null
            ? formattedVersion
            : $"{formattedVersion}+{formattedCommit}";
    }

    public static string FormatDisplayVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return "unknown";

        return ShortenLongText(ShortenCommitHashes(version.Trim().Trim('\'')));
    }

    public static string? FormatCommitSha(string? commitSha)
    {
        var normalized = NormalizeCommitSha(commitSha);
        return normalized == null ? null : normalized[..Math.Min(8, normalized.Length)];
    }

    public static string ShortenKnownVersions(string? message, params string?[] versions)
    {
        var result = ShortenCommitHashes(message ?? "");
        foreach (var version in versions)
        {
            if (string.IsNullOrWhiteSpace(version))
                continue;

            var shortened = FormatDisplayVersion(version);
            if (!string.Equals(version, shortened, StringComparison.Ordinal))
                result = result.Replace(version, shortened, StringComparison.Ordinal);
        }

        return result;
    }

    public static string ShortenCommitHashes(string value)
        => CommitShaPattern.Replace(value, match =>
        {
            var normalized = NormalizeCommitSha(match.Value);
            return normalized == null ? match.Value : normalized[..Math.Min(8, normalized.Length)];
        });

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

    private static bool LooksLikeCommitSha(string value)
        => value.Length is >= 7 and <= 64
           && value.Any(static ch => ch is >= 'a' and <= 'f')
           && value.All(Uri.IsHexDigit);

    private static string ShortenLongText(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length > 32
            ? $"{trimmed[..12]}...{trimmed[^7..]}"
            : trimmed;
    }
}
