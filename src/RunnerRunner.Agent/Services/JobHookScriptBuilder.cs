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
        # Installed by RunnerRunner. Registers secret values with the provider's
        # log masker, then renders a "RunnerRunner environment" banner. Values
        # come from env vars seeded by the RunnerRunner server.
        #
        # This hook's exit status is the job's fate: a non-zero exit fails the
        # job before the first step. Every branch below is therefore written to
        # succeed, and the script ends with an explicit "exit 0".

        # Mask first, so nothing printed afterwards can leak a secret.
        # RR_SECRET_KEYS carries variable NAMES only; values are read from the
        # environment with printenv. "::add-mask::" is line-oriented, so a
        # multi-line value (a PEM private key, for example) must be registered
        # one line at a time or masking silently fails for all of it.
        if [ -n "${RR_SECRET_KEYS:-}" ]; then
            echo "$RR_SECRET_KEYS" | tr ',' '\n' | while IFS= read -r rr_key; do
                if [ -n "$rr_key" ]; then
                    rr_value=$(printenv "$rr_key" 2>/dev/null)
                    if [ -n "$rr_value" ]; then
                        printf '%s\n' "$rr_value" | while IFS= read -r rr_line; do
                            if [ ${#rr_line} -ge 4 ]; then
                                echo "::add-mask::$rr_line"
                            fi
                        done
                    fi
                fi
            done
        fi

        # Banner stays opt-in. These values must match IsHookRequested: the hook
        # is now installed for masking alone, so a profile that explicitly set
        # the sentinel to "0" must not suddenly start printing a banner.
        case "${RR_HOOK_JOB_STARTED_REQUESTED:-}" in
            ""|0|[Ff][Aa][Ll][Ss][Ee]) exit 0 ;;
        esac

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
        exit 0
        """;

    public static string BuildPowerShellScript() =>
        """
        # Installed by RunnerRunner. Registers secret values with the provider's
        # log masker, then renders a "RunnerRunner environment" banner. Values
        # come from env vars seeded by the RunnerRunner server.
        #
        # Deliberately avoids .NET methods that carry a ReadOnlySpan overload:
        # PowerShell 7.2 running on .NET 7/8 throws while *retrieving* such a
        # member, and a throw here would fail the job before the first step.
        # The -split operator and Environment::GetEnvironmentVariable are safe.

        # Mask first, so nothing printed afterwards can leak a secret.
        # "::add-mask::" is line-oriented, so multi-line values must be
        # registered one line at a time or masking silently fails.
        if ($env:RR_SECRET_KEYS) {
            foreach ($rrKey in ($env:RR_SECRET_KEYS -split ',')) {
                $rrKey = $rrKey.Trim()
                if (-not $rrKey) { continue }
                $rrValue = [Environment]::GetEnvironmentVariable($rrKey)
                if (-not $rrValue) { continue }
                foreach ($rrLine in ($rrValue -split "`r?`n")) {
                    if ($rrLine.Trim().Length -ge 4) { Write-Host "::add-mask::$rrLine" }
                }
            }
        }

        # Banner stays opt-in. These values must match IsHookRequested: the hook
        # is now installed for masking alone, so a profile that explicitly set
        # the sentinel to "0" must not suddenly start printing a banner.
        $rrBanner = $env:RR_HOOK_JOB_STARTED_REQUESTED
        if (-not $rrBanner -or $rrBanner -eq '0' -or $rrBanner -eq 'false') { exit 0 }

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
        exit 0
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

    /// <summary>
    /// True if the hook must be installed at all. Masking is not optional: a
    /// profile that carries secrets needs the hook even when it never opted
    /// into the banner, so backends gate installation on this rather than on
    /// <see cref="IsHookRequested"/> alone. The script itself still renders the
    /// banner only when the opt-in sentinel is present.
    /// </summary>
    public static bool IsHookNeeded(IReadOnlyDictionary<string, string> envVars) =>
        IsHookRequested(envVars) || Core.SecretEnvironment.HasSecrets(envVars);
}
