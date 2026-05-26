using System.Text;
using RunnerRunner.Core.Models;

namespace RunnerRunner.Server.Services.HostWorkers;

public enum HostWorkerEnrollmentTarget
{
    LinuxDocker,
    MacOSNative,
    MacOSDocker,
    WindowsService,
    WindowsDockerWindows,
    WindowsDockerLinux
}

public enum HostWorkerEnrollmentRemoteShell
{
    Bash,
    PowerShell,
    Unsupported
}

public sealed class HostWorkerEnrollmentOptions
{
    public string? PublicServerUrl { get; init; }
    public string ReleaseBaseUrl { get; init; } = "https://github.com/redth/RunnerRunner/releases/latest/download";
    public string HostWorkerImage { get; init; } = "ghcr.io/redth/runnerrunner-hostworker:latest";
    public string WindowsHostWorkerImage { get; init; } = "ghcr.io/redth/runnerrunner-hostworker-windows:latest";
    public string? HttpProxy { get; init; }
    public string? HttpsProxy { get; init; }
    public string? NoProxy { get; init; }
}

public sealed record HostWorkerEnrollmentProxy(string? HttpProxy, string? HttpsProxy, string? NoProxy)
{
    public bool HasAny =>
        !string.IsNullOrWhiteSpace(HttpProxy) ||
        !string.IsNullOrWhiteSpace(HttpsProxy) ||
        !string.IsNullOrWhiteSpace(NoProxy);
}

public sealed record HostWorkerEnrollmentRequest(
    HostWorkerEnrollmentTarget Target,
    string ServerUrl,
    string EnrollmentToken,
    string HostId,
    string HostName,
    HostWorkerEnrollmentProxy Proxy);

public sealed record HostWorkerManualUpdateRequest(
    HostWorkerEnrollmentTarget Target,
    string HostId,
    string HostName,
    string? WorkerId,
    string? ContainerId,
    HostWorkerEnrollmentProxy Proxy,
    HostWorkerManualUpdatePackage? Package = null);

public sealed record HostWorkerRemovalRequest(
    HostWorkerEnrollmentTarget Target,
    string HostId,
    string HostName,
    string? WorkerId,
    string? ContainerId,
    string? RunnerBasePath,
    string? WorkDirectory);

public sealed record HostWorkerManualUpdatePackage(
    HostWorkerUpdateSourceKind Source,
    string? RequestedVersion,
    string Version,
    string? AssetName,
    string? AssetUrl,
    string? Sha256,
    string? ContainerImage);

public sealed record HostWorkerEnrollmentCommandBlock(
    string Title,
    string Description,
    string Language,
    string Command);

public sealed record HostWorkerEnrollmentInstructions(
    HostWorkerEnrollmentTarget Target,
    HostPlatform HostPlatform,
    string Title,
    string Summary,
    IReadOnlyList<string> Prerequisites,
    IReadOnlyList<HostWorkerEnrollmentCommandBlock> CommandBlocks,
    string RemoteSetupScript,
    HostWorkerEnrollmentRemoteShell RemoteShell,
    IReadOnlyList<string> Notes);

public sealed class HostWorkerEnrollmentGuideBuilder
{
    private readonly IConfiguration _configuration;

