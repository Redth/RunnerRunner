using Microsoft.Extensions.Logging;
using NSubstitute;
using RunnerRunner.Agent.Services;
using RunnerRunner.Agent.Tests.TestSupport;
using RunnerRunner.Core.Hub;
using RunnerRunner.Core.Interfaces;
using RunnerRunner.Core.Models;

namespace RunnerRunner.Agent.Tests.Services;

public class RunnerLifecycleManagerTests
{
    private readonly ILogger<RunnerLifecycleManager> _logger = Substitute.For<ILogger<RunnerLifecycleManager>>();

    [Fact]
    public async Task StartRunner_TracksInstance()
    {
        var manager = new RunnerLifecycleManager(_logger);
        var backend = new FakeRunnerBackend
        {
            StartHandler = _ => new RunnerInstanceInfo
            {
                InstanceHandle = "container-123",
                RunnerName = "test-runner"
            }
        };

        var command = new DeployRunnerCommand
        {
            InstanceId = "inst-1",
            ProfileId = "prof-1",
            RunnerName = "test-runner",
            EnvironmentVariables = new() { ["KEY"] = "val" },
            Labels = ["linux"],
            RunnerGroup = "Default"
        };

        var result = await manager.StartRunnerAsync(command, backend);

        Assert.NotNull(result);
        Assert.Equal("container-123", result!.InstanceHandle);
        Assert.Equal("test-runner", result.RunnerName);
        Assert.Single(manager.RunningInstances);
        Assert.True(manager.RunningInstances.ContainsKey("inst-1"));
        Assert.Single(backend.StartRequests);
    }

    [Fact]
    public async Task StopRunner_RemovesFromTracking()
    {
        var manager = new RunnerLifecycleManager(_logger);
        var backend = new FakeRunnerBackend
        {
            StartHandler = _ => new RunnerInstanceInfo
            {
                InstanceHandle = "container-456",
                RunnerName = "runner-1"
            }
        };

        var command = new DeployRunnerCommand
        {
            InstanceId = "inst-2",
            ProfileId = "prof-1",
            RunnerName = "runner-1"
        };

        await manager.StartRunnerAsync(command, backend);
        Assert.Single(manager.RunningInstances);

        await manager.StopRunnerAsync("inst-2");

        Assert.Empty(manager.RunningInstances);
        Assert.Equal(["container-456"], backend.StopRequests);
    }

    [Fact]
    public async Task StopRunner_UnknownInstance_DoesNotThrow()
    {
        var manager = new RunnerLifecycleManager(_logger);
        await manager.StopRunnerAsync("nonexistent");
        // Should not throw
    }

    [Fact]
    public async Task CheckHealth_DelegatesToBackend()
    {
        var manager = new RunnerLifecycleManager(_logger);
        var backend = Substitute.For<IRunnerBackend>();

        backend.StartRunnerAsync(Arg.Any<RunnerStartRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RunnerInstanceInfo { InstanceHandle = "handle-1", RunnerName = "r1" });

        backend.GetHealthAsync("handle-1", Arg.Any<CancellationToken>())
            .Returns(new RunnerHealthStatus { IsRunning = true, Status = "running" });

        var command = new DeployRunnerCommand { InstanceId = "i1", ProfileId = "p1", RunnerName = "r1" };
        await manager.StartRunnerAsync(command, backend);

        var health = await manager.CheckHealthAsync("i1");

        Assert.NotNull(health);
        Assert.True(health!.IsRunning);
        Assert.Equal("running", health.Status);
    }

    [Fact]
    public async Task CheckHealth_UnknownInstance_ReturnsNull()
    {
        var manager = new RunnerLifecycleManager(_logger);
        var health = await manager.CheckHealthAsync("unknown");
        Assert.Null(health);
    }

    [Fact]
    public async Task MultipleRunners_TrackedIndependently()
    {
        var manager = new RunnerLifecycleManager(_logger);
        var backend = Substitute.For<IRunnerBackend>();

        backend.StartRunnerAsync(Arg.Any<RunnerStartRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var req = callInfo.Arg<RunnerStartRequest>();
                return new RunnerInstanceInfo { InstanceHandle = $"handle-{req.RunnerName}", RunnerName = req.RunnerName };
            });

        await manager.StartRunnerAsync(
            new DeployRunnerCommand { InstanceId = "i1", ProfileId = "p1", RunnerName = "r1" }, backend);
        await manager.StartRunnerAsync(
            new DeployRunnerCommand { InstanceId = "i2", ProfileId = "p1", RunnerName = "r2" }, backend);
        await manager.StartRunnerAsync(
            new DeployRunnerCommand { InstanceId = "i3", ProfileId = "p2", RunnerName = "r3" }, backend);

        Assert.Equal(3, manager.RunningInstances.Count);

        await manager.StopRunnerAsync("i2");

