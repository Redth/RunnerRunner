using Microsoft.Extensions.DependencyInjection;
using RunnerRunner.Server.Data;
using Shiny.DocumentDb;

namespace RunnerRunner.Server.Tests.Data;

public class ResilientDocumentStoreTests
{
    [Fact]
    public void AddRunnerRunnerDocumentStore_RegistersResilientDocumentStore()
    {
        var services = new ServiceCollection();

        services.AddRunnerRunnerDocumentStore("Host=localhost;Database=test;Username=test;Password=test");

        var descriptor = Assert.Single(services, service => service.ServiceType == typeof(IDocumentStore));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        Assert.Equal(typeof(ResilientDocumentStore), descriptor.ImplementationType);
    }

    [Fact]
    public void IsClosedConnectionError_MatchesNpgsqlClosedConnectionMessage()
    {
        var exception = new InvalidOperationException("Connection is not open");

        Assert.True(ResilientDocumentStore.IsClosedConnectionError(exception));
    }

    [Fact]
    public void IsClosedConnectionError_MatchesNestedClosedConnectionMessage()
    {
        var exception = new InvalidOperationException(
            "Document store query failed",
            new InvalidOperationException("Connection is not open"));

        Assert.True(ResilientDocumentStore.IsClosedConnectionError(exception));
    }

    [Fact]
    public void IsClosedConnectionError_DoesNotMatchUnrelatedInvalidOperation()
    {
        var exception = new InvalidOperationException("A document with this ID already exists.");

        Assert.False(ResilientDocumentStore.IsClosedConnectionError(exception));
    }
}
