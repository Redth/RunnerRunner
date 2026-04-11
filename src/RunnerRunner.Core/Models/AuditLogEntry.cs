namespace RunnerRunner.Core.Models;

public class AuditLogEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public required string Action { get; set; }
    public required string EntityType { get; set; }
    public Guid? EntityId { get; set; }
    public string? Details { get; set; }
    public string? UserName { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
