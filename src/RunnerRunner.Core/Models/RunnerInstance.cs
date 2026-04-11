namespace RunnerRunner.Core.Models;

public class RunnerInstance
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string HostId { get; set; } = "";
    public string ProfileId { get; set; } = "";

    public required string RunnerName { get; set; }
    public RunnerInstanceStatus Status { get; set; } = RunnerInstanceStatus.Pending;
    public string? ContainerId { get; set; }
    public string? VmName { get; set; }
    public int? ProcessId { get; set; }
    public string? ErrorMessage { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? StoppedAt { get; set; }
    public DateTime? LastHealthCheck { get; set; }
}
