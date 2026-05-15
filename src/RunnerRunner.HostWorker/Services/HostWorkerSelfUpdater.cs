using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using RunnerRunner.Core.Hub;

namespace RunnerRunner.HostWorker.Services;

internal sealed class HostWorkerSelfUpdater
{
    private readonly IConfiguration _configuration;
    private readonly HostWorkerIdentity _identity;
    private readonly HostWorkerPaths _paths;
    private readonly ILogger<HostWorkerSelfUpdater> _logger;

    public HostWorkerSelfUpdater(
        IConfiguration configuration,
        HostWorkerIdentity identity,
        HostWorkerPaths paths,
        ILogger<HostWorkerSelfUpdater> logger)
    {
        _configuration = configuration;
        _identity = identity;
        _paths = paths;
        _logger = logger;
    }

    public async Task ApplyAsync(
        HostWorkerUpdateCommand command,
        Func<HostWorkerUpdateStatusEvent, CancellationToken, Task> publish,
        CancellationToken ct)
    {
        if (HostWorkerRuntimeDetector.IsContainer())
        {
            await ApplyContainerAsync(command, publish, ct);
            return;
        }

        await PublishAsync("downloading", $"Downloading {command.AssetName}.", false, false, null, command, publish, ct);

        var updateRoot = Path.Combine(_paths.DataRoot, "updates", Sanitize(command.TargetVersion));
        var downloadPath = Path.Combine(updateRoot, command.AssetName);
        var payloadPath = Path.Combine(updateRoot, "payload");
        Directory.CreateDirectory(updateRoot);

        await DownloadAsync(command.AssetUrl, downloadPath, ct);
        await PublishAsync("verifying", "Verifying release checksum.", false, false, null, command, publish, ct);
        await VerifySha256Async(downloadPath, command.Sha256, ct);

        if (Directory.Exists(payloadPath))
            Directory.Delete(payloadPath, recursive: true);
        Directory.CreateDirectory(payloadPath);
        Extract(downloadPath, payloadPath);

        var installPlan = PrepareInstallPlan(command.TargetVersion, payloadPath);
        await PublishAsync("staged", $"Staged {command.TargetVersion}; restarting HostWorker.", false, false, null, command, publish, ct);

        await installPlan.PrepareAsync(ct);
        installPlan.StartHandoffProcess();

        await PublishAsync("restarting", $"Restarting into {command.TargetVersion}.", true, true, null, command, publish, ct);
        ScheduleExit();
    }

