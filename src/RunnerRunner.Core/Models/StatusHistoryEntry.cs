namespace RunnerRunner.Core.Models;

/// <summary>
/// Records a single status transition in a runner instance's lifecycle.
/// </summary>
public class StatusHistoryEntry
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public RunnerInstanceStatus Status { get; set; }
    public string? StatusMessage { get; set; }

    /// <summary>
    /// What triggered the transition: "grain_call", "timer", "webhook", "health_check"
    /// </summary>
    public string Source { get; set; } = "grain_call";
}
