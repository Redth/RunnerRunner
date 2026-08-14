using Microsoft.Extensions.Configuration;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Services.HostWorkers;

namespace RunnerRunner.Server.Tests.Services;

public sealed class HostWorkerEnrollmentGuideBuilderTests
{
    [Fact]
    public void ResolveServerUrl_UsesConfiguredPublicUrl()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["HostWorkerEnrollment:PublicServerUrl"] = "https://runner.example.com/hostworker"
        });

        var serverUrl = builder.ResolveServerUrl("http://localhost:4779/");

        Assert.Equal("https://runner.example.com/hostworker", serverUrl);
    }

    [Fact]
    public void ResolveServerUrl_DerivesGrpcPortFromDefaultUiPort()
    {
        var builder = CreateBuilder();

        var serverUrl = builder.ResolveServerUrl("http://192.168.2.4:4779/");

        Assert.Equal("http://192.168.2.4:4780", serverUrl);
    }

    [Fact]
    public void ResolveServerUrl_UsesOrleansAdvertisedIpWhenPublicUrlIsNotConfigured()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["Orleans:AdvertisedIPAddress"] = "192.168.2.4",
            ["Kestrel:Endpoints:HostWorkerGrpc:Url"] = "http://+:4780"
        });

        var serverUrl = builder.ResolveServerUrl("http://localhost:4779/");

        Assert.Equal("http://192.168.2.4:4780", serverUrl);
    }

    [Fact]
    public void ResolveServerUrl_UsesConfiguredAdvertisedPortWithOrleansIp()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["Orleans:AdvertisedIPAddress"] = "192.168.2.4",
            ["HostWorkerEnrollment:PublicServerScheme"] = "https",
            ["HostWorkerEnrollment:PublicServerPort"] = "9443"
        });

        var serverUrl = builder.ResolveServerUrl("http://localhost:4779/");

        Assert.Equal("https://192.168.2.4:9443", serverUrl);
    }

    [Fact]
    public void BuildLinuxDocker_IncludesConnectionArgumentsAndProxy()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["HostWorkerEnrollment:HostWorkerImage"] = "ghcr.io/example/worker:test"
        });

        var instructions = builder.Build(new HostWorkerEnrollmentRequest(
            HostWorkerEnrollmentTarget.LinuxDocker,
            "http://runner.example.com:4780",
            "token-123",
            "linux-host-1",
            "Linux Host 1",
            new HostWorkerEnrollmentProxy(
                "http://proxy:8080",
                "http://secure-proxy:8080",
                "localhost,127.0.0.1")));

        var command = Assert.Single(instructions.CommandBlocks).Command;
        Assert.Contains("ghcr.io/example/worker:test", command);
        Assert.Contains("HostWorker__ServerUrl: 'http://runner.example.com:4780'", command);
        Assert.Contains("HostWorker__EnrollmentToken: 'token-123'", command);
        Assert.Contains("HostWorker__HostId: 'linux-host-1'", command);
        Assert.Contains("HTTP_PROXY: 'http://proxy:8080'", command);
        Assert.Contains("HTTPS_PROXY: 'http://secure-proxy:8080'", command);
        Assert.Contains("NO_PROXY: 'localhost,127.0.0.1'", command);
    }

    [Fact]
    public void BuildWindowsService_ProducesPowerShellAutoSetup()
    {
        var builder = CreateBuilder();

        var instructions = builder.Build(new HostWorkerEnrollmentRequest(
            HostWorkerEnrollmentTarget.WindowsService,
            "http://runner.example.com:4780",
            "token-123",
            "windows-host-1",
            "Windows Host 1",
            new HostWorkerEnrollmentProxy(null, null, null)));

        Assert.Equal(HostWorkerEnrollmentRemoteShell.PowerShell, instructions.RemoteShell);
        Assert.Contains("Install-HostWorker.ps1", instructions.RemoteSetupScript);
        Assert.Contains("-ServerUrl 'http://runner.example.com:4780'", instructions.RemoteSetupScript);
        Assert.Contains("-EnrollmentToken 'token-123'", instructions.RemoteSetupScript);
    }

    [Fact]
    public void BuildWindowsDockerWindows_UsesSlashSafeDataMountTarget()
    {
        var builder = CreateBuilder();

        var instructions = builder.Build(new HostWorkerEnrollmentRequest(
            HostWorkerEnrollmentTarget.WindowsDockerWindows,
            "http://runner.example.com:4780",
            "token-123",
            "windows-docker-1",
            "Windows Docker 1",
            new HostWorkerEnrollmentProxy(null, null, null)));

        var command = Assert.Single(instructions.CommandBlocks).Command;
        Assert.Contains("target=C:/ProgramData/RunnerRunner", command);
        Assert.DoesNotContain(@"target=C:\ProgramData\RunnerRunner", command);
    }

    [Fact]
    public void BuildWindowsDockerLinux_ProvidesWsl2AndArm64Onboarding()
    {
        var builder = CreateBuilder();

        var instructions = builder.Build(new HostWorkerEnrollmentRequest(
            HostWorkerEnrollmentTarget.WindowsDockerLinux,
            "http://runner.example.com:4780",
            "token-123",
            "windows-wsl2-1",
            "Windows WSL2 1",
            new HostWorkerEnrollmentProxy(null, null, null)));

        Assert.Equal(HostPlatform.Linux, instructions.HostPlatform);
        Assert.Equal(3, instructions.CommandBlocks.Count);
        Assert.Contains("wsl --set-default-version 2", instructions.CommandBlocks[0].Command);
        Assert.Contains("wsl --install -d Ubuntu", instructions.CommandBlocks[0].Command);
        Assert.Contains("docker info --format '{{.OSType}}'", instructions.CommandBlocks[1].Command);
        Assert.Contains("ghcr.io/redth/runnerrunner-hostworker:latest", instructions.CommandBlocks[2].Command);
        Assert.Contains(instructions.Notes, note => note.Contains("downloads ARM64 runner agents automatically"));
        Assert.Contains(instructions.SetupLinks!, link => link.Title == "Install WSL");
        Assert.Contains(instructions.SetupLinks!, link => link.Title == "Docker Desktop WSL2 backend");
    }

    [Fact]
    public void BuildManualUpdate_ForMacOSNative_PreservesExistingConfig()
    {
        var builder = CreateBuilder();

        var instructions = builder.BuildManualUpdate(new HostWorkerManualUpdateRequest(
            HostWorkerEnrollmentTarget.MacOSNative,
            "host-1",
            "Mac Mini",
            "mac-mini",
            null,
            new HostWorkerEnrollmentProxy(null, null, null)));

        var command = Assert.Single(instructions.CommandBlocks).Command;
        Assert.Equal(HostWorkerEnrollmentRemoteShell.Bash, instructions.RemoteShell);
        Assert.Contains("--preserve-config", command);
        Assert.DoesNotContain("--enrollment-token", command);
    }

    [Fact]
    public void BuildManualUpdate_ForMacOSNative_UsesSelectedAsset()
    {
        var builder = CreateBuilder();

        var instructions = builder.BuildManualUpdate(new HostWorkerManualUpdateRequest(
            HostWorkerEnrollmentTarget.MacOSNative,
            "host-1",
            "Mac Mini",
            "mac-mini",
            null,
            new HostWorkerEnrollmentProxy(null, null, null),
            new HostWorkerManualUpdatePackage(
                HostWorkerUpdateSourceKind.Release,
                "main",
                "abc123def456",
                "runnerrunner-hostworker-osx-arm64.tar.gz",
                "https://runner.example.test/asset.tar.gz?sha256=abc",
                "abc",
                null)));

        var command = Assert.Single(instructions.CommandBlocks).Command;
        Assert.Contains("version='abc123def456'", command);
        Assert.Contains("asset_url='https://runner.example.test/asset.tar.gz?sha256=abc'", command);
        Assert.Contains("expected_sha256='abc'", command);
        Assert.Contains("set -euo pipefail", command);
        Assert.Contains("Authorization: Bearer ${enrollment_token}", command);
        Assert.Contains("if ! curl \"${curl_args[@]}\"", command);
        Assert.Contains("Failed to download HostWorker update asset from ${asset_url}", command);
        Assert.Contains("tar -xzf", command);
        Assert.DoesNotContain("install-host-macos.sh", command);
    }

    [Fact]
    public void BuildManualUpdate_ForWindowsService_UsesSelectedAssetWithExistingToken()
    {
        var builder = CreateBuilder();

        var instructions = builder.BuildManualUpdate(new HostWorkerManualUpdateRequest(
            HostWorkerEnrollmentTarget.WindowsService,
            "host-1",
            "Windows Host",
            "windows-host",
            null,
            new HostWorkerEnrollmentProxy(null, null, null),
            new HostWorkerManualUpdatePackage(
                HostWorkerUpdateSourceKind.Release,
                "main",
                "abc123def456",
                "runnerrunner-hostworker-win-x64.zip",
                "https://runner.example.test/asset.zip?sha256=abc",
                "abc",
                null)));

        var command = Assert.Single(instructions.CommandBlocks).Command;
        Assert.Equal(HostWorkerEnrollmentRemoteShell.PowerShell, instructions.RemoteShell);
        Assert.Contains("$downloadHeaders", command);
        Assert.Contains("Authorization", command);
        Assert.Contains("Invoke-WebRequest $assetUrl -OutFile $archive -Headers $downloadHeaders", command);
        Assert.Contains("Failed to download HostWorker update asset", command);
        Assert.Contains("Expand-Archive $archive", command);
    }

    [Fact]
    public void BuildManualUpdate_ForDocker_PreservesExistingContainerIdentity()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["HostWorkerEnrollment:HostWorkerImage"] = "ghcr.io/example/worker:test"
        });

        var instructions = builder.BuildManualUpdate(new HostWorkerManualUpdateRequest(
            HostWorkerEnrollmentTarget.LinuxDocker,
            "host-1",
            "Linux Host",
            "worker-1",
            "abcdef123456",
            new HostWorkerEnrollmentProxy(null, null, null)));

        var command = Assert.Single(instructions.CommandBlocks).Command;
        Assert.Contains("container='abcdef123456'", command);
        Assert.Contains("image='ghcr.io/example/worker:test'", command);
        Assert.Contains("HostWorker__EnrollmentToken=$enrollment_token", command);
        Assert.Contains("HostWorker__ServerUrl=$server_url", command);
        Assert.Contains("docker pull \"$image\"", command);
    }

    [Fact]
    public void BuildManualUpdate_ForDocker_UsesSelectedImage()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["HostWorkerEnrollment:HostWorkerImage"] = "ghcr.io/example/worker:latest"
        });

        var instructions = builder.BuildManualUpdate(new HostWorkerManualUpdateRequest(
            HostWorkerEnrollmentTarget.LinuxDocker,
            "host-1",
            "Linux Host",
            "worker-1",
            "abcdef123456",
            new HostWorkerEnrollmentProxy(null, null, null),
            new HostWorkerManualUpdatePackage(
                HostWorkerUpdateSourceKind.Release,
                "main",
                "abc123def456",
                null,
                null,
                null,
                "ghcr.io/example/worker:abc123def456")));

        var command = Assert.Single(instructions.CommandBlocks).Command;
        Assert.Contains("image='ghcr.io/example/worker:abc123def456'", command);
        Assert.DoesNotContain("image='ghcr.io/example/worker:latest'", command);
    }

    [Fact]
    public void BuildManualUpdate_ForWindowsDockerWindows_UsesSlashSafeDataMountTarget()
    {
        var builder = CreateBuilder();

        var instructions = builder.BuildManualUpdate(new HostWorkerManualUpdateRequest(
            HostWorkerEnrollmentTarget.WindowsDockerWindows,
            "host-1",
            "Windows Docker",
            "windows-docker",
            "abcdef123456",
            new HostWorkerEnrollmentProxy(null, null, null)));

        var command = Assert.Single(instructions.CommandBlocks).Command;
        Assert.Contains("target=C:/ProgramData/RunnerRunner", command);
        Assert.DoesNotContain(@"target=C:\ProgramData\RunnerRunner", command);
    }

    [Fact]
    public void BuildRemoval_ForWindowsService_RemovesServiceAndData()
    {
        var builder = CreateBuilder();

        var instructions = builder.BuildRemoval(new HostWorkerRemovalRequest(
            HostWorkerEnrollmentTarget.WindowsService,
            "host-1",
            "Windows Host",
            "windows-host",
            null,
            @"C:\rr",
            @"C:\rr\work"));

        var command = Assert.Single(instructions.CommandBlocks).Command;
        Assert.Equal(HostWorkerEnrollmentRemoteShell.PowerShell, instructions.RemoteShell);
        Assert.Contains("sc.exe delete $serviceName", command);
        Assert.Contains("Stop-Process -Id $runnerProcessId", command);
        Assert.Contains("rr.pid", command);
        Assert.Contains(@"C:\Program Files\RunnerRunner", command);
        Assert.Contains(@"C:\ProgramData\RunnerRunner", command);
        Assert.Contains(@"C:\rr", command);
    }

    [Fact]
    public void BuildRemoval_ForMacOSNative_UnloadsLaunchAgent()
    {
        var builder = CreateBuilder();

        var instructions = builder.BuildRemoval(new HostWorkerRemovalRequest(
            HostWorkerEnrollmentTarget.MacOSNative,
            "host-1",
            "Mac Host",
            "mac-host",
            null,
            "/Users/runner/.runnerrunner",
            "/Users/runner/.runnerrunner/work"));

        var command = Assert.Single(instructions.CommandBlocks).Command;
        Assert.Equal(HostWorkerEnrollmentRemoteShell.Bash, instructions.RemoteShell);
        Assert.Contains("launchctl bootout", command);
        Assert.Contains("kill \"$pid\"", command);
        Assert.Contains("rr.pid", command);
        Assert.Contains("com.runnerrunner.hostworker", command);
        Assert.Contains("rm -rf \"${install_root}\"", command);
    }

    [Fact]
    public void BuildRemoval_ForLinuxDocker_RemovesContainerAndVolumes()
    {
        var builder = CreateBuilder();

        var instructions = builder.BuildRemoval(new HostWorkerRemovalRequest(
            HostWorkerEnrollmentTarget.LinuxDocker,
            "host-1",
            "Linux Docker",
            "linux-docker",
            "abcdef123456",
            "/var/lib/runnerrunner",
            null));

        var command = Assert.Single(instructions.CommandBlocks).Command;
        Assert.Equal(HostWorkerEnrollmentRemoteShell.Bash, instructions.RemoteShell);
        Assert.Contains("container='abcdef123456'", command);
        Assert.Contains("docker compose down -v --remove-orphans", command);
        Assert.Contains("runnerrunner-hostworker-data", command);
        Assert.Contains("label=runnerrunner.managed=true", command);
    }

    [Fact]
    public void BuildRemoval_ForWindowsDocker_RemovesContainerAndVolume()
    {
        var builder = CreateBuilder();

        var instructions = builder.BuildRemoval(new HostWorkerRemovalRequest(
            HostWorkerEnrollmentTarget.WindowsDockerWindows,
            "host-1",
            "Windows Docker",
            "windows-docker",
            "abcdef123456",
            null,
            null));

        var command = Assert.Single(instructions.CommandBlocks).Command;
        Assert.Equal(HostWorkerEnrollmentRemoteShell.PowerShell, instructions.RemoteShell);
        Assert.Contains("$container = 'abcdef123456'", command);
        Assert.Contains("docker rm -f $container", command);
        Assert.Contains("runnerrunner-hostworker-windows-data", command);
        Assert.Contains("label=runnerrunner.managed=true", command);
    }

    [Fact]
    public void GetTargetForHost_UsesContainerizedWindowsTarget()
    {
        var host = new Host
        {
            Name = "windows-container",
            Platform = HostPlatform.Windows,
            IsContainerized = true
        };

        var target = HostWorkerEnrollmentGuideBuilder.GetTargetForHost(host);

        Assert.Equal(HostWorkerEnrollmentTarget.WindowsDockerWindows, target);
    }

    private static HostWorkerEnrollmentGuideBuilder CreateBuilder(Dictionary<string, string?>? values = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values ?? new Dictionary<string, string?>())
            .Build();

        return new HostWorkerEnrollmentGuideBuilder(configuration);
    }
}
