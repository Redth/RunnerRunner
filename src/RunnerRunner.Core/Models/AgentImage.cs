namespace RunnerRunner.Core.Models;

public enum ImageType
{
    Docker,
    Tart
}

/// <summary>
/// Cached image inventory entry reported by an agent.
/// Server stores this as a cache — the agent is the source of truth.
/// </summary>
public class AgentImage
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public required string HostId { get; set; }
    public ImageType ImageType { get; set; }
    public required string Repository { get; set; }
    public string Tag { get; set; } = "latest";
    public string? ImageId { get; set; }
    public long SizeBytes { get; set; }
    public DateTime? ImageCreatedAt { get; set; }
    public DateTime LastReportedAt { get; set; } = DateTime.UtcNow;
}
