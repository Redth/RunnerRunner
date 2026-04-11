using System.Diagnostics;
using RunnerRunner.Core.Interfaces;
using RunnerRunner.Core.Models;

namespace RunnerRunner.Agent.Backends;

/// <summary>
/// Execution backend that runs runner agents directly as native processes on the host.
/// Useful for macOS bare-metal scenarios where Docker isn't suitable and
/// for running multiple parallel runners without VM overhead.
/// </summary>
public class NativeBackend : IRunnerBackend
{
    private readonly ILogger<NativeBackend> _logger;
    private readonly Dictionary<string, Process> _processes = new();

    public ExecutionBackend BackendType => ExecutionBackend.Native;

    public NativeBackend(ILogger<NativeBackend> logger)
    {
        _logger = logger;
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

    public async Task<RunnerInstanceInfo> StartRunnerAsync(RunnerStartRequest request, CancellationToken ct = default)
    {
        // Determine runner installation directory
        var agentVersion = request.RunnerAgentVersion ?? "latest";
        var runnerDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".runnerrunner", "agents", "github", agentVersion);

        if (!Directory.Exists(runnerDir))
        {
            throw new DirectoryNotFoundException(
                $"Runner agent not found at {runnerDir}. Install the runner agent first.");
        }

        // Configure the runner (config.sh / config.cmd)
        var isWindows = OperatingSystem.IsWindows();
        var configScript = Path.Combine(runnerDir, isWindows ? "config.cmd" : "config.sh");
        var runScript = Path.Combine(runnerDir, isWindows ? "run.cmd" : "run.sh");

        // Set up working directory for this instance
        var workDir = Path.Combine(runnerDir, "_work", request.RunnerName);
        Directory.CreateDirectory(workDir);

        // Configure the runner
        var configArgs = new List<string>
        {
            "--url", request.RunnerUrl ?? "",
            "--token", request.RegistrationToken ?? "",
            "--name", request.RunnerName,
            "--work", workDir,
            "--labels", string.Join(",", request.Labels),
            "--runnergroup", request.RunnerGroup,
            "--unattended",
            "--replace"
        };

        if (request.Ephemeral)
            configArgs.Add("--ephemeral");

        _logger.LogInformation("Configuring runner {RunnerName} at {Dir}", request.RunnerName, runnerDir);

        var configProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = isWindows ? "cmd.exe" : "/bin/bash",
                Arguments = isWindows
                    ? $"/c \"{configScript}\" {string.Join(" ", configArgs)}"
                    : $"\"{configScript}\" {string.Join(" ", configArgs)}",
                WorkingDirectory = runnerDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        foreach (var envVar in request.EnvironmentVariables)
            configProcess.StartInfo.EnvironmentVariables[envVar.Key] = envVar.Value;

        configProcess.Start();
        await configProcess.WaitForExitAsync(ct);

        if (configProcess.ExitCode != 0)
        {
            var error = await configProcess.StandardError.ReadToEndAsync(ct);
            throw new InvalidOperationException($"Runner configuration failed: {error}");
        }

        // Start the runner
        _logger.LogInformation("Starting runner process {RunnerName}", request.RunnerName);

        var runProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = isWindows ? "cmd.exe" : "/bin/bash",
                Arguments = isWindows ? $"/c \"{runScript}\"" : $"\"{runScript}\"",
                WorkingDirectory = runnerDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        foreach (var envVar in request.EnvironmentVariables)
            runProcess.StartInfo.EnvironmentVariables[envVar.Key] = envVar.Value;

        runProcess.Start();

        var instanceHandle = runProcess.Id.ToString();
        _processes[instanceHandle] = runProcess;

        return new RunnerInstanceInfo
        {
            InstanceHandle = instanceHandle,
            RunnerName = request.RunnerName
        };
    }

    public Task StopRunnerAsync(string instanceHandle, CancellationToken ct = default)
    {
        if (_processes.TryGetValue(instanceHandle, out var process))
        {
            _logger.LogInformation("Stopping native runner process {PID}", instanceHandle);

            if (!process.HasExited)
            {
                // Send graceful termination signal
                process.Kill(entireProcessTree: false);
                process.WaitForExit(30_000);

                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }

            _processes.Remove(instanceHandle);
            process.Dispose();
        }

        return Task.CompletedTask;
    }

    public Task<RunnerHealthStatus> GetHealthAsync(string instanceHandle, CancellationToken ct = default)
    {
        if (_processes.TryGetValue(instanceHandle, out var process))
        {
            return Task.FromResult(new RunnerHealthStatus
            {
                IsRunning = !process.HasExited,
                Status = process.HasExited ? $"exited:{process.ExitCode}" : "running"
            });
        }

        return Task.FromResult(new RunnerHealthStatus
        {
            IsRunning = false,
            Status = "not_found"
        });
    }
}
