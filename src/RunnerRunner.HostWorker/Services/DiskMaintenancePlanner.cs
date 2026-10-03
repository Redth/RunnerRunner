namespace RunnerRunner.HostWorker.Services;

/// <summary>A single prunable entry directly beneath a configured cache root.</summary>
internal sealed record PruneEntry(string Path, DateTime LastWriteUtc, bool IsDirectory);

/// <summary>
/// Pure selection rules for host housekeeping, kept free of filesystem access so the
/// age policy can be unit tested without creating real caches.
/// </summary>
internal static class DiskMaintenancePlanner
{
    /// <summary>
    /// Returns the entries old enough to delete. Age is measured from the entry's effective
    /// last-write time, so anything an in-flight build is still touching is retained.
    /// </summary>
    public static IReadOnlyList<PruneEntry> SelectExpired(
        IEnumerable<PruneEntry> entries,
        DateTime nowUtc,
        TimeSpan minimumAge)
    {
        if (minimumAge <= TimeSpan.Zero)
            return [];

        return entries
            .Where(entry => nowUtc - entry.LastWriteUtc >= minimumAge)
            .OrderBy(entry => entry.LastWriteUtc)
            .ToList();
    }

    /// <summary>
    /// True when a host is below its free-space floor and should prune aggressively.
    /// </summary>
    public static bool IsBelowFloor(long freeBytes, double minimumFreeGb)
        => minimumFreeGb > 0 && freeBytes / (1024d * 1024d * 1024d) < minimumFreeGb;
}
