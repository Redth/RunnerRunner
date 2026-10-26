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
    public async Task DeployRunner_StartFailure_PreservesUncertainClaimAndRejectsCommand()
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

        Assert.DoesNotContain(HostWorkerMessageKinds.RunnerStopped, NonLogKinds(sink));
        var health = sink.Published
            .Where(m => m.Kind == HostWorkerMessageKinds.RunnerHealth)
            .Select(HostWorkerProtocol.DeserializePayload<RunnerHealthUpdateEvent>).Last();
        Assert.Equal(RunnerInstanceStatus.Stopping, health.Status);
        Assert.Contains("simulated start failure", health.StatusMessage);

        var rejected = SinglePayload<HostWorkerCommandStatus>(sink, HostWorkerMessageKinds.CommandRejected);
        Assert.False(rejected.Success);
        Assert.Contains("simulated start failure", rejected.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeployRunner_RedeliveryAfterWorkerRestart_NeverStartsAnotherEnvironment(bool firstStartFails)
    {
        using var directory = HostWorkerTestDirectory.Create("durable-deploy");
        var original = new FakeRunnerBackend
        {
            StartHandler = _ => firstStartFails
                ? throw new IOException("uncertain backend result")
                : new RunnerInstanceInfo { InstanceHandle = "h1", RunnerName = "r1" }
        };
        var envelope = new HostCommandEnvelope
        {
            Kind = HostCommandKind.DeployRunner,
            DeployRunner = new DeployRunnerCommand
            {
                InstanceId = "persistent-i1", ProfileId = "p1", RunnerName = "r1", Backend = ExecutionBackend.Native,
                JitConfig = "private-jit-fixture", RegistrationToken = "private-token-fixture",
                EnvironmentVariables = new() { ["SECRET"] = "private-env-fixture" }
            }
        };
        using (var first = CreateProcessor(directory, out _, out _, nativeBackend: original))
            await first.ProcessCommandAsync(CreateCommand("first-command", envelope), CancellationToken.None);

        var replacement = new FakeRunnerBackend();
        using var second = CreateProcessor(directory, out var sink, out _, nativeBackend: replacement);
        await second.ProcessCommandAsync(CreateCommand("different-command-id", envelope), CancellationToken.None);
        Assert.Single(original.StartRequests);
        Assert.Empty(replacement.StartRequests);
        Assert.DoesNotContain(HostWorkerMessageKinds.RunnerStopped, NonLogKinds(sink));
        Assert.Contains(HostWorkerMessageKinds.CommandRejected, NonLogKinds(sink));
        var intent = await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(Path.Combine(directory.Path, "deployment-intents"), "*.json")));
        Assert.DoesNotContain("private-jit-fixture", intent);
        Assert.DoesNotContain("private-token-fixture", intent);
        Assert.DoesNotContain("private-env-fixture", intent);

        if (firstStartFails)
        {
            await second.ProcessCommandAsync(CreateCommand("stop-unreconciled", new HostCommandEnvelope
            {
                Kind = HostCommandKind.StopRunner,
                StopRunner = new StopRunnerCommand { InstanceId = "persistent-i1" }
            }), CancellationToken.None);
            Assert.DoesNotContain(HostWorkerMessageKinds.RunnerStopped, NonLogKinds(sink));
            Assert.Empty(replacement.StopRequests);
        }
    }

    [Fact]
    public async Task StopRunner_StopFailure_RetainsCapacityAndRejectsCommand()
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
        CreateIntents(directory).Begin(new DeployRunnerCommand { InstanceId = "inst-stop", RunnerName = "runner-stop", Backend = ExecutionBackend.Native });
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
        Assert.Single(lifecycle.RunningInstances);
        Assert.DoesNotContain(HostWorkerMessageKinds.RunnerStopped, NonLogKinds(sink));
        var health = SinglePayload<RunnerHealthUpdateEvent>(sink, HostWorkerMessageKinds.RunnerHealth);
        Assert.Equal(RunnerInstanceStatus.Stopping, health.Status);
        Assert.Contains("Stop failed: simulated stop failure", health.StatusMessage);

        var rejected = SinglePayload<HostWorkerCommandStatus>(sink, HostWorkerMessageKinds.CommandRejected);
        Assert.False(rejected.Success);
        Assert.Contains("simulated stop failure", rejected.Error);
    }

    [Theory]
    [InlineData("health_check_failed", false)]
    [InlineData("stopped", false)]
    [InlineData("exited:0", false)]
    [InlineData("exited:0", true)]
    public async Task HealthPublication_RequiresConfirmedCleanupBeforeReleasingCapacity(string status, bool cleanupConfirmed)
    {
        using var directory = HostWorkerTestDirectory.Create("uncertain-health");
        using var processor = CreateProcessor(directory, out var sink, out _);
        CreateIntents(directory).Begin(new DeployRunnerCommand { InstanceId = "i1", RunnerName = "r1" });
        await processor.PublishRunnerHealthAsync(new ManagedRunnerHealth
        {
            Runner = new ManagedRunner { InstanceId = "i1", ProfileId = "p1", RunnerName = "r1", InstanceHandle = "h1", Backend = new FakeRunnerBackend(), IsEphemeral = true },
            Health = new RunnerHealthStatus { IsRunning = false, Status = status },
            CleanupConfirmed = cleanupConfirmed
        }, CancellationToken.None);

        if (cleanupConfirmed)
        {
            var stopped = SinglePayload<RunnerStoppedEvent>(sink, HostWorkerMessageKinds.RunnerStopped);
            Assert.Equal("completed", stopped.Reason);
        }
        else
        {
            Assert.DoesNotContain(HostWorkerMessageKinds.RunnerStopped, NonLogKinds(sink));
            Assert.Equal(RunnerInstanceStatus.Stopping, SinglePayload<RunnerHealthUpdateEvent>(sink, HostWorkerMessageKinds.RunnerHealth).Status);
        }
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

    [Fact]
    public async Task GetRunnerLogs_ReturnsSnapshotWithoutRepublishingItAsLogFrame()
    {
        using var directory = HostWorkerTestDirectory.Create("command-runner-logs");
        var runnerBasePath = Path.Combine(directory.Path, "runners");
        var instanceDir = Path.Combine(runnerBasePath, "instances", "rr-native");
        Directory.CreateDirectory(instanceDir);
        await File.WriteAllTextAsync(Path.Combine(instanceDir, "rr-instance.json"), """{"InstanceId":"inst-1","RunnerName":"runner-1"}""");
        await File.WriteAllTextAsync(Path.Combine(instanceDir, "runner.log"), "native runner failed\n");
        // Fake backends don't implement native log lookup (the production type-check
        // _nativeBackend is NativeBackend won't match), so the fallback message is expected.
        using var processor = CreateProcessor(directory, out var sink, out _, runnerBasePath: runnerBasePath);

        await processor.ProcessCommandAsync(CreateCommand("cmd-runner-logs", new HostCommandEnvelope
        {
            Kind = HostCommandKind.GetRunnerLogs,
            GetRunnerLogs = new GetRunnerLogsCommand
            {
                InstanceHandle = "pid-123",
                RunnerInstanceId = "inst-1",
                TailLines = 25
            }
        }), CancellationToken.None);

        var frames = sink.Published
            .Where(message => message.Kind == HostWorkerMessageKinds.LogFrame)
            .Select(HostWorkerProtocol.DeserializePayload<HostWorkerLogFrame>)
            .ToArray();
        Assert.DoesNotContain(frames, frame => frame.StreamKind == "runner.output");

        var logs = SinglePayload<RunnerLogsEvent>(sink, HostWorkerMessageKinds.RunnerLogs);
        Assert.Equal("pid-123", logs.InstanceHandle);
        Assert.Equal("inst-1", logs.RunnerInstanceId);
        Assert.Equal("(No logs available for this runner instance)", logs.Logs);
    }

    [Fact]
    public async Task GetHostLogs_ReturnsSnapshotWithoutRepublishingItAsLogFrame()
    {
        using var directory = HostWorkerTestDirectory.Create("command-host-logs");
        using var processor = CreateProcessor(directory, out var sink, out _);

        await processor.ProcessCommandAsync(CreateCommand("cmd-host-logs", new HostCommandEnvelope
        {
            Kind = HostCommandKind.GetHostLogs,
            GetHostLogs = new GetHostLogsCommand
            {
                TailLines = 25
            }
        }), CancellationToken.None);

        var frames = sink.Published
            .Where(message => message.Kind == HostWorkerMessageKinds.LogFrame)
            .Select(HostWorkerProtocol.DeserializePayload<HostWorkerLogFrame>)
            .ToArray();
        Assert.DoesNotContain(frames, frame => frame.StreamKind == "worker.process");

        var logs = SinglePayload<HostLogsEvent>(sink, HostWorkerMessageKinds.HostLogs);
        Assert.Contains("No readable HostWorker log file was found", logs.Logs);
    }

    private static DeploymentIntentStore CreateIntents(HostWorkerTestDirectory directory)
        => new(new HostWorkerPaths(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["HostWorker:DataRoot"] = directory.Path }).Build()), "host-1");

    [Fact]
    public async Task RecoveredStop_RefusesNameOnlyTartOwnershipAndForeignHandle()
    {
        using var directory = HostWorkerTestDirectory.Create("bound-cleanup");
        var docker = new FakeRunnerBackend { BackendType = ExecutionBackend.Docker };
        var tart = new FakeRunnerBackend { BackendType = ExecutionBackend.Tart };
        var intents = CreateIntents(directory);
        intents.Begin(new DeployRunnerCommand { InstanceId = "owned", RunnerName = "runner", Backend = ExecutionBackend.Tart });
        intents.MarkStarted("owned", "owned-vm");
        using (var processor = CreateProcessor(directory, out var rejected, out _, dockerBackend: docker, tartBackend: tart))
        {
            await processor.ProcessCommandAsync(CreateCommand("foreign", new HostCommandEnvelope
            {
                Kind = HostCommandKind.StopRunner,
                StopRunner = new StopRunnerCommand { InstanceId = "owned", InstanceHandle = "other-vm" }
            }), CancellationToken.None);
            Assert.Empty(docker.StopRequests);
            Assert.Empty(tart.StopRequests);
            Assert.DoesNotContain(HostWorkerMessageKinds.RunnerStopped, NonLogKinds(rejected));
        }
        using var recovered = CreateProcessor(directory, out var sink, out _, dockerBackend: docker, tartBackend: tart);
        await recovered.ProcessCommandAsync(CreateCommand("owned-stop", new HostCommandEnvelope
        {
            Kind = HostCommandKind.StopRunner,
            StopRunner = new StopRunnerCommand { InstanceId = "owned", InstanceHandle = "owned-vm" }
        }), CancellationToken.None);
        Assert.Empty(docker.StopRequests);
        Assert.Empty(tart.StopRequests);
        Assert.DoesNotContain(HostWorkerMessageKinds.RunnerStopped, NonLogKinds(sink));
        Assert.Equal("started", intents.Read("owned")!.State);
    }

    [Fact]
    public async Task CleanupReceipt_ProcessRestartResendsOnlyUntilServerAcknowledges()
    {
        using var directory = HostWorkerTestDirectory.Create("command-outbox-restart");
        using (var first = CreateProcessor(directory, out _, out _, dockerBackend: new FakeRunnerBackend
            { BackendType = ExecutionBackend.Docker, IsAvailable = false }))
        {
            await first.ProcessCommandAsync(CreateCommand("deploy", new HostCommandEnvelope
            {
                Kind = HostCommandKind.DeployRunner,
                DeployRunner = new DeployRunnerCommand { InstanceId = "never-started", RunnerName = "runner", Backend = ExecutionBackend.Docker }
            }), CancellationToken.None);
        }
        var backend = new FakeRunnerBackend { BackendType = ExecutionBackend.Docker };
        using (var restarted = CreateProcessor(directory, out var sink, out _, dockerBackend: backend))
        {
            await restarted.ReplayCleanupReceiptsAsync(CancellationToken.None);
            var receipt = SinglePayload<RunnerStoppedEvent>(sink, HostWorkerMessageKinds.RunnerStopped);
            Assert.True(receipt.NeverStarted);
            Assert.NotNull(receipt.CleanupReceiptId);
            restarted.AcknowledgeCleanup(receipt);
            Assert.Empty(backend.StartRequests);
        }
        using var third = CreateProcessor(directory, out var emptySink, out _, dockerBackend: backend);
        await third.ReplayCleanupReceiptsAsync(CancellationToken.None);
        Assert.Empty(emptySink.Published);
    }

    [Theory]
    [InlineData(NativeRunnerState.Running, false, false)]
    [InlineData(NativeRunnerState.Created, true, false)]
    [InlineData(NativeRunnerState.Exited, true, false)]
    [InlineData(NativeRunnerState.Absent, false, true)]
    [InlineData(NativeRunnerState.Unknown, false, false)]
    public async Task Recovery_AdoptsLiveOrCleansOnlyOwnedPartialResource(NativeRunnerState observed, bool expectedStop, bool savedHandle)
    {
        using var directory = HostWorkerTestDirectory.Create("native-recovery");
        var paths = new HostWorkerPaths(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["HostWorker:DataRoot"] = directory.Path }).Build());
        var intents = new DeploymentIntentStore(paths, "host-1");
        var claim = intents.Begin(new DeployRunnerCommand { InstanceId = "job", ProfileId = "profile", RunnerName = "runner", Backend = ExecutionBackend.Docker });
        intents.MarkCreating("job", claim.Ownership! with { ImageId = "sha256:immutable", Platform = "windows" });
        if (savedHandle) intents.MarkCreated("job", "native-id");
        var docker = new FakeRunnerBackend { BackendType = ExecutionBackend.Docker,
            InspectHandler = (owner, handle) => new(observed, "native-id", 0) };
        using var processor = CreateProcessor(directory, out var sink, out var lifecycle, dockerBackend: docker);
        await processor.RecoverOwnedRunnersAsync(CancellationToken.None);
        Assert.Empty(docker.StartRequests); // No JIT or deployment replay on any recovery path.
        Assert.Equal(expectedStop ? 1 : 0, docker.StopRequests.Count);
        if (observed == NativeRunnerState.Running)
        {
            Assert.Equal("native-id", Assert.Single(lifecycle.RunningInstances).Value.InstanceHandle);
            Assert.Empty(intents.PendingCleanupReceipts());
            Assert.Equal("native-id", SinglePayload<RunnerStartedEvent>(sink, HostWorkerMessageKinds.RunnerStarted).InstanceHandle);
            await processor.RecoverOwnedRunnersAsync(CancellationToken.None);
            Assert.Single(lifecycle.RunningInstances);
        }
        else if (observed == NativeRunnerState.Unknown)
        {
            Assert.Empty(lifecycle.RunningInstances);
            Assert.Empty(intents.PendingCleanupReceipts());
            Assert.Equal("creating", intents.Read("job")!.State);
        }
        else Assert.False(Assert.Single(intents.PendingCleanupReceipts()).NeverStarted);
    }

    [Fact]
    public async Task Recovery_AbsenceWithoutImmutableHandleRetainsCapacity()
    {
        using var directory = HostWorkerTestDirectory.Create("lost-create");
        var paths = new HostWorkerPaths(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["HostWorker:DataRoot"] = directory.Path }).Build());
        var intents = new DeploymentIntentStore(paths, "host-1");
        var claim = intents.Begin(new DeployRunnerCommand { InstanceId = "job", RunnerName = "runner", Backend = ExecutionBackend.Docker });
        intents.MarkCreating("job", claim.Ownership! with { ImageId = "sha256:immutable", Platform = "windows" });
        var docker = new FakeRunnerBackend { BackendType = ExecutionBackend.Docker,
            InspectHandler = (_, _) => new(NativeRunnerState.Absent, null) };
        using var processor = CreateProcessor(directory, out _, out _, dockerBackend: docker);
        await processor.RecoverOwnedRunnersAsync(CancellationToken.None);
        Assert.Empty(intents.PendingCleanupReceipts());
        Assert.Equal("creating", intents.Read("job")!.State);
        Assert.Empty(docker.StartRequests);
        Assert.Empty(docker.StopRequests);
    }

    [Fact]
    public async Task PreparedDeployment_RegistersOnlyAfterExactSingleUseActivation()
    {
        using var directory = HostWorkerTestDirectory.Create("prepared-activation");
        var docker = new FakeRunnerBackend { BackendType = ExecutionBackend.Docker,
            StartHandler = _ => new RunnerInstanceInfo { Prepared = true, RunnerName = "runner", InstanceHandle = "native" } };
        using var processor = CreateProcessor(directory, out var sink, out var lifecycle, dockerBackend: docker);
        await processor.ProcessCommandAsync(CreateCommand("prepare", new HostCommandEnvelope
        { Kind = HostCommandKind.DeployRunner, DeployRunner = new DeployRunnerCommand
          { InstanceId = "prepared", RunnerName = "runner", Backend = ExecutionBackend.Docker, DeferRegistration = true } }), CancellationToken.None);
        Assert.Empty(docker.Activations);
        Assert.Null(Assert.Single(docker.StartRequests).JitConfig);
        Assert.Single(sink.Published.Where(m => m.Kind == HostWorkerMessageKinds.RunnerPrepared));
        Assert.DoesNotContain(HostWorkerMessageKinds.RunnerStarted, NonLogKinds(sink));
        await processor.PublishRunnerHealthAsync(new ManagedRunnerHealth
        { Runner = lifecycle.RunningInstances["prepared"], Health = new RunnerHealthStatus { IsRunning = true } }, CancellationToken.None);
        Assert.DoesNotContain(sink.Published.Where(m => m.Kind == HostWorkerMessageKinds.RunnerHealth),
            m => HostWorkerProtocol.DeserializePayload<RunnerHealthUpdateEvent>(m).Status == RunnerInstanceStatus.Running);
        foreach (var handle in new[] { "foreign", "native", "native" })
            await processor.ProcessCommandAsync(CreateCommand(Guid.NewGuid().ToString(), new HostCommandEnvelope
            { Kind = HostCommandKind.ActivateRunner, ActivateRunner = new ActivateRunnerCommand
              { InstanceId = "prepared", InstanceHandle = handle, JitConfig = "private-jit-fixture" } }), CancellationToken.None);
        Assert.Equal(("native", "private-jit-fixture"), Assert.Single(docker.Activations));
        Assert.Single(sink.Published.Where(m => m.Kind == HostWorkerMessageKinds.RunnerStarted));
        foreach (var file in Directory.GetFiles(Path.Combine(directory.Path, "deployment-intents")))
            Assert.DoesNotContain("private-jit-fixture", await File.ReadAllTextAsync(file));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparedRecovery_RetiresOnlyWhenActivationWasNeverAttempted(bool activationAttempted)
    {
        using var directory = HostWorkerTestDirectory.Create("prepared-recovery");
        var paths = new HostWorkerPaths(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["HostWorker:DataRoot"] = directory.Path }).Build());
        var intents = new DeploymentIntentStore(paths, "host-1");
        var intent = intents.Begin(new DeployRunnerCommand
        { InstanceId = "prepared", RunnerName = "runner", Backend = ExecutionBackend.Docker, DeferRegistration = true });
        intents.MarkCreating("prepared", intent.Ownership! with { ImageId = "immutable", Platform = "windows" });
        intents.MarkPrepared("prepared", "native");
        if (activationAttempted) intents.ClaimActivation("prepared", "native");
        var docker = new FakeRunnerBackend { BackendType = ExecutionBackend.Docker,
            InspectHandler = (_, _) => new(NativeRunnerState.Running, "native") };
        using var processor = CreateProcessor(directory, out var sink, out var lifecycle, dockerBackend: docker);
        await processor.RecoverOwnedRunnersAsync(CancellationToken.None);
        Assert.Empty(docker.StartRequests);
        Assert.Empty(docker.Activations);
        Assert.Equal(activationAttempted ? 0 : 1, docker.StopRequests.Count);
        Assert.Equal(activationAttempted ? 1 : 0, lifecycle.RunningInstances.Count);
        Assert.DoesNotContain(HostWorkerMessageKinds.RunnerStarted, NonLogKinds(sink));
        if (activationAttempted)
        {
            Assert.Equal("activating", intents.Read("prepared")!.State);
            await processor.PublishRunnerHealthAsync(new ManagedRunnerHealth
            { Runner = lifecycle.RunningInstances["prepared"], Health = new RunnerHealthStatus { IsRunning = true } }, CancellationToken.None);
            Assert.DoesNotContain(sink.Published.Where(m => m.Kind == HostWorkerMessageKinds.RunnerHealth),
                m => HostWorkerProtocol.DeserializePayload<RunnerHealthUpdateEvent>(m).Status == RunnerInstanceStatus.Running);
        }
    }

    private static HostCommandProcessor CreateProcessor(
        HostWorkerTestDirectory directory,
        out FakeHostWorkerEventSink sink,
        out RunnerLifecycleManager lifecycle,
        FakeRunnerBackend? dockerBackend = null,
        FakeRunnerBackend? tartBackend = null,
        FakeRunnerBackend? nativeBackend = null,
        string? runnerBasePath = null)
    {
        var configValues = new Dictionary<string, string?>
        {
            ["HostWorker:DataRoot"] = directory.Path,
            ["HostWorker:ResourceUsageTimeoutSeconds"] = "1"
        };
        if (!string.IsNullOrWhiteSpace(runnerBasePath))
            configValues["Runner:BasePath"] = runnerBasePath;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
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
                paths,
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
