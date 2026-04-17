using Orleans;

namespace RunnerRunner.Core.Models;

[GenerateSerializer]
public class EnvironmentVariableSet
{
    [Id(0)]
    public string Id { get; set; } = Guid.NewGuid().ToString();
    [Id(1)]
    public required string Name { get; set; }
    [Id(2)]
    public string? Description { get; set; }
    [Id(3)]
    public Dictionary<string, string> Variables { get; set; } = new();
    [Id(4)]
    public HashSet<string> SecretKeys { get; set; } = [];
    [Id(5)]
    public int Priority { get; set; }
    [Id(6)]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [Id(7)]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
