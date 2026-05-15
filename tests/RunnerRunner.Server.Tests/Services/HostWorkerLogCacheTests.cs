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

    [Fact]
    public void GetStreams_PreservesFrameMetadata()
    {
        var cache = CreateCache(maxFramesPerStream: 10);
        cache.Ingest("host-1", new HostWorkerLogFrame
        {
            StreamKind = "task.progress",
            StreamId = "task-1",
            TaskId = "task-1",
            Category = "ImagePull",
            Level = "Information",
            SourceType = "Task",
            SourceName = "Pull image",
            Offset = 0,
            Text = "Downloading 50%\n",
            Timestamp = DateTimeOffset.UnixEpoch,
            Tags = new Dictionary<string, string> { ["image"] = "ubuntu:latest" }
        });

        var stream = Assert.Single(cache.GetStreams());
        var frame = Assert.Single(stream.Frames);
        Assert.Equal("host-1", stream.HostId);
        Assert.Equal("task-1", frame.TaskId);
        Assert.Equal("ImagePull", frame.Category);
        Assert.Equal("Task", frame.SourceType);
        Assert.Equal("ubuntu:latest", frame.Tags["image"]);
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
