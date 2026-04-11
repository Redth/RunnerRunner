using System.Diagnostics;
using RunnerRunner.Core.Hub;

namespace RunnerRunner.Agent.Services;

/// <summary>
/// Periodically reports agent health metrics to the server.
/// </summary>
public class HealthReporter
{
    private readonly ILogger<HealthReporter> _logger;
    private readonly RunnerLifecycleManager _lifecycleManager;

    public HealthReporter(ILogger<HealthReporter> logger, RunnerLifecycleManager lifecycleManager)
    {
        _logger = logger;
        _lifecycleManager = lifecycleManager;
    }

    public HeartbeatEvent CollectMetrics(string agentId)
    {
        var process = Process.GetCurrentProcess();
        return new HeartbeatEvent
        {
            AgentId = agentId,
            CpuUsagePercent = 0, // TODO: Implement proper CPU measurement
            MemoryUsagePercent = process.WorkingSet64 / (double)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes) * 100,
            DiskUsagePercent = 0, // TODO: Implement disk usage measurement
            RunningInstanceCount = _lifecycleManager.RunningInstances.Count
        };
    }
}
