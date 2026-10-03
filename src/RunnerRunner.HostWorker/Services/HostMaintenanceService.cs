using RunnerRunner.Agent.Services;

namespace RunnerRunner.HostWorker.Services;

/// <summary>
/// Periodically reclaims disk space taken by CI build caches (Xcode archives, DerivedData,
/// engine toolchain stages). Without this a long-lived build host eventually fails jobs with
/// "No space left on device" partway through a run.
/// </summary>
internal sealed class HostMaintenanceService : BackgroundService
{
    private static readonly string[] DefaultMacOsCacheRoots =
    [
        "~/Library/Developer/Xcode/Archives",
        "~/Library/Developer/Xcode/DerivedData",
        "~/Library/Caches/godot",
        "~/Library/Caches/Godot",
        "~/Library/Caches/org.godotengine.godot",
    ];

    private readonly HostResourceUsageCollector _resourceUsage;
    private readonly RunnerLifecycleManager _lifecycleManager;
    private readonly ILogger<HostMaintenanceService> _logger;

    private readonly bool _enabled;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _lowDiskRetryInterval;
    private readonly TimeSpan _routineMaxAge;
    private readonly TimeSpan _lowDiskMaxAge;
    private readonly double _minimumFreeDiskGb;
    private readonly int _maxScanDepth;
    private readonly string[] _cacheRoots;

    public HostMaintenanceService(
        IConfiguration configuration,
        HostResourceUsageCollector resourceUsage,
        RunnerLifecycleManager lifecycleManager,
        ILogger<HostMaintenanceService> logger)
    {
        _resourceUsage = resourceUsage;
        _lifecycleManager = lifecycleManager;
        _logger = logger;

        _enabled = configuration.GetValue("HostWorker:Maintenance:Enabled", true);
        _interval = TimeSpan.FromHours(Math.Max(0.25, configuration.GetValue("HostWorker:Maintenance:IntervalHours", 6d)));
        _routineMaxAge = TimeSpan.FromDays(Math.Max(1, configuration.GetValue("HostWorker:Maintenance:MaxAgeDays", 7d)));
        _lowDiskMaxAge = TimeSpan.FromDays(Math.Max(0.5, configuration.GetValue("HostWorker:Maintenance:LowDiskMaxAgeDays", 1d)));
        _minimumFreeDiskGb = Math.Max(0, configuration.GetValue("HostWorker:Maintenance:MinimumFreeDiskGb", 30d));
        _lowDiskRetryInterval = TimeSpan.FromMinutes(Math.Max(
            1,
            configuration.GetValue("HostWorker:Maintenance:LowDiskRetryMinutes", 15d)));
        _maxScanDepth = Math.Max(1, configuration.GetValue("HostWorker:Maintenance:MaxScanDepth", 4));

        var configured = configuration.GetSection("HostWorker:Maintenance:CacheRoots").Get<string[]>();
        _cacheRoots = configured is { Length: > 0 } ? configured : DefaultMacOsCacheRoots;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("Host maintenance is disabled by configuration");
            return;
        }

