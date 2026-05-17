using Orleans;

namespace RunnerRunner.Core.Models;

[GenerateSerializer]
public class HostResourceUsage
{
    [Id(0)]
    public int? RunningTartVmCount { get; set; }
}
