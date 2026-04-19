using RunnerRunner.Core.Models;
using Shiny.DocumentDb;

namespace RunnerRunner.Server.Services;

/// <summary>
/// Resolves a profile's <see cref="RunnerInitStep"/> definitions into transport-ready
/// <see cref="ResolvedInitStep"/> instances. Handles per-step env composition
/// (referenced sets + overrides on top of the runner's base env) and resolves
/// <see cref="InitStepShell.Auto"/> based on the target backend / host platform.
/// </summary>
public static class InitStepResolver
{
    public static async Task<List<ResolvedInitStep>> ResolveAsync(
        IDocumentStore store,
        RunnerProfile profile,
        IReadOnlyDictionary<string, string> baseRunnerEnv,
        ExecutionBackend backend,
        HostPlatform hostPlatform)
    {
        var resolved = new List<ResolvedInitStep>(profile.InitSteps.Count);
        if (profile.InitSteps.Count == 0)
            return resolved;

        var neededSetIds = profile.InitSteps
            .Where(s => s.Enabled)
            .SelectMany(s => s.EnvironmentVariableSetIds)
            .ToHashSet(StringComparer.Ordinal);

        var allSets = neededSetIds.Count > 0
            ? (await store.Query<EnvironmentVariableSet>().ToList()).ToList()
            : new List<EnvironmentVariableSet>();

        foreach (var step in profile.InitSteps)
        {
            if (!step.Enabled)
                continue;

            // Start with the runner's base env so steps see e.g. RR_INSTANCE_ID, tokens, etc.
            var env = new Dictionary<string, string>(baseRunnerEnv, StringComparer.Ordinal);
            var secretKeys = new HashSet<string>(profile.EnvironmentOverrideSecretKeys);

            // Layer 1: step's referenced env variable sets (in priority order)
            var selectedSets = allSets
                .Where(s => step.EnvironmentVariableSetIds.Contains(s.Id))
                .OrderBy(s => s.Priority)
                .ToList();

            foreach (var set in selectedSets)
            {
                foreach (var kvp in set.Variables)
                    env[kvp.Key] = kvp.Value;
                foreach (var key in set.SecretKeys)
                    secretKeys.Add(key);
            }

            // Layer 2: step-level overrides
            foreach (var kvp in step.EnvironmentOverrides)
                env[kvp.Key] = kvp.Value;
            foreach (var key in step.EnvironmentOverrideSecretKeys)
                secretKeys.Add(key);

            resolved.Add(new ResolvedInitStep
            {
                Id = step.Id,
                Name = step.Name,
                Phase = step.Phase,
                Shell = ResolveShell(step.Shell, backend, hostPlatform),
                Script = step.Script,
                ContinueOnError = step.ContinueOnError,
                TimeoutSeconds = step.TimeoutSeconds,
                WorkingDirectory = step.WorkingDirectory,
                EnvironmentVariables = env,
                SecretKeys = secretKeys,
            });
        }

        return resolved;
    }

    private static InitStepShell ResolveShell(InitStepShell requested, ExecutionBackend backend, HostPlatform hostPlatform)
    {
        if (requested != InitStepShell.Auto)
            return requested;

        // For Docker we can't know for sure what's in the image; default to bash on
        // Linux images and PowerShell on Windows images. Backend wrappers fall back
        // to /bin/sh if bash is unavailable in the container.
        return backend switch
        {
            ExecutionBackend.Tart => InitStepShell.Bash, // Tart is macOS
            ExecutionBackend.Docker => hostPlatform == HostPlatform.Windows
                ? InitStepShell.PowerShell
                : InitStepShell.Bash,
            ExecutionBackend.Native => hostPlatform == HostPlatform.Windows
                ? InitStepShell.PowerShell
                : InitStepShell.Bash,
            _ => InitStepShell.Bash,
        };
    }
}
