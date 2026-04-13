using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Grains.Interfaces;
using RunnerRunner.Server.Grains.State;
using Shiny.DocumentDb;

namespace RunnerRunner.Server.Grains;

public class ProfileGrain : Grain, IProfileGrain
{
    private readonly IPersistentState<ProfileGrainStateWrapper> _state;
    private readonly ILogger<ProfileGrain> _logger;
    private readonly IServiceProvider _serviceProvider;

    private Dictionary<string, string>? _cachedEnvVars;

    public ProfileGrain(
        [PersistentState("profile", "PersistentStore")]
        IPersistentState<ProfileGrainStateWrapper> state,
        ILogger<ProfileGrain> logger,
        IServiceProvider serviceProvider)
    {
        _state = state;
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    public Task<RunnerProfile?> GetProfile()
    {
        return Task.FromResult(_state.State.Profile);
    }

    public async Task SetProfile(RunnerProfile profile)
    {
        _state.State.Profile = profile;
        _cachedEnvVars = null;
        await _state.WriteStateAsync();
        _logger.LogInformation("Profile {ProfileId} updated: {Name}", this.GetPrimaryKeyString(), profile.Name);
    }

    public async Task<Dictionary<string, string>> ComposeEnvironmentVariables()
    {
        if (_cachedEnvVars is not null)
            return _cachedEnvVars;

        var profile = _state.State.Profile;
        if (profile is null)
            return new Dictionary<string, string>();

        var composed = new Dictionary<string, string>();

        if (profile.EnvironmentVariableSetIds.Count > 0)
        {
            using var scope = _serviceProvider.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();

            foreach (var setId in profile.EnvironmentVariableSetIds)
            {
                var envSet = await store.Get<EnvironmentVariableSet>(setId);
                if (envSet is null)
                {
                    _logger.LogWarning("Environment variable set {SetId} not found for profile {ProfileId}", setId, this.GetPrimaryKeyString());
                    continue;
                }

                foreach (var kvp in envSet.Variables)
                {
                    composed[kvp.Key] = kvp.Value;
                }
            }
        }

        // Profile-level overrides take precedence
        foreach (var kvp in profile.EnvironmentOverrides)
        {
            composed[kvp.Key] = kvp.Value;
        }

        _cachedEnvVars = composed;
        return composed;
    }
}
