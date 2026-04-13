using Orleans.Runtime;
using RunnerRunner.Server.Grains.Interfaces;
using RunnerRunner.Server.Grains.State;

namespace RunnerRunner.Server.Grains;

public class HostGroupGrain : Grain, IHostGroupGrain
{
    private readonly IPersistentState<HostGroupGrainState> _state;

    public HostGroupGrain(
        [PersistentState("hostGroup", "PersistentStore")]
        IPersistentState<HostGroupGrainState> state)
    {
        _state = state;
    }

    public async Task SetConfig(string name, string? description, Dictionary<string, string> sharedLabels)
    {
        _state.State.Name = name;
        _state.State.Description = description;
        _state.State.SharedLabels = sharedLabels;
        await _state.WriteStateAsync();
    }

    public async Task AddHost(string hostId)
    {
        if (!_state.State.HostIds.Contains(hostId))
        {
            _state.State.HostIds.Add(hostId);
            await _state.WriteStateAsync();
        }
    }

    public async Task RemoveHost(string hostId)
    {
        if (_state.State.HostIds.Remove(hostId))
            await _state.WriteStateAsync();
    }

    public Task<List<string>> GetHostIds() => Task.FromResult(_state.State.HostIds);

    public Task<HostGroupGrainState> GetState() => Task.FromResult(_state.State);
}
