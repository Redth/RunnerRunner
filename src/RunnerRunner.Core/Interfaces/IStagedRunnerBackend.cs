namespace RunnerRunner.Core.Interfaces;

public interface IStagedRunnerBackend
{
    Task ActivateRunnerAsync(NativeRunnerOwnership ownership, string handle, string jitConfig, CancellationToken ct);
}
