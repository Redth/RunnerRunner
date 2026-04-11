namespace RunnerRunner.Core.Models;

public class Host
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public required string Name { get; set; }
    public HostPlatform Platform { get; set; }
    public AgentStatus AgentStatus { get; set; } = AgentStatus.Offline;
    public DateTime? LastHeartbeat { get; set; }
    public string? AgentVersion { get; set; }
    public string? OsVersion { get; set; }
    public string? Architecture { get; set; }
    public List<string> Capabilities { get; set; } = [];
    public Dictionary<string, string> EnvironmentOverrides { get; set; } = new();
    public string? EnrollmentToken { get; set; }
    public bool IsApproved { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
