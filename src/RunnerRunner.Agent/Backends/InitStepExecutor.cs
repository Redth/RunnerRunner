using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using RunnerRunner.Core.Models;

namespace RunnerRunner.Agent.Backends;

/// <summary>
/// Runs <see cref="ResolvedInitStep"/> steps locally as child processes. Used by the
/// <see cref="NativeBackend"/> directly; the Docker and Tart backends build equivalent
/// shell fragments instead of invoking this, but share the shell-resolution and
/// quoting helpers exposed here.
/// </summary>
public class InitStepExecutor
{
    private readonly ILogger _logger;

    public InitStepExecutor(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Runs every step in <paramref name="steps"/> whose phase matches <paramref name="phase"/>
    /// sequentially. Honors per-step timeout and ContinueOnError.
    /// Throws <see cref="InitStepFailedException"/> when a non-ContinueOnError step fails,
    /// so callers can abort the provisioning flow.
    /// </summary>
    /// <param name="logFile">Optional log file to also append step output to.</param>
    public async Task RunAsync(
        IEnumerable<ResolvedInitStep> steps,
        InitStepPhase phase,
        string defaultWorkingDir,
        IReadOnlyDictionary<string, string> baseEnv,
        string? logFile,
        CancellationToken ct)
    {
        foreach (var step in steps.Where(s => s.Phase == phase))
        {
            await RunOneAsync(step, defaultWorkingDir, baseEnv, logFile, ct);
        }
    }

    private async Task RunOneAsync(
        ResolvedInitStep step,
        string defaultWorkingDir,
        IReadOnlyDictionary<string, string> baseEnv,
        string? logFile,
        CancellationToken ct)
    {
        var workDir = string.IsNullOrWhiteSpace(step.WorkingDirectory)
            ? defaultWorkingDir
            : step.WorkingDirectory!;
        try { Directory.CreateDirectory(workDir); } catch { /* best effort */ }

        var (fileName, arguments, scriptFile) = BuildProcessInvocation(step);

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = Directory.Exists(workDir) ? workDir : Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        // Base env first (runner env), then step overrides. The step env already
        // includes the base env because the server composed it that way, but
        // ensure nothing the agent added on top is lost.
        foreach (var kv in baseEnv)
            psi.Environment[kv.Key] = kv.Value;
        foreach (var kv in step.EnvironmentVariables)
            psi.Environment[kv.Key] = kv.Value;

        var prefix = $"[init:{step.Name}]";
        WriteLine(logFile, $"{prefix} starting (shell={step.Shell}, phase={step.Phase}, timeout={step.TimeoutSeconds}s, cwd={psi.WorkingDirectory})");
        _logger.LogInformation("{Prefix} starting (shell={Shell}, phase={Phase})", prefix, step.Shell, step.Phase);

        var sw = Stopwatch.StartNew();
        int exitCode;
        try
        {
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException($"Failed to start init step '{step.Name}'");

            var stdoutTask = StreamOutputAsync(process.StandardOutput, logFile, prefix, ct);
            var stderrTask = StreamOutputAsync(process.StandardError, logFile, prefix, ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, step.TimeoutSeconds)));

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                WriteLine(logFile, $"{prefix} timed out after {step.TimeoutSeconds}s — killed");
                if (step.ContinueOnError)
                {
                    _logger.LogWarning("{Prefix} timed out but ContinueOnError is set", prefix);
                    return;
                }
                throw new InitStepFailedException(step.Name, -1, $"Step timed out after {step.TimeoutSeconds}s");
            }

            await Task.WhenAll(stdoutTask, stderrTask);
            exitCode = process.ExitCode;
        }
        finally
        {
            if (scriptFile != null)
            {
                try { File.Delete(scriptFile); } catch { }
            }
        }

        sw.Stop();
        WriteLine(logFile, $"{prefix} exited rc={exitCode} in {sw.Elapsed.TotalSeconds:0.0}s");

        if (exitCode != 0)
        {
            _logger.LogWarning("{Prefix} failed with exit code {Code}", prefix, exitCode);
            if (!step.ContinueOnError)
                throw new InitStepFailedException(step.Name, exitCode, $"Init step '{step.Name}' failed with exit code {exitCode}");
        }
        else
        {
            _logger.LogInformation("{Prefix} completed rc=0 in {Elapsed:0.0}s", prefix, sw.Elapsed.TotalSeconds);
        }
    }

    private static (string FileName, string Arguments, string? ScriptFile) BuildProcessInvocation(ResolvedInitStep step)
    {
        // Write the script body to a temp file and invoke the chosen shell against it.
        // This avoids quoting/escaping headaches and keeps the user's script verbatim.
        var tmp = Path.Combine(Path.GetTempPath(), $"rr-init-{step.Id}-{Guid.NewGuid():N}");
        switch (step.Shell)
        {
            case InitStepShell.PowerShell:
            {
                var path = tmp + ".ps1";
                File.WriteAllText(path, step.Script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                var exe = ResolvePowerShell();
                return (exe, $"-NoLogo -NoProfile -ExecutionPolicy Bypass -File \"{path}\"", path);
            }
            case InitStepShell.Cmd:
            {
                var path = tmp + ".cmd";
                File.WriteAllText(path, step.Script);
                return ("cmd.exe", $"/c \"{path}\"", path);
            }
            case InitStepShell.Sh:
            {
                var path = tmp + ".sh";
                File.WriteAllText(path, step.Script);
                TryChmodExec(path);
                return ("/bin/sh", $"\"{path}\"", path);
            }
            case InitStepShell.Bash:
            default:
            {
                var path = tmp + ".sh";
                File.WriteAllText(path, step.Script);
                TryChmodExec(path);
                var bash = ResolveBash();
                return (bash, $"-lc \". '{path}'\"", path);
            }
        }
    }

    private static string ResolveBash()
    {
        foreach (var candidate in new[] { "/bin/bash", "/usr/bin/bash", "/usr/local/bin/bash", "/opt/homebrew/bin/bash" })
            if (File.Exists(candidate)) return candidate;
        return "bash";
    }

    private static string ResolvePowerShell()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "powershell.exe";
        foreach (var candidate in new[] { "/usr/local/bin/pwsh", "/opt/homebrew/bin/pwsh", "/usr/bin/pwsh" })
            if (File.Exists(candidate)) return candidate;
        return "pwsh";
    }

    private static void TryChmodExec(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "chmod",
                Arguments = $"+x \"{path}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            p?.WaitForExit(1000);
        }
        catch { }
    }

    private async Task StreamOutputAsync(StreamReader reader, string? logFile, string prefix, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null) break;
                var formatted = $"{prefix} {line}";
                _logger.LogInformation("{Line}", formatted);
                WriteLine(logFile, formatted);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error streaming output for {Prefix}", prefix);
        }
    }

    private static void WriteLine(string? logFile, string line)
    {
        if (string.IsNullOrEmpty(logFile))
            return;
        try
        {
            File.AppendAllText(logFile, line + Environment.NewLine);
        }
        catch { }
    }
}

/// <summary>Thrown when an init step exits non-zero and ContinueOnError is false.</summary>
public class InitStepFailedException : Exception
{
    public string StepName { get; }
    public int ExitCode { get; }

    public InitStepFailedException(string stepName, int exitCode, string message) : base(message)
    {
        StepName = stepName;
        ExitCode = exitCode;
    }
}
