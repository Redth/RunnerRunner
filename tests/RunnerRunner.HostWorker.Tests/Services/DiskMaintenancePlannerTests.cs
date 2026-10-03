using RunnerRunner.HostWorker.Services;

namespace RunnerRunner.HostWorker.Tests.Services;

public class DiskMaintenancePlannerTests
{
    private static readonly DateTime Now = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

    private static PruneEntry EntryAgedDays(string path, double days)
        => new(path, Now.AddDays(-days), IsDirectory: true);

    [Fact]
    public void SelectExpired_KeepsEntriesYoungerThanThreshold()
    {
        var entries = new[] { EntryAgedDays("/caches/fresh", 1) };

        var expired = DiskMaintenancePlanner.SelectExpired(entries, Now, TimeSpan.FromDays(7));

        Assert.Empty(expired);
    }

    [Fact]
    public void SelectExpired_SelectsEntriesOlderThanThreshold()
    {
        var entries = new[]
        {
            EntryAgedDays("/caches/stale", 30),
            EntryAgedDays("/caches/fresh", 2)
        };

        var expired = DiskMaintenancePlanner.SelectExpired(entries, Now, TimeSpan.FromDays(7));

        Assert.Equal(["/caches/stale"], expired.Select(e => e.Path));
    }

    [Fact]
    public void SelectExpired_TreatsThresholdAsInclusive()
    {
        var entries = new[] { EntryAgedDays("/caches/exactly-seven", 7) };

        var expired = DiskMaintenancePlanner.SelectExpired(entries, Now, TimeSpan.FromDays(7));

        Assert.Single(expired);
    }

    [Fact]
    public void SelectExpired_OrdersOldestFirst()
    {
        var entries = new[]
        {
            EntryAgedDays("/caches/older", 30),
            EntryAgedDays("/caches/oldest", 90),
            EntryAgedDays("/caches/old", 8)
        };

        var expired = DiskMaintenancePlanner.SelectExpired(entries, Now, TimeSpan.FromDays(7));

        Assert.Equal(
            ["/caches/oldest", "/caches/older", "/caches/old"],
            expired.Select(e => e.Path));
    }

    [Fact]
    public void SelectExpired_WithNonPositiveAge_SelectsNothing()
    {
        // A misconfigured zero/negative age must never be read as "delete everything".
        var entries = new[] { EntryAgedDays("/caches/stale", 400) };

        Assert.Empty(DiskMaintenancePlanner.SelectExpired(entries, Now, TimeSpan.Zero));
        Assert.Empty(DiskMaintenancePlanner.SelectExpired(entries, Now, TimeSpan.FromDays(-1)));
    }

    [Fact]
    public void SelectExpired_IgnoresEntriesWithFutureTimestamps()
    {
        var entries = new[] { EntryAgedDays("/caches/clock-skew", -5) };

        Assert.Empty(DiskMaintenancePlanner.SelectExpired(entries, Now, TimeSpan.FromDays(7)));
    }

    [Theory]
    [InlineData(10, 30, true)]
    [InlineData(29.9, 30, true)]
    [InlineData(30, 30, false)]
    [InlineData(400, 30, false)]
    public void IsBelowFloor_ComparesFreeSpaceToThreshold(double freeGb, double minimumGb, bool expected)
    {
        var freeBytes = (long)(freeGb * 1024 * 1024 * 1024);

        Assert.Equal(expected, DiskMaintenancePlanner.IsBelowFloor(freeBytes, minimumGb));
    }

    [Fact]
    public void IsBelowFloor_WithThresholdDisabled_IsNeverBelow()
    {
        Assert.False(DiskMaintenancePlanner.IsBelowFloor(0, 0));
    }
}
