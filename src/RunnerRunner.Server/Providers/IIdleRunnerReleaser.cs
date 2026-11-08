using RunnerRunner.Core.Models;

namespace RunnerRunner.Server.Providers;

/// <summary>
/// What the provider said when asked to give up a runner we want to stop. Only
/// <see cref="Deregistered"/>, <see cref="NotRegistered"/> and <see cref="NotChecked"/>
/// allow the stop: everything else means the runner may be running a job.
/// </summary>
public enum RunnerReleaseOutcome
{
    /// <summary>The provider removed the idle runner's registration, so it can no longer be handed a job.</summary>
    Deregistered,

    /// <summary>The provider has no registration for the runner: it already ran its one job, or never registered.</summary>
    NotRegistered,

    /// <summary>The provider says the runner is running a job. Stopping it would kill that job.</summary>
    Busy,

    /// <summary>The provider could not be asked (no credential, scope or a failed call). Treat as possibly busy.</summary>
    Unverified,

    /// <summary>The runner's provider can't be asked whether a runner is busy; the caller keeps its old behaviour.</summary>
    NotChecked
}

/// <summary>
/// A provider that can deregister a runner only while it is idle, atomically on the
/// provider's side. Stopping a just-in-time runner that only <em>looks</em> idle is not
/// safe: the provider can hand it any queued job with matching labels until its
/// registration is gone, including in the seconds a container takes to stop. Removing the
/// registration first, and only if the provider agrees the runner is idle, closes that window.
/// </summary>
public interface IIdleRunnerReleaser
{
    /// <summary>
    /// Deregisters <paramref name="runnerName"/> if, and only if, the provider says it is idle.
    /// <paramref name="repository"/> ("owner/repo") is where the runner was registered when known.
    /// Throws when the provider can't be asked.
    /// </summary>
    Task<RunnerReleaseOutcome> ReleaseIdleRunnerAsync(
        ProviderCredential credential,
        string? repository,
        string runnerName,
        CancellationToken ct = default);
}
