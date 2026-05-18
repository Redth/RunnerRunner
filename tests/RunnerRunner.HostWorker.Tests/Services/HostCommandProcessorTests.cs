using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RunnerRunner.Agent.Services;
using RunnerRunner.Core.HostWorkers;
using RunnerRunner.Core.Hub;
using RunnerRunner.Core.Interfaces;
using RunnerRunner.Core.Models;
using RunnerRunner.HostWorker.Services;
using RunnerRunner.HostWorker.Tests.TestSupport;

namespace RunnerRunner.HostWorker.Tests.Services;

public class HostCommandProcessorTests
{
    [Fact]
    public async Task DeployRunner_StartSuccess_PublishesHealthStartedAndCompleted()
    {
        using var directory = HostWorkerTestDirectory.Create("command-deploy-success");
        var native = new FakeRunnerBackend
        {
            BackendType = ExecutionBackend.Native,
            StartHandler = _ => new RunnerInstanceInfo
            {
                InstanceHandle = "pid-123",
                RunnerName = "runner-1"
            }
        };
        using var processor = CreateProcessor(directory, out var sink, out _, nativeBackend: native);

        await processor.ProcessCommandAsync(CreateCommand("cmd-start", new HostCommandEnvelope
        {
            Kind = HostCommandKind.DeployRunner,
            DeployRunner = new DeployRunnerCommand
            {
                InstanceId = "inst-1",
                ProfileId = "profile-1",
                RunnerName = "runner-1",
                Backend = ExecutionBackend.Native,
                Provider = RunnerProvider.GitHubActions,
                Labels = ["linux", "self-hosted"]
            }
        }), CancellationToken.None);

        Assert.Equal(
            [
                HostWorkerMessageKinds.CommandAccepted,
                HostWorkerMessageKinds.RunnerHealth,
                HostWorkerMessageKinds.RunnerStarted,
                HostWorkerMessageKinds.CommandCompleted
            ],
            NonLogKinds(sink));
        Assert.Single(native.StartRequests);

        var started = SinglePayload<RunnerStartedEvent>(sink, HostWorkerMessageKinds.RunnerStarted);
        Assert.Equal("inst-1", started.InstanceId);
        Assert.Equal("pid-123", started.InstanceHandle);
        Assert.Equal(ExecutionBackend.Native, started.Backend);

        var journal = await File.ReadAllTextAsync(
            Path.Combine(directory.Path, "journals", "commands.jsonl"));
        Assert.Contains("\"status\":\"received\"", journal);
        Assert.Contains("\"status\":\"completed\"", journal);
    }

    [Fact]
    public async Task DeployRunner_BackendUnavailable_PublishesFailedStopWithoutStarting()
    {
        using var directory = HostWorkerTestDirectory.Create("command-backend-unavailable");
        var docker = new FakeRunnerBackend
        {
            BackendType = ExecutionBackend.Docker,
            IsAvailable = false
        };
        using var processor = CreateProcessor(directory, out var sink, out _, dockerBackend: docker);

        await processor.ProcessCommandAsync(CreateCommand("cmd-unavailable", new HostCommandEnvelope
        {
            Kind = HostCommandKind.DeployRunner,
            DeployRunner = new DeployRunnerCommand
            {
                InstanceId = "inst-unavailable",
                ProfileId = "profile-1",
                RunnerName = "runner-unavailable",
                Backend = ExecutionBackend.Docker,
                Provider = RunnerProvider.GitHubActions
            }
        }), CancellationToken.None);

        Assert.Empty(docker.StartRequests);
        Assert.Contains(HostWorkerMessageKinds.CommandCompleted, NonLogKinds(sink));
        Assert.DoesNotContain(HostWorkerMessageKinds.CommandRejected, NonLogKinds(sink));

        var stopped = SinglePayload<RunnerStoppedEvent>(sink, HostWorkerMessageKinds.RunnerStopped);
        Assert.Equal("failed", stopped.Reason);
        Assert.Contains("not available", stopped.ErrorMessage);
    }

    [Fact]
    public async Task DeployRunner_StartFailure_PublishesFailedStopAndRejectsCommand()
    {
        using var directory = HostWorkerTestDirectory.Create("command-deploy-failure");
        var native = new FakeRunnerBackend
        {
            BackendType = ExecutionBackend.Native,
            StartHandler = _ => throw new InvalidOperationException("simulated start failure")
        };
        using var processor = CreateProcessor(directory, out var sink, out var lifecycle, nativeBackend: native);

        await processor.ProcessCommandAsync(CreateCommand("cmd-start-fails", new HostCommandEnvelope
        {
            Kind = HostCommandKind.DeployRunner,
            DeployRunner = new DeployRunnerCommand
            {
                InstanceId = "inst-fails",
                ProfileId = "profile-1",
                RunnerName = "runner-fails",
                Backend = ExecutionBackend.Native,
                Provider = RunnerProvider.GitHubActions
            }
        }), CancellationToken.None);

        Assert.Empty(lifecycle.RunningInstances);
        Assert.DoesNotContain(HostWorkerMessageKinds.CommandCompleted, NonLogKinds(sink));

        var stopped = SinglePayload<RunnerStoppedEvent>(sink, HostWorkerMessageKinds.RunnerStopped);
        Assert.Equal("failed", stopped.Reason);
        Assert.Contains("simulated start failure", stopped.ErrorMessage);

        var rejected = SinglePayload<HostWorkerCommandStatus>(sink, HostWorkerMessageKinds.CommandRejected);
        Assert.False(rejected.Success);
        Assert.Contains("simulated start failure", rejected.Error);
    }

