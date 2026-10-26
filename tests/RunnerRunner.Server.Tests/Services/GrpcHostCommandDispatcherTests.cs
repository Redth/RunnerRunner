using NSubstitute;
using RunnerRunner.Server.Services.Admission;
using Microsoft.Extensions.Logging.Abstractions;
using RunnerRunner.Core.HostWorkers;
using RunnerRunner.Core.Hub;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Services;
using RunnerRunner.Server.Services.HostWorkers;

namespace RunnerRunner.Server.Tests.Services;

public class GrpcHostCommandDispatcherTests
{
    [Fact]
    public async Task DispatchDeployRunnerAsync_SendsCommandToRegisteredWorker()
    {
        var registry = new HostWorkerConnectionRegistry();
        await using var connection = registry.Register("host-1", "worker-name");
        using var tasks = new LongRunningTaskService(NullLogger<LongRunningTaskService>.Instance);
        var dispatcher = new GrpcHostCommandDispatcher(registry, tasks, Substitute.For<IResourceAdmission>(), NullLogger<GrpcHostCommandDispatcher>.Instance);

        await dispatcher.DispatchDeployRunnerAsync("worker-name", new DeployRunnerCommand
        {
            InstanceId = "runner-1",
            ProfileId = "profile-1",
            RunnerName = "runner-name"
        });

        var message = await ReadOneAsync(connection);
        var envelope = HostWorkerProtocol.DeserializeCommand(message);

        Assert.Equal(HostWorkerMessageKinds.Command, message.Kind);
        Assert.Equal("host-1", message.HostId);
        Assert.NotEmpty(message.CommandId);
        Assert.Equal("host-1:DeployRunner:runner-1", message.IdempotencyKey);
        Assert.Equal(HostCommandKind.DeployRunner, envelope.Kind);
        Assert.Equal("runner-1", envelope.DeployRunner?.InstanceId);
    }

    [Fact]
    public async Task DispatchListImagesAsync_ThrowsWhenWorkerIsMissing()
    {
        using var tasks = new LongRunningTaskService(NullLogger<LongRunningTaskService>.Instance);
        var dispatcher = new GrpcHostCommandDispatcher(
            new HostWorkerConnectionRegistry(),
            tasks,
            Substitute.For<IResourceAdmission>(),
            NullLogger<GrpcHostCommandDispatcher>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => dispatcher.DispatchListImagesAsync("missing-host", new ListImagesCommand()));

        Assert.Contains("missing-host", ex.Message);
    }

    [Fact]
    public async Task CanDispatchToHost_ReturnsCurrentConnectionState()
    {
        var registry = new HostWorkerConnectionRegistry();
        using var tasks = new LongRunningTaskService(NullLogger<LongRunningTaskService>.Instance);
        var dispatcher = new GrpcHostCommandDispatcher(registry, tasks, Substitute.For<IResourceAdmission>(), NullLogger<GrpcHostCommandDispatcher>.Instance);

        Assert.False(dispatcher.CanDispatchToHost("host-1"));

        await using (registry.Register("host-1", "worker-name"))
        {
            Assert.True(dispatcher.CanDispatchToHost("host-1"));
            Assert.True(dispatcher.CanDispatchToHost("worker-name"));
        }

        Assert.False(dispatcher.CanDispatchToHost("host-1"));
        Assert.False(dispatcher.CanDispatchToHost("worker-name"));
    }

    [Fact]
    public async Task DispatchPullImageAsync_TracksTaskAndSendsTaskId()
    {
        var registry = new HostWorkerConnectionRegistry();
        await using var connection = registry.Register("host-1");
        using var tasks = new LongRunningTaskService(NullLogger<LongRunningTaskService>.Instance);
        var dispatcher = new GrpcHostCommandDispatcher(registry, tasks, Substitute.For<IResourceAdmission>(), NullLogger<GrpcHostCommandDispatcher>.Instance);

        await dispatcher.DispatchPullImageAsync("host-1", new PullImageCommand
        {
            ImageType = ImageType.Docker,
            ImageName = "library/ubuntu",
            Tag = "latest"
        });

        var message = await ReadOneAsync(connection);
        var envelope = HostWorkerProtocol.DeserializeCommand(message);
        var task = Assert.Single(tasks.GetSnapshot());

        Assert.Equal(HostCommandKind.PullImage, envelope.Kind);
        Assert.NotNull(envelope.PullImage?.TaskId);
        Assert.Equal(task.Id, envelope.PullImage.TaskId);
        Assert.Equal(LongRunningTaskStatus.Running, task.Status);
    }

