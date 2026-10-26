using Microsoft.Extensions.Configuration;
using RunnerRunner.Core.Hub;
using RunnerRunner.Core.Models;
using RunnerRunner.HostWorker.Services;
using RunnerRunner.HostWorker.Tests.TestSupport;

namespace RunnerRunner.HostWorker.Tests.Services;

public class DeploymentIntentStoreTests
{
    [Fact]
    public async Task ConcurrentClaims_OnlyOneCanCrossTheBackendBoundary()
    {
        using var directory = HostWorkerTestDirectory.Create("claims");
        var paths = new HostWorkerPaths(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["HostWorker:DataRoot"] = directory.Path }).Build());
        var first = new DeploymentIntentStore(paths, "host-1");
        var second = new DeploymentIntentStore(paths, "host-1");
        var command = new DeployRunnerCommand { InstanceId = "instance", RunnerName = "runner" };
        var successes = 0;
        await Task.WhenAll(new[] { first, second }.Select(store => Task.Run(() =>
        {
            try { store.Begin(command); Interlocked.Increment(ref successes); }
            catch (IOException) { }
        })));
        Assert.Equal(1, successes);
        Assert.True(first.Contains(command.InstanceId));
        Assert.True(second.Contains(command.InstanceId));
    }

    [Fact]
    public void ReleasedClaim_CannotBeReopenedOrReused()
    {
        using var directory = HostWorkerTestDirectory.Create("released-claim");
        var paths = new HostWorkerPaths(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["HostWorker:DataRoot"] = directory.Path }).Build());
        var store = new DeploymentIntentStore(paths, "host-1");
        var command = new DeployRunnerCommand { InstanceId = "../instance", RunnerName = "runner" };
        store.Begin(command);
        store.CompleteCleanup(command.InstanceId, null, true, "failed");
        store.MarkStarted(command.InstanceId, "late-handle");
        Assert.Throws<IOException>(() => store.Begin(command));
        var intent = File.ReadAllText(Assert.Single(Directory.GetFiles(Path.Combine(directory.Path, "deployment-intents"), "*.json")));
        Assert.Contains("\"State\":\"released\"", intent);
        Assert.DoesNotContain("late-handle", intent);
    }
    [Fact]
    public void CleanupOutbox_SurvivesRestartUntilExactAcknowledgement()
    {
        using var directory = HostWorkerTestDirectory.Create("cleanup-outbox");
        var paths = Paths(directory.Path);
        var first = new DeploymentIntentStore(paths, "host-1");
        first.Begin(new DeployRunnerCommand { InstanceId = "job", RunnerName = "runner", Backend = ExecutionBackend.Docker,
            JitConfig = "private-jit", EnvironmentVariables = new() { ["TOKEN"] = "private-token" } });
        first.MarkStarted("job", "native-id");
        var receipt = first.CompleteCleanup("job", "native-id", false, "completed");
        var restarted = new DeploymentIntentStore(paths, "host-1");
        Assert.Equal(receipt.CleanupReceiptId, Assert.Single(restarted.PendingCleanupReceipts()).CleanupReceiptId);
        Assert.Throws<InvalidDataException>(() => restarted.AcknowledgeCleanup(new RunnerStoppedEvent
        {
            InstanceId = "job", Backend = ExecutionBackend.Docker, InstanceHandle = "native-id",
            CleanupConfirmed = true, CleanupReceiptId = "old-or-foreign", Reason = "accepted"
        }));
        Assert.Single(restarted.PendingCleanupReceipts());
        restarted.AcknowledgeCleanup(receipt);
        restarted.AcknowledgeCleanup(receipt);
        var third = new DeploymentIntentStore(paths, "host-1");
        Assert.Empty(third.PendingCleanupReceipts());
        Assert.Throws<IOException>(() => third.Begin(new DeployRunnerCommand { InstanceId = "job", RunnerName = "another" }));
        var json = File.ReadAllText(Assert.Single(Directory.GetFiles(Path.Combine(directory.Path, "deployment-intents"), "*.json")));
        Assert.DoesNotContain("private-", json);
    }

    [Theory]
    [InlineData("other", false)]
    [InlineData(null, true)]
    public void CleanupOutbox_RejectsChangedNativeOwnership(string? handle, bool neverStarted)
    {
        using var directory = HostWorkerTestDirectory.Create("cleanup-owner");
        var store = new DeploymentIntentStore(Paths(directory.Path), "host-1");
        store.Begin(new DeployRunnerCommand { InstanceId = "job", RunnerName = "runner" });
        store.MarkStarted("job", "native-id");
        Assert.Throws<InvalidDataException>(() => store.CompleteCleanup("job", handle, neverStarted, "completed"));
        Assert.Empty(store.PendingCleanupReceipts());
        Assert.Equal("started", store.Read("job")!.State);
    }

    [Fact]
    public void CleanupOutbox_BoundedBatchesDoNotStarveLaterReceipts()
    {
        using var directory = HostWorkerTestDirectory.Create("cleanup-batches");
        var store = new DeploymentIntentStore(Paths(directory.Path), "host-1");
        for (var i = 0; i < 5; i++)
        {
            store.Begin(new DeployRunnerCommand { InstanceId = $"job-{i}", RunnerName = "runner" });
            store.CompleteCleanup($"job-{i}", null, true, "failed");
        }
        var receipts = Enumerable.Range(0, 3).SelectMany(_ => store.PendingCleanupReceipts(2)).ToArray();
        Assert.Equal(6, receipts.Length);
        Assert.Equal(5, receipts.Select(r => r.InstanceId).Distinct().Count());
    }

    [Fact]
    public void CleanupOutbox_ForeignOrCorruptRecordCannotBecomeAbsence()
    {
        using var directory = HostWorkerTestDirectory.Create("corrupt-outbox");
        var paths = Paths(directory.Path);
        var store = new DeploymentIntentStore(paths, "host-1");
        store.Begin(new DeployRunnerCommand { InstanceId = "job", RunnerName = "runner" });
        Assert.Throws<InvalidDataException>(() => new DeploymentIntentStore(paths, "host-2").PendingCleanupReceipts());
        File.WriteAllText(Directory.GetFiles(Path.Combine(directory.Path, "deployment-intents"), "*.json").Single(), "{");
        Assert.Throws<System.Text.Json.JsonException>(() => store.PendingCleanupReceipts());
    }

    [Fact]
    public void ProcessLock_ExcludesSecondWorkerButAllowsCleanRestart()
    {
        using var directory = HostWorkerTestDirectory.Create("process-lock");
        var paths = Paths(directory.Path);
        using (var first = new HostWorkerProcessLock(paths))
            Assert.Throws<IOException>(() => new HostWorkerProcessLock(paths));
        using var restarted = new HostWorkerProcessLock(paths);
        Assert.True(File.Exists(Path.Combine(paths.DataRoot, "worker.lock")));
    }

    private static HostWorkerPaths Paths(string root) => new(new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["HostWorker:DataRoot"] = root }).Build());

}
