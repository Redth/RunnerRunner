using System.Formats.Tar;
using RunnerRunner.Agent.Backends;
using RunnerRunner.Core.Models;
using RunnerRunner.Core.Interfaces;
using Docker.DotNet.Models;

namespace RunnerRunner.Agent.Tests.Backends;

public class DockerBackendTests
{
    [Theory]
    [InlineData("running", true, NativeRunnerState.Running)]
    [InlineData("created", false, NativeRunnerState.Created)]
    [InlineData("exited", false, NativeRunnerState.Exited)]
    [InlineData("restarting", true, NativeRunnerState.Unknown)]
    public void Recovery_InspectsOwnedStateWithoutStarting(string state, bool running, NativeRunnerState expected)
    {
        var (owner, inspect) = OwnedInspection();
        inspect.State = new ContainerState { Status = state, Running = running, ExitCode = 0 };
        var result = DockerBackend.ObserveOwned(inspect, owner, null); // Lost Create response recovered by nonce/name.
        Assert.Equal(expected, result.State);
        Assert.Equal("native-id", result.Handle);
    }

    [Theory]
    [InlineData("host")]
    [InlineData("intent")]
    [InlineData("profile")]
    [InlineData("image")]
    [InlineData("name")]
    [InlineData("platform")]
    [InlineData("cpu")]
    [InlineData("memory")]
    [InlineData("disk")]
    [InlineData("isolation")]
    [InlineData("handle")]
    public void Recovery_RefusesForeignOrChangedNativeResource(string changed)
    {
        var (owner, inspect) = OwnedInspection();
        switch (changed)
        {
            case "host": inspect.Config.Labels["runnerrunner.host-id"] = "foreign"; break;
            case "intent": inspect.Config.Labels["runnerrunner.intent-id"] = "foreign"; break;
            case "profile": inspect.Config.Labels["runnerrunner.profile-id"] = "foreign"; break;
            case "image": inspect.Image = "other-image"; break;
            case "name": inspect.Name = "/rr-name-only-collision"; break;
            case "platform": inspect.Platform = "linux"; break;
            case "cpu": inspect.HostConfig.CPUCount++; break;
            case "memory": inspect.HostConfig.Memory++; break;
            case "disk": inspect.HostConfig.StorageOpt["size"] = "999G"; break;
            case "isolation": inspect.HostConfig.Isolation = "process"; break;
            case "handle": inspect.ID = "replacement-id"; break;
        }
        Assert.Throws<InvalidOperationException>(() => DockerBackend.ObserveOwned(inspect, owner, "native-id"));
    }

    private static (NativeRunnerOwnership, ContainerInspectResponse) OwnedInspection()
    {
        var owner = new NativeRunnerOwnership { HostId = "host", InstanceId = "instance", IntentId = "nonce",
            ProfileId = "profile", RunnerName = "runner", ImageId = "sha256:immutable", Platform = "windows",
            CpuCount = 2, MemoryMiB = 1024, DiskGiB = 20, HyperVIsolation = true };
        var inspect = new ContainerInspectResponse
        {
            ID = "native-id", Name = "/" + owner.NativeName, Image = owner.ImageId, Platform = "windows",
            State = new ContainerState { Status = "running", Running = true },
            Config = new Config { Labels = new Dictionary<string, string>
            {
                ["runnerrunner.managed"] = "true", ["runnerrunner.host-id"] = owner.HostId,
                ["runnerrunner.intent-id"] = owner.IntentId, ["runnerrunner.instance-id"] = owner.InstanceId,
                ["runnerrunner.profile-id"] = owner.ProfileId, ["runnerrunner.runner-name"] = owner.RunnerName
            } },
            HostConfig = DockerBackend.BuildHostConfig(new DockerImageConfig { RegistryUrl = "", ImageName = "image",
                CpuCount = 2, MemorySizeMb = 1024, DiskSizeGb = 20, HyperVIsolation = true }, true, true)
        };
        return (owner, inspect);
    }

    [Fact]
    public void WindowsHostConfig_RequiresExplicitLimitsAndHyperVIsolation()
    {
        var config = new DockerImageConfig { RegistryUrl = "", ImageName = "qualified", CpuCount = 6, MemorySizeMb = 9216, DiskSizeGb = 256, HyperVIsolation = true };
        var host = DockerBackend.BuildHostConfig(config, isWindows: true, ephemeral: true);
        Assert.Equal("hyperv", host.Isolation);
        Assert.Equal(6, host.CPUCount);
        Assert.Equal(9216L * 1024 * 1024, host.Memory);
        Assert.Equal("256G", host.StorageOpt["size"]);
        Assert.False(host.AutoRemove);
        Assert.Equal(Docker.DotNet.Models.RestartPolicyKind.No, host.RestartPolicy.Name);
        Assert.Null(host.Binds);
    }

    [Fact]
    public void WindowsHostConfig_PreservesDefaultIsolationAndOptionalLimits()
    {
        var config = new DockerImageConfig { RegistryUrl = "", ImageName = "existing" };
        var host = DockerBackend.BuildHostConfig(config, true, true);
        Assert.Equal("default", host.Isolation);
        Assert.Equal(0, host.CPUCount);
        Assert.Equal(0, host.Memory);
        Assert.Null(host.StorageOpt);
        config.CpuCount = 2;
        Assert.Equal(2, DockerBackend.BuildHostConfig(config, true, true).CPUCount);
    }

    [Theory]
    [InlineData(0, 1024, 20)]
    [InlineData(2, -1, 20)]
    [InlineData(2, 1024, 0)]
    public void WindowsHostConfig_RejectsInvalidLimits(int cpu, int memory, int disk)
    {
        var config = new DockerImageConfig { RegistryUrl = "", ImageName = "qualified", CpuCount = cpu, MemorySizeMb = memory, DiskSizeGb = disk };
        Assert.Throws<InvalidOperationException>(() => DockerBackend.BuildHostConfig(config, true, true));
    }

    [Fact]
    public void LinuxHostConfig_UsesLinuxCpuLimitWithoutWindowsIsolation()
    {
        var config = new DockerImageConfig { RegistryUrl = "", ImageName = "qualified", CpuCount = 2, MemorySizeMb = 1024 };
        var host = DockerBackend.BuildHostConfig(config, false, true);
        Assert.Equal(2_000_000_000, host.NanoCPUs);
        Assert.Equal(0, host.CPUCount);
        Assert.Null(host.Isolation);
        Assert.Null(host.StorageOpt);
    }

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
