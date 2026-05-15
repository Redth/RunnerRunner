using Microsoft.Extensions.Configuration;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Services.Logs;

namespace RunnerRunner.Server.Tests.Services;

public class ObservedLogStoreTests
{
    [Fact]
    public void Query_AppliesRetentionAndTailInSequenceOrder()
    {
        var store = CreateStore(maxEntries: 3);

        store.Add(new ObservedLogEntry { Message = "one" });
        store.Add(new ObservedLogEntry { Message = "two" });
        store.Add(new ObservedLogEntry { Message = "three" });
        store.Add(new ObservedLogEntry { Message = "four" });

        var result = store.Query(new ObservedLogQuery { Tail = 10 });

        Assert.Collection(
            result,
            entry => Assert.Equal("two", entry.Message),
            entry => Assert.Equal("three", entry.Message),
            entry => Assert.Equal("four", entry.Message));
    }

    [Fact]
    public void Query_FiltersBySourceLevelCategoryAndText()
    {
        var store = CreateStore(maxEntries: 10);
        store.Add(new ObservedLogEntry
        {
            SourceType = ObservedLogSourceType.Server,
            Category = "RunnerRunner.Server.Services.OrchestrationEngine",
            Level = ObservedLogLevel.Information,
            Message = "reconciled runners"
        });
        store.Add(new ObservedLogEntry
        {
            SourceType = ObservedLogSourceType.Grain,
            SourceId = "server:grains",
            SourceName = "Orleans grains",
            Category = "RunnerRunner.Server.Grains.RunnerInstanceGrain",
            StreamKind = ObservedLogStreamKind.Grain,
            Level = ObservedLogLevel.Warning,
            Message = "runner activation slow"
        });

        var result = store.Query(new ObservedLogQuery
        {
            SourceType = ObservedLogSourceType.Grain,
            MinimumLevel = ObservedLogLevel.Warning,
            Category = "RunnerInstance",
            SearchText = "activation",
            Tail = 10
        });

        var entry = Assert.Single(result);
        Assert.Equal("runner activation slow", entry.Message);
    }

    private static ObservedLogStore CreateStore(int maxEntries)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logs:Recent:MaxEntries"] = maxEntries.ToString()
            })
            .Build();
        return new ObservedLogStore(configuration);
    }
}
