using System.Formats.Tar;
using RunnerRunner.Agent.Backends;
using RunnerRunner.Core.Models;

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
        Assert.Contains("Refusing to idle on the image entrypoint", entrypoint[4]);
        Assert.DoesNotContain("cmd.exe", entrypoint[4]);
    }

    [Fact]
    public void BuildJitEntrypointOverride_IncludesPreAndPostInitSteps()
    {
        var entrypoint = DockerBackend.BuildJitEntrypointOverride(
            isWindowsContainer: false,
            entrypoint: null,
            cmd: null,
            shell: ["/bin/sh", "-c"],
            initSteps:
            [
                new ResolvedInitStep
                {
                    Name = "prepare",
                    Phase = InitStepPhase.PreRunner,
                    Shell = InitStepShell.Sh,
                    Script = "echo pre"
                },
                new ResolvedInitStep
                {
                    Name = "cleanup",
                    Phase = InitStepPhase.PostExit,
                    Shell = InitStepShell.Sh,
                    Script = "echo post"
                }
            ]);

        Assert.Equal("/bin/sh", entrypoint[0]);
        Assert.Contains("echo pre", entrypoint[2]);
        Assert.Contains("echo post", entrypoint[2]);
    }

    [Fact]
    public void BuildHookTarArchive_ContainsExecutableHookScript()
    {
        using var stream = DockerBackend.BuildHookTarArchive(
            "runnerrunner",
            "job-started.sh",
            "echo job-started",
            isWindows: false);
        using var reader = new TarReader(stream);

        var entries = new Dictionary<string, string?>();
        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) != null)
        {
            string? body = null;
            if (entry.DataStream != null)
            {
                using var textReader = new StreamReader(entry.DataStream);
                body = textReader.ReadToEnd();
            }

            entries[entry.Name] = body;
        }

        Assert.True(entries.ContainsKey("runnerrunner/"));
        Assert.Equal("echo job-started", entries["runnerrunner/job-started.sh"]);
    }
}
