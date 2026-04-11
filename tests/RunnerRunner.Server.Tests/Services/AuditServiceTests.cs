using Microsoft.Extensions.Logging;
using NSubstitute;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Services;

namespace RunnerRunner.Server.Tests.Services;

public class AuditServiceTests
{
    [Fact]
    public async Task LogAsync_InsertsEntry()
    {
        var store = TestDocumentStore.Create();
        var logger = Substitute.For<ILogger<AuditService>>();
        var service = new AuditService(store, logger);

        await service.LogAsync("Created", "RunnerProfile", Guid.NewGuid().ToString(), "Created profile 'test'");

        var entries = await service.GetRecentAsync();
        Assert.Single(entries);
        Assert.Equal("Created", entries[0].Action);
        Assert.Equal("RunnerProfile", entries[0].EntityType);
        Assert.Contains("test", entries[0].Details!);
    }

    [Fact]
    public async Task LogAsync_NullEntityId_Works()
    {
        var store = TestDocumentStore.Create();
        var logger = Substitute.For<ILogger<AuditService>>();
        var service = new AuditService(store, logger);

        await service.LogAsync("SystemStart", "System");

        var entries = await service.GetRecentAsync();
        Assert.Single(entries);
        Assert.Null(entries[0].EntityId);
    }

    [Fact]
    public async Task GetRecentAsync_ReturnsOrderedByTimestamp()
    {
        var store = TestDocumentStore.Create();
        var logger = Substitute.For<ILogger<AuditService>>();
        var service = new AuditService(store, logger);

        await service.LogAsync("First", "Test");
        await Task.Delay(50); // ensure different timestamps
        await service.LogAsync("Second", "Test");
        await Task.Delay(50);
        await service.LogAsync("Third", "Test");

        var entries = await service.GetRecentAsync();
        Assert.Equal(3, entries.Count);
        Assert.Equal("Third", entries[0].Action);
        Assert.Equal("Second", entries[1].Action);
        Assert.Equal("First", entries[2].Action);
    }

    [Fact]
    public async Task GetRecentAsync_RespectsLimit()
    {
        var store = TestDocumentStore.Create();
        var logger = Substitute.For<ILogger<AuditService>>();
        var service = new AuditService(store, logger);

        for (int i = 0; i < 10; i++)
            await service.LogAsync($"Action-{i}", "Test");

        var entries = await service.GetRecentAsync(3);
        Assert.Equal(3, entries.Count);
    }
}
