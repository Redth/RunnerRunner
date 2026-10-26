using RunnerRunner.Core.Interfaces;

namespace RunnerRunner.Agent.Backends;

/// <summary>Host-local Docker access. Signing keys never enter deployment commands or containers.</summary>
public interface IWindowsJobDocker
{
    IReadOnlyDictionary<string, string> Environment { get; }
    Task<byte[]> CreatePrivateInputAsync(NativeRunnerOwnership ownership, CancellationToken cancellationToken);
}
