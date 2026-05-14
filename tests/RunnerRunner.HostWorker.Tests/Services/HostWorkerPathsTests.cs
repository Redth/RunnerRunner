using Microsoft.Extensions.Configuration;
using RunnerRunner.HostWorker.Services;

namespace RunnerRunner.HostWorker.Tests.Services;

public class HostWorkerPathsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyLogRoot_UsesDataRootLogsDirectory(string? configuredLogRoot)
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), $"rr-hostworker-paths-{Guid.NewGuid():N}");
        try
        {
            var values = new Dictionary<string, string?>
            {
                ["HostWorker:DataRoot"] = dataRoot,
                ["HostWorker:LogRoot"] = configuredLogRoot
            };
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(values)
                .Build();

            var paths = new HostWorkerPaths(configuration);

            Assert.Equal(Path.Combine(dataRoot, "logs"), paths.LogRoot);
            Assert.True(Directory.Exists(paths.DataRoot));
            Assert.True(Directory.Exists(paths.LogRoot));
            Assert.True(Directory.Exists(Path.GetDirectoryName(paths.CommandJournalPath)));
        }
        finally
        {
            if (Directory.Exists(dataRoot))
                Directory.Delete(dataRoot, recursive: true);
        }
    }

    [Fact]
    public void ConfiguredLogRoot_IsUsed()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), $"rr-hostworker-paths-{Guid.NewGuid():N}");
        var logRoot = Path.Combine(Path.GetTempPath(), $"rr-hostworker-logs-{Guid.NewGuid():N}");
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["HostWorker:DataRoot"] = dataRoot,
                    ["HostWorker:LogRoot"] = logRoot
                })
                .Build();

            var paths = new HostWorkerPaths(configuration);

            Assert.Equal(logRoot, paths.LogRoot);
            Assert.True(Directory.Exists(paths.LogRoot));
        }
        finally
        {
            if (Directory.Exists(dataRoot))
                Directory.Delete(dataRoot, recursive: true);

            if (Directory.Exists(logRoot))
                Directory.Delete(logRoot, recursive: true);
        }
    }
}
