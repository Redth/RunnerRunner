using System.Diagnostics;
using RunnerRunner.Agent.Backends;
using RunnerRunner.Core.Models;

namespace RunnerRunner.HostWorker.Services;

internal sealed class HostResourceUsageCollector
{
    private readonly HostWorkerIdentity _identity;
    private readonly ILogger<HostResourceUsageCollector> _logger;
    private readonly TartBackend _tartBackend;
    private readonly TimeSpan _timeout;
    private readonly string _diskProbePath;

    public HostResourceUsageCollector(
        IConfiguration configuration,
        HostWorkerIdentity identity,
        HostWorkerPaths paths,
        ILogger<HostResourceUsageCollector> logger,
        ILoggerFactory loggerFactory)
    {
        _identity = identity;
        _logger = logger;
        _tartBackend = new TartBackend(loggerFactory.CreateLogger<TartBackend>());
        _timeout = TimeSpan.FromSeconds(Math.Max(1, configuration.GetValue("HostWorker:ResourceUsageTimeoutSeconds", 5)));
        _diskProbePath = paths.DataRoot;
    }

    public async Task<HostResourceUsage?> CollectAsync(string reason, CancellationToken ct)
    {
        var usage = new HostResourceUsage();
        var collectedAnything = false;

        if (TryReadDiskSpace(out var freeBytes, out var totalBytes))
        {
            usage.FreeDiskBytes = freeBytes;
            usage.TotalDiskBytes = totalBytes;
            collectedAnything = true;
        }

        if (await TryCountRunningTartVmsAsync(reason, ct) is int runningTartVmCount)
        {
            usage.RunningTartVmCount = runningTartVmCount;
            collectedAnything = true;
        }

        return collectedAnything ? usage : null;
    }

    public bool TryReadDiskSpace(out long freeBytes, out long totalBytes)
    {
        freeBytes = 0;
        totalBytes = 0;

        try
        {
            // On Unix, DriveInfo resolves the filesystem containing the given path, so probe the
            // data root directly; "/" would report the system volume rather than the one holding
            // the working directories. Windows needs an actual drive root.
            var probe = OperatingSystem.IsWindows()
                ? Path.GetPathRoot(_diskProbePath) ?? _diskProbePath
                : _diskProbePath;

            var driveInfo = new DriveInfo(probe);
            if (!driveInfo.IsReady)
                return false;

            freeBytes = driveInfo.AvailableFreeSpace;
            totalBytes = driveInfo.TotalSize;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to read disk space for {Path}", _diskProbePath);
            return false;
        }
    }

    private async Task<int?> TryCountRunningTartVmsAsync(string reason, CancellationToken ct)
    {
        if (_identity.Platform != HostPlatform.MacOS
            || !ToolExists("tart", "/opt/homebrew/bin/tart", "/usr/local/bin/tart"))
            return null;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_timeout);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var runningTartVmCount = await _tartBackend.CountRunningVmsAsync(timeoutCts.Token);
            stopwatch.Stop();
            _logger.LogDebug(
                "Tart resource usage check for {Reason} completed in {ElapsedMilliseconds}ms: {RunningTartVmCount} running VM(s)",
                reason,
                stopwatch.ElapsedMilliseconds,
                runningTartVmCount);

            return runningTartVmCount;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            _logger.LogWarning(
                "Tart resource usage check for {Reason} timed out after {ElapsedMilliseconds}ms (limit {TimeoutSeconds:n1}s)",
                reason,
                stopwatch.ElapsedMilliseconds,
                _timeout.TotalSeconds);
            return null;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(
                ex,
                "Tart resource usage check for {Reason} failed after {ElapsedMilliseconds}ms",
                reason,
                stopwatch.ElapsedMilliseconds);
            return null;
        }
    }

    private static bool ToolExists(string command, params string[] preferredPaths)
    {
        foreach (var preferredPath in preferredPaths)
        {
            if (File.Exists(preferredPath))
                return true;
        }

        var envPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var pathPart in envPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(pathPart, command);
            if (File.Exists(candidate))
                return true;
        }

        return false;
    }
}
