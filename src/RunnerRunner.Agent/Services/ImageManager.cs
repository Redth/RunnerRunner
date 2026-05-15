using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using RunnerRunner.Core.Hub;
using RunnerRunner.Core.Models;

namespace RunnerRunner.Agent.Services;

/// <summary>
/// Manages Docker and Tart images on the agent host.
/// Handles listing, pulling (with progress), deleting, and registry auth.
/// </summary>
public partial class ImageManager
{
    private readonly ILogger<ImageManager> _logger;

    public ImageManager(ILogger<ImageManager> logger)
    {
        _logger = logger;
    }

    // ─── Docker ────────────────────────────────────────────

    public async Task<List<AgentImageInfo>> ListDockerImagesAsync(CancellationToken ct = default)
    {
        var result = await RunCommandAsync("docker", "images --format json", ct);
        if (result.ExitCode != 0) return [];

        var images = new List<AgentImageInfo>();
        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var json = JsonDocument.Parse(line).RootElement;
                var repo = json.GetProperty("Repository").GetString() ?? "";
                if (repo == "<none>") continue;

                images.Add(new AgentImageInfo
                {
                    ImageType = ImageType.Docker,
                    Repository = repo,
                    Tag = json.GetProperty("Tag").GetString() ?? "latest",
                    ImageId = json.GetProperty("ID").GetString(),
                    SizeBytes = ParseDockerSize(json.GetProperty("Size").GetString() ?? "0"),
                    CreatedAt = null
                });
            }
            catch { /* skip unparseable lines */ }
        }

        _logger.LogInformation("Listed {Count} Docker images", images.Count);
        return images;
    }

    public async Task PullDockerImageAsync(
        string imageName, string tag, string? registryUrl,
        Func<ImagePullProgressEvent, Task> onProgress,
        CancellationToken ct = default)
    {
        var fullImage = ImageReference.Build(registryUrl, imageName, tag);
        _logger.LogInformation("Pulling Docker image {Image}", fullImage);

        var process = new Process
        {
            StartInfo = CreateProcessStartInfo("docker", $"pull {fullImage}")
        };

        process.Start();

        var errorLines = new List<string>();
        using var progressLock = new SemaphoreSlim(1, 1);
        var lastPercent = 0.0;
        long lastDownloaded = 0;
        long lastTotal = 0;

        var stdoutTask = ReadDockerPullOutputAsync(process.StandardOutput, isError: false);
        var stderrTask = ReadDockerPullOutputAsync(process.StandardError, isError: true);
        await process.WaitForExitAsync(ct);
        await Task.WhenAll(stdoutTask, stderrTask);

        if (process.ExitCode != 0)
        {
            var error = string.Join(Environment.NewLine, errorLines).Trim();
            if (string.IsNullOrWhiteSpace(error))
                error = $"docker pull exited with code {process.ExitCode}.";

            throw new InvalidOperationException($"Docker pull failed: {error}");
        }

        async Task ReadDockerPullOutputAsync(TextReader reader, bool isError)
        {
            string? line;
            while ((line = await reader.ReadLineAsync(ct)) != null)
            {
                var status = line.Trim();
                if (string.IsNullOrWhiteSpace(status))
                    continue;

                if (isError)
                    errorLines.Add(status);

                var progress = ParseDockerPullProgress(status);
                await progressLock.WaitAsync(ct);
                try
                {
                    if (progress.HasValue)
                    {
                        lastPercent = Math.Clamp(progress.Value.percent, 0, 100);
                        lastDownloaded = progress.Value.downloaded;
                        lastTotal = progress.Value.total;
                    }

                    await onProgress(new ImagePullProgressEvent
                    {
                        HostId = "",
                        ImageType = ImageType.Docker,
                        ImageName = fullImage,
                        ProgressPercent = lastPercent,
                        BytesDownloaded = lastDownloaded,
                        BytesTotal = lastTotal,
                        Status = status
                    });
                }
                finally
                {
                    progressLock.Release();
                }
            }
        }
    }

    public async Task DeleteDockerImageAsync(string imageId, CancellationToken ct = default)
    {
        _logger.LogInformation("Deleting Docker image {ImageId}", imageId);
        var result = await RunCommandAsync("docker", $"rmi {imageId}", ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Docker rmi failed: {result.Output}");
    }

    public async Task LoginDockerRegistryAsync(string registryUrl, string? username, string? password, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password)) return;

        _logger.LogInformation("Logging in to Docker registry {Registry}", registryUrl);
        var process = new Process
        {
            StartInfo = CreateProcessStartInfo("docker", $"login {registryUrl} -u {username} --password-stdin", redirectStandardInput: true)
        };

        process.Start();
        await process.StandardInput.WriteLineAsync(password);
        process.StandardInput.Close();
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync(ct);
            throw new InvalidOperationException($"Docker login failed: {error}");
        }
    }

    // ─── Tart ──────────────────────────────────────────────

    public async Task<List<AgentImageInfo>> ListTartImagesAsync(CancellationToken ct = default)
    {
        var result = await RunCommandAsync("tart", "list --format json", ct);
        if (result.ExitCode != 0)
        {
            _logger.LogWarning("tart list failed (exit {Code}): {Output}", result.ExitCode, result.Output);
            return [];
        }

        var images = new List<AgentImageInfo>();
        try
        {
            var jsonArray = JsonDocument.Parse(result.Output).RootElement;
            foreach (var item in jsonArray.EnumerateArray())
            {
                var name = item.TryGetProperty("Name", out var np) ? np.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(name)) continue;

                // "Size" is the actual disk usage in GB, "Disk" is the virtual disk size
                var sizeBytes = item.TryGetProperty("Size", out var sp) ? ParseTartSizeBytes(sp) : 0;
                var state = item.TryGetProperty("State", out var stp) ? stp.GetString() : "unknown";

                images.Add(new AgentImageInfo
                {
                    ImageType = ImageType.Tart,
                    Repository = name,
                    Tag = state ?? "local",
                    ImageId = name,
                    SizeBytes = sizeBytes,
                    CreatedAt = null
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse tart list output: {Output}", result.Output);
        }

        _logger.LogInformation("Listed {Count} Tart images", images.Count);
        return images;
    }

    public async Task PullTartImageAsync(
        string imageName,
        string tag,
        string? registryUrl,
        Func<ImagePullProgressEvent, Task> onProgress,
        CancellationToken ct = default)
    {
        var fullImage = ImageReference.Build(registryUrl, imageName, tag);
        _logger.LogInformation("Pulling Tart image {Image}", fullImage);

        var process = new Process
        {
            StartInfo = CreateProcessStartInfo("tart", $"pull {fullImage}")
        };

        process.Start();

        // Tart outputs progress to stderr
        var lastPercent = 0.0;
        string? eline; while ((eline = await process.StandardError.ReadLineAsync(ct)) != null)
        {
            var line = eline;
            

            var percent = ParseTartProgress(line);
            if (percent.HasValue && percent.Value > lastPercent)
            {
                lastPercent = percent.Value;
                await onProgress(new ImagePullProgressEvent
                {
                    HostId = "",
                    ImageType = ImageType.Tart,
                    ImageName = fullImage,
                    ProgressPercent = percent.Value,
                    Status = line.Trim()
                });
            }
        }

        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Tart pull failed for {fullImage}");
    }

    public async Task DeleteTartImageAsync(string imageName, CancellationToken ct = default)
    {
        _logger.LogInformation("Deleting Tart image {Name}", imageName);
        var result = await RunCommandAsync("tart", $"delete {imageName}", ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Tart delete failed: {result.Output}");
    }

    // ─── Helpers ───────────────────────────────────────────

    private static async Task<(int ExitCode, string Output)> RunCommandAsync(
        string command, string arguments, CancellationToken ct)
    {
        try
        {
            var process = new Process
            {
                StartInfo = CreateProcessStartInfo(command, arguments)
            };

            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync(ct);
            var error = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            return (process.ExitCode, process.ExitCode == 0 ? output.Trim() : error.Trim());
        }
        catch
        {
            return (-1, "");
        }
    }

    private static ProcessStartInfo CreateProcessStartInfo(string command, string arguments, bool redirectStandardInput = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveToolPath(command),
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardInput = redirectStandardInput,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        var commonPaths = new[]
        {
            "/opt/homebrew/bin",
            "/opt/homebrew/sbin",
            "/usr/local/bin",
            "/usr/bin",
            "/bin",
            "/usr/sbin",
            "/sbin"
        };

        startInfo.Environment["PATH"] = string.Join(
            Path.PathSeparator,
            commonPaths
                .Concat(currentPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                .Distinct(StringComparer.Ordinal));

        return startInfo;
    }

    private static string ResolveToolPath(string command)
    {
        IEnumerable<string> candidates = command switch
        {
            "tart" =>
            [
                Environment.GetEnvironmentVariable("RUNNERRUNNER_TART_PATH") ?? "",
                "/opt/homebrew/bin/tart",
                "/usr/local/bin/tart",
                "/Applications/Tart.app/Contents/MacOS/tart",
                "tart"
            ],
            "docker" =>
            [
                Environment.GetEnvironmentVariable("RUNNERRUNNER_DOCKER_PATH") ?? "",
                "/usr/local/bin/docker",
                "/opt/homebrew/bin/docker",
                "/usr/bin/docker",
                "docker"
            ],
            _ => [command]
        };

        foreach (var candidate in candidates.Where(c => !string.IsNullOrWhiteSpace(c)))
        {
            if (!Path.IsPathRooted(candidate) || File.Exists(candidate))
                return candidate;
        }

        return command;
    }

    private static long ParseDockerSize(string size)
    {
        // Parse "45.2MB", "1.2GB", "500kB"
        var match = SizeRegex().Match(size);
        if (!match.Success) return 0;
        var value = double.Parse(match.Groups[1].Value);
        return match.Groups[2].Value.ToUpperInvariant().Replace("IB", "B") switch
        {
            "KB" => (long)(value * 1024),
            "MB" => (long)(value * 1024 * 1024),
            "GB" => (long)(value * 1024 * 1024 * 1024),
            "TB" => (long)(value * 1024 * 1024 * 1024 * 1024),
            _ => (long)value
        };
    }

    private static long ParseTartSizeBytes(JsonElement sizeElement)
    {
        return sizeElement.ValueKind switch
        {
            JsonValueKind.Number when sizeElement.TryGetInt64(out var sizeGb) =>
                sizeGb > 10_000_000 ? sizeGb : sizeGb * 1024 * 1024 * 1024,
            JsonValueKind.Number when sizeElement.TryGetDouble(out var sizeGbDouble) =>
                sizeGbDouble > 10_000_000 ? (long)sizeGbDouble : (long)(sizeGbDouble * 1024 * 1024 * 1024),
            JsonValueKind.String => ParseDockerSize(sizeElement.GetString() ?? "0"),
            _ => 0
        };
    }

    private static (double percent, long downloaded, long total)? ParseDockerPullProgress(string line)
    {
        // Match lines like: "50.2MB/120.5MB" or percentage patterns
        var match = ProgressRegex().Match(line);
        if (match.Success)
        {
            var dl = ParseDockerSize(match.Groups[1].Value + match.Groups[2].Value);
            var tot = ParseDockerSize(match.Groups[3].Value + match.Groups[4].Value);
            if (tot > 0) return ((double)dl / tot * 100, dl, tot);
        }
        return null;
    }

    private static double? ParseTartProgress(string line)
    {
        // Tart outputs percentage like "  45%" or "Downloading: 45.2%"
        var match = PercentRegex().Match(line);
        if (match.Success && double.TryParse(match.Groups[1].Value, out var pct))
            return pct;
        return null;
    }

    [GeneratedRegex(@"([\d.]+)\s*(KIB|MIB|GIB|TIB|KB|MB|GB|TB|B)", RegexOptions.IgnoreCase)]
    private static partial Regex SizeRegex();

    [GeneratedRegex(@"([\d.]+)\s*(KIB|MIB|GIB|TIB|KB|MB|GB|TB|B)\s*/\s*([\d.]+)\s*(KIB|MIB|GIB|TIB|KB|MB|GB|TB|B)", RegexOptions.IgnoreCase)]
    private static partial Regex ProgressRegex();

    [GeneratedRegex(@"([\d.]+)\s*%")]
    private static partial Regex PercentRegex();
}
