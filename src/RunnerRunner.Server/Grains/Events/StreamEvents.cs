using RunnerRunner.Core.Models;

namespace RunnerRunner.Server.Grains.Events;

[GenerateSerializer]
public class RunnerStatusChangedEvent
{
    [Id(0)] public string InstanceId { get; set; } = "";
    [Id(1)] public string RunnerName { get; set; } = "";
    [Id(2)] public string HostId { get; set; } = "";
    [Id(3)] public RunnerInstanceStatus Status { get; set; }
    [Id(4)] public string? StatusMessage { get; set; }
    [Id(5)] public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

[GenerateSerializer]
public class HostStatusChangedEvent
{
    [Id(0)] public string HostId { get; set; } = "";
    [Id(1)] public string HostName { get; set; } = "";
    [Id(2)] public AgentStatus Status { get; set; }
    [Id(3)] public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
