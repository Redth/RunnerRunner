namespace RunnerRunner.Core.Models;

public class EnvironmentVariableSet
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public required string Name { get; set; }
    public string? Description { get; set; }
    public Dictionary<string, string> Variables { get; set; } = new();
    public HashSet<string> SecretKeys { get; set; } = [];
    public int Priority { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