    [Fact]
    public async Task Register_ReplacesStaleConnectionAndAliases()
    {
        var registry = new HostWorkerConnectionRegistry();
        await using var first = registry.Register("host-1", "old-alias");
        await using var second = registry.Register("host-1", "new-alias");

        await registry.SendAsync("new-alias", HostWorkerProtocol.CreateMessage("host-1", "test", new { Value = 1 }));
        var message = await ReadOneAsync(second);

        Assert.Equal("test", message.Kind);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => registry.SendAsync("old-alias", HostWorkerProtocol.CreateMessage("host-1", "test", new { Value = 2 })));
    }

    [Fact]
    public async Task DispatchApplyHostWorkerUpdateAsync_SendsUpdateCommand()
    {
        var registry = new HostWorkerConnectionRegistry();
        await using var connection = registry.Register("host-1");
        using var tasks = new LongRunningTaskService(NullLogger<LongRunningTaskService>.Instance);
        var dispatcher = new GrpcHostCommandDispatcher(registry, tasks, Substitute.For<IResourceAdmission>(), NullLogger<GrpcHostCommandDispatcher>.Instance);

        await dispatcher.DispatchApplyHostWorkerUpdateAsync("host-1", new HostWorkerUpdateCommand
        {
            TargetVersion = "v1.2.3",
            AssetName = "runnerrunner-hostworker-linux-x64.tar.gz",
            AssetUrl = "https://example.test/asset",
            Sha256 = "abc"
        });

        var message = await ReadOneAsync(connection);
        var envelope = HostWorkerProtocol.DeserializeCommand(message);

        Assert.Equal(HostCommandKind.ApplyHostWorkerUpdate, envelope.Kind);
        Assert.Equal("v1.2.3", envelope.ApplyHostWorkerUpdate?.TargetVersion);
        Assert.Equal("host-1:ApplyHostWorkerUpdate:v1.2.3", message.IdempotencyKey);
    }

    [Fact]
    public async Task Admission_IsCanonicalAndPrecedesDelivery()
    {
        var registry = new HostWorkerConnectionRegistry();
        await using var connection = registry.Register("host-1", "alias");
        using var tasks = new LongRunningTaskService(NullLogger<LongRunningTaskService>.Instance);
        var admission = Substitute.For<IResourceAdmission>();
        admission.GetBudgetAsync("host-1", Arg.Any<CancellationToken>()).Returns(new HostResourceBudget(4, 8192, 100, 2, 0));
        connection.SupportsDurableOwnership = true;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        admission.ReserveAsync("host-1", Arg.Any<DeployRunnerCommand>(), Arg.Any<CancellationToken>())
            .Returns(_ => { entered.SetResult(); return gate.Task; });
        var dispatcher = new GrpcHostCommandDispatcher(registry, tasks, admission, NullLogger<GrpcHostCommandDispatcher>.Instance);
        var dispatch = dispatcher.DispatchDeployRunnerAsync("alias", new DeployRunnerCommand { InstanceId = "i1", RunnerName = "r1" });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var received = ReadOneAsync(connection);
        Assert.False(received.IsCompleted);
        gate.SetResult();
        await dispatch;
        Assert.Equal("host-1", (await received).HostId);
    }

    [Fact]
    public async Task RejectedAdmission_DoesNotEnqueueCommand()
    {
        var registry = new HostWorkerConnectionRegistry();
        await using var connection = registry.Register("host-1");
        using var tasks = new LongRunningTaskService(NullLogger<LongRunningTaskService>.Instance);
        var admission = Substitute.For<IResourceAdmission>();
        admission.GetBudgetAsync("host-1", Arg.Any<CancellationToken>()).Returns(new HostResourceBudget(4, 8192, 100, 2, 0));
        connection.SupportsDurableOwnership = true;
        admission.ReserveAsync(Arg.Any<string>(), Arg.Any<DeployRunnerCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new ResourceAdmissionException("full")));
        var dispatcher = new GrpcHostCommandDispatcher(registry, tasks, admission, NullLogger<GrpcHostCommandDispatcher>.Instance);
        await Assert.ThrowsAsync<ResourceAdmissionException>(() => dispatcher.DispatchDeployRunnerAsync("host-1", new DeployRunnerCommand { InstanceId = "i1", RunnerName = "r1" }));
        connection.Complete();
        await foreach (var _ in connection.ReadAllAsync(CancellationToken.None)) Assert.Fail("Denied job reached worker");
    }

    [Fact]
    public async Task DisconnectAfterReservation_DoesNotReleaseOwnership()
    {
        var registry = new HostWorkerConnectionRegistry();
        await using var connection = registry.Register("host-1");
        using var tasks = new LongRunningTaskService(NullLogger<LongRunningTaskService>.Instance);
        var admission = Substitute.For<IResourceAdmission>();
        admission.GetBudgetAsync("host-1", Arg.Any<CancellationToken>()).Returns(new HostResourceBudget(4, 8192, 100, 2, 0));
        connection.SupportsDurableOwnership = true;
        admission.ReserveAsync(Arg.Any<string>(), Arg.Any<DeployRunnerCommand>(), Arg.Any<CancellationToken>())
            .Returns(_ => { connection.Complete(); return Task.CompletedTask; });
        var dispatcher = new GrpcHostCommandDispatcher(registry, tasks, admission, NullLogger<GrpcHostCommandDispatcher>.Instance);
        await Assert.ThrowsAsync<System.Threading.Channels.ChannelClosedException>(() => dispatcher.DispatchDeployRunnerAsync("host-1", new DeployRunnerCommand { InstanceId = "i1", RunnerName = "r1" }));
        await admission.DidNotReceiveWithAnyArgs().ConfirmCleanupAsync(default!, default!, default, default, default);
    }

    [Fact]
    public async Task UnbudgetedNativeHostKeepsExistingDispatchWithoutReservation()
    {
        var registry = new HostWorkerConnectionRegistry();
        await using var connection = registry.Register("legacy");
        using var tasks = new LongRunningTaskService(NullLogger<LongRunningTaskService>.Instance);
        var admission = Substitute.For<IResourceAdmission>();
        var dispatcher = new GrpcHostCommandDispatcher(registry, tasks, admission, NullLogger<GrpcHostCommandDispatcher>.Instance);
        await dispatcher.DispatchDeployRunnerAsync("legacy", new DeployRunnerCommand
        { InstanceId = "native", RunnerName = "native", Backend = ExecutionBackend.Native });
        Assert.Equal("native", HostWorkerProtocol.DeserializeCommand(await ReadOneAsync(connection)).DeployRunner!.InstanceId);
        await admission.DidNotReceiveWithAnyArgs().ReserveAsync(default!, default!);
    }

    [Fact]
    public async Task BudgetedHostRefusesOldWorkerBeforeReservationOrDelivery()
    {
        var registry = new HostWorkerConnectionRegistry();
        await using var connection = registry.Register("old-worker");
        using var tasks = new LongRunningTaskService(NullLogger<LongRunningTaskService>.Instance);
        var admission = Substitute.For<IResourceAdmission>();
        admission.GetBudgetAsync("old-worker", Arg.Any<CancellationToken>()).Returns(new HostResourceBudget(4, 8192, 100, 2, 0));
        var dispatcher = new GrpcHostCommandDispatcher(registry, tasks, admission, NullLogger<GrpcHostCommandDispatcher>.Instance);
        await Assert.ThrowsAsync<ResourceAdmissionException>(() => dispatcher.DispatchDeployRunnerAsync("old-worker",
            new DeployRunnerCommand { InstanceId = "old", RunnerName = "old" }));
        await admission.DidNotReceiveWithAnyArgs().ReserveAsync(default!, default!);
        connection.Complete();
        await foreach (var _ in connection.ReadAllAsync(CancellationToken.None)) Assert.Fail("Unsupported worker received deployment");
    }

    private static async Task<HostWorkerMessage> ReadOneAsync(HostWorkerConnection connection)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var message in connection.ReadAllAsync(cts.Token))
            return message;

        throw new InvalidOperationException("No message was queued for the worker.");
    }
}
