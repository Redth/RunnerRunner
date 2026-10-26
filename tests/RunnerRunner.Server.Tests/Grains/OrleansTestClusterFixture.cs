using NSubstitute;
using RunnerRunner.Server.Services.Admission;
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

    internal IResourceAdmission Admission => TestServices.Admission;
    internal FakeProviderHttpApi RegistrationApi => TestServices.RegistrationApi;

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
                    services.AddSingleton(testServices.Admission);
                    services.AddSingleton(testServices.HostCommands);
                    services.AddSingleton<IHostCommandDispatcher>(sp => sp.GetRequiredService<RecordingHostCommandDispatcher>());
                    services.AddSingleton<RunnerRegistrationCleanupService>();
                    services.AddSingleton<IHttpClientFactory>(testServices.RegistrationApi);
                    services.AddSingleton<GitHubAuthenticationService>();
                    services.AddSingleton<JitConfigService>();
                });
        }
    }

    internal sealed class ClusterTestServices
    {
        public IDocumentStore DocumentStore { get; } = TestDocumentStore.Create();
        internal FakeProviderHttpApi RegistrationApi { get; } = new FakeProviderHttpApi().RespondJson(_ => true, "{\"encoded_jit_config\":\"private-jit-test\"}");
        public IResourceAdmission Admission { get; } = Substitute.For<IResourceAdmission>();

        internal RecordingHostCommandDispatcher HostCommands { get; } = new();
    }
}
