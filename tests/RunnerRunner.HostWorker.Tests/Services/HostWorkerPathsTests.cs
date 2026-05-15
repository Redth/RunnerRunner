using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RunnerRunner.HostWorker;
using RunnerRunner.HostWorker.Services;

namespace RunnerRunner.HostWorker.Tests.Services;

public class HostWorkerPathsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyDataRoot_UsesDefaultDataRoot(string? configuredDataRoot)
    {
        var defaultDataRoot = Path.Combine(Path.GetTempPath(), $"rr-hostworker-default-paths-{Guid.NewGuid():N}");
        try
        {
            var values = new Dictionary<string, string?>
            {
                ["HostWorker:DataRoot"] = configuredDataRoot
            };
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(values)
                .Build();

            var paths = new HostWorkerPaths(configuration, () => defaultDataRoot);

            Assert.Equal(defaultDataRoot, paths.DataRoot);
            Assert.Equal(Path.Combine(defaultDataRoot, "logs"), paths.LogRoot);
            Assert.True(Directory.Exists(paths.DataRoot));
            Assert.True(Directory.Exists(paths.LogRoot));
            Assert.True(Directory.Exists(Path.GetDirectoryName(paths.CommandJournalPath)));
        }
        finally
        {
            if (Directory.Exists(defaultDataRoot))
                Directory.Delete(defaultDataRoot, recursive: true);
        }
    }

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
    public void HostApplicationBuilder_LoadsDataRootFromEnvironmentVariables()
    {
        const string dataRootVariableName = "HostWorker__DataRoot";
        const string logRootVariableName = "HostWorker__LogRoot";

        var previousDataRoot = Environment.GetEnvironmentVariable(dataRootVariableName);
        var previousLogRoot = Environment.GetEnvironmentVariable(logRootVariableName);
        var dataRoot = Path.Combine(Path.GetTempPath(), $"rr-hostworker-env-paths-{Guid.NewGuid():N}");

        try
        {
            Environment.SetEnvironmentVariable(dataRootVariableName, dataRoot);
            Environment.SetEnvironmentVariable(logRootVariableName, null);

            var builder = Host.CreateApplicationBuilder([]);
            var paths = new HostWorkerPaths(
                builder.Configuration,
                () => throw new InvalidOperationException("HostWorker__DataRoot should come from environment variables."));

            Assert.Equal(dataRoot, paths.DataRoot);
            Assert.Equal(Path.Combine(dataRoot, "logs"), paths.LogRoot);
            Assert.True(Directory.Exists(paths.DataRoot));
        }
        finally
        {
            Environment.SetEnvironmentVariable(dataRootVariableName, previousDataRoot);
            Environment.SetEnvironmentVariable(logRootVariableName, previousLogRoot);

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

    [Fact]
    public void HostWorkerLoggingProvider_DoesNotCreateLoggerFactoryDependencyCycle()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), $"rr-hostworker-di-paths-{Guid.NewGuid():N}");
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["HostWorker:DataRoot"] = dataRoot
                })
                .Build();

            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddLogging();
            services.AddSingleton(_ => HostWorkerIdentityResolver.Resolve(configuration));
            services.AddSingleton<HostWorkerPaths>();
            services.AddSingleton<HostWorkerLocalLogStore>();
            services.AddSingleton<HostWorkerLogPublisher>();
            services.AddSingleton<ILoggerProvider, HostWorkerObservedLoggerProvider>();

            using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });

            _ = provider.GetRequiredService<ILoggerFactory>();
        }
        finally
        {
            if (Directory.Exists(dataRoot))
                Directory.Delete(dataRoot, recursive: true);
        }
    }
}
