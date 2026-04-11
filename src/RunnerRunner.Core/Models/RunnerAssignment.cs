namespace RunnerRunner.Core.Models;

public class RunnerAssignment
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string HostId { get; set; } = "";
    public string ProfileId { get; set; } = "";    public int DesiredCount { get; set; } = 1;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
