using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RunnerRunner.Core.Models;
using RunnerRunner.HostWorker.Services;

namespace RunnerRunner.HostWorker.Tests.Services;

public class HostResourceUsageCollectorTests
{
    private static HostResourceUsageCollector CreateCollector(string dataRoot)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HostWorker:DataRoot"] = dataRoot
            })
            .Build();

        return new HostResourceUsageCollector(
            configuration,
            new HostWorkerIdentity("disk-host", "Disk Host", HostPlatform.Linux, "arm64"),
            new HostWorkerPaths(configuration),
            NullLogger<HostResourceUsageCollector>.Instance,
            NullLoggerFactory.Instance);
    }

    [Fact]
    public void TryReadDiskSpace_ReportsFreeSpaceForDataRoot()
    {
        // The data root is a plain directory, not a mount point. If DriveInfo cannot resolve it
        // the host silently stops reporting disk usage and the server-side gate never engages.
        var dataRoot = Path.Combine(Path.GetTempPath(), $"rr-disk-{Guid.NewGuid():N}");
        try
        {
            var collector = CreateCollector(dataRoot);

            Assert.True(collector.TryReadDiskSpace(out var freeBytes, out var totalBytes));
            Assert.True(freeBytes > 0, "expected a positive free byte count");
            Assert.True(totalBytes >= freeBytes, "total size should be at least the free space");
        }
        finally
        {
            if (Directory.Exists(dataRoot))
                Directory.Delete(dataRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CollectAsync_ReportsDiskUsageWithoutTart()
    {
        // Tart is absent in CI; disk telemetry must still flow (it previously returned null).
        var dataRoot = Path.Combine(Path.GetTempPath(), $"rr-disk-{Guid.NewGuid():N}");
        try
        {
            var collector = CreateCollector(dataRoot);

            var usage = await collector.CollectAsync("test", CancellationToken.None);

            Assert.NotNull(usage);
            Assert.NotNull(usage!.FreeDiskBytes);
            Assert.True(usage.FreeDiskBytes > 0);
        }
        finally
        {
            if (Directory.Exists(dataRoot))
                Directory.Delete(dataRoot, recursive: true);
        }
    }
}
