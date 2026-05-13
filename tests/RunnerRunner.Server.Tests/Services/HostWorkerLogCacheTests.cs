using Microsoft.Extensions.Configuration;
using RunnerRunner.Core.HostWorkers;
using RunnerRunner.Server.Services.HostWorkers;

namespace RunnerRunner.Server.Tests.Services;

public class HostWorkerLogCacheTests
{
    [Fact]
    public void GetTail_ReturnsBoundedFramesInOffsetOrder()
    {
        var cache = CreateCache(maxFramesPerStream: 3);

        cache.Ingest("host-1", Frame("stream-1", 0, "one\n"));
        cache.Ingest("host-1", Frame("stream-1", 4, "two\n"));
        cache.Ingest("host-1", Frame("stream-1", 8, "three\n"));
        cache.Ingest("host-1", Frame("stream-1", 14, "four\n"));

        var tail = cache.GetTail("host-1", "stream-1", maxFrames: 10);

        Assert.Collection(
            tail,
            frame => Assert.Equal("two\n", frame.Text),
            frame => Assert.Equal("three\n", frame.Text),
            frame => Assert.Equal("four\n", frame.Text));
    }

    [Fact]
    public void Ingest_IgnoresFramesOlderThanLatestOffset()
    {
        var cache = CreateCache(maxFramesPerStream: 10);

        cache.Ingest("host-1", Frame("stream-1", 10, "newer\n"));
        cache.Ingest("host-1", Frame("stream-1", 5, "older\n"));

        Assert.Equal("newer\n", cache.GetTextTail("host-1", "stream-1", maxFrames: 10));
    }

    private static HostWorkerLogCache CreateCache(int maxFramesPerStream)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HostWorker:LogCache:MaxFramesPerStream"] = maxFramesPerStream.ToString()
            })
            .Build();

        return new HostWorkerLogCache(configuration);
    }

    private static HostWorkerLogFrame Frame(string streamId, long offset, string text)
        => new()
        {
            StreamKind = "worker.command",
            StreamId = streamId,
            Offset = offset,
            Text = text,
            Timestamp = DateTimeOffset.UnixEpoch
        };
}
