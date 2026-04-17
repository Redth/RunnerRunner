using Orleans;

namespace RunnerRunner.Core.Models;

[GenerateSerializer]
public class TartImageConfig
{
    [Id(0)]
    public string Id { get; set; } = Guid.NewGuid().ToString();
    [Id(1)]
    public required string RegistryUrl { get; set; }
    [Id(2)]
    public required string ImageName { get; set; }
    [Id(3)]
    public string Tag { get; set; } = "latest";
    [Id(4)]
    public int? DiskSizeGb { get; set; }
    [Id(5)]
    public int? CpuCount { get; set; }
    [Id(6)]
    public int? MemorySizeGb { get; set; }
    [Id(7)]
    public string? Display { get; set; }
    [Id(8)]
    public List<SharedDirMount> SharedDirs { get; set; } = [];

    /// <summary>SSH user for the VM (default: admin)</summary>
    [Id(9)]
    public string SshUser { get; set; } = "admin";

    /// <summary>SSH password (if key-based auth not configured)</summary>
    [Id(10)]
    public string? SshPassword { get; set; }
}

[GenerateSerializer]
public class SharedDirMount
{
    [Id(0)]
    public required string Name { get; set; }
    [Id(1)]
    public required string HostPath { get; set; }
    [Id(2)]
    public bool ReadOnly { get; set; }
}