        Assert.Equal(2, manager.RunningInstances.Count);
        Assert.True(manager.RunningInstances.ContainsKey("i1"));
        Assert.False(manager.RunningInstances.ContainsKey("i2"));
        Assert.True(manager.RunningInstances.ContainsKey("i3"));
    }

    [Fact]
    public async Task StartRunner_PassesCorrectRequestToBackend()
    {
        var manager = new RunnerLifecycleManager(_logger);
        var backend = Substitute.For<IRunnerBackend>();

        RunnerStartRequest? capturedRequest = null;
        backend.StartRunnerAsync(Arg.Any<RunnerStartRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedRequest = callInfo.Arg<RunnerStartRequest>();
                return new RunnerInstanceInfo { InstanceHandle = "h1", RunnerName = capturedRequest.RunnerName };
            });

        var command = new DeployRunnerCommand
        {
            InstanceId = "i1",
            ProfileId = "p1",
            RunnerName = "my-runner",
            EnvironmentVariables = new() { ["CI"] = "true", ["NODE_ENV"] = "test" },
            Labels = ["linux", "docker"],
            RunnerGroup = "CustomGroup",
            Ephemeral = true,
            RegistrationToken = "reg-token-123",
            RunnerUrl = "https://github.com/org"
        };

        await manager.StartRunnerAsync(command, backend);

        Assert.NotNull(capturedRequest);
        Assert.Equal("my-runner", capturedRequest!.RunnerName);
        Assert.Equal("i1", capturedRequest.InstanceId);
        Assert.Equal("reg-token-123", capturedRequest.RegistrationToken);
        Assert.Equal("https://github.com/org", capturedRequest.RunnerUrl);
        Assert.True(capturedRequest.Ephemeral);
        Assert.Equal("CustomGroup", capturedRequest.RunnerGroup);
        Assert.Equal(2, capturedRequest.EnvironmentVariables.Count);
        Assert.Equal(2, capturedRequest.Labels.Count);
    }

    [Fact]
    public async Task CollectRunnerHealth_RemovesExitedRunnerFromTracking()
    {
        var manager = new RunnerLifecycleManager(_logger);
        var backend = new FakeRunnerBackend
        {
            StartHandler = _ => new RunnerInstanceInfo { InstanceHandle = "handle-dead", RunnerName = "dead-runner" }
        };
        backend.HealthByHandle["handle-dead"] = new RunnerHealthStatus { IsRunning = false, Status = "exited:1" };

        await manager.StartRunnerAsync(
            new DeployRunnerCommand { InstanceId = "dead-1", ProfileId = "p1", RunnerName = "dead-runner" },
            backend);

        var snapshots = await manager.CollectRunnerHealthAsync();

        Assert.Single(snapshots);
        Assert.False(snapshots[0].Health.IsRunning);
        Assert.Empty(manager.RunningInstances);
    }

    [Fact]
    public async Task StartRunner_WhenBackendThrows_DoesNotTrackInstance()
    {
        var manager = new RunnerLifecycleManager(_logger);
        var backend = new FakeRunnerBackend
        {
            StartHandler = _ => throw new InvalidOperationException("start failed")
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartRunnerAsync(
            new DeployRunnerCommand { InstanceId = "failed-1", ProfileId = "p1", RunnerName = "failed-runner" },
            backend));

        Assert.Empty(manager.RunningInstances);
        Assert.Single(backend.StartRequests);
    }

    [Fact]
    public async Task StopRunner_WhenBackendThrows_RemovesInstanceFromTracking()
    {
        var manager = new RunnerLifecycleManager(_logger);
        var backend = new FakeRunnerBackend
        {
            StartHandler = _ => new RunnerInstanceInfo { InstanceHandle = "handle-stop-fails", RunnerName = "stop-fails" },
            StopAsyncHandler = (_, _) => throw new InvalidOperationException("stop failed")
        };
        await manager.StartRunnerAsync(
            new DeployRunnerCommand { InstanceId = "stop-fails-1", ProfileId = "p1", RunnerName = "stop-fails" },
            backend);

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StopRunnerAsync("stop-fails-1"));

        Assert.Empty(manager.RunningInstances);
        Assert.Equal(["handle-stop-fails"], backend.StopRequests);
    }

    [Fact]
    public async Task CollectRunnerHealth_WhenBackendThrows_ReturnsFailedSnapshotAndRemovesRunner()
    {
        var manager = new RunnerLifecycleManager(_logger);
        var backend = new FakeRunnerBackend
        {
            StartHandler = _ => new RunnerInstanceInfo { InstanceHandle = "handle-health-fails", RunnerName = "health-fails" },
            HealthAsyncHandler = (_, _) => throw new TimeoutException("health timed out")
        };
        await manager.StartRunnerAsync(
            new DeployRunnerCommand { InstanceId = "health-fails-1", ProfileId = "p1", RunnerName = "health-fails" },
            backend);

        var snapshots = await manager.CollectRunnerHealthAsync();

        var snapshot = Assert.Single(snapshots);
        Assert.False(snapshot.Health.IsRunning);
        Assert.Equal("health_check_failed", snapshot.Health.Status);
        Assert.Empty(manager.RunningInstances);
    }
}
