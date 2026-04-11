namespace RunnerRunner.Core.Models;

public class RunnerAgentVersion
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public RunnerProvider Provider { get; set; }
    public required string Version { get; set; }
    public string? DownloadUrlLinuxX64 { get; set; }
    public string? DownloadUrlLinuxArm64 { get; set; }
    public string? DownloadUrlMacOsX64 { get; set; }
    public string? DownloadUrlMacOsArm64 { get; set; }
    public string? DownloadUrlWindowsX64 { get; set; }
    public string? DownloadUrlWindowsArm64 { get; set; }
    public bool IsLatest { get; set; }
    public DateTime DiscoveredAt { get; set; } = DateTime.UtcNow;
}
