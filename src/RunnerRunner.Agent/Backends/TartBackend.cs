using System.Diagnostics;
using System.Text.Json;
using RunnerRunner.Core.Interfaces;
using RunnerRunner.Core.Models;

namespace RunnerRunner.Agent.Backends;

/// <summary>
/// Execution backend that runs runner instances in Tart macOS VMs.
/// Uses the tart CLI for VM lifecycle management and SSH for runner setup.
/// </summary>
public class TartBackend : IRunnerBackend
{
    private readonly ILogger<TartBackend> _logger;

    public ExecutionBackend BackendType => ExecutionBackend.Tart;

    public TartBackend(ILogger<TartBackend> logger)
    {
        _logger = logger;
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await RunCommandAsync("tart", "--version", ct);
            return result.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<RunnerInstanceInfo> StartRunnerAsync(RunnerStartRequest request, CancellationToken ct = default)
    {
        var config = request.TartConfig
            ?? throw new InvalidOperationException("TartConfig is required for Tart backend");

        var sourceImage = $"{config.RegistryUrl}/{config.ImageName}:{config.Tag}";
        var vmName = $"rr-{request.RunnerName}";

        // Clone the base image for this runner instance
        _logger.LogInformation("Cloning tart image {Source} → {VM}", sourceImage, vmName);
        await RunCommandAsync("tart", $"clone {sourceImage} {vmName}", ct);

        // Configure VM resources if specified
        if (config.CpuCount.HasValue)
            await RunCommandAsync("tart", $"set {vmName} --cpu {config.CpuCount.Value}", ct);
        if (config.MemorySizeGb.HasValue)
            await RunCommandAsync("tart", $"set {vmName} --memory {config.MemorySizeGb.Value * 1024}", ct);
        if (config.DiskSizeGb.HasValue)
            await RunCommandAsync("tart", $"set {vmName} --disk-size {config.DiskSizeGb.Value}", ct);
        if (!string.IsNullOrEmpty(config.Display))
            await RunCommandAsync("tart", $"set {vmName} --display {config.Display}", ct);

        // Prepare shared directory with .env file for auto-configuration
        var configDir = Path.Combine(Path.GetTempPath(), "runnerrunner", vmName);
        Directory.CreateDirectory(configDir);

        // Write environment variables as .env file
        var envFileContent = string.Join("\n",
            request.EnvironmentVariables.Select(kvp => $"{kvp.Key}={kvp.Value}"));
        await File.WriteAllTextAsync(Path.Combine(configDir, ".env"), envFileContent, ct);

        // Build tart run arguments with shared directories
        var dirArgs = $"--dir config:{configDir}";
        foreach (var dir in config.SharedDirs)
        {
            var roFlag = dir.ReadOnly ? ":ro" : "";
            dirArgs += $" --dir {dir.Name}:{dir.HostPath}{roFlag}";
        }

        // Start the VM in the background
        _logger.LogInformation("Starting tart VM {VM}", vmName);
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "tart",
                Arguments = $"run {vmName} --no-graphics {dirArgs}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.Start();

        return new RunnerInstanceInfo
        {
            InstanceHandle = vmName,
            RunnerName = request.RunnerName
        };
    }

    public async Task StopRunnerAsync(string instanceHandle, CancellationToken ct = default)
    {
        _logger.LogInformation("Stopping tart VM {VM}", instanceHandle);

        // Gracefully stop the VM
        await RunCommandAsync("tart", $"stop {instanceHandle}", ct);

        // Delete the cloned VM
        await RunCommandAsync("tart", $"delete {instanceHandle}", ct);

        // Clean up config directory
        var configDir = Path.Combine(Path.GetTempPath(), "runnerrunner", instanceHandle);
        if (Directory.Exists(configDir))
            Directory.Delete(configDir, recursive: true);
    }

    public async Task<RunnerHealthStatus> GetHealthAsync(string instanceHandle, CancellationToken ct = default)
    {
        var result = await RunCommandAsync("tart", $"ip {instanceHandle} --resolver=arp", ct);

        return new RunnerHealthStatus
        {
            IsRunning = result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Output),
            Status = result.ExitCode == 0 ? "running" : "stopped"
        };
    }

    private static async Task<(int ExitCode, string Output)> RunCommandAsync(
        string command, string arguments, CancellationToken ct)
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
        await process.WaitForExitAsync(ct);

        return (process.ExitCode, output.Trim());
    }
}
