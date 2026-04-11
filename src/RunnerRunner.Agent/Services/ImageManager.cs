using System.Diagnostics;
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
        string imageName, string tag,
        Func<ImagePullProgressEvent, Task> onProgress,
        CancellationToken ct = default)
    {
        var fullImage = $"{imageName}:{tag}";
        _logger.LogInformation("Pulling Docker image {Image}", fullImage);

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = $"pull {fullImage}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        process.Start();

        // Parse Docker pull progress output
        string? line; while ((line = await process.StandardOutput.ReadLineAsync(ct)) != null)
        {
            
            

            // Docker outputs lines like: "Downloading  [====>  ]  12.5MB/45.2MB"
            var progress = ParseDockerPullProgress(line);
            if (progress.HasValue)
            {
                await onProgress(new ImagePullProgressEvent
                {
                    HostId = "",
                    ImageType = ImageType.Docker,
                    ImageName = fullImage,
                    ProgressPercent = progress.Value.percent,
                    BytesDownloaded = progress.Value.downloaded,
                    BytesTotal = progress.Value.total,
                    Status = line.Trim()
                });
            }
        }

        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync(ct);
            throw new InvalidOperationException($"Docker pull failed: {error}");
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
            StartInfo = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = $"login {registryUrl} -u {username} --password-stdin",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
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
        if (result.ExitCode != 0) return [];

        var images = new List<AgentImageInfo>();
        try
        {
            var jsonArray = JsonDocument.Parse(result.Output).RootElement;
            foreach (var item in jsonArray.EnumerateArray())
            {
                var name = item.GetProperty("Name").GetString() ?? "";
                var diskSize = item.TryGetProperty("DiskSize", out var ds) ? ds.GetInt64() : 0;

                images.Add(new AgentImageInfo
                {
                    ImageType = ImageType.Tart,
                    Repository = name,
                    Tag = "local",
                    ImageId = name,
                    SizeBytes = diskSize * 1024 * 1024 * 1024, // GB to bytes
                    CreatedAt = null
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse tart list output");
        }

        _logger.LogInformation("Listed {Count} Tart images", images.Count);
        return images;
    }

    public async Task PullTartImageAsync(
        string imageName,
        Func<ImagePullProgressEvent, Task> onProgress,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Pulling Tart image {Image}", imageName);

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "tart",
                Arguments = $"pull {imageName}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
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
                    ImageName = imageName,
                    ProgressPercent = percent.Value,
                    Status = line.Trim()
                });
            }
        }

        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Tart pull failed for {imageName}");
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
                StartInfo = new ProcessStartInfo
                {
                    FileName = command,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
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

    private static long ParseDockerSize(string size)
    {
        // Parse "45.2MB", "1.2GB", "500kB"
        var match = SizeRegex().Match(size);
        if (!match.Success) return 0;
        var value = double.Parse(match.Groups[1].Value);
        return match.Groups[2].Value.ToUpper() switch
        {
            "KB" => (long)(value * 1024),
            "MB" => (long)(value * 1024 * 1024),
            "GB" => (long)(value * 1024 * 1024 * 1024),
            "TB" => (long)(value * 1024 * 1024 * 1024 * 1024),
            _ => (long)value
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

    [GeneratedRegex(@"([\d.]+)\s*(KB|MB|GB|TB|B)", RegexOptions.IgnoreCase)]
    private static partial Regex SizeRegex();

    [GeneratedRegex(@"([\d.]+)\s*(KB|MB|GB|TB|B)\s*/\s*([\d.]+)\s*(KB|MB|GB|TB|B)", RegexOptions.IgnoreCase)]
    private static partial Regex ProgressRegex();

    [GeneratedRegex(@"([\d.]+)\s*%")]
    private static partial Regex PercentRegex();
}
