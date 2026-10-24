using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
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

        // The replacement takes over the current container's name so anything that starts the
        // HostWorker by name (compose, autostart scripts) resumes the new version rather than
        // resurrecting the old one. The old container is renamed out of the way and kept stopped
        // for rollback; only the most recent retired container is retained.
        var currentName = current.Name?.TrimStart('/');
        var canonicalName = ResolveCanonicalContainerName(currentName);
        var retiredName = BuildRetiredContainerName(canonicalName);
        var originalRestartPolicy = current.HostConfig?.RestartPolicy;

        await RemoveRetiredContainerAsync(client, retiredName, current.ID, ct);
        var renamed = !string.Equals(currentName, retiredName, StringComparison.Ordinal);
        if (renamed)
            await client.Containers.RenameContainerAsync(current.ID, new ContainerRenameParameters { NewName = retiredName }, ct);

        string? replacementId = null;
        try
        {
            // Without this an `always` restart policy would bring the retired container back on the
            // next daemon restart, leaving two HostWorkers with the same identity.
            await client.Containers.UpdateContainerAsync(current.ID, new ContainerUpdateParameters
            {
                RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.No }
            }, ct);

            var (primaryNetwork, additionalNetworks) = SplitNetworks(current);
            var createResponse = await client.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Image = command.ContainerImage,
                Name = canonicalName,
                Env = BuildReplacementEnvironment(current.Config.Env, command.ContainerImage),
                Labels = BuildReplacementLabels(current.Config.Labels, current.ID),
                HostConfig = current.HostConfig,
                NetworkingConfig = primaryNetwork is { } primary
                    ? new NetworkingConfig
                    {
                        EndpointsConfig = new Dictionary<string, EndpointSettings>
                        {
                            [primary.Key] = new() { Aliases = FilterNetworkAliases(primary.Key, primary.Value?.Aliases, current.ID) }
                        }
                    }
                    : null
            }, ct);
            replacementId = createResponse.ID;

            // HostConfig only carries the primary network. Networks attached afterwards
            // (e.g. `docker network connect <compose network>`) must be re-attached, otherwise
            // the replacement cannot resolve the server by its service name.
            foreach (var (network, endpoint) in additionalNetworks)
            {
                await client.Networks.ConnectNetworkAsync(network, new NetworkConnectParameters
                {
                    Container = replacementId,
                    EndpointConfig = new EndpointSettings
                    {
                        Aliases = FilterNetworkAliases(network, endpoint?.Aliases, current.ID)
                    }
                }, ct);
            }

            await client.Containers.StartContainerAsync(replacementId, null, ct);
        }
        catch
        {
            await RollBackReplacementAsync(client, replacementId, current.ID, renamed ? currentName ?? canonicalName : null, originalRestartPolicy);
            throw;
        }

        // Schedule the stop before publishing so a failed publish can't leave both containers running.
        ScheduleContainerStop(currentContainerId);
        await PublishAsync("restarting", $"Started replacement container {replacementId[..12]} with {command.ContainerImage}.", true, true, null, command, publish, ct);
    }

    /// <summary>
    /// Splits the container's networks into the primary one (from <c>HostConfig.NetworkMode</c>,
    /// which may be a name or an ID) and those attached afterwards with <c>docker network connect</c>.
    /// </summary>
    internal static (KeyValuePair<string, EndpointSettings?>? Primary, List<KeyValuePair<string, EndpointSettings?>> Additional) SplitNetworks(ContainerInspectResponse current)
    {
        var networkMode = current.HostConfig?.NetworkMode;
        // host / none / container:<id> modes cannot be combined with other networks.
        if (networkMode is "host" or "none" || networkMode?.StartsWith("container:", StringComparison.Ordinal) == true)
            return (null, []);

        var primaryMode = string.IsNullOrEmpty(networkMode) || networkMode == "default" ? "bridge" : networkMode;
        KeyValuePair<string, EndpointSettings?>? primary = null;
        var additional = new List<KeyValuePair<string, EndpointSettings?>>();
        foreach (var (name, endpoint) in current.NetworkSettings?.Networks ?? new Dictionary<string, EndpointSettings>())
        {
            var isPrimary = string.Equals(name, primaryMode, StringComparison.Ordinal)
                || (!string.IsNullOrEmpty(endpoint?.NetworkID) && endpoint.NetworkID.StartsWith(primaryMode, StringComparison.Ordinal));
            if (isPrimary && primary == null)
                primary = new KeyValuePair<string, EndpointSettings?>(name, endpoint);
            else
                additional.Add(new KeyValuePair<string, EndpointSettings?>(name, endpoint));
        }

        return (primary, additional);
    }

    internal static IList<string>? FilterNetworkAliases(string network, IList<string>? aliases, string currentContainerId)
    {
        // The default bridge network rejects network-scoped aliases.
        if (network == "bridge")
            return null;

        // Docker adds the container's 12-char short ID as an alias; don't carry the old one across.
        var shortId = currentContainerId.Length >= 12 ? currentContainerId[..12] : currentContainerId;
        var filtered = aliases?
            .Where(a => !string.IsNullOrWhiteSpace(a) && !string.Equals(a, shortId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return filtered is { Count: > 0 } ? filtered : null;
    }

    internal static string ResolveCanonicalContainerName(string? currentName)
    {
        if (string.IsNullOrWhiteSpace(currentName))
            return "runnerrunner-host-worker";

        // An operator may have rolled back by starting the retired container; don't nest suffixes.
        return currentName.EndsWith(RetiredContainerSuffix, StringComparison.Ordinal) && currentName.Length > RetiredContainerSuffix.Length
            ? currentName[..^RetiredContainerSuffix.Length]
            : currentName;
    }

    private const string RetiredContainerSuffix = "-replaced";

    internal static string BuildRetiredContainerName(string canonicalName)
    {
        var prefixLength = Math.Min(canonicalName.Length, 63 - RetiredContainerSuffix.Length);
        return canonicalName[..prefixLength] + RetiredContainerSuffix;
    }

    private async Task RemoveRetiredContainerAsync(DockerClient client, string retiredName, string currentContainerId, CancellationToken ct)
    {
        try
        {
            var previous = await client.Containers.InspectContainerAsync(retiredName, ct);
            if (string.Equals(previous.ID, currentContainerId, StringComparison.Ordinal))
                return;

            if (previous.State?.Running == true)
            {
                _logger.LogWarning("Retired HostWorker container {ContainerId} ({Name}) is running; removing it before the update",
                    previous.ID[..12], retiredName);
            }

            await client.Containers.RemoveContainerAsync(previous.ID, new ContainerRemoveParameters { Force = true }, ct);
            _logger.LogInformation("Removed previously retired HostWorker container {ContainerId}", previous.ID[..12]);
        }
        catch (DockerContainerNotFoundException)
        {
            // Nothing retired from an earlier update.
        }
    }

    private async Task RollBackReplacementAsync(
        DockerClient client, string? replacementId, string currentContainerId, string? originalName, RestartPolicy? originalRestartPolicy)
    {
        try
        {
            if (replacementId != null)
                await client.Containers.RemoveContainerAsync(replacementId, new ContainerRemoveParameters { Force = true }, CancellationToken.None);

            if (originalName != null)
                await client.Containers.RenameContainerAsync(currentContainerId, new ContainerRenameParameters { NewName = originalName }, CancellationToken.None);

            if (originalRestartPolicy != null)
            {
                await client.Containers.UpdateContainerAsync(currentContainerId, new ContainerUpdateParameters
                {
                    RestartPolicy = originalRestartPolicy
                }, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to roll back HostWorker container replacement; container {ContainerId} may need to be renamed back to {Name} and its restart policy restored",
                currentContainerId[..12], originalName);
        }
    }

    private async Task DownloadAsync(string url, string outputPath, CancellationToken ct)
    {
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddEnrollmentTokenAuthorization(request, url);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await input.CopyToAsync(output, ct);
    }

    private void AddEnrollmentTokenAuthorization(HttpRequestMessage request, string url)
    {
        var enrollmentToken = _configuration["HostWorker:EnrollmentToken"];
        if (string.IsNullOrWhiteSpace(enrollmentToken) || !IsRunnerRunnerUpdateAssetUrl(url))
            return;

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", enrollmentToken.Trim());
    }

    internal static bool IsRunnerRunnerUpdateAssetUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
           uri.AbsolutePath.Contains("/api/hostworker-updates/", StringComparison.OrdinalIgnoreCase);

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
    {
        var buildInfo = HostWorkerVersion.BuildInfo;
        return publish(new HostWorkerUpdateStatusEvent
        {
            HostId = _identity.HostId,
            CurrentVersion = buildInfo.InformationalVersion,
            CurrentCommitSha = buildInfo.CommitSha,
            TargetVersion = command.TargetVersion,
            TargetCommitSha = command.TargetCommitSha,
            Stage = stage,
            Message = message,
            IsComplete = isComplete,
            Success = success,
            Error = error
        }, ct);
    }

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