    private async Task ApplyContainerAsync(
        HostWorkerUpdateCommand command,
        Func<HostWorkerUpdateStatusEvent, CancellationToken, Task> publish,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command.ContainerImage))
            throw new InvalidOperationException("This HostWorker is running in a container, but no target container image was provided.");

        var currentContainerId = HostWorkerRuntimeDetector.TryDetectContainerId();
        if (string.IsNullOrWhiteSpace(currentContainerId))
            throw new InvalidOperationException("Unable to determine the current HostWorker container ID.");

        await PublishAsync("pulling", $"Pulling {command.ContainerImage}.", false, false, null, command, publish, ct);
        using var client = CreateDockerClient();
        await client.Images.CreateImageAsync(
            CreateImageParameters(command.ContainerImage),
            null,
            new Progress<JSONMessage>(message =>
            {
                if (!string.IsNullOrWhiteSpace(message.Status))
                    _logger.LogDebug("HostWorker image pull: {Status}", message.Status);
            }),
            ct);

        await PublishAsync("creating", "Creating replacement HostWorker container.", false, false, null, command, publish, ct);
        var current = await client.Containers.InspectContainerAsync(currentContainerId, ct);
        var replacementName = BuildReplacementContainerName(current.Name);
        var createResponse = await client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = command.ContainerImage,
            Name = replacementName,
            Env = BuildReplacementEnvironment(current.Config.Env, command.ContainerImage),
            Labels = BuildReplacementLabels(current.Config.Labels, current.ID),
            HostConfig = current.HostConfig
        }, ct);

        await client.Containers.StartContainerAsync(createResponse.ID, null, ct);
        await PublishAsync("restarting", $"Started replacement container {createResponse.ID[..12]} with {command.ContainerImage}.", true, true, null, command, publish, ct);
        ScheduleContainerStop(currentContainerId);
    }

    private async Task DownloadAsync(string url, string outputPath, CancellationToken ct)
    {
        using var client = new HttpClient();
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await input.CopyToAsync(output, ct);
    }

    private static async Task VerifySha256Async(string path, string expectedSha256, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256))
            throw new InvalidOperationException("Release asset did not include a SHA256 checksum.");

        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Checksum mismatch for update asset. Expected {expectedSha256}, got {actual}.");
    }

    private static void Extract(string archivePath, string destinationPath)
    {
        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archivePath, destinationPath, overwriteFiles: true);
            return;
        }

        if (archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
            archivePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            using var file = File.OpenRead(archivePath);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gzip, destinationPath, overwriteFiles: true);
            return;
        }

        throw new InvalidOperationException($"Unsupported HostWorker update archive '{Path.GetFileName(archivePath)}'.");
    }

    private InstallPlan PrepareInstallPlan(string targetVersion, string payloadPath)
    {
        var appDirectory = Path.GetFullPath(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var appDirectoryInfo = new DirectoryInfo(appDirectory);
        var versionsDirectory = appDirectoryInfo.Parent?.Name.Equals("versions", StringComparison.OrdinalIgnoreCase) == true
            ? appDirectoryInfo.Parent
            : null;

        if (versionsDirectory?.Parent != null)
        {
            var targetDirectory = Path.Combine(versionsDirectory.FullName, Sanitize(targetVersion));
            return new VersionedInstallPlan(payloadPath, targetDirectory, Path.Combine(versionsDirectory.Parent.FullName, "current"), _logger);
        }

        if (OperatingSystem.IsWindows())
        {
            var serviceName = _configuration["HostWorker:WindowsServiceName"] ?? "RunnerRunnerHostWorker";
            return new WindowsFlatInstallPlan(payloadPath, appDirectory, serviceName, _logger);
        }

        var restartCommand = _configuration["HostWorker:RestartCommand"];
        return new UnixFlatInstallPlan(payloadPath, appDirectory, restartCommand, _logger);
    }

    private Task PublishAsync(
        string stage,
        string message,
        bool isComplete,
        bool success,
        string? error,
        HostWorkerUpdateCommand command,
        Func<HostWorkerUpdateStatusEvent, CancellationToken, Task> publish,
        CancellationToken ct)
        => publish(new HostWorkerUpdateStatusEvent
        {
            HostId = _identity.HostId,
            CurrentVersion = HostWorkerVersion.Current,
            TargetVersion = command.TargetVersion,
            Stage = stage,
            Message = message,
            IsComplete = isComplete,
            Success = success,
            Error = error
        }, ct);

    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_')
                builder.Append(ch);
            else
                builder.Append('_');
        }

        return builder.Length == 0 ? "update" : builder.ToString();
    }

    private static void ScheduleExit()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            Environment.Exit(0);
        });
    }

    private void ScheduleContainerStop(string currentContainerId)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            try
            {
                using var client = CreateDockerClient();
                await client.Containers.StopContainerAsync(
                    currentContainerId,
                    new ContainerStopParameters { WaitBeforeKillSeconds = 10 },
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to stop replaced HostWorker container {ContainerId}", currentContainerId);
            }
        });
    }

    private static DockerClient CreateDockerClient()
    {
        var dockerHost = Environment.GetEnvironmentVariable("DOCKER_HOST");
        Uri endpoint;
        if (!string.IsNullOrWhiteSpace(dockerHost))
        {
            endpoint = new Uri(dockerHost);
        }
        else if (OperatingSystem.IsWindows())
        {
            endpoint = new Uri("npipe://./pipe/docker_engine");
        }
        else
        {
            endpoint = new Uri("unix:///var/run/docker.sock");
        }

        return new DockerClientConfiguration(endpoint).CreateClient();
    }

    private static ImagesCreateParameters CreateImageParameters(string image)
    {
        var lastSlash = image.LastIndexOf('/');
        var tagIndex = image.LastIndexOf(':');
        if (tagIndex > lastSlash)
        {
            return new ImagesCreateParameters
            {
                FromImage = image[..tagIndex],
                Tag = image[(tagIndex + 1)..]
            };
        }

        return new ImagesCreateParameters { FromImage = image };
    }

    private static string BuildReplacementContainerName(string? currentName)
    {
        var normalized = string.IsNullOrWhiteSpace(currentName)
            ? "runnerrunner-host-worker"
            : currentName.TrimStart('/');
        var suffix = "-update-" + Guid.NewGuid().ToString("N")[..12];
        var prefixLength = Math.Min(normalized.Length, 63 - suffix.Length);
        return normalized[..prefixLength] + suffix;
    }

    private static IList<string> BuildReplacementEnvironment(IList<string>? environment, string targetImage)
    {
        const string containerImageKey = "HostWorker__ContainerImage";
        var result = environment?.ToList() ?? [];
        var index = result.FindIndex(x => x.StartsWith(containerImageKey + "=", StringComparison.OrdinalIgnoreCase));
        var value = $"{containerImageKey}={targetImage}";
        if (index >= 0)
            result[index] = value;
        else
            result.Add(value);

        return result;
    }

    private static IDictionary<string, string> BuildReplacementLabels(IDictionary<string, string>? labels, string currentContainerId)
    {
        var replacementLabels = labels == null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(labels, StringComparer.Ordinal);

        replacementLabels["runnerrunner.hostworker.replaces"] = currentContainerId;
        replacementLabels["runnerrunner.hostworker.updated-at"] = DateTimeOffset.UtcNow.ToString("O");
        return replacementLabels;
    }

    private abstract class InstallPlan
    {
        protected InstallPlan(string payloadPath, ILogger logger)
        {
            PayloadPath = payloadPath;
            Logger = logger;
        }

        protected string PayloadPath { get; }
        protected ILogger Logger { get; }

        public abstract Task PrepareAsync(CancellationToken ct);
        public abstract void StartHandoffProcess();

        protected static void CopySettings(string sourceDirectory, string targetDirectory)
        {
            var productionSettings = Path.Combine(sourceDirectory, "appsettings.Production.json");
            if (File.Exists(productionSettings))
                File.Copy(productionSettings, Path.Combine(targetDirectory, "appsettings.Production.json"), overwrite: true);
        }
    }

    private sealed class VersionedInstallPlan : InstallPlan
    {
        private readonly string _targetDirectory;
        private readonly string _currentLink;

        public VersionedInstallPlan(string payloadPath, string targetDirectory, string currentLink, ILogger logger)
            : base(payloadPath, logger)
        {
            _targetDirectory = targetDirectory;
            _currentLink = currentLink;
        }

        public override Task PrepareAsync(CancellationToken ct)
        {
            if (Directory.Exists(_targetDirectory))
                Directory.Delete(_targetDirectory, recursive: true);
            CopyDirectory(PayloadPath, _targetDirectory);
            CopySettings(AppContext.BaseDirectory, _targetDirectory);
            TryMakeExecutable(Path.Combine(_targetDirectory, "RunnerRunner.HostWorker"));
            if (File.Exists(_currentLink) || Directory.Exists(_currentLink))
                DeleteExistingLink(_currentLink);
            Directory.CreateSymbolicLink(_currentLink, _targetDirectory);
            return Task.CompletedTask;
        }

        public override void StartHandoffProcess()
        {
            Logger.LogInformation("HostWorker update staged at {TargetDirectory}; supervisor restart will run {CurrentLink}", _targetDirectory, _currentLink);
        }
    }

    private sealed class UnixFlatInstallPlan : InstallPlan
    {
        private readonly string _appDirectory;
        private readonly string? _restartCommand;

        public UnixFlatInstallPlan(string payloadPath, string appDirectory, string? restartCommand, ILogger logger)
            : base(payloadPath, logger)
        {
            _appDirectory = appDirectory;
            _restartCommand = restartCommand;
        }

        public override Task PrepareAsync(CancellationToken ct)
        {
            CopySettings(_appDirectory, PayloadPath);
            var scriptPath = Path.Combine(Path.GetDirectoryName(PayloadPath)!, "apply-update.sh");
            var script = $$"""
                #!/usr/bin/env bash
                set -euo pipefail
                while kill -0 {{Environment.ProcessId}} >/dev/null 2>&1; do sleep 1; done
                cp -R "{{PayloadPath}}"/. "{{_appDirectory}}"/
                chmod +x "{{Path.Combine(_appDirectory, "RunnerRunner.HostWorker")}}" >/dev/null 2>&1 || true
                {{(string.IsNullOrWhiteSpace(_restartCommand) ? "" : _restartCommand)}}
                """;
            File.WriteAllText(scriptPath, script);
            TryMakeExecutable(scriptPath);
            return Task.CompletedTask;
        }

        public override void StartHandoffProcess()
        {
            var scriptPath = Path.Combine(Path.GetDirectoryName(PayloadPath)!, "apply-update.sh");
            Process.Start(new ProcessStartInfo("/bin/sh", Quote(scriptPath)) { UseShellExecute = false });
        }
    }

    private sealed class WindowsFlatInstallPlan : InstallPlan
    {
        private readonly string _appDirectory;
        private readonly string _serviceName;

        public WindowsFlatInstallPlan(string payloadPath, string appDirectory, string serviceName, ILogger logger)
            : base(payloadPath, logger)
        {
            _appDirectory = appDirectory;
            _serviceName = serviceName;
        }

        public override Task PrepareAsync(CancellationToken ct)
        {
            CopySettings(_appDirectory, PayloadPath);
            var scriptPath = Path.Combine(Path.GetDirectoryName(PayloadPath)!, "Apply-Update.ps1");
            var script = $$"""
                $ErrorActionPreference = "Stop"
                $pidToWait = {{Environment.ProcessId}}
                while (Get-Process -Id $pidToWait -ErrorAction SilentlyContinue) { Start-Sleep -Seconds 1 }
                Copy-Item -Path "{{PayloadPath}}\*" -Destination "{{_appDirectory}}" -Recurse -Force
                $service = Get-Service -Name "{{_serviceName}}" -ErrorAction SilentlyContinue
                if ($service -and $service.Status -ne "Running") { Start-Service -Name "{{_serviceName}}" }
                """;
            File.WriteAllText(scriptPath, script);
            return Task.CompletedTask;
        }

        public override void StartHandoffProcess()
        {
            var scriptPath = Path.Combine(Path.GetDirectoryName(PayloadPath)!, "Apply-Update.ps1");
            Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File {Quote(scriptPath)}")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
    }

    private static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);
        foreach (var directory in Directory.GetDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(directory.Replace(sourceDirectory, targetDirectory, StringComparison.Ordinal));
        foreach (var file in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(sourceDirectory, targetDirectory, StringComparison.Ordinal), overwrite: true);
    }

    private static void TryMakeExecutable(string path)
    {
        if (!File.Exists(path) || OperatingSystem.IsWindows())
            return;

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                      UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                                      UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch
        {
            // Best-effort only; chmod may be unavailable on some filesystems.
        }
    }

    private static void DeleteExistingLink(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (UnauthorizedAccessException)
        {
            Directory.Delete(path);
        }
        catch (IOException)
        {
            Directory.Delete(path);
        }
    }

    private static string Quote(string value)
        => OperatingSystem.IsWindows()
            ? "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
}