    public HostWorkerEnrollmentGuideBuilder(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public HostWorkerEnrollmentOptions GetOptions()
    {
        var section = _configuration.GetSection("HostWorkerEnrollment");
        return new HostWorkerEnrollmentOptions
        {
            PublicServerUrl = NullIfWhiteSpace(section["PublicServerUrl"]),
            ReleaseBaseUrl = NullIfWhiteSpace(section["ReleaseBaseUrl"]) ?? "https://github.com/redth/RunnerRunner/releases/latest/download",
            HostWorkerImage = NullIfWhiteSpace(section["HostWorkerImage"]) ?? "ghcr.io/redth/runnerrunner-hostworker:latest",
            WindowsHostWorkerImage = NullIfWhiteSpace(section["WindowsHostWorkerImage"]) ?? "ghcr.io/redth/runnerrunner-hostworker-windows:latest",
            HttpProxy = NullIfWhiteSpace(section["HttpProxy"]),
            HttpsProxy = NullIfWhiteSpace(section["HttpsProxy"]),
            NoProxy = NullIfWhiteSpace(section["NoProxy"])
        };
    }

    public string ResolveServerUrl(string uiBaseUri)
    {
        var configured =
            NullIfWhiteSpace(GetOptions().PublicServerUrl) ??
            NullIfWhiteSpace(_configuration["HostWorker:AdvertisedServerUrl"]) ??
            NullIfWhiteSpace(_configuration["HostWorker:ServerUrl"]);

        if (configured != null)
            return configured.TrimEnd('/');

        var advertisedAddress = NullIfWhiteSpace(_configuration["Orleans:AdvertisedIPAddress"]);
        if (advertisedAddress != null)
            return BuildAdvertisedServerUrl(advertisedAddress);

        return DeriveServerUrlFromUiBase(uiBaseUri);
    }

    public HostWorkerEnrollmentInstructions Build(HostWorkerEnrollmentRequest request)
    {
        var options = GetOptions();
        return request.Target switch
        {
            HostWorkerEnrollmentTarget.LinuxDocker => BuildLinuxDocker(request, options),
            HostWorkerEnrollmentTarget.MacOSNative => BuildMacOSNative(request, options),
            HostWorkerEnrollmentTarget.MacOSDocker => BuildMacOSDocker(request, options),
            HostWorkerEnrollmentTarget.WindowsService => BuildWindowsService(request, options),
            HostWorkerEnrollmentTarget.WindowsDockerWindows => BuildWindowsDockerWindows(request, options),
            HostWorkerEnrollmentTarget.WindowsDockerLinux => BuildWindowsDockerLinux(request, options),
            _ => throw new ArgumentOutOfRangeException(nameof(request.Target), request.Target, "Unsupported enrollment target.")
        };
    }

    public HostWorkerEnrollmentInstructions BuildManualUpdate(HostWorkerManualUpdateRequest request)
    {
        var options = GetOptions();
        return request.Target switch
        {
            HostWorkerEnrollmentTarget.LinuxDocker => BuildDockerManualUpdate(request, options.HostWorkerImage, HostPlatform.Linux),
            HostWorkerEnrollmentTarget.MacOSDocker => BuildDockerManualUpdate(request, options.HostWorkerImage, HostPlatform.Linux),
            HostWorkerEnrollmentTarget.WindowsDockerLinux => BuildDockerManualUpdate(request, options.HostWorkerImage, HostPlatform.Linux),
            HostWorkerEnrollmentTarget.MacOSNative => BuildMacOSManualUpdate(request, options),
            HostWorkerEnrollmentTarget.WindowsService => BuildWindowsServiceManualUpdate(request, options),
            HostWorkerEnrollmentTarget.WindowsDockerWindows => BuildWindowsDockerManualUpdate(request, options),
            _ => throw new ArgumentOutOfRangeException(nameof(request.Target), request.Target, "Unsupported manual update target.")
        };
    }

    public HostWorkerEnrollmentInstructions BuildRemoval(HostWorkerRemovalRequest request)
        => request.Target switch
        {
            HostWorkerEnrollmentTarget.LinuxDocker => BuildDockerRemoval(request, HostPlatform.Linux),
            HostWorkerEnrollmentTarget.MacOSDocker => BuildDockerRemoval(request, HostPlatform.Linux),
            HostWorkerEnrollmentTarget.WindowsDockerLinux => BuildDockerRemoval(request, HostPlatform.Linux),
            HostWorkerEnrollmentTarget.MacOSNative => BuildMacOSNativeRemoval(request),
            HostWorkerEnrollmentTarget.WindowsService => BuildWindowsServiceRemoval(request),
            HostWorkerEnrollmentTarget.WindowsDockerWindows => BuildWindowsDockerRemoval(request),
            _ => throw new ArgumentOutOfRangeException(nameof(request.Target), request.Target, "Unsupported removal target.")
        };

    public static string GetTargetDisplayName(HostWorkerEnrollmentTarget target)
        => target switch
        {
            HostWorkerEnrollmentTarget.LinuxDocker => "Linux host - Docker",
            HostWorkerEnrollmentTarget.MacOSNative => "macOS host - native LaunchAgent",
            HostWorkerEnrollmentTarget.MacOSDocker => "macOS host - Docker Linux workers",
            HostWorkerEnrollmentTarget.WindowsService => "Windows host - native service",
            HostWorkerEnrollmentTarget.WindowsDockerWindows => "Windows host - Windows containers",
            HostWorkerEnrollmentTarget.WindowsDockerLinux => "Windows host - WSL/Linux Docker",
            _ => target.ToString()
        };

    public static string GetDefaultHostName(HostWorkerEnrollmentTarget target, string token)
    {
        var suffix = string.IsNullOrWhiteSpace(token) ? "new" : token[..Math.Min(6, token.Length)].ToLowerInvariant();
        var prefix = target switch
        {
            HostWorkerEnrollmentTarget.LinuxDocker => "linux-docker",
            HostWorkerEnrollmentTarget.MacOSNative => "mac",
            HostWorkerEnrollmentTarget.MacOSDocker => "mac-docker",
            HostWorkerEnrollmentTarget.WindowsService => "windows",
            HostWorkerEnrollmentTarget.WindowsDockerWindows => "windows-docker",
            HostWorkerEnrollmentTarget.WindowsDockerLinux => "windows-linux-docker",
            _ => "host"
        };
        return $"{prefix}-{suffix}";
    }

    public static HostWorkerEnrollmentTarget GetTargetForHost(Host host)
    {
        if (host.IsContainerized)
        {
            if (host.Platform == HostPlatform.Windows)
                return HostWorkerEnrollmentTarget.WindowsDockerWindows;

            return HostWorkerEnrollmentTarget.LinuxDocker;
        }

        return host.Platform switch
        {
            HostPlatform.MacOS => HostWorkerEnrollmentTarget.MacOSNative,
            HostPlatform.Windows => HostWorkerEnrollmentTarget.WindowsService,
            _ => HostWorkerEnrollmentTarget.LinuxDocker
        };
    }

    private HostWorkerEnrollmentInstructions BuildLinuxDocker(
        HostWorkerEnrollmentRequest request,
        HostWorkerEnrollmentOptions options)
    {
        var compose = $$"""
        mkdir -p ~/runnerrunner-hostworker
        cd ~/runnerrunner-hostworker
        cat > compose.yaml <<'YAML'
        services:
          host-worker:
            image: {{options.HostWorkerImage}}
            container_name: runnerrunner-host-worker
            restart: unless-stopped
            environment:
              HostWorker__ServerUrl: {{YamlString(request.ServerUrl)}}
              HostWorker__EnrollmentToken: {{YamlString(request.EnrollmentToken)}}
              HostWorker__HostId: {{YamlString(request.HostId)}}
              HostWorker__HostName: {{YamlString(request.HostName)}}
              HostWorker__Platform: Linux
              HostWorker__DataRoot: /var/lib/runnerrunner
              DOTNET_ENVIRONMENT: Production
        {{BuildComposeProxyEnvironment(request.Proxy)}}    volumes:
              - /var/run/docker.sock:/var/run/docker.sock
              - hostworker-data:/var/lib/runnerrunner

        volumes:
          hostworker-data:
        YAML

        docker compose up -d
        docker compose logs -f host-worker
        """;

        var remote = compose.Replace("docker compose logs -f host-worker", "docker compose ps host-worker && docker compose logs --tail=80 host-worker");

        return new HostWorkerEnrollmentInstructions(
            HostWorkerEnrollmentTarget.LinuxDocker,
            HostPlatform.Linux,
            GetTargetDisplayName(HostWorkerEnrollmentTarget.LinuxDocker),
            "Run the HostWorker as a Linux container with access to the host Docker socket. Use this for Linux hosts or any host that should provide Linux container runners.",
            [
                "Docker Engine and the Docker Compose plugin are installed.",
                "The target host can reach the advertised HostWorker gRPC URL.",
                "The user running the command can access /var/run/docker.sock."
            ],
            [
                new(
                    "Create and start the HostWorker compose service",
                    "Run this on the target Linux host. It creates a durable data volume and starts the worker.",
                    "bash",
                    NormalizeCommand(compose))
            ],
            NormalizeCommand(remote),
            HostWorkerEnrollmentRemoteShell.Bash,
            BuildNotes(request));
    }

    private HostWorkerEnrollmentInstructions BuildMacOSNative(
        HostWorkerEnrollmentRequest request,
        HostWorkerEnrollmentOptions options)
    {
        var command = $$"""
        curl -fsSL {{ShellQuote(options.ReleaseBaseUrl + "/install-host-macos.sh")}} -o install-host-macos.sh
        chmod +x install-host-macos.sh
        {{BuildBashProxyExports(request.Proxy)}}./install-host-macos.sh \
          --host-id {{ShellQuote(request.HostId)}} \
          --host-name {{ShellQuote(request.HostName)}} \
          --server-url {{ShellQuote(request.ServerUrl)}} \
          --enrollment-token {{ShellQuote(request.EnrollmentToken)}}{{BuildBashInstallerProxyArgs(request.Proxy)}}
        ~/.runnerrunner/runnerrunner-host logs
        """;

        var remote = command.Replace("~/.runnerrunner/runnerrunner-host logs", "~/.runnerrunner/runnerrunner-host status");

        return new HostWorkerEnrollmentInstructions(
            HostWorkerEnrollmentTarget.MacOSNative,
            HostPlatform.MacOS,
            GetTargetDisplayName(HostWorkerEnrollmentTarget.MacOSNative),
            "Install the HostWorker as a LaunchAgent for the interactive macOS user. This is the best choice for Tart, Xcode, Keychain, and native macOS runner work.",
            [
                "Run from the macOS user account that should own runners.",
                "The target can reach the advertised HostWorker gRPC URL.",
                "Install Docker Desktop or Tart separately if this host should provide those backends."
            ],
            [
                new(
                    "Download and run the macOS installer",
                    "Run this in Terminal on the macOS host. The service is installed under ~/.runnerrunner.",
                    "bash",
                    NormalizeCommand(command))
            ],
            NormalizeCommand(remote),
            HostWorkerEnrollmentRemoteShell.Bash,
            BuildNotes(request));
    }

    private HostWorkerEnrollmentInstructions BuildMacOSDocker(
        HostWorkerEnrollmentRequest request,
        HostWorkerEnrollmentOptions options)
    {
        var command = $$"""
        docker rm -f runnerrunner-host-worker 2>/dev/null || true
        docker run -d \
          --name runnerrunner-host-worker \
          --restart unless-stopped \
          -e HostWorker__ServerUrl={{ShellQuote(request.ServerUrl)}} \
          -e HostWorker__EnrollmentToken={{ShellQuote(request.EnrollmentToken)}} \
          -e HostWorker__HostId={{ShellQuote(request.HostId)}} \
          -e HostWorker__HostName={{ShellQuote(request.HostName)}} \
          -e HostWorker__Platform='Linux' \
          -e HostWorker__DataRoot='/var/lib/runnerrunner' \
          -e DOTNET_ENVIRONMENT='Production' \
        {{BuildDockerProxyArgs(request.Proxy)}}  -v /var/run/docker.sock:/var/run/docker.sock \
          -v runnerrunner-hostworker-data:/var/lib/runnerrunner \
          {{options.HostWorkerImage}}
        docker logs -f runnerrunner-host-worker
        """;

        var remote = command.Replace("docker logs -f runnerrunner-host-worker", "docker ps --filter name=runnerrunner-host-worker && docker logs --tail=80 runnerrunner-host-worker");

        return new HostWorkerEnrollmentInstructions(
            HostWorkerEnrollmentTarget.MacOSDocker,
            HostPlatform.Linux,
            GetTargetDisplayName(HostWorkerEnrollmentTarget.MacOSDocker),
            "Run the Linux HostWorker image through Docker Desktop. This contributes Linux container runner capacity from a macOS machine.",
            [
                "Docker Desktop is installed and running.",
                "Use native macOS mode instead if you need Tart, Xcode, or Keychain-backed runners.",
                "The target can reach the advertised HostWorker gRPC URL."
            ],
            [
                new(
                    "Run the Linux HostWorker container",
                    "Run this on macOS when you only need Linux Docker runners.",
                    "bash",
                    NormalizeCommand(command))
            ],
            NormalizeCommand(remote),
            HostWorkerEnrollmentRemoteShell.Bash,
            BuildNotes(request));
    }

    private HostWorkerEnrollmentInstructions BuildWindowsService(
        HostWorkerEnrollmentRequest request,
        HostWorkerEnrollmentOptions options)
    {
        var command = $$"""
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        $deployDir = 'C:\Program Files\RunnerRunner'
        New-Item -ItemType Directory -Force -Path $deployDir | Out-Null
        Invoke-WebRequest {{PowerShellQuote(options.ReleaseBaseUrl + "/runnerrunner-hostworker-win-x64.zip")}} -OutFile .\runnerrunner-hostworker-win-x64.zip
        Invoke-WebRequest {{PowerShellQuote(options.ReleaseBaseUrl + "/Install-HostWorker.ps1")}} -OutFile .\Install-HostWorker.ps1
        Expand-Archive .\runnerrunner-hostworker-win-x64.zip -DestinationPath $deployDir -Force
        powershell -ExecutionPolicy Bypass -File .\Install-HostWorker.ps1 `
          -DeployDir $deployDir `
          -HostId {{PowerShellQuote(request.HostId)}} `
          -HostName {{PowerShellQuote(request.HostName)}} `
          -ServerUrl {{PowerShellQuote(request.ServerUrl)}} `
          -EnrollmentToken {{PowerShellQuote(request.EnrollmentToken)}}{{BuildPowerShellInstallerProxyArgs(request.Proxy)}}
        Get-Content -Wait 'C:\ProgramData\RunnerRunner\logs\hostworker.out.log'
        """;

        var remote = command.Replace("Get-Content -Wait 'C:\\ProgramData\\RunnerRunner\\logs\\hostworker.out.log'", "Get-Service RunnerRunnerHostWorker");

        return new HostWorkerEnrollmentInstructions(
            HostWorkerEnrollmentTarget.WindowsService,
            HostPlatform.Windows,
            GetTargetDisplayName(HostWorkerEnrollmentTarget.WindowsService),
            "Install the HostWorker as a native Windows service. Use this for Windows native jobs and Windows Docker container runners.",
            [
                "Run PowerShell as Administrator.",
                "The target can reach the advertised HostWorker gRPC URL.",
                "Install Docker Engine and switch it to Windows container mode if this host should provide Windows Docker runners."
            ],
            [
                new(
                    "Download and install the Windows HostWorker service",
                    "Run this in an elevated PowerShell session on the Windows host.",
                    "powershell",
                    NormalizeCommand(command))
            ],
            NormalizeCommand(remote),
            HostWorkerEnrollmentRemoteShell.PowerShell,
            BuildNotes(request));
    }

    private HostWorkerEnrollmentInstructions BuildWindowsDockerWindows(
        HostWorkerEnrollmentRequest request,
        HostWorkerEnrollmentOptions options)
    {
        var command = $$"""
        $ErrorActionPreference = 'Stop'
        docker rm -f runnerrunner-host-worker-windows 2>$null
        docker run -d `
          --name runnerrunner-host-worker-windows `
          --restart unless-stopped `
          -e 'HostWorker__ServerUrl={{request.ServerUrl}}' `
          -e 'HostWorker__EnrollmentToken={{request.EnrollmentToken}}' `
          -e 'HostWorker__HostId={{request.HostId}}' `
          -e 'HostWorker__HostName={{request.HostName}}' `
          -e 'HostWorker__Platform=Windows' `
          -e 'DOTNET_ENVIRONMENT=Production' `
        {{BuildWindowsDockerProxyArgs(request.Proxy)}}  --mount 'type=npipe,source=\\.\pipe\docker_engine,target=\\.\pipe\docker_engine' `
          --mount 'type=volume,source=runnerrunner-hostworker-windows-data,target=C:/ProgramData/RunnerRunner' `
          {{options.WindowsHostWorkerImage}}
        docker logs -f runnerrunner-host-worker-windows
        """;

        var remote = command.Replace("docker logs -f runnerrunner-host-worker-windows", "docker ps --filter name=runnerrunner-host-worker-windows; docker logs --tail=80 runnerrunner-host-worker-windows");

        return new HostWorkerEnrollmentInstructions(
            HostWorkerEnrollmentTarget.WindowsDockerWindows,
            HostPlatform.Windows,
            GetTargetDisplayName(HostWorkerEnrollmentTarget.WindowsDockerWindows),
            "Run the Windows HostWorker image as a Windows container with access to the Docker named pipe.",
            [
                "Run PowerShell as Administrator.",
                "Docker Engine is in Windows container mode.",
                "The target can reach the advertised HostWorker gRPC URL."
            ],
            [
                new(
                    "Run the Windows HostWorker container",
                    "Run this in an elevated PowerShell session on the Windows Docker host.",
                    "powershell",
                    NormalizeCommand(command))
            ],
            NormalizeCommand(remote),
            HostWorkerEnrollmentRemoteShell.PowerShell,
            BuildNotes(request));
    }

    private HostWorkerEnrollmentInstructions BuildWindowsDockerLinux(
        HostWorkerEnrollmentRequest request,
        HostWorkerEnrollmentOptions options)
    {
        var command = $$"""
        docker rm -f runnerrunner-host-worker 2>/dev/null || true
        docker run -d \
          --name runnerrunner-host-worker \
          --restart unless-stopped \
          -e HostWorker__ServerUrl={{ShellQuote(request.ServerUrl)}} \
          -e HostWorker__EnrollmentToken={{ShellQuote(request.EnrollmentToken)}} \
          -e HostWorker__HostId={{ShellQuote(request.HostId)}} \
          -e HostWorker__HostName={{ShellQuote(request.HostName)}} \
          -e HostWorker__Platform='Linux' \
          -e HostWorker__DataRoot='/var/lib/runnerrunner' \
          -e DOTNET_ENVIRONMENT='Production' \
        {{BuildDockerProxyArgs(request.Proxy)}}  -v /var/run/docker.sock:/var/run/docker.sock \
          -v runnerrunner-hostworker-data:/var/lib/runnerrunner \
          {{options.HostWorkerImage}}
        docker logs -f runnerrunner-host-worker
        """;

        return new HostWorkerEnrollmentInstructions(
            HostWorkerEnrollmentTarget.WindowsDockerLinux,
            HostPlatform.Linux,
            GetTargetDisplayName(HostWorkerEnrollmentTarget.WindowsDockerLinux),
            "Run the Linux HostWorker image from WSL or Docker Desktop's Linux engine. This contributes Linux container runner capacity from a Windows machine.",
            [
                "Run this inside WSL or a Linux shell with Docker access.",
                "Docker Desktop is using Linux containers.",
                "The target can reach the advertised HostWorker gRPC URL."
            ],
            [
                new(
                    "Run the Linux HostWorker container from WSL",
                    "Run this in WSL or another Linux shell on the Windows host.",
                    "bash",
                    NormalizeCommand(command))
            ],
            NormalizeCommand(command.Replace("docker logs -f runnerrunner-host-worker", "docker ps --filter name=runnerrunner-host-worker && docker logs --tail=80 runnerrunner-host-worker")),
            HostWorkerEnrollmentRemoteShell.Bash,
            BuildNotes(request));
    }

    private static IReadOnlyList<string> BuildNotes(HostWorkerEnrollmentRequest request)
    {
        var notes = new List<string>
        {
            $"Advertised HostWorker URL: {request.ServerUrl}",
            "This enrollment token is one-time visible in the wizard. The server stores only its hash.",
            "After the worker connects, this wizard will switch to the success step and the Hosts table will show the detected OS, architecture, and capabilities."
        };

        if (request.Proxy.HasAny)
            notes.Add("Proxy settings were included from server configuration. Confirm the target network should use them before running the command.");

        return notes;
    }

    private HostWorkerEnrollmentInstructions BuildDockerManualUpdate(
        HostWorkerManualUpdateRequest request,
        string image,
        HostPlatform platform)
    {
        var targetImage = string.IsNullOrWhiteSpace(request.Package?.ContainerImage)
            ? image
            : request.Package!.ContainerImage!;
        var container = string.IsNullOrWhiteSpace(request.ContainerId)
            ? "runnerrunner-host-worker"
            : request.ContainerId;
        var command = NormalizeCommand("""
        container=__CONTAINER__
        image=__IMAGE__
        container_name='runnerrunner-host-worker'
        fallback_host_id=__HOST_ID__
        fallback_host_name=__HOST_NAME__
        env_format='{{range .Config.Env}}{{println .}}{{end}}'
        env_lines="$(docker inspect "$container" --format "$env_format")"

        get_env() {
          printf '%s\n' "$env_lines" | awk -F= -v key="$1" '$1 == key { print substr($0, index($0, "=") + 1); exit }'
        }

        server_url="$(get_env HostWorker__ServerUrl)"
        enrollment_token="$(get_env HostWorker__EnrollmentToken)"
        host_id="$(get_env HostWorker__HostId)"
        host_name="$(get_env HostWorker__HostName)"
        data_root="$(get_env HostWorker__DataRoot)"
        http_proxy="$(get_env HostWorker__HttpProxy)"
        https_proxy="$(get_env HostWorker__HttpsProxy)"
        no_proxy="$(get_env HostWorker__NoProxy)"

        : "${host_id:=$fallback_host_id}"
        : "${host_name:=$fallback_host_name}"
        : "${data_root:=/var/lib/runnerrunner}"

        if [ -z "$server_url" ] || [ -z "$enrollment_token" ]; then
          echo "The existing container does not expose HostWorker__ServerUrl and HostWorker__EnrollmentToken. Re-run Add HostWorker to create a new token." >&2
          exit 1
        fi

        proxy_args=()
        if [ -n "$http_proxy" ]; then proxy_args+=("-e" "HTTP_PROXY=$http_proxy" "-e" "HostWorker__HttpProxy=$http_proxy"); fi
        if [ -n "$https_proxy" ]; then proxy_args+=("-e" "HTTPS_PROXY=$https_proxy" "-e" "HostWorker__HttpsProxy=$https_proxy"); fi
        if [ -n "$no_proxy" ]; then proxy_args+=("-e" "NO_PROXY=$no_proxy" "-e" "HostWorker__NoProxy=$no_proxy"); fi

        docker pull "$image"
        docker rm -f "$container"
        docker run -d \
          --name "$container_name" \
          --restart unless-stopped \
          -e "HostWorker__ServerUrl=$server_url" \
          -e "HostWorker__EnrollmentToken=$enrollment_token" \
          -e "HostWorker__HostId=$host_id" \
          -e "HostWorker__HostName=$host_name" \
          -e "HostWorker__Platform=Linux" \
          -e "HostWorker__DataRoot=$data_root" \
          -e "DOTNET_ENVIRONMENT=Production" \
          "${proxy_args[@]}" \
          -v /var/run/docker.sock:/var/run/docker.sock \
          -v runnerrunner-hostworker-data:/var/lib/runnerrunner \
          "$image"
        docker logs -f "$container_name"
        """)
            .Replace("__CONTAINER__", ShellQuote(container))
            .Replace("__IMAGE__", ShellQuote(targetImage))
            .Replace("__HOST_ID__", ShellQuote(request.WorkerId ?? request.HostId))
            .Replace("__HOST_NAME__", ShellQuote(request.HostName));

        var remote = command.Replace("docker logs -f \"$container_name\"", "docker ps --filter name=\"$container_name\" && docker logs --tail=80 \"$container_name\"");

        return new HostWorkerEnrollmentInstructions(
            request.Target,
            platform,
            $"Manual update - {GetTargetDisplayName(request.Target)}",
            "Replace the HostWorker container from SSH while preserving the current server URL, enrollment token, host identity, and proxy settings from the existing container environment.",
            [
                "Docker is installed and reachable from the SSH session.",
                "The current HostWorker container was started with HostWorker__ServerUrl and HostWorker__EnrollmentToken environment variables.",
                "Stop or drain active runners before replacing the worker container."
            ],
            [
                new(
                    "Pull and replace the HostWorker container",
                    "Run this on the target host when the web UI update path cannot reach the old worker.",
                    "bash",
                    command)
            ],
            remote,
            HostWorkerEnrollmentRemoteShell.Bash,
            BuildManualUpdateNotes(request));
    }

    private HostWorkerEnrollmentInstructions BuildMacOSManualUpdate(
        HostWorkerManualUpdateRequest request,
        HostWorkerEnrollmentOptions options)
    {
        if (request.Package != null && !string.IsNullOrWhiteSpace(request.Package.AssetUrl))
            return BuildMacOSAssetManualUpdate(request, request.Package);

        var command = $$"""
        curl -fsSL {{ShellQuote(options.ReleaseBaseUrl + "/install-host-macos.sh")}} -o install-host-macos.sh
        chmod +x install-host-macos.sh
        {{BuildBashProxyExports(request.Proxy)}}./install-host-macos.sh --preserve-config{{BuildBashInstallerProxyArgs(request.Proxy)}}
        ~/.runnerrunner/runnerrunner-host logs
        """;
        var remote = command.Replace("~/.runnerrunner/runnerrunner-host logs", "~/.runnerrunner/runnerrunner-host status");

        return new HostWorkerEnrollmentInstructions(
            request.Target,
            HostPlatform.MacOS,
            $"Manual update - {GetTargetDisplayName(request.Target)}",
            "Download the current macOS HostWorker installer and update the LaunchAgent install while preserving the existing appsettings.Production.json.",
            [
                "Run from the same macOS user account that owns the existing LaunchAgent.",
                "The existing install has ~/.runnerrunner/current/appsettings.Production.json.",
                "Stop or drain active runners before restarting the worker."
            ],
            [
                new(
                    "Run the macOS HostWorker updater",
                    "Run this in Terminal or use SSH auto setup.",
                    "bash",
                    NormalizeCommand(command))
            ],
            NormalizeCommand(remote),
            HostWorkerEnrollmentRemoteShell.Bash,
            BuildManualUpdateNotes(request));
    }

    private HostWorkerEnrollmentInstructions BuildMacOSAssetManualUpdate(
        HostWorkerManualUpdateRequest request,
        HostWorkerManualUpdatePackage package)
    {
        var command = NormalizeCommand("""
        set -euo pipefail
        install_root="${INSTALL_ROOT:-${HOME}/.runnerrunner}"
        service_label='com.runnerrunner.hostworker'
        version=__VERSION__
        asset_url=__ASSET_URL__
        expected_sha256=__SHA256__
        tmp_dir="$(mktemp -d)"
        trap 'rm -rf "${tmp_dir}"' EXIT

        existing_settings="${install_root}/current/appsettings.Production.json"
        if [ ! -f "${existing_settings}" ]; then
          echo "Manual update requires an existing ${existing_settings}." >&2
          exit 1
        fi

        settings_backup="${tmp_dir}/appsettings.Production.json"
        cp "${existing_settings}" "${settings_backup}"
        enrollment_token="$(awk -F'"' '/"EnrollmentToken"[[:space:]]*:/ { print $4; exit }' "${existing_settings}")"

        archive="${tmp_dir}/hostworker.tar.gz"
        curl_args=(-fsSL "${asset_url}" -o "${archive}")
        if [[ "${asset_url}" == *"/api/hostworker-updates/"* && -n "${enrollment_token}" ]]; then
          curl_args=(-H "Authorization: Bearer ${enrollment_token}" "${curl_args[@]}")
        fi
        if ! curl "${curl_args[@]}"; then
          echo "Failed to download HostWorker update asset from ${asset_url}" >&2
          exit 1
        fi
        if [ -n "${expected_sha256}" ]; then
          actual_sha256="$(shasum -a 256 "${archive}" | awk '{print $1}')"
          if [ "${actual_sha256}" != "${expected_sha256}" ]; then
            echo "Checksum mismatch for ${asset_url}. Expected ${expected_sha256}, got ${actual_sha256}." >&2
            exit 1
          fi
        fi

        version_dir="${install_root}/versions/${version}"
        rm -rf "${version_dir}"
        mkdir -p "${version_dir}" "${install_root}/logs"
        tar -xzf "${archive}" -C "${version_dir}"
        chmod +x "${version_dir}/RunnerRunner.HostWorker"
        codesign --force -s - "${version_dir}/RunnerRunner.HostWorker" >/dev/null 2>&1 || true
        cp "${settings_backup}" "${version_dir}/appsettings.Production.json"
        chmod 0600 "${version_dir}/appsettings.Production.json"

        ln -sfn "${version_dir}" "${install_root}/current"
        launchctl kickstart -k "gui/$(id -u)/${service_label}"
        "${install_root}/runnerrunner-host" status
        """)
            .Replace("__VERSION__", ShellQuote(package.Version))
            .Replace("__ASSET_URL__", ShellQuote(package.AssetUrl ?? ""))
            .Replace("__SHA256__", ShellQuote(package.Sha256 ?? ""));

        var summary = package.Source == HostWorkerUpdateSourceKind.Release
            ? "Download the selected HostWorker asset and update the LaunchAgent install while preserving the existing appsettings.Production.json."
            : "Download the selected HostWorker artifact from RunnerRunner and update the LaunchAgent install while preserving the existing appsettings.Production.json.";

        return new HostWorkerEnrollmentInstructions(
            request.Target,
            HostPlatform.MacOS,
            $"Manual update - {GetTargetDisplayName(request.Target)}",
            summary,
            [
                "Run from the same macOS user account that owns the existing LaunchAgent.",
                "The existing install has ~/.runnerrunner/current/appsettings.Production.json.",
                "Stop or drain active runners before restarting the worker."
            ],
            [
                new(
                    "Run the selected macOS HostWorker updater",
                    "Run this in Terminal or use SSH auto setup.",
                    "bash",
                    command)
            ],
            command,
            HostWorkerEnrollmentRemoteShell.Bash,
            BuildManualUpdateNotes(request));
    }

    private HostWorkerEnrollmentInstructions BuildWindowsServiceManualUpdate(
        HostWorkerManualUpdateRequest request,
        HostWorkerEnrollmentOptions options)
    {
        if (request.Package != null && !string.IsNullOrWhiteSpace(request.Package.AssetUrl))
            return BuildWindowsServiceAssetManualUpdate(request, request.Package);

        var command = $$"""
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        $deployDir = 'C:\Program Files\RunnerRunner'
        New-Item -ItemType Directory -Force -Path $deployDir | Out-Null
        Invoke-WebRequest {{PowerShellQuote(options.ReleaseBaseUrl + "/runnerrunner-hostworker-win-x64.zip")}} -OutFile .\runnerrunner-hostworker-win-x64.zip
        Invoke-WebRequest {{PowerShellQuote(options.ReleaseBaseUrl + "/Install-HostWorker.ps1")}} -OutFile .\Install-HostWorker.ps1
        $service = Get-Service -Name RunnerRunnerHostWorker -ErrorAction SilentlyContinue
        if ($service -and $service.Status -ne 'Stopped') {
          Stop-Service -Name RunnerRunnerHostWorker -Force
          $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
        }
        Expand-Archive .\runnerrunner-hostworker-win-x64.zip -DestinationPath $deployDir -Force
        powershell -ExecutionPolicy Bypass -File .\Install-HostWorker.ps1 `
          -DeployDir $deployDir `
          -PreserveConfig
        Get-Content -Wait 'C:\ProgramData\RunnerRunner\logs\hostworker.out.log'
        """;
        var remote = command.Replace("Get-Content -Wait 'C:\\ProgramData\\RunnerRunner\\logs\\hostworker.out.log'", "Get-Service RunnerRunnerHostWorker");

        return new HostWorkerEnrollmentInstructions(
            request.Target,
            HostPlatform.Windows,
            $"Manual update - {GetTargetDisplayName(request.Target)}",
            "Download the current Windows HostWorker package, replace the service binaries, and preserve the existing appsettings.Production.json.",
            [
                "Run PowerShell as Administrator.",
                "The existing install has C:\\Program Files\\RunnerRunner\\appsettings.Production.json.",
                "Stop or drain active runners before restarting the worker service."
            ],
            [
                new(
                    "Update the Windows HostWorker service",
                    "Run this in an elevated PowerShell session or use SSH auto setup.",
                    "powershell",
                    NormalizeCommand(command))
            ],
            NormalizeCommand(remote),
            HostWorkerEnrollmentRemoteShell.PowerShell,
            BuildManualUpdateNotes(request));
    }

    private HostWorkerEnrollmentInstructions BuildWindowsServiceAssetManualUpdate(
        HostWorkerManualUpdateRequest request,
        HostWorkerManualUpdatePackage package)
    {
        var command = NormalizeCommand("""
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        $deployDir = 'C:\Program Files\RunnerRunner'
        $serviceName = 'RunnerRunnerHostWorker'
        $version = __VERSION__
        $assetUrl = __ASSET_URL__
        $expectedSha256 = __SHA256__
        $settingsPath = Join-Path $deployDir 'appsettings.Production.json'

        if (-not (Test-Path $settingsPath)) {
          throw "Manual update requires an existing $settingsPath."
        }
        $settings = Get-Content -Raw -Path $settingsPath | ConvertFrom-Json
        $enrollmentToken = $settings.HostWorker.EnrollmentToken
        $downloadHeaders = @{}
        if ($assetUrl -like '*/api/hostworker-updates/*' -and -not [string]::IsNullOrWhiteSpace($enrollmentToken)) {
          $downloadHeaders['Authorization'] = "Bearer $enrollmentToken"
        }

        $tmpDir = Join-Path ([System.IO.Path]::GetTempPath()) ('runnerrunner-hostworker-update-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force -Path $tmpDir | Out-Null
        try {
          $archive = Join-Path $tmpDir 'hostworker.zip'
          $settingsBackup = Join-Path $tmpDir 'appsettings.Production.json'
          Copy-Item -Path $settingsPath -Destination $settingsBackup -Force
          try {
            if ($downloadHeaders.Count -gt 0) {
              Invoke-WebRequest $assetUrl -OutFile $archive -Headers $downloadHeaders
            }
            else {
              Invoke-WebRequest $assetUrl -OutFile $archive
            }
          }
          catch {
            throw "Failed to download HostWorker update asset from ${assetUrl}: $($_.Exception.Message)"
          }

          if (-not [string]::IsNullOrWhiteSpace($expectedSha256)) {
            $actualSha256 = (Get-FileHash -Algorithm SHA256 -Path $archive).Hash.ToLowerInvariant()
            if ($actualSha256 -ne $expectedSha256.ToLowerInvariant()) {
              throw "Checksum mismatch for $assetUrl. Expected $expectedSha256, got $actualSha256."
            }
          }

          $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
          if ($null -eq $service) {
            throw "Service '$serviceName' was not found."
          }

          if ($service.Status -ne 'Stopped') {
            Stop-Service -Name $serviceName -Force
            $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
          }

          Expand-Archive $archive -DestinationPath $deployDir -Force
          Copy-Item -Path $settingsBackup -Destination $settingsPath -Force
          Start-Service -Name $serviceName
          Get-Service -Name $serviceName
        }
        finally {
          Remove-Item -Path $tmpDir -Recurse -Force -ErrorAction SilentlyContinue
        }
        """)
            .Replace("__VERSION__", PowerShellQuote(package.Version))
            .Replace("__ASSET_URL__", PowerShellQuote(package.AssetUrl ?? ""))
            .Replace("__SHA256__", PowerShellQuote(package.Sha256 ?? ""));
        return new HostWorkerEnrollmentInstructions(
            request.Target,
            HostPlatform.Windows,
            $"Manual update - {GetTargetDisplayName(request.Target)}",
            "Download the selected Windows HostWorker package, replace the service binaries, and preserve the existing appsettings.Production.json.",
            [
                "Run PowerShell as Administrator.",
                "The existing install has C:\\Program Files\\RunnerRunner\\appsettings.Production.json.",
                "Stop or drain active runners before restarting the worker service."
            ],
            [
                new(
                    "Update the Windows HostWorker service to the selected build",
                    "Run this in an elevated PowerShell session or use SSH auto setup.",
                    "powershell",
                    command)
            ],
            command,
            HostWorkerEnrollmentRemoteShell.PowerShell,
            BuildManualUpdateNotes(request));
    }

    private HostWorkerEnrollmentInstructions BuildWindowsDockerManualUpdate(
        HostWorkerManualUpdateRequest request,
        HostWorkerEnrollmentOptions options)
    {
        var targetImage = string.IsNullOrWhiteSpace(request.Package?.ContainerImage)
            ? options.WindowsHostWorkerImage
            : request.Package!.ContainerImage!;
        var container = string.IsNullOrWhiteSpace(request.ContainerId)
            ? "runnerrunner-host-worker-windows"
            : request.ContainerId;
        var command = NormalizeCommand("""
        $ErrorActionPreference = 'Stop'
        $container = __CONTAINER__
        $image = __IMAGE__
        $containerName = 'runnerrunner-host-worker-windows'
        $envFormat = '{{range .Config.Env}}{{println .}}{{end}}'
        $envLines = docker inspect $container --format $envFormat

        function Get-ContainerEnv([string]$Name) {
          foreach ($line in $envLines) {
            $parts = $line -split '=', 2
            if ($parts.Length -eq 2 -and $parts[0] -eq $Name) {
              return $parts[1]
            }
          }
          return ''
        }

        $serverUrl = Get-ContainerEnv 'HostWorker__ServerUrl'
        $enrollmentToken = Get-ContainerEnv 'HostWorker__EnrollmentToken'
        $hostId = Get-ContainerEnv 'HostWorker__HostId'
        $hostName = Get-ContainerEnv 'HostWorker__HostName'
        if ([string]::IsNullOrWhiteSpace($hostId)) { $hostId = __HOST_ID__ }
        if ([string]::IsNullOrWhiteSpace($hostName)) { $hostName = __HOST_NAME__ }

        if ([string]::IsNullOrWhiteSpace($serverUrl) -or [string]::IsNullOrWhiteSpace($enrollmentToken)) {
          throw 'The existing container does not expose HostWorker__ServerUrl and HostWorker__EnrollmentToken. Re-run Add HostWorker to create a new token.'
        }

        $dockerArgs = @(
          'run', '-d',
          '--name', $containerName,
          '--restart', 'unless-stopped',
          '-e', "HostWorker__ServerUrl=$serverUrl",
          '-e', "HostWorker__EnrollmentToken=$enrollmentToken",
          '-e', "HostWorker__HostId=$hostId",
          '-e', "HostWorker__HostName=$hostName",
          '-e', 'HostWorker__Platform=Windows',
          '-e', 'DOTNET_ENVIRONMENT=Production'
        )

        foreach ($name in @('HTTP_PROXY', 'HTTPS_PROXY', 'NO_PROXY', 'HostWorker__HttpProxy', 'HostWorker__HttpsProxy', 'HostWorker__NoProxy')) {
          $value = Get-ContainerEnv $name
          if (-not [string]::IsNullOrWhiteSpace($value)) {
            $dockerArgs += @('-e', "$name=$value")
          }
        }

        $dockerArgs += @(
          '--mount', 'type=npipe,source=\\.\pipe\docker_engine,target=\\.\pipe\docker_engine',
          '--mount', 'type=volume,source=runnerrunner-hostworker-windows-data,target=C:/ProgramData/RunnerRunner',
          $image
        )

        docker pull $image
        docker rm -f $container
        docker @dockerArgs
        docker logs -f $containerName
        """)
            .Replace("__CONTAINER__", PowerShellQuote(container))
            .Replace("__IMAGE__", PowerShellQuote(targetImage))
            .Replace("__HOST_ID__", PowerShellQuote(request.WorkerId ?? request.HostId))
            .Replace("__HOST_NAME__", PowerShellQuote(request.HostName));
        var remote = command.Replace("docker logs -f $containerName", "docker ps --filter name=$containerName; docker logs --tail=80 $containerName");

        return new HostWorkerEnrollmentInstructions(
            request.Target,
            HostPlatform.Windows,
            $"Manual update - {GetTargetDisplayName(request.Target)}",
            "Replace the Windows HostWorker container from SSH while preserving the current server URL, enrollment token, host identity, and proxy settings from the existing container environment.",
            [
                "Run PowerShell as Administrator.",
                "Docker Engine is in Windows container mode.",
                "Stop or drain active runners before replacing the worker container."
            ],
            [
                new(
                    "Pull and replace the Windows HostWorker container",
                    "Run this in an elevated PowerShell session or use SSH auto setup.",
                    "powershell",
                    command)
            ],
            remote,
            HostWorkerEnrollmentRemoteShell.PowerShell,
            BuildManualUpdateNotes(request));
    }

    private static IReadOnlyList<string> BuildManualUpdateNotes(HostWorkerManualUpdateRequest request)
    {
        var notes = new List<string>
        {
            $"Host: {request.HostName}",
            "Manual updates bypass the web UI command channel and are intended for recovery when an old worker can no longer self-update.",
            "The wizard waits for a fresh heartbeat from the existing host after you run the update."
        };

        if (request.Package != null)
        {
            var requested = string.IsNullOrWhiteSpace(request.Package.RequestedVersion)
                ? request.Package.Version
                : request.Package.RequestedVersion;
            notes.Add($"Target update: {request.Package.Source.ToDisplayName()} {requested}.");
        }

        if (request.Proxy.HasAny)
            notes.Add("Proxy settings from server configuration are included when the manual update command can persist them.");

        return notes;
    }

    private HostWorkerEnrollmentInstructions BuildDockerRemoval(
        HostWorkerRemovalRequest request,
        HostPlatform platform)
    {
        var container = string.IsNullOrWhiteSpace(request.ContainerId)
            ? "runnerrunner-host-worker"
            : request.ContainerId;
        var command = NormalizeCommand("""
        set -euo pipefail
        container=__CONTAINER__
        container_name='runnerrunner-host-worker'

        if [ -d "${HOME}/runnerrunner-hostworker" ]; then
          (cd "${HOME}/runnerrunner-hostworker" && docker compose down -v --remove-orphans || true)
        fi

        if docker container inspect "$container" >/dev/null 2>&1; then
          docker rm -f "$container"
        fi
        if [ "$container" != "$container_name" ] && docker container inspect "$container_name" >/dev/null 2>&1; then
          docker rm -f "$container_name"
        fi

        runner_containers="$(docker ps -aq --filter label=runnerrunner.managed=true)"
        if [ -n "$runner_containers" ]; then
          docker rm -f $runner_containers
        fi

        docker volume rm runnerrunner-hostworker-data runnerrunner-hostworker_hostworker-data hostworker-data 2>/dev/null || true
        rm -rf "${HOME}/runnerrunner-hostworker"
        """)
            .Replace("__CONTAINER__", ShellQuote(container));

        return new HostWorkerEnrollmentInstructions(
            request.Target,
            platform,
            $"Remove - {GetTargetDisplayName(request.Target)}",
            "Stop and remove the HostWorker container, RunnerRunner-managed runner containers, Docker volumes, and the generated compose project from the host.",
            [
                "Run this on the Docker host that owns the HostWorker.",
                "The user running the command can access Docker.",
                "This removes HostWorker data, logs, and RunnerRunner-managed runner containers on that Docker engine."
            ],
            [
                new(
                    "Clean up the Docker HostWorker",
                    "Run this on the target host before removing the host record from RunnerRunner.",
                    "bash",
                    command)
            ],
            command,
            HostWorkerEnrollmentRemoteShell.Bash,
            BuildRemovalNotes(request));
    }

    private HostWorkerEnrollmentInstructions BuildMacOSNativeRemoval(HostWorkerRemovalRequest request)
    {
        var command = NormalizeCommand("""
        set -euo pipefail
        install_root="${INSTALL_ROOT:-${HOME}/.runnerrunner}"
        service_label='com.runnerrunner.hostworker'
        runner_base_path=__RUNNER_BASE_PATH__
        work_directory=__WORK_DIRECTORY__
        native_base_path="${runner_base_path:-${install_root}}"
        instances_dir="${native_base_path}/instances"

        if [ -d "$instances_dir" ]; then
          find "$instances_dir" -name rr.pid -print | while IFS= read -r pid_file; do
            pid="$(cat "$pid_file" 2>/dev/null || true)"
            case "$pid" in ''|*[!0-9]*) continue ;; esac
            kill "$pid" 2>/dev/null || true
          done
        fi

        launchctl bootout "gui/$(id -u)/${service_label}" 2>/dev/null || true
        launchctl unload "${HOME}/Library/LaunchAgents/${service_label}.plist" 2>/dev/null || true
        rm -f "${HOME}/Library/LaunchAgents/${service_label}.plist"
        rm -rf "${install_root}"
        if [ -n "$work_directory" ]; then rm -rf "$work_directory"; fi
        if [ -n "$runner_base_path" ] && [ "$runner_base_path" != "$install_root" ]; then rm -rf "$runner_base_path"; fi
        """)
            .Replace("__RUNNER_BASE_PATH__", ShellQuote(request.RunnerBasePath ?? ""))
            .Replace("__WORK_DIRECTORY__", ShellQuote(request.WorkDirectory ?? ""));

        return new HostWorkerEnrollmentInstructions(
            request.Target,
            HostPlatform.MacOS,
            $"Remove - {GetTargetDisplayName(request.Target)}",
            "Stop RunnerRunner-managed native runner processes, unload the macOS LaunchAgent, and remove the HostWorker install, logs, update cache, and local runner working directories.",
            [
                "Run from the same macOS user account that owns the existing LaunchAgent.",
                "This removes ~/.runnerrunner by default.",
                "Stop or drain active runners before deleting the server host record."
            ],
            [
                new(
                    "Remove the macOS HostWorker LaunchAgent",
                    "Run this in Terminal on the target macOS host.",
                    "bash",
                    command)
            ],
            command,
            HostWorkerEnrollmentRemoteShell.Bash,
            BuildRemovalNotes(request));
    }

    private HostWorkerEnrollmentInstructions BuildWindowsServiceRemoval(HostWorkerRemovalRequest request)
    {
        var command = NormalizeCommand("""
        $ErrorActionPreference = 'Stop'
        $serviceName = 'RunnerRunnerHostWorker'
        $deployDir = 'C:\Program Files\RunnerRunner'
        $dataDir = 'C:\ProgramData\RunnerRunner'
        $runnerBasePath = __RUNNER_BASE_PATH__
        $workDirectory = __WORK_DIRECTORY__
        $nativeBasePath = if ([string]::IsNullOrWhiteSpace($runnerBasePath)) { 'C:\rr' } else { $runnerBasePath }
        $instancesDir = Join-Path $nativeBasePath 'instances'

        if (Test-Path $instancesDir) {
          Get-ChildItem -Path $instancesDir -Filter rr.pid -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object {
            $pidText = [string](Get-Content -Raw -Path $_.FullName -ErrorAction SilentlyContinue)
            $pidText = $pidText.Trim()
            $runnerProcessId = 0
            if ([int]::TryParse($pidText, [ref]$runnerProcessId)) {
              Stop-Process -Id $runnerProcessId -Force -ErrorAction SilentlyContinue
            }
          }
        }

        $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        if ($null -ne $service) {
          if ($service.Status -ne 'Stopped') {
            Stop-Service -Name $serviceName -Force
            $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
          }
          sc.exe delete $serviceName | Out-Null
          Start-Sleep -Seconds 2
        }

        Remove-Item -Path $deployDir -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -Path $dataDir -Recurse -Force -ErrorAction SilentlyContinue
        if (-not [string]::IsNullOrWhiteSpace($workDirectory)) {
          Remove-Item -Path $workDirectory -Recurse -Force -ErrorAction SilentlyContinue
        }
        Remove-Item -Path $nativeBasePath -Recurse -Force -ErrorAction SilentlyContinue
        """)
            .Replace("__RUNNER_BASE_PATH__", PowerShellQuote(request.RunnerBasePath ?? ""))
            .Replace("__WORK_DIRECTORY__", PowerShellQuote(request.WorkDirectory ?? ""));

        return new HostWorkerEnrollmentInstructions(
            request.Target,
            HostPlatform.Windows,
            $"Remove - {GetTargetDisplayName(request.Target)}",
            "Stop RunnerRunner-managed native runner processes, delete the Windows service, then remove the HostWorker binaries, ProgramData, logs, and local runner working directories.",
            [
                "Run PowerShell as Administrator.",
                "This removes C:\\Program Files\\RunnerRunner and C:\\ProgramData\\RunnerRunner.",
                "Stop or drain active runners before deleting the server host record."
            ],
            [
                new(
                    "Remove the Windows HostWorker service",
                    "Run this in an elevated PowerShell session on the Windows host.",
                    "powershell",
                    command)
            ],
            command,
            HostWorkerEnrollmentRemoteShell.PowerShell,
            BuildRemovalNotes(request));
    }

    private HostWorkerEnrollmentInstructions BuildWindowsDockerRemoval(HostWorkerRemovalRequest request)
    {
        var container = string.IsNullOrWhiteSpace(request.ContainerId)
            ? "runnerrunner-host-worker-windows"
            : request.ContainerId;
        var command = NormalizeCommand("""
        $ErrorActionPreference = 'Stop'
        $container = __CONTAINER__
        $containerName = 'runnerrunner-host-worker-windows'

        docker rm -f $container 2>$null
        if ($container -ne $containerName) {
          docker rm -f $containerName 2>$null
        }

        $runnerContainers = docker ps -aq --filter 'label=runnerrunner.managed=true'
        if ($runnerContainers) {
          docker rm -f $runnerContainers
        }

        docker volume rm runnerrunner-hostworker-windows-data 2>$null
        """)
            .Replace("__CONTAINER__", PowerShellQuote(container));

        return new HostWorkerEnrollmentInstructions(
            request.Target,
            HostPlatform.Windows,
            $"Remove - {GetTargetDisplayName(request.Target)}",
            "Remove the Windows HostWorker container, RunnerRunner-managed runner containers, and the HostWorker data volume from the Windows Docker host.",
            [
                "Run PowerShell as Administrator.",
                "Docker Engine is in Windows container mode.",
                "This removes the runnerrunner-hostworker-windows-data Docker volume."
            ],
            [
                new(
                    "Remove the Windows HostWorker container",
                    "Run this in an elevated PowerShell session on the Windows Docker host.",
                    "powershell",
                    command)
            ],
            command,
            HostWorkerEnrollmentRemoteShell.PowerShell,
            BuildRemovalNotes(request));
    }

    private static IReadOnlyList<string> BuildRemovalNotes(HostWorkerRemovalRequest request) =>
        [
            $"Host: {request.HostName}",
            "After the host-side cleanup succeeds, remove the host record from RunnerRunner to clear assignments, instances, and direct provisioning-rule targeting.",
            "If the host is still online, RunnerRunner will try to stop active runners before deleting its server-side records."
        ];

    private static string DeriveServerUrlFromUiBase(string uiBaseUri)
    {
        if (!Uri.TryCreate(uiBaseUri, UriKind.Absolute, out var uri))
            return uiBaseUri.TrimEnd('/');

        var builder = new UriBuilder(uri)
        {
            Path = "",
            Query = "",
            Fragment = ""
        };

        if (builder.Port == 4779)
            builder.Port = 4780;

        return builder.Uri.ToString().TrimEnd('/');
    }

    private string BuildAdvertisedServerUrl(string host)
    {
        var scheme = ResolveGrpcScheme();
        var port = ResolveGrpcPort();
        var formattedHost = host.Contains(':', StringComparison.Ordinal) && !host.StartsWith("[", StringComparison.Ordinal)
            ? $"[{host}]"
            : host;

        return $"{scheme}://{formattedHost}:{port}";
    }

    private string ResolveGrpcScheme()
    {
        var configured = NullIfWhiteSpace(_configuration["HostWorkerEnrollment:PublicServerScheme"]);
        if (configured is "http" or "https")
            return configured;

        var endpointUrl = NullIfWhiteSpace(_configuration["Kestrel:Endpoints:HostWorkerGrpc:Url"]);
        if (endpointUrl != null && endpointUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return "https";

        return "http";
    }

    private int ResolveGrpcPort()
    {
        if (TryReadPort(_configuration["HostWorkerEnrollment:PublicServerPort"], out var configuredPort))
            return configuredPort;

        if (TryReadPort(_configuration["HOSTWORKER_GRPC_PORT"], out var environmentPort))
            return environmentPort;

        var endpointUrl = NullIfWhiteSpace(_configuration["Kestrel:Endpoints:HostWorkerGrpc:Url"]);
        if (endpointUrl != null)
        {
            var normalizedEndpointUrl = endpointUrl
                .Replace("://+:", "://localhost:", StringComparison.Ordinal)
                .Replace("://*:", "://localhost:", StringComparison.Ordinal);
            if (Uri.TryCreate(normalizedEndpointUrl, UriKind.Absolute, out var uri) && uri.Port > 0)
                return uri.Port;
        }

        return 4780;
    }

    private static bool TryReadPort(string? value, out int port)
    {
        if (int.TryParse(value, out port) && port is > 0 and <= 65535)
            return true;

        port = 0;
        return false;
    }

    private static string BuildComposeProxyEnvironment(HostWorkerEnrollmentProxy proxy)
    {
        if (!proxy.HasAny)
            return string.Empty;

        var builder = new StringBuilder();
        AppendComposeEnvironment(builder, "HTTP_PROXY", proxy.HttpProxy);
        AppendComposeEnvironment(builder, "HTTPS_PROXY", proxy.HttpsProxy);
        AppendComposeEnvironment(builder, "NO_PROXY", proxy.NoProxy);
        AppendComposeEnvironment(builder, "HostWorker__HttpProxy", proxy.HttpProxy);
        AppendComposeEnvironment(builder, "HostWorker__HttpsProxy", proxy.HttpsProxy);
        AppendComposeEnvironment(builder, "HostWorker__NoProxy", proxy.NoProxy);
        return builder.ToString();
    }

    private static void AppendComposeEnvironment(StringBuilder builder, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        builder.Append("      ");
        builder.Append(name);
        builder.Append(": ");
        builder.AppendLine(YamlString(value));
    }

    private static string BuildBashProxyExports(HostWorkerEnrollmentProxy proxy)
    {
        if (!proxy.HasAny)
            return string.Empty;

        var builder = new StringBuilder();
        AppendBashExport(builder, "HTTP_PROXY", proxy.HttpProxy);
        AppendBashExport(builder, "HTTPS_PROXY", proxy.HttpsProxy);
        AppendBashExport(builder, "NO_PROXY", proxy.NoProxy);
        return builder.ToString();
    }

    private static void AppendBashExport(StringBuilder builder, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        builder.Append("export ");
        builder.Append(name);
        builder.Append('=');
        builder.Append(ShellQuote(value));
        builder.AppendLine();
    }

    private static string BuildDockerProxyArgs(HostWorkerEnrollmentProxy proxy)
    {
        if (!proxy.HasAny)
            return string.Empty;

        var builder = new StringBuilder();
        AppendDockerEnvironment(builder, "HTTP_PROXY", proxy.HttpProxy);
        AppendDockerEnvironment(builder, "HTTPS_PROXY", proxy.HttpsProxy);
        AppendDockerEnvironment(builder, "NO_PROXY", proxy.NoProxy);
        AppendDockerEnvironment(builder, "HostWorker__HttpProxy", proxy.HttpProxy);
        AppendDockerEnvironment(builder, "HostWorker__HttpsProxy", proxy.HttpsProxy);
        AppendDockerEnvironment(builder, "HostWorker__NoProxy", proxy.NoProxy);
        return builder.ToString();
    }

    private static void AppendDockerEnvironment(StringBuilder builder, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        builder.Append("  -e ");
        builder.Append(name);
        builder.Append('=');
        builder.Append(ShellQuote(value));
        builder.AppendLine(" \\");
    }

    private static string BuildWindowsDockerProxyArgs(HostWorkerEnrollmentProxy proxy)
    {
        if (!proxy.HasAny)
            return string.Empty;

        var builder = new StringBuilder();
        AppendWindowsDockerEnvironment(builder, "HTTP_PROXY", proxy.HttpProxy);
        AppendWindowsDockerEnvironment(builder, "HTTPS_PROXY", proxy.HttpsProxy);
        AppendWindowsDockerEnvironment(builder, "NO_PROXY", proxy.NoProxy);
        AppendWindowsDockerEnvironment(builder, "HostWorker__HttpProxy", proxy.HttpProxy);
        AppendWindowsDockerEnvironment(builder, "HostWorker__HttpsProxy", proxy.HttpsProxy);
        AppendWindowsDockerEnvironment(builder, "HostWorker__NoProxy", proxy.NoProxy);
        return builder.ToString();
    }

    private static string BuildBashInstallerProxyArgs(HostWorkerEnrollmentProxy proxy)
    {
        if (!proxy.HasAny)
            return string.Empty;

        var builder = new StringBuilder();
        AppendBashInstallerArg(builder, "--http-proxy", proxy.HttpProxy);
        AppendBashInstallerArg(builder, "--https-proxy", proxy.HttpsProxy);
        AppendBashInstallerArg(builder, "--no-proxy", proxy.NoProxy);
        return builder.ToString();
    }

    private static void AppendBashInstallerArg(StringBuilder builder, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        builder.Append(" \\");
        builder.AppendLine();
        builder.Append("  ");
        builder.Append(name);
        builder.Append(' ');
        builder.Append(ShellQuote(value));
    }

    private static string BuildPowerShellInstallerProxyArgs(HostWorkerEnrollmentProxy proxy)
    {
        if (!proxy.HasAny)
            return string.Empty;

        var builder = new StringBuilder();
        AppendPowerShellInstallerArg(builder, "-HttpProxy", proxy.HttpProxy);
        AppendPowerShellInstallerArg(builder, "-HttpsProxy", proxy.HttpsProxy);
        AppendPowerShellInstallerArg(builder, "-NoProxy", proxy.NoProxy);
        return builder.ToString();
    }

    private static void AppendPowerShellInstallerArg(StringBuilder builder, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        builder.Append(" `");
        builder.AppendLine();
        builder.Append("  ");
        builder.Append(name);
        builder.Append(' ');
        builder.Append(PowerShellQuote(value));
    }

    private static void AppendWindowsDockerEnvironment(StringBuilder builder, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        builder.Append("  -e '");
        builder.Append(name);
        builder.Append('=');
        builder.Append(value.Replace("'", "''"));
        builder.AppendLine("' `");
    }

    private static string NormalizeCommand(string command)
    {
        var lines = command.Replace("\r\n", "\n").Split('\n');
        var first = Array.FindIndex(lines, line => !string.IsNullOrWhiteSpace(line));
        var last = Array.FindLastIndex(lines, line => !string.IsNullOrWhiteSpace(line));
        if (first < 0 || last < 0)
            return string.Empty;

        var trimmed = lines[first..(last + 1)];
        var indent = trimmed
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(LeadingSpaces)
            .DefaultIfEmpty(0)
            .Min();

        return string.Join('\n', trimmed.Select(line => line.Length >= indent ? line[indent..].TrimEnd() : line.TrimEnd()));
    }

    private static int LeadingSpaces(string value)
    {
        var count = 0;
        while (count < value.Length && value[count] == ' ')
            count++;
        return count;
    }

    private static string YamlString(string value)
        => "'" + value.Replace("'", "''") + "'";

    private static string ShellQuote(string value)
        => "'" + value.Replace("'", "'\"'\"'") + "'";

    private static string PowerShellQuote(string value)
        => "'" + value.Replace("'", "''") + "'";

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}
