namespace RunnerRunner.Core.Models;

/// <summary>
/// Records a single status transition in a runner instance's lifecycle.
/// </summary>
[GenerateSerializer]
public class StatusHistoryEntry
{
    [Id(0)]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    [Id(1)]
    public RunnerInstanceStatus Status { get; set; }
    [Id(2)]
    public string? StatusMessage { get; set; }

    /// <summary>
    /// What triggered the transition: "grain_call", "timer", "webhook", "health_check"
    /// </summary>
    [Id(3)]
    public string Source { get; set; } = "grain_call";
}
