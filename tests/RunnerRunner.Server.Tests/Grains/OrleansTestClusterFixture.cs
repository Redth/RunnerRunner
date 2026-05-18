using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Hosting;
using Orleans.TestingHost;
using RunnerRunner.Server.Services;
using RunnerRunner.Server.Tests.TestSupport;
using Shiny.DocumentDb;

namespace RunnerRunner.Server.Tests.Grains;

[CollectionDefinition(Name)]
public sealed class OrleansClusterCollection : ICollectionFixture<OrleansTestClusterFixture>
{
    public const string Name = "Orleans cluster";
}

public sealed class OrleansTestClusterFixture : IDisposable
{
    public OrleansTestClusterFixture()
    {
        TestServices = new ClusterTestServices();
        SiloConfigurator.TestServices = TestServices;

        var builder = new TestClusterBuilder();
        builder.Options.InitialSilosCount = 1;
        builder.AddSiloBuilderConfigurator<SiloConfigurator>();

        Cluster = builder.Build();
        Cluster.Deploy();
    }

    public TestCluster Cluster { get; }

    public IGrainFactory GrainFactory => Cluster.GrainFactory;

    public IDocumentStore DocumentStore => TestServices.DocumentStore;

    internal RecordingHostCommandDispatcher HostCommands => TestServices.HostCommands;

    private ClusterTestServices TestServices { get; }

    public void Dispose()
    {
        Cluster.StopAllSilos();
        SiloConfigurator.TestServices = null;
    }

    private sealed class SiloConfigurator : ISiloConfigurator
    {
        internal static ClusterTestServices? TestServices { get; set; }

        public void Configure(ISiloBuilder siloBuilder)
        {
            var testServices = TestServices ?? new ClusterTestServices();

            siloBuilder
                .AddMemoryGrainStorage("Default")
                .AddMemoryGrainStorage("PersistentStore")
                .AddMemoryGrainStorage("PubSubStore")
                .AddMemoryStreams("RunnerEvents")
                .UseInMemoryReminderService()
                .Configure<ReminderOptions>(options =>
                {
                    options.MinimumReminderPeriod = TimeSpan.FromSeconds(1);
                })
                .ConfigureServices(services =>
                {
                    services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
                    services.AddSingleton(testServices.DocumentStore);
                    services.AddSingleton(testServices.HostCommands);
                    services.AddSingleton<IHostCommandDispatcher>(sp => sp.GetRequiredService<RecordingHostCommandDispatcher>());
                    services.AddSingleton<RunnerRegistrationCleanupService>();
                });
        }
    }

    internal sealed class ClusterTestServices
    {
        public IDocumentStore DocumentStore { get; } = TestDocumentStore.Create();

        internal RecordingHostCommandDispatcher HostCommands { get; } = new();
    }
}
