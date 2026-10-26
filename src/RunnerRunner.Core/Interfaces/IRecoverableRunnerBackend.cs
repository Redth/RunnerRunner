using System.Security.Cryptography;
using System.Text;

namespace RunnerRunner.Core.Interfaces;

/// <summary>Inspection of an already-owned resource; must never create or start it.</summary>
public interface IRecoverableRunnerBackend
{
    Task<NativeRunnerObservation> InspectOwnedAsync(NativeRunnerOwnership ownership, string? handle, CancellationToken ct);
}

public enum NativeRunnerState { Unknown, Absent, Created, Running, Exited }

public sealed record NativeRunnerObservation(NativeRunnerState State, string? Handle, long? ExitCode = null);

/// <summary>Public immutable ownership facts, independent of registration credentials.</summary>
public sealed record NativeRunnerOwnership
{
    public required string HostId { get; init; }
    public required string InstanceId { get; init; }
    public required string IntentId { get; init; }
    public required string ProfileId { get; init; }
    public required string RunnerName { get; init; }
    public int? CpuCount { get; init; }
    public int? MemoryMiB { get; init; }
    public int? DiskGiB { get; init; }
    public bool HyperVIsolation { get; init; }
    public string? ImageId { get; init; }
    public string? Platform { get; init; }

    public string NativeName => "rr-" + Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(HostId + "\0" + InstanceId))).ToLowerInvariant();
}
