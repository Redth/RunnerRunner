using RunnerRunner.Core.Interfaces;
using RunnerRunner.Core.Models;

namespace RunnerRunner.HostWorker.Tests.TestSupport;

internal sealed class FakeRunnerBackend : IRunnerBackend
{
    public ExecutionBackend BackendType { get; init; } = ExecutionBackend.Native;
    public bool IsAvailable { get; set; } = true;
    public List<RunnerStartRequest> StartRequests { get; } = [];
    public List<string> StopRequests { get; } = [];
    public Dictionary<string, RunnerHealthStatus> HealthByHandle { get; } = new(StringComparer.Ordinal);
    public Func<RunnerStartRequest, RunnerInstanceInfo>? StartHandler { get; set; }
    public Func<RunnerStartRequest, CancellationToken, Task<RunnerInstanceInfo>>? StartAsyncHandler { get; set; }
    public Func<string, CancellationToken, Task>? StopAsyncHandler { get; set; }
    public Func<string, CancellationToken, Task<RunnerHealthStatus>>? HealthAsyncHandler { get; set; }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
        => Task.FromResult(IsAvailable);

    public Task<RunnerInstanceInfo> StartRunnerAsync(RunnerStartRequest request, CancellationToken ct = default)
    {
        StartRequests.Add(request);
        if (StartAsyncHandler != null)
            return StartAsyncHandler(request, ct);

        var info = StartHandler?.Invoke(request) ?? new RunnerInstanceInfo
        {
            InstanceHandle = $"handle-{request.RunnerName}",
            RunnerName = request.RunnerName
        };

        HealthByHandle.TryAdd(info.InstanceHandle, new RunnerHealthStatus
        {
            IsRunning = true,
            Status = "running"
        });

        return Task.FromResult(info);
    }

    public Task StopRunnerAsync(string instanceHandle, CancellationToken ct = default)
    {
        StopRequests.Add(instanceHandle);
        return StopAsyncHandler?.Invoke(instanceHandle, ct) ?? Task.CompletedTask;
    }

    public Task<RunnerHealthStatus> GetHealthAsync(string instanceHandle, CancellationToken ct = default)
        => HealthAsyncHandler?.Invoke(instanceHandle, ct)
           ?? Task.FromResult(HealthByHandle.TryGetValue(instanceHandle, out var health)
               ? health
               : new RunnerHealthStatus { IsRunning = false, Status = "missing" });
}
