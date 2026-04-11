using RunnerRunner.Core.Models;

namespace RunnerRunner.Core.Interfaces;

/// <summary>
/// Abstraction for CI provider-specific operations (GitHub, Gitea, AzDO).
/// Implemented server-side to manage registration tokens and version discovery.
/// </summary>
public interface IRunnerProviderPlugin
{
    RunnerProvider Provider { get; }

    /// <summary>
    /// Generate a short-lived registration token for runner auto-registration.
    /// </summary>
    Task<string> GetRegistrationTokenAsync(ProviderCredential credential, CancellationToken ct = default);

    /// <summary>
    /// Remove/deregister a runner by name from the CI provider.
    /// </summary>
    Task RemoveRunnerAsync(ProviderCredential credential, string runnerName, CancellationToken ct = default);

    /// <summary>
    /// Discover available runner agent versions from the provider's release feed.
    /// </summary>
    Task<List<RunnerAgentVersion>> GetAvailableVersionsAsync(CancellationToken ct = default);
}
