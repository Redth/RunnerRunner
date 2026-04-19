using System.Text;

namespace RunnerRunner.Agent.Services;

/// <summary>
/// Produces the <c>ACTIONS_RUNNER_HOOK_JOB_STARTED</c> hook script that
/// <c>actions/runner</c> (and Gitea's <c>act_runner</c>) run at job start.
/// Its stdout shows up as its own collapsible group right next to
/// "Set up job" in the GitHub Actions job log.
///
/// The scripts read <c>RR_META_*</c> env vars so a single static script
/// works for every deployment — the server just seeds the metadata and
/// the agent points the runner at the script.
/// </summary>
public static class JobHookScriptBuilder
{
    public const string BashFileName = "rr-job-started.sh";
    public const string PowerShellFileName = "rr-job-started.ps1";

    /// <summary>Hook env var consumed by actions/runner and act_runner.</summary>
    public const string HookEnvVarName = "ACTIONS_RUNNER_HOOK_JOB_STARTED";

    /// <summary>
    /// Server-set sentinel indicating the profile opted into the
    /// job-started banner. Agent backends check this and install the hook.
    /// </summary>
    public const string RequestedEnvVarName = "RR_HOOK_JOB_STARTED_REQUESTED";

    public static string BuildBashScript() =>
        """
        #!/bin/sh
        # Installed by RunnerRunner. Renders a "RunnerRunner environment"
        # banner at the start of every job. Values come from RR_META_*
        # env vars seeded by the RunnerRunner server.
        echo "::group::RunnerRunner environment"
        echo "Backend:         ${RR_META_BACKEND:-unknown}"
        echo "Host:            ${RR_META_HOST:-unknown}"
        echo "Profile:         ${RR_META_PROFILE:-unknown}"
        echo "Provider:        ${RR_META_PROVIDER:-unknown}"
        if [ -n "${RR_META_IMAGE:-}" ]; then
            if [ -n "${RR_META_TAG:-}" ]; then
                echo "Image:           ${RR_META_IMAGE}:${RR_META_TAG}"
            else
                echo "Image:           ${RR_META_IMAGE}"
            fi
        fi
        [ -n "${RR_META_IMAGE_DIGEST:-}" ] && echo "Digest:          ${RR_META_IMAGE_DIGEST}"
        [ -n "${RR_META_AGENT_VERSION:-}" ] && echo "Agent version:   ${RR_META_AGENT_VERSION}"
        [ -n "${RR_META_INSTANCE_ID:-}" ] && echo "Instance:        ${RR_META_INSTANCE_ID}"
        echo "::endgroup::"
        """;

    public static string BuildPowerShellScript() =>
        """
        # Installed by RunnerRunner. Renders a "RunnerRunner environment"
        # banner at the start of every job. Values come from RR_META_*
        # env vars seeded by the RunnerRunner server.
        Write-Host "::group::RunnerRunner environment"
        Write-Host ("Backend:         " + ($env:RR_META_BACKEND | ForEach-Object { if ($_) { $_ } else { 'unknown' } }))
        Write-Host ("Host:            " + ($env:RR_META_HOST | ForEach-Object { if ($_) { $_ } else { 'unknown' } }))
        Write-Host ("Profile:         " + ($env:RR_META_PROFILE | ForEach-Object { if ($_) { $_ } else { 'unknown' } }))
        Write-Host ("Provider:        " + ($env:RR_META_PROVIDER | ForEach-Object { if ($_) { $_ } else { 'unknown' } }))
        if ($env:RR_META_IMAGE) {
            if ($env:RR_META_TAG) {
                Write-Host ("Image:           {0}:{1}" -f $env:RR_META_IMAGE, $env:RR_META_TAG)
            } else {
                Write-Host ("Image:           {0}" -f $env:RR_META_IMAGE)
            }
        }
        if ($env:RR_META_IMAGE_DIGEST) { Write-Host ("Digest:          {0}" -f $env:RR_META_IMAGE_DIGEST) }
        if ($env:RR_META_AGENT_VERSION) { Write-Host ("Agent version:   {0}" -f $env:RR_META_AGENT_VERSION) }
        if ($env:RR_META_INSTANCE_ID) { Write-Host ("Instance:        {0}" -f $env:RR_META_INSTANCE_ID) }
        Write-Host "::endgroup::"
        """;

    /// <summary>
    /// Writes the bash hook script to <paramref name="directory"/>, returning
    /// its absolute path. Ensures it is marked executable on Unix.
    /// </summary>
    public static string WriteBashScript(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, BashFileName);
        File.WriteAllText(path, BuildBashScript(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute); }
            catch { /* best effort */ }
        }

        return path;
    }

    /// <summary>
    /// Writes the PowerShell hook script to <paramref name="directory"/>,
    /// returning its absolute path.
    /// </summary>
    public static string WritePowerShellScript(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, PowerShellFileName);
        File.WriteAllText(path, BuildPowerShellScript(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    /// <summary>
    /// True if the <paramref name="envVars"/> dictionary includes the
    /// opt-in sentinel from the server. Backends should call this before
    /// installing the hook.
    /// </summary>
    public static bool IsHookRequested(IReadOnlyDictionary<string, string> envVars) =>
        envVars.TryGetValue(RequestedEnvVarName, out var v)
        && !string.IsNullOrWhiteSpace(v)
        && v != "0"
        && !v.Equals("false", StringComparison.OrdinalIgnoreCase);
}
