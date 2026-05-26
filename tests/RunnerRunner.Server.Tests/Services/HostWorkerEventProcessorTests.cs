using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using RunnerRunner.Core.HostWorkers;
using RunnerRunner.Core.Hub;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Grains.Interfaces;
using RunnerRunner.Server.Services;
using RunnerRunner.Server.Services.HostWorkers;
using Shiny.DocumentDb;
using Host = RunnerRunner.Core.Models.Host;

namespace RunnerRunner.Server.Tests.Services;

public class HostWorkerEventProcessorTests
{
    [Fact]
    public async Task HandleMessageAsync_IngestsLogFramesIntoRecentCache()
    {
        var cache = CreateCache();
        var processor = CreateProcessor(cache);
        var message = HostWorkerProtocol.CreateMessage(
            "worker-host",
            HostWorkerMessageKinds.LogFrame,
            new HostWorkerLogFrame
            {
                StreamKind = "worker.command",
                StreamId = "command-1",
                Offset = 0,
                Text = "accepted\n",
                Timestamp = DateTimeOffset.UnixEpoch
            });

        await processor.HandleMessageAsync("canonical-host", message, CancellationToken.None);

        Assert.Equal("accepted\n", cache.GetTextTail("canonical-host", "command-1", maxFrames: 10));
    }

    [Fact]
    public async Task HandleMessageAsync_DoesNotThrowForMalformedWorkerMessage()
    {
        var cache = CreateCache();
        var processor = CreateProcessor(cache);
        var message = new HostWorkerMessage
        {
            HostId = "worker-host",
            Kind = HostWorkerMessageKinds.LogFrame,
            PayloadJson = "{not-json"
        };

        await processor.HandleMessageAsync("canonical-host", message, CancellationToken.None);

        Assert.Empty(cache.GetTail("canonical-host", "command-1", maxFrames: 10));
    }

    [Fact]
    public async Task HandleMessageAsync_StoresObservedTartUsageFromHeartbeat()
    {
        var store = TestDocumentStore.Create();
        await store.Insert(new Host
        {
            Id = "canonical-host",
            Name = "mac-mini",
            Platform = HostPlatform.MacOS
        });
        var processor = CreateProcessor(store);
        var message = HostWorkerProtocol.CreateMessage(
            "worker-host",
            HostWorkerMessageKinds.Heartbeat,
            new HeartbeatEvent
            {
                AgentId = "worker-host",
                RunningInstanceCount = 0,
                ResourceUsage = new HostResourceUsage { RunningTartVmCount = 2 }
            });

        await processor.HandleMessageAsync("canonical-host", message, CancellationToken.None);

        var host = await store.Get<Host>("canonical-host");
        Assert.NotNull(host);
        Assert.Equal(2, host.ObservedRunningTartVMs);
        Assert.NotNull(host.ObservedResourceUsageAt);
    }

    private static HostWorkerEventProcessor CreateProcessor(HostWorkerLogCache cache)
        => new(
            Substitute.For<IDocumentStore>(),
            Substitute.For<IGrainFactory>(),
            cache,
            new LongRunningTaskService(NullLogger<LongRunningTaskService>.Instance),
            new ConfigurationBuilder().Build(),
            NullLogger<HostWorkerEventProcessor>.Instance);

    [Fact]
    public async Task WorkerConnectedAsync_EnrollsPendingHostWithHashedToken()
    {
        var token = HostEnrollmentToken.Create();
        var store = TestDocumentStore.Create();
        await store.Insert(new Host
        {
            Name = "pending-host",
            EnrollmentTokenHash = HostEnrollmentToken.Hash(token),
            EnrollmentTokenCreatedAt = DateTime.UtcNow
        });
        var processor = CreateProcessor(store);

        var hostId = await processor.WorkerConnectedAsync(
            new AgentInfo
            {
                AgentId = "worker-1",
                Name = "mac-mini-1",
                Platform = HostPlatform.MacOS,
                Architecture = "arm64"
            },
            "peer",
            new Dictionary<string, string> { ["pool"] = "mac" },
            token,
            CancellationToken.None);

        var host = await store.Get<Host>(hostId);
        Assert.NotNull(host);
        Assert.Equal("worker-1", host.WorkerId);
        Assert.Equal("mac-mini-1", host.Name);
        Assert.True(host.IsApproved);
        Assert.NotNull(host.EnrolledAt);
        Assert.Null(host.EnrollmentToken);
        Assert.Equal("mac", host.Labels["pool"]);
    }

    [Fact]
    public async Task WorkerConnectedAsync_StoresContainerRuntimeInfo()
    {
        var token = HostEnrollmentToken.Create();
        var store = TestDocumentStore.Create();
        await store.Insert(new Host
        {
            Name = "pending-host",
            EnrollmentTokenHash = HostEnrollmentToken.Hash(token),
            EnrollmentTokenCreatedAt = DateTime.UtcNow
        });
        var processor = CreateProcessor(store);

        var hostId = await processor.WorkerConnectedAsync(
            new AgentInfo
            {
                AgentId = "worker-1",
                Name = "linux-docker",
                Platform = HostPlatform.Linux,
                Architecture = "x64",
                Runtime = new HostWorkerRuntimeInfo
                {
                    IsContainer = true,
                    ContainerId = "abcdef123456",
                    ContainerImage = "ghcr.io/redth/runnerrunner-hostworker:main"
                }
            },
            "peer",
            new Dictionary<string, string>(),
            token,
            CancellationToken.None);

        var host = await store.Get<Host>(hostId);

        Assert.NotNull(host);
        Assert.True(host.IsContainerized);
        Assert.Equal("abcdef123456", host.ContainerId);
        Assert.Equal("ghcr.io/redth/runnerrunner-hostworker:main", host.ContainerImage);
    }