        // Let the worker finish connecting before touching the filesystem.
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(_interval);
        do
        {
            var deferred = false;
            try
            {
                deferred = RunSweep();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Host maintenance sweep failed");
            }

            // The server stops dispatching to a host below the floor, and heartbeats keep that
            // low reading fresh, so waiting a full interval would strand the host for hours while
            // queued jobs march toward their pending timeout. Re-check soon instead.
            if (deferred && !await SafeDelayAsync(_lowDiskRetryInterval, stoppingToken))
                return;
        }
        while (await SafeWaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task<bool> SafeDelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Runs one sweep. Returns true when an aggressive prune was deferred and should be retried soon.</summary>
    private bool RunSweep()
    {
        var hasDisk = _resourceUsage.TryReadDiskSpace(out var freeBefore, out var totalBytes);

        var reclaimed = PruneExpired(_routineMaxAge, "routine");

        // Re-read rather than adjusting the earlier reading: other processes change free
        // space too, and the reclaimed total is only an estimate of what the delete freed.
        var stillLow = hasDisk
            && _resourceUsage.TryReadDiskSpace(out var freeAfterRoutine, out _)
            && DiskMaintenancePlanner.IsBelowFloor(freeAfterRoutine, _minimumFreeDiskGb);

        var deferred = false;

        if (stillLow)
        {
            // Still short on space after the routine pass. Only escalate while the host is idle:
            // a shorter age threshold could otherwise race a build that is between writes.
            var runningInstances = _lifecycleManager.RunningInstances.Count;
            if (runningInstances > 0)
            {
                deferred = true;
                _logger.LogWarning(
                    "Host is below the {MinimumFreeDiskGb:n0} GB free-disk floor but {RunningInstances} runner(s) are active; deferring the aggressive prune for {RetryMinutes:n0} minute(s)",
                    _minimumFreeDiskGb,
                    runningInstances,
                    _lowDiskRetryInterval.TotalMinutes);
            }
            else
            {
                _logger.LogWarning(
                    "Host is below the {MinimumFreeDiskGb:n0} GB free-disk floor; pruning caches older than {MaxAgeDays:n1} day(s)",
                    _minimumFreeDiskGb,
                    _lowDiskMaxAge.TotalDays);
                reclaimed += PruneExpired(_lowDiskMaxAge, "low-disk");
            }
        }

        if (!hasDisk || reclaimed <= 0)
            return deferred;

        _resourceUsage.TryReadDiskSpace(out var freeAfter, out _);
        _logger.LogInformation(
            "Host maintenance reclaimed {ReclaimedGb:n1} GB; free space {FreeGb:n1} GB of {TotalGb:n1} GB (was {FreeBeforeGb:n1} GB)",
            reclaimed / (1024d * 1024d * 1024d),
            freeAfter / (1024d * 1024d * 1024d),
            totalBytes / (1024d * 1024d * 1024d),
            freeBefore / (1024d * 1024d * 1024d));

        return deferred;
    }

    private long PruneExpired(TimeSpan maxAge, string pass)
    {
        var nowUtc = DateTime.UtcNow;
        var reclaimed = 0L;

        foreach (var root in _cacheRoots.Select(ExpandHome).Where(Directory.Exists))
        {
            var entries = EnumerateEntries(root, nowUtc - maxAge);
            foreach (var entry in DiskMaintenancePlanner.SelectExpired(entries, nowUtc, maxAge))
                reclaimed += TryDelete(entry, pass, maxAge);
        }

        return reclaimed;
    }

    private IReadOnlyList<PruneEntry> EnumerateEntries(string root, DateTime cutoffUtc)
    {
        try
        {
            return new DirectoryInfo(root)
                .EnumerateFileSystemInfos()
                .Select(info => new PruneEntry(
                    info.FullName,
                    EffectiveLastWriteUtc(info, cutoffUtc, _maxScanDepth),
                    info is DirectoryInfo))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to enumerate cache root {Root}", root);
            return [];
        }
    }

    /// <summary>
    /// A directory's own timestamp only moves when its immediate children change, so a build
    /// writing deep inside one (Xcode reuses <c>DerivedData/&lt;Project&gt;/Build/Intermediates.noindex/...</c>)
    /// can leave the top level looking untouched for weeks. Walk down a bounded number of levels
    /// looking for anything newer than the cutoff, returning as soon as one is found: the caller
    /// only needs to know whether the entry is older than the cutoff, not its exact newest mtime,
    /// so this stays cheap even over very large trees.
    /// </summary>
    public static DateTime EffectiveLastWriteUtc(FileSystemInfo info, DateTime cutoffUtc, int maxDepth)
    {
        var newest = info.LastWriteTimeUtc;

        if (newest > cutoffUtc || info is not DirectoryInfo directory || maxDepth <= 0)
            return newest;

        try
        {
            foreach (var child in directory.EnumerateFileSystemInfos())
            {
                var childNewest = EffectiveLastWriteUtc(child, cutoffUtc, maxDepth - 1);
                if (childNewest > newest)
                    newest = childNewest;

                if (newest > cutoffUtc)
                    break;
            }
        }
        catch (Exception)
        {
            // An unreadable child must never make an entry look older than it is.
            return DateTime.UtcNow;
        }

        return newest;
    }

    private long TryDelete(PruneEntry entry, string pass, TimeSpan maxAge)
    {
        try
        {
            var size = MeasureSize(entry);

            if (entry.IsDirectory)
                Directory.Delete(entry.Path, recursive: true);
            else
                File.Delete(entry.Path);

            _logger.LogInformation(
                "Pruned {Path} ({SizeGb:n2} GB, {AgeDays:n1} days old, {Pass} pass, threshold {MaxAgeDays:n1} days)",
                entry.Path,
                size / (1024d * 1024d * 1024d),
                (DateTime.UtcNow - entry.LastWriteUtc).TotalDays,
                pass,
                maxAge.TotalDays);

            return size;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to prune {Path}", entry.Path);
            return 0;
        }
    }

    private static long MeasureSize(PruneEntry entry)
    {
        try
        {
            if (!entry.IsDirectory)
                return new FileInfo(entry.Path).Length;

            return new DirectoryInfo(entry.Path)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(file => file.Length);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static string ExpandHome(string path)
    {
        if (!path.StartsWith('~'))
            return path;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, path.TrimStart('~').TrimStart('/', '\\'));
    }
}
