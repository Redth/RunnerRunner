using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RunnerRunner.Core.HostWorkers;
using RunnerRunner.Core.Models;
using RunnerRunner.HostWorker.Services;
using RunnerRunner.HostWorker.Tests.TestSupport;

namespace RunnerRunner.HostWorker.Tests.Services;

public class HostWorkerLogPublisherTests
{
    [Fact]
    public async Task PublishProcessLog_ForwardsLogFrameToAttachedSink()
    {
        using var directory = HostWorkerTestDirectory.Create("log-publisher");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HostWorker:DataRoot"] = directory.Path
            })
            .Build();
        var identity = new HostWorkerIdentity("host-1", "linux-host", HostPlatform.Linux, "x64");
        var paths = new HostWorkerPaths(configuration);
        var publisher = new HostWorkerLogPublisher(identity, new HostWorkerLocalLogStore(paths));
        var sink = new FakeHostWorkerEventSink();
        publisher.AttachEventSink(sink);

        publisher.PublishProcessLog(
            "RunnerRunner.HostWorker.Tests.Component",
            LogLevel.Information,
            "hello worker",
            exception: null,
            tags: new Dictionary<string, string> { ["correlation"] = "test-1" });

        var message = await sink.WaitForKindAsync(HostWorkerMessageKinds.LogFrame);
        var frame = HostWorkerProtocol.DeserializePayload<HostWorkerLogFrame>(message);

        Assert.Equal("host-1", message.HostId);
        Assert.Equal(HostWorkerMessageKinds.LogFrame, message.Kind);
        Assert.Equal("worker.process", frame.StreamKind);
        Assert.Contains("hello worker", frame.Text);
        Assert.Equal("test-1", frame.Tags["correlation"]);
    }
}
