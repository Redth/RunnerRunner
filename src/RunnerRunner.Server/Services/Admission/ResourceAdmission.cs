using RunnerRunner.Core.Hub;
using RunnerRunner.Core.Models;

namespace RunnerRunner.Server.Services.Admission;

public sealed record ResourceAmounts(int CpuCount, long MemoryMiB, long DiskGiB)
{
    public static ResourceAmounts FromCommand(DeployRunnerCommand command)
    {
        var amounts = command.Backend switch
        {
            ExecutionBackend.Docker when command.DockerConfig is { } docker => new ResourceAmounts(
                docker.CpuCount ?? 0, docker.MemorySizeMb ?? 0, docker.DiskSizeGb ?? 0),
            ExecutionBackend.Tart when command.TartConfig is { } tart => new ResourceAmounts(
                tart.CpuCount ?? 0, checked((long)(tart.MemorySizeGb ?? 0) * 1024), tart.DiskSizeGb ?? 0),
            _ => throw new ResourceAdmissionException("An isolated Docker or Tart environment with explicit resource limits is required.")
        };
        if (amounts.CpuCount <= 0 || amounts.MemoryMiB <= 0 || amounts.DiskGiB <= 0)
            throw new ResourceAdmissionException("Positive CPU, memory and disk limits are required before admission.");
        return amounts;
    }
}

// This is the job budget AFTER the OS, services and persistent VMs have been reserved.
// It is deliberately independent of heartbeat snapshots and Orleans display counters.
public sealed record HostResourceBudget(int CpuCount, long MemoryMiB, long DiskGiB, int DockerSlots, int TartSlots)
{
    public void Validate()
    {
        if (CpuCount < 0 || MemoryMiB < 0 || DiskGiB < 0 || DockerSlots < 0 || TartSlots < 0)
            throw new ResourceAdmissionException("Host job budgets must be nonnegative.");
    }
}

public sealed record ResourceReservation(string InstanceId, string HostId, ExecutionBackend Backend,
    ResourceAmounts Amounts, string? InstanceHandle, bool Released);

public sealed class ResourceAdmissionException(string message) : InvalidOperationException(message);

public interface IResourceAdmission
{
    Task ReserveAsync(string hostId, DeployRunnerCommand command, CancellationToken ct = default);
    Task<bool> RecordStartedAsync(string hostId, string instanceId, ExecutionBackend backend, string handle, CancellationToken ct = default);
    Task<bool> ConfirmCleanupAsync(string hostId, string instanceId, ExecutionBackend backend, string? handle, bool neverStarted, CancellationToken ct = default);
    Task<ResourceReservation?> GetReservationAsync(string instanceId, CancellationToken ct = default);
    Task<HostResourceBudget?> GetBudgetAsync(string hostId, CancellationToken ct = default);
    Task SetBudgetAsync(string hostId, HostResourceBudget budget, CancellationToken ct = default);
}
