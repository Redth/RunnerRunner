namespace RunnerRunner.Core.Models;

public class TartImageConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public required string RegistryUrl { get; set; }
    public required string ImageName { get; set; }
    public string Tag { get; set; } = "latest";
    public int? DiskSizeGb { get; set; }
    public int? CpuCount { get; set; }
    public int? MemorySizeGb { get; set; }
    public string? Display { get; set; }
    public List<SharedDirMount> SharedDirs { get; set; } = [];

    /// <summary>SSH user for the VM (default: admin)</summary>
    public string SshUser { get; set; } = "admin";

    /// <summary>SSH password (if key-based auth not configured)</summary>
    public string? SshPassword { get; set; }
}

public class SharedDirMount
{
    public required string Name { get; set; }
    public required string HostPath { get; set; }
    public bool ReadOnly { get; set; }
}
