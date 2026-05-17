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

    public HostResourceUsageCollector(
        IConfiguration configuration,
        HostWorkerIdentity identity,
        ILogger<HostResourceUsageCollector> logger,
        ILoggerFactory loggerFactory)
    {
        _identity = identity;
        _logger = logger;
        _tartBackend = new TartBackend(loggerFactory.CreateLogger<TartBackend>());
        _timeout = TimeSpan.FromSeconds(Math.Max(1, configuration.GetValue("HostWorker:ResourceUsageTimeoutSeconds", 5)));
    }

    public async Task<HostResourceUsage?> CollectAsync(string reason, CancellationToken ct)
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

            return new HostResourceUsage
            {
                RunningTartVmCount = runningTartVmCount
            };
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
