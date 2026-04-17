using RunnerRunner.Agent.Backends;

namespace RunnerRunner.Agent.Tests.Backends;

public class DockerBackendTests
{
    [Fact]
    public void BuildJitEntrypointOverride_UsesLinuxShellWrapperForLinuxContainers()
    {
        var entrypoint = DockerBackend.BuildJitEntrypointOverride(
            isWindowsContainer: false,
            entrypoint: ["/bin/bash", "-lc", "echo hello"],
            cmd: [],
            shell: ["/bin/bash", "-lc"]);

        Assert.Equal("/bin/bash", entrypoint[0]);
        Assert.Equal("-lc", entrypoint[1]);
        Assert.Contains("run.sh", entrypoint[2]);
        Assert.Contains("/home/*/actions-runner/run.sh", entrypoint[2]);
        Assert.Contains("find /home /actions-runner /runner", entrypoint[2]);
        Assert.Contains("RR_JIT_CONFIG", entrypoint[2]);
    }

    [Fact]
    public void BuildJitEntrypointOverride_UsesPowerShellWrapperForWindowsContainers()
    {
        var entrypoint = DockerBackend.BuildJitEntrypointOverride(
            isWindowsContainer: true,
            entrypoint: ["cmd.exe", "/S", "/C", "echo hello"],
            cmd: [],
            shell: null);

        Assert.Equal("powershell.exe", entrypoint[0]);
        Assert.Contains("run.cmd", entrypoint[4]);
        Assert.Contains("RR_JIT_CONFIG", entrypoint[4]);
        Assert.Contains("cmd.exe", entrypoint[4]);
    }
}