    [Fact]
    public async Task WorkerConnectedAsync_MarksRestartingUpdateCurrentWhenCommitMatches()
    {
        const string commitSha = "3bf419d5a5a9f21f24f69dfb44b13639a4137448";
        var token = HostEnrollmentToken.Create();
        var store = TestDocumentStore.Create();
        var pendingHost = new Host
        {
            Id = "host-1",
            Name = "pending-host",
            EnrollmentTokenHash = HostEnrollmentToken.Hash(token),
            EnrollmentTokenCreatedAt = DateTime.UtcNow,
            UpdateStatus = "Restarting",
            LatestAvailableVersion = "v0.3.0",
            LatestAvailableCommitSha = commitSha,
            IsDraining = true,
            PendingHostWorkerUpdateSource = "release",
            PendingHostWorkerUpdateQueuedAt = DateTime.UtcNow,
            PendingHostWorkerUpdateDispatchedAt = DateTime.UtcNow
        };
        await store.Insert(pendingHost);
        using var tasks = new LongRunningTaskService(NullLogger<LongRunningTaskService>.Instance);
        tasks.TrackHostWorkerUpdate(pendingHost, "v0.3.0", commitSha, "Restarting HostWorker.", "Restarting");
        var processor = CreateProcessor(store, tasks);

        var hostId = await processor.WorkerConnectedAsync(
            new AgentInfo
            {
                AgentId = "worker-1",
                Name = "linux-ci-01",
                Platform = HostPlatform.Linux,
                Architecture = "x64",
                AgentVersion = $"v0.3.0+{commitSha}",
                AgentCommitSha = commitSha
            },
            "peer",
            new Dictionary<string, string>(),
            token,
            CancellationToken.None);

        var host = await store.Get<Host>(hostId);

        Assert.NotNull(host);
        Assert.Equal("Current", host.UpdateStatus);
        Assert.Equal(commitSha, host.AgentCommitSha);
        Assert.NotNull(host.LastUpdateCompletedAt);
        Assert.False(host.IsDraining);
        Assert.Null(host.PendingHostWorkerUpdateSource);
        var task = Assert.Single(tasks.GetSnapshot());
        Assert.Equal(LongRunningTaskStatus.Succeeded, task.Status);
        Assert.Equal(100, task.ProgressPercent);
    }

    [Fact]
    public async Task HandleMessageAsync_TracksHostWorkerUpdateStatus()
    {
        var store = TestDocumentStore.Create();
        await store.Insert(new Host
        {
            Id = "host-1",
            Name = "linux-host",
            Platform = HostPlatform.Linux,
            UpdateStatus = "Queued",
            LatestAvailableVersion = "v2.0.0"
        });
        using var tasks = new LongRunningTaskService(NullLogger<LongRunningTaskService>.Instance);
        var processor = CreateProcessor(store, tasks);
        var message = HostWorkerProtocol.CreateMessage(
            "worker-host",
            HostWorkerMessageKinds.UpdateStatus,
            new HostWorkerUpdateStatusEvent
            {
                HostId = "worker-host",
                TargetVersion = "v2.0.0",
                Stage = "downloading",
                Message = "Downloading update.",
                Success = true
            });

        await processor.HandleMessageAsync("host-1", message, CancellationToken.None);

        var host = await store.Get<Host>("host-1");
        Assert.NotNull(host);
        Assert.Equal("Downloading", host.UpdateStatus);
        var task = Assert.Single(tasks.GetSnapshot());
        Assert.Equal(LongRunningTaskKind.HostWorkerUpdate, task.Kind);
        Assert.Equal(LongRunningTaskStatus.Running, task.Status);
        Assert.Equal("Downloading update.", task.StatusText);
        Assert.Equal(30, task.ProgressPercent);
    }

    [Fact]
    public async Task WorkerConnectedAsync_RejectsWrongTokenForKnownHost()
    {
        var token = HostEnrollmentToken.Create();
        var store = TestDocumentStore.Create();
        await store.Insert(new Host
        {
            Id = "host-1",
            Name = "mac-mini-1",
            WorkerId = "worker-1",
            EnrollmentTokenHash = HostEnrollmentToken.Hash(token),
            IsApproved = true
        });
        var processor = CreateProcessor(store);

        await Assert.ThrowsAsync<Grpc.Core.RpcException>(() => processor.WorkerConnectedAsync(
            new AgentInfo
            {
                AgentId = "worker-1",
                Name = "mac-mini-1",
                Platform = HostPlatform.MacOS
            },
            "peer",
            new Dictionary<string, string>(),
            "wrong-token",
            CancellationToken.None));
    }

    private static HostWorkerEventProcessor CreateProcessor(IDocumentStore store, LongRunningTaskService? tasks = null)
    {
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<IHostGrain>(Arg.Any<string>(), null).Returns(Substitute.For<IHostGrain>());
        grainFactory.GetGrain<ISchedulerGrain>(Arg.Any<long>(), null).Returns(Substitute.For<ISchedulerGrain>());

        return new HostWorkerEventProcessor(
            store,
            grainFactory,
            CreateCache(),
            tasks ?? new LongRunningTaskService(NullLogger<LongRunningTaskService>.Instance),
            new ConfigurationBuilder().Build(),
            NullLogger<HostWorkerEventProcessor>.Instance);
    }

    private static HostWorkerLogCache CreateCache()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HostWorker:LogCache:MaxFramesPerStream"] = "10"
            })
            .Build();

        return new HostWorkerLogCache(configuration);
    }
}
