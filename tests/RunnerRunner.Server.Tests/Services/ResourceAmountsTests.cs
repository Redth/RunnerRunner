using RunnerRunner.Core.Hub;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Services.Admission;

namespace RunnerRunner.Server.Tests.Services;

public class ResourceAmountsTests
{
    [Theory]
    [InlineData(0, 4096, 64)]
    [InlineData(2, 0, 64)]
    [InlineData(2, 4096, 0)]
    [InlineData(-1, 4096, 64)]
    public void MissingOrInvalidDockerResourcesFailClosed(int cpu, int memory, int disk)
    {
        Assert.Throws<ResourceAdmissionException>(() => ResourceAmounts.FromCommand(new DeployRunnerCommand
        {
            RunnerName = "runner", Backend = ExecutionBackend.Docker,
            DockerConfig = new() { RegistryUrl = "", ImageName = "fixture", CpuCount = cpu, MemorySizeMb = memory, DiskSizeGb = disk }
        }));
    }

    [Fact]
    public void TartMemoryUsesMiBWithoutIntegerOverflow()
    {
        Assert.Equal(checked((long)int.MaxValue * 1024), ResourceAmounts.FromCommand(new DeployRunnerCommand
        {
            RunnerName = "runner", Backend = ExecutionBackend.Tart,
            TartConfig = new() { RegistryUrl = "", ImageName = "fixture", CpuCount = 2, MemorySizeGb = int.MaxValue, DiskSizeGb = 64 }
        }).MemoryMiB);
    }

    [Fact]
    public void UnboundedNativeExecutionIsNotAdmitted()
        => Assert.Throws<ResourceAdmissionException>(() => ResourceAmounts.FromCommand(new DeployRunnerCommand { RunnerName = "runner", Backend = ExecutionBackend.Native }));
}
