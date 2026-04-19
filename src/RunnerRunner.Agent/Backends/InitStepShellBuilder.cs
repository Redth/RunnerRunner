using System.Text;
using RunnerRunner.Core.Models;

namespace RunnerRunner.Agent.Backends;

/// <summary>
/// Builds inline shell-script fragments for <see cref="ResolvedInitStep"/>s that run
/// inside a Docker container or a Tart VM (i.e. not as local child processes of the
/// agent). These are composed into the backend's provisioning wrapper scripts.
/// </summary>
internal static class InitStepShellBuilder
{
    /// <summary>
    /// Emit a Linux shell fragment that executes the given steps sequentially (in the
    /// order provided). The fragment expects to be embedded in a POSIX shell context.
    /// Each step runs in its own subshell via the selected interpreter (bash/sh/pwsh)
    /// and respects timeout + continue-on-error.
    /// </summary>
    public static string BuildLinuxFragment(IEnumerable<ResolvedInitStep> steps, string phaseLabel)
    {
        var sb = new StringBuilder();
        foreach (var step in steps)
        {
            var heredocTag = $"RR_STEP_{Guid.NewGuid():N}";
            var scriptPath = $"/tmp/rr-init-{SanitizeForShell(step.Id)}.script";

            sb.AppendLine($"echo '[RunnerRunner] [init:{EscSingle(step.Name)}] starting (phase={phaseLabel}, shell={step.Shell}, timeout={step.TimeoutSeconds}s)'");

            // Write script body via heredoc (quoted tag → no expansion inside body)
            sb.AppendLine($"cat > {scriptPath} <<'{heredocTag}'");
            sb.Append(step.Script);
            if (!step.Script.EndsWith('\n')) sb.AppendLine();
            sb.AppendLine(heredocTag);

            // Env exports (in a subshell so they don't leak between steps)
            sb.AppendLine("(");
            foreach (var kv in step.EnvironmentVariables)
                sb.AppendLine($"  export {kv.Key}='{EscSingle(kv.Value)}';");

            var interpreter = step.Shell switch
            {
                InitStepShell.Sh => $"/bin/sh '{scriptPath}'",
                InitStepShell.PowerShell => $"pwsh -NoLogo -NoProfile -File '{scriptPath}'",
                _ => $"bash '{scriptPath}'",
            };

            var cdCmd = string.IsNullOrWhiteSpace(step.WorkingDirectory)
                ? ""
                : $"cd '{EscSingle(step.WorkingDirectory!)}' && ";

            // `timeout` on Alpine/BusyBox works with numeric seconds; fall back to raw if unavailable
            sb.AppendLine($"  if command -v timeout >/dev/null 2>&1; then");
            sb.AppendLine($"    {cdCmd}timeout {step.TimeoutSeconds} {interpreter}");
            sb.AppendLine($"  else");
            sb.AppendLine($"    {cdCmd}{interpreter}");
            sb.AppendLine($"  fi");
            sb.AppendLine("); rc=$?;");
            sb.AppendLine($"rm -f {scriptPath} 2>/dev/null || true;");
            sb.AppendLine($"echo \"[RunnerRunner] [init:{EscDouble(step.Name)}] exited rc=$rc\";");
            if (!step.ContinueOnError)
                sb.AppendLine($"if [ $rc -ne 0 ]; then echo '[RunnerRunner] [init:{EscSingle(step.Name)}] aborting (ContinueOnError=false)'; exit $rc; fi");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Emit a PowerShell fragment that executes the given steps sequentially in a
    /// Windows container. Script bodies are written to temp files in $env:TEMP and
    /// executed via the selected interpreter. Respects timeout + continue-on-error.
    /// </summary>
    public static string BuildWindowsFragment(IEnumerable<ResolvedInitStep> steps, string phaseLabel)
    {
        var sb = new StringBuilder();
        foreach (var step in steps)
        {
            var scriptPath = $"$env:TEMP\\rr-init-{SanitizeForShell(step.Id)}";
            var ext = step.Shell switch
            {
                InitStepShell.Cmd => ".cmd",
                InitStepShell.PowerShell => ".ps1",
                _ => ".ps1",
            };
            sb.AppendLine($"Write-Host '[RunnerRunner] [init:{EscPsh(step.Name)}] starting (phase={phaseLabel}, shell={step.Shell}, timeout={step.TimeoutSeconds}s)'");
            sb.AppendLine($"$rrPath = \"{scriptPath}{ext}\"");
            sb.AppendLine($"Set-Content -Path $rrPath -Value @'");
            sb.Append(step.Script);
            if (!step.Script.EndsWith('\n')) sb.AppendLine();
            sb.AppendLine("'@");

            foreach (var kv in step.EnvironmentVariables)
                sb.AppendLine($"$env:{kv.Key} = '{EscPsh(kv.Value)}'");

            if (!string.IsNullOrWhiteSpace(step.WorkingDirectory))
                sb.AppendLine($"Push-Location -Path '{EscPsh(step.WorkingDirectory!)}'");

            var launch = step.Shell switch
            {
                InitStepShell.Cmd => "& cmd.exe /c $rrPath",
                _ => "& powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File $rrPath",
            };
            sb.AppendLine("$rrStarted = Get-Date");
            sb.AppendLine($"$rrJob = Start-Job -ScriptBlock {{ param($p, $launchKind) if ($launchKind -eq 'cmd') {{ & cmd.exe /c $p }} else {{ & powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File $p }} }} -ArgumentList $rrPath, '{(step.Shell == InitStepShell.Cmd ? "cmd" : "ps")}'");
            sb.AppendLine($"if (Wait-Job $rrJob -Timeout {step.TimeoutSeconds}) {{ Receive-Job $rrJob; $rrRc = if ($rrJob.State -eq 'Completed') {{ 0 }} else {{ 1 }} }} else {{ Stop-Job $rrJob; Write-Host '[RunnerRunner] [init:{EscPsh(step.Name)}] timed out'; $rrRc = 124 }}");
            sb.AppendLine("Remove-Job $rrJob -Force -ErrorAction SilentlyContinue");

            if (!string.IsNullOrWhiteSpace(step.WorkingDirectory))
                sb.AppendLine("Pop-Location");

            sb.AppendLine("Remove-Item -Path $rrPath -Force -ErrorAction SilentlyContinue");
            sb.AppendLine($"Write-Host \"[RunnerRunner] [init:{EscPsh(step.Name)}] exited rc=$rrRc\"");
            if (!step.ContinueOnError)
                sb.AppendLine($"if ($rrRc -ne 0) {{ Write-Host '[RunnerRunner] [init:{EscPsh(step.Name)}] aborting (ContinueOnError=false)'; exit $rrRc }}");

            _ = launch; // retained for readability; actual launch handled by Start-Job above
        }
        return sb.ToString();
    }

    private static string EscSingle(string value) => value.Replace("'", "'\"'\"'");
    private static string EscDouble(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    private static string EscPsh(string value) => value.Replace("'", "''");
    private static string SanitizeForShell(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
            sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
        return sb.ToString();
    }
}
