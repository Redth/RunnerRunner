using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RunnerRunner.Agent.Services;
using RunnerRunner.Core.HostWorkers;
using RunnerRunner.Core.Interfaces;
using RunnerRunner.Core.Models;
using RunnerRunner.HostWorker.Services;
using RunnerRunner.HostWorker.Tests.TestSupport;

namespace RunnerRunner.HostWorker.Tests.Services;

public class HostWorkerConnectionServiceTests
{
    [Fact]
    public void CreateHello_IncludesIdentityEnrollmentRuntimeAndLabels()
    {
        using var directory = HostWorkerTestDirectory.Create("connection-hello");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HostWorker:DataRoot"] = directory.Path,
                ["HostWorker:EnrollmentToken"] = "enroll-token",
                ["DOCKER_HOST"] = "unix:///var/run/docker.sock"
            })
            .Build();
        var identity = new HostWorkerIdentity("host-hello", "Hello Host", HostPlatform.Linux, "arm64");
        var paths = new HostWorkerPaths(configuration);
        var logStore = new HostWorkerLocalLogStore(paths);
        var lifecycle = new RunnerLifecycleManager(NullLogger<RunnerLifecycleManager>.Instance);
        using var processor = new HostCommandProcessor(
            configuration,
            identity,
            lifecycle,
            new ImageManager(NullLogger<ImageManager>.Instance),
            paths,
            logStore,
            new HostWorkerSelfUpdater(configuration, identity, paths, NullLogger<HostWorkerSelfUpdater>.Instance),
            new HostResourceUsageCollector(
                configuration,
                identity,
                NullLogger<HostResourceUsageCollector>.Instance,
                NullLoggerFactory.Instance),
            NullLogger<HostCommandProcessor>.Instance,
            NullLoggerFactory.Instance,
            new FakeRunnerBackend { BackendType = ExecutionBackend.Docker },
            new FakeRunnerBackend { BackendType = ExecutionBackend.Tart },
            new FakeRunnerBackend { BackendType = ExecutionBackend.Native });
        var logPublisher = new HostWorkerLogPublisher(identity, logStore);
        using var connection = new HostWorkerConnectionService(
            configuration,
            identity,
            processor,
            lifecycle,
            logPublisher,
            NullLogger<HostWorkerConnectionService>.Instance,
            new HostResourceUsageCollector(
                configuration,
                identity,
                NullLogger<HostResourceUsageCollector>.Instance,
                NullLoggerFactory.Instance));

        var message = connection.CreateHello();
        var hello = HostWorkerProtocol.DeserializePayload<HostWorkerHello>(message);

        Assert.Equal(HostWorkerMessageKinds.Hello, message.Kind);
        Assert.Equal("host-hello", message.HostId);
        Assert.Equal("host-hello", hello.Agent.AgentId);
        Assert.Equal("Hello Host", hello.Agent.Name);
        Assert.Equal(HostPlatform.Linux, hello.Agent.Platform);
        Assert.Equal("arm64", hello.Agent.Architecture);
        Assert.Equal("enroll-token", hello.EnrollmentToken);
        Assert.Equal("linux", hello.Labels["os"]);
        Assert.Equal("arm64", hello.Labels["arch"]);
        Assert.Equal("true", hello.Labels["docker"]);
        Assert.Equal("linux", hello.Labels["docker_os"]);
        Assert.Contains("native", hello.Agent.Capabilities);
        Assert.Contains("docker", hello.Agent.Capabilities);
        Assert.NotNull(hello.Agent.Runtime);
    }

    [Fact]
    public void DetectCapabilities_AddsDockerWhenDockerHostIsConfigured()
    {
        var identity = new HostWorkerIdentity("windows-docker", "windows-docker", HostPlatform.Windows, "X64");

        var capabilities = HostWorkerConnectionService.DetectCapabilities(
            identity,
            "npipe://./pipe/docker_engine",
            _ => false,
            _ => false);

        Assert.Contains("native", capabilities);
        Assert.Contains("docker", capabilities);
    }

    [Fact]
    public void DetectCapabilities_DoesNotAddDockerWithoutDockerHint()
    {
        var identity = new HostWorkerIdentity("windows-native", "windows-native", HostPlatform.Windows, "X64");

        var capabilities = HostWorkerConnectionService.DetectCapabilities(
            identity,
            null,
            _ => false,
            _ => false);

        Assert.Contains("native", capabilities);
        Assert.DoesNotContain("docker", capabilities);
    }

    [Fact]
    public void DetectCapabilities_AddsTartOnlyForMacOSHosts()
    {
        var macIdentity = new HostWorkerIdentity("mac", "mac", HostPlatform.MacOS, "Arm64");
        var linuxIdentity = new HostWorkerIdentity("linux", "linux", HostPlatform.Linux, "X64");

        var macCapabilities = HostWorkerConnectionService.DetectCapabilities(
            macIdentity,
            null,
            _ => false,
            command => command == "tart");
        var linuxCapabilities = HostWorkerConnectionService.DetectCapabilities(
            linuxIdentity,
            null,
            _ => false,
            command => command == "tart");

        Assert.Contains("tart", macCapabilities);
        Assert.DoesNotContain("tart", linuxCapabilities);
    }

    [Theory]
    [InlineData(HostPlatform.Linux, null, "linux")]
    [InlineData(HostPlatform.MacOS, null, "linux")]
    [InlineData(HostPlatform.Windows, null, "linux")]
    [InlineData(HostPlatform.Windows, "windows", "windows")]
    public void ResolveDockerOs_DefaultsToLinuxUnlessConfigured(
        HostPlatform platform,
        string? configured,
        string expected)
    {
        Assert.Equal(expected, HostWorkerConnectionService.ResolveDockerOs(platform, configured));
    }
}
