using Microsoft.Extensions.Logging;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Grains.Interfaces;

namespace RunnerRunner.Server.Grains;

public class SchedulerGrain : Grain, ISchedulerGrain
{
    private readonly ILogger<SchedulerGrain> _logger;
    private readonly HashSet<string> _registeredHosts = new();

    public SchedulerGrain(ILogger<SchedulerGrain> logger)
    {
        _logger = logger;
    }

    public Task RegisterHost(string hostId)
    {
        _registeredHosts.Add(hostId);
        _logger.LogInformation("Host {HostId} registered with scheduler ({Count} total)", hostId, _registeredHosts.Count);
        return Task.CompletedTask;
    }

    public Task UnregisterHost(string hostId)
    {
        _registeredHosts.Remove(hostId);
        _logger.LogInformation("Host {HostId} unregistered from scheduler ({Count} remaining)", hostId, _registeredHosts.Count);
        return Task.CompletedTask;
    }

    public async Task<string?> SelectHost(Dictionary<string, string> requiredLabels, ExecutionBackend backend)
    {
        if (_registeredHosts.Count == 0)
        {
            _logger.LogWarning("No hosts registered — cannot select host");
            return null;
        }

        string? bestHost = null;
        int bestLoad = int.MaxValue;

        foreach (var hostId in _registeredHosts)
        {
            var hostGrain = GrainFactory.GetGrain<IHostGrain>(hostId);
            var state = await hostGrain.GetState();

            if (state.Status != AgentStatus.Online)
                continue;

            if (!LabelsMatch(state.Labels, requiredLabels))
                continue;

            if (!await hostGrain.CanAcceptRunner(backend))
                continue;

            var totalRunning = state.RunningDockerContainers + state.RunningTartVMs + state.RunningNativeProcesses;
            if (totalRunning < bestLoad)
            {
                bestLoad = totalRunning;
                bestHost = hostId;
            }
        }

        if (bestHost != null)
            _logger.LogInformation("Selected host {HostId} for backend {Backend} (load: {Load})", bestHost, backend, bestLoad);
        else
            _logger.LogWarning("No eligible host found for backend {Backend} with labels {@Labels}", backend, requiredLabels);

        return bestHost;
    }

    private static bool LabelsMatch(Dictionary<string, string> hostLabels, Dictionary<string, string> required)
    {
        foreach (var req in required)
        {
            if (!hostLabels.TryGetValue(req.Key, out var hostVal) ||
                !hostVal.Equals(req.Value, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }
}
