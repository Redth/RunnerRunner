using Orleans;

namespace RunnerRunner.Core.Models;

[GenerateSerializer]
public class HostResourceUsage
{
    [Id(0)]
    public int? RunningTartVmCount { get; set; }

    /// <summary>
    /// Free bytes on the volume that holds the host's working directories.
    /// Null when the host could not determine it (older HostWorker builds never report it).
    /// </summary>
    [Id(1)]
    public long? FreeDiskBytes { get; set; }

    /// <summary>Total size in bytes of the same volume, for context in the UI.</summary>
    [Id(2)]
    public long? TotalDiskBytes { get; set; }
}