    [Fact]
    public async Task StopRunner_StopFailure_PublishesFailedStopAndRejectsCommand()
    {
        using var directory = HostWorkerTestDirectory.Create("command-stop-failure");
        var native = new FakeRunnerBackend
        {
            BackendType = ExecutionBackend.Native,
            StartHandler = _ => new RunnerInstanceInfo
            {
                InstanceHandle = "pid-456",
                RunnerName = "runner-stop"
            },
            StopAsyncHandler = (_, _) => throw new InvalidOperationException("simulated stop failure")
        };
        using var processor = CreateProcessor(directory, out var sink, out var lifecycle, nativeBackend: native);
        await lifecycle.StartRunnerAsync(new DeployRunnerCommand
        {
            InstanceId = "inst-stop",
            ProfileId = "profile-1",
            RunnerName = "runner-stop",
            Backend = ExecutionBackend.Native,
            Provider = RunnerProvider.GitHubActions
        }, native);

        await processor.ProcessCommandAsync(CreateCommand("cmd-stop-fails", new HostCommandEnvelope
        {
            Kind = HostCommandKind.StopRunner,
            StopRunner = new StopRunnerCommand
            {
                InstanceId = "inst-stop"
            }
        }), CancellationToken.None);

        Assert.Equal(["pid-456"], native.StopRequests);
        Assert.Empty(lifecycle.RunningInstances);

        var stopped = SinglePayload<RunnerStoppedEvent>(sink, HostWorkerMessageKinds.RunnerStopped);
        Assert.Equal("failed", stopped.Reason);
        Assert.Contains("Stop failed: simulated stop failure", stopped.ErrorMessage);

        var rejected = SinglePayload<HostWorkerCommandStatus>(sink, HostWorkerMessageKinds.CommandRejected);
        Assert.False(rejected.Success);
        Assert.Contains("simulated stop failure", rejected.Error);
    }

    [Fact]
    public async Task MalformedCommandEnvelope_PublishesRejectedStatus()
    {
        using var directory = HostWorkerTestDirectory.Create("command-malformed");
        using var processor = CreateProcessor(directory, out var sink, out _);

        await processor.ProcessCommandAsync(CreateCommand("cmd-malformed", new HostCommandEnvelope
        {
            Kind = HostCommandKind.DeployRunner
        }), CancellationToken.None);

        Assert.Contains(HostWorkerMessageKinds.CommandAccepted, NonLogKinds(sink));
        Assert.DoesNotContain(HostWorkerMessageKinds.CommandCompleted, NonLogKinds(sink));

        var rejected = SinglePayload<HostWorkerCommandStatus>(sink, HostWorkerMessageKinds.CommandRejected);
        Assert.Equal("cmd-malformed", rejected.CommandId);
        Assert.False(rejected.Success);
        Assert.Contains("Unsupported or malformed", rejected.Error);
    }

    private static HostCommandProcessor CreateProcessor(
        HostWorkerTestDirectory directory,
        out FakeHostWorkerEventSink sink,
        out RunnerLifecycleManager lifecycle,
        FakeRunnerBackend? dockerBackend = null,
        FakeRunnerBackend? tartBackend = null,
        FakeRunnerBackend? nativeBackend = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HostWorker:DataRoot"] = directory.Path,
                ["HostWorker:ResourceUsageTimeoutSeconds"] = "1"
            })
            .Build();
        var identity = new HostWorkerIdentity("host-1", "test-host", HostPlatform.Linux, "x64");
        var paths = new HostWorkerPaths(configuration);
        var logStore = new HostWorkerLocalLogStore(paths);
        lifecycle = new RunnerLifecycleManager(NullLogger<RunnerLifecycleManager>.Instance);
        var processor = new HostCommandProcessor(
            configuration,
            identity,
            lifecycle,
            new ImageManager(NullLogger<ImageManager>.Instance),
            paths,
            logStore,
            new HostWorkerSelfUpdater(configuration, identity, paths, NullLogger<HostWorkerSelfUpdater>.Instance),
            new HostResourceUsageCollector(
                configuration,
                identity,
                NullLogger<HostResourceUsageCollector>.Instance,
                NullLoggerFactory.Instance),
            NullLogger<HostCommandProcessor>.Instance,
            NullLoggerFactory.Instance,
            dockerBackend ?? new FakeRunnerBackend { BackendType = ExecutionBackend.Docker },
            tartBackend ?? new FakeRunnerBackend { BackendType = ExecutionBackend.Tart },
            nativeBackend ?? new FakeRunnerBackend { BackendType = ExecutionBackend.Native });

        sink = new FakeHostWorkerEventSink();
        processor.AttachEventSink(sink);
        return processor;
    }

    private static HostWorkerMessage CreateCommand(string commandId, HostCommandEnvelope envelope)
        => HostWorkerProtocol.CreateMessage(
            "host-1",
            HostWorkerMessageKinds.Command,
            envelope,
            commandId);

    private static string[] NonLogKinds(FakeHostWorkerEventSink sink)
        => sink.Published
            .Select(message => message.Kind)
            .Where(kind => kind != HostWorkerMessageKinds.LogFrame)
            .ToArray();

    private static TPayload SinglePayload<TPayload>(FakeHostWorkerEventSink sink, string kind)
        => HostWorkerProtocol.DeserializePayload<TPayload>(
            Assert.Single(sink.Published.Where(message => message.Kind == kind)));
}
