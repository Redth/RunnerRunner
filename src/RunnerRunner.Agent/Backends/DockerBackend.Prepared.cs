using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Docker.DotNet.Models;
using RunnerRunner.Core.Interfaces;
using RunnerRunner.Core.Models;

namespace RunnerRunner.Agent.Backends;

public partial class DockerBackend
{
    private async Task PrepareJobDockerAsync(string handle, NativeRunnerOwnership ownership, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        var input = await _jobDocker!.CreatePrivateInputAsync(ownership, deadline.Token);
        try { await ExecutePrivateWindowsCheckAsync(handle, JobDockerPreparationScript, deadline.Token, input); }
        finally { CryptographicOperations.ZeroMemory(input); }
    }

    // This script is public; the leaf and its per-job private key arrive only on stdin.
    internal const string JobDockerPreparationScript = """
        $ErrorActionPreference = 'Stop'
        $line = [Console]::In.ReadLine()
        if (!$line -or $line.Length -gt 131072) { throw 'Invalid job Docker input' }
        $material = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($line)) | ConvertFrom-Json
        $root = 'C:\rr-docker-client'
        if (Test-Path -LiteralPath $root) { throw 'Job Docker material already exists' }
        New-Item -ItemType Directory -Path $root | Out-Null
        foreach ($pair in @(@('ca.pem',$material.ca),@('cert.pem',$material.cert),@('key.pem',$material.key))) {
            if (!$pair[1]) { throw 'Incomplete job Docker material' }
            $file = [IO.File]::Open((Join-Path $root $pair[0]),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
            try { $bytes = [Text.Encoding]::ASCII.GetBytes($pair[1]); $file.Write($bytes,0,$bytes.Length); $file.Flush($true) }
            finally { $file.Dispose() }
        }
        $material = $null; $line = $null; $bytes = $null
        $info = & docker.exe info --format '{{json .}}' 2>$null
        if ($LASTEXITCODE -ne 0) { throw 'Job Docker TLS/API readiness failed' }
        $info = $info | ConvertFrom-Json
        if ($info.OSType -cne 'linux' -or $info.ID -cne $env:RR_JOB_DOCKER_DAEMON_ID) { throw 'Job Docker identity differs' }
        exit 0
        """;

    internal static void ValidatePreparedRequest(RunnerStartRequest request, string platform)
    {
        if (platform != "windows" || request.Provider != RunnerProvider.GitHubActions ||
            !string.IsNullOrEmpty(request.JitConfig) || !string.IsNullOrEmpty(request.RegistrationToken))
            throw new InvalidOperationException("Prepared Windows startup must not contain registration credentials.");
        var checks = request.InitSteps.Where(s => s.Phase == InitStepPhase.PreRunner).ToArray();
        if (checks.Length == 0 || checks.Any(s => s.ContinueOnError || string.IsNullOrWhiteSpace(s.Script) ||
            s.TimeoutSeconds is < 1 or > 600))
            throw new InvalidOperationException("Prepared startup requires bounded, mandatory readiness checks.");
    }

    private static string PreparationPath(string intentId)
    {
        if (!Guid.TryParseExact(intentId, "N", out _))
            throw new InvalidOperationException("Invalid preparation ownership nonce.");
        return @"C:\rr-control\" + intentId;
    }

    internal static List<string> BuildPreparedWindowsEntrypoint(string intentId, List<ResolvedInitStep> steps)
    {
        var root = PreparationPath(intentId);
        var post = InitStepShellBuilder.BuildWindowsFragment(steps.Where(s => s.Phase == InitStepPhase.PostExit), "PostExit");
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $root = '{{root}}'
            if (Test-Path -LiteralPath $root) { throw 'Preparation directory already exists' }
            New-Item -ItemType Directory -Path $root | Out-Null
            $deadline = [DateTime]::UtcNow.AddMinutes(15)
            $activation = Join-Path $root 'activation.json'
            while (!(Test-Path -LiteralPath $activation)) {
                if ([DateTime]::UtcNow -gt $deadline) { exit 92 }
                Start-Sleep -Seconds 1
            }
            $config = Get-Content -LiteralPath $activation -Raw | ConvertFrom-Json
            Remove-Item -LiteralPath $activation
            if ($config.instanceId -cne $env:RR_INSTANCE_ID -or $config.intentId -cne '{{intentId}}' -or !$config.jitConfig) {
                throw 'Activation does not match this environment'
            }
            $run = @('C:\actions-runner\run.cmd','C:\runner\run.cmd') | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
            if (!$run) { exit 91 }
            Set-Location (Split-Path -Parent $run)
            & $run --jitconfig $config.jitConfig
            $rrExit = $LASTEXITCODE
            $config = $null
            {{post}}
            exit $rrExit
            """;
        return ["powershell.exe", "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(script))];
    }

    private async Task CheckPreparedWindowsAsync(string handle, RunnerStartRequest request, CancellationToken ct)
    {
        var checks = request.InitSteps.Where(s => s.Phase == InitStepPhase.PreRunner).ToArray();
        var script = """
            $ErrorActionPreference = 'Stop'
            $run = @('C:\actions-runner\run.cmd','C:\runner\run.cmd') | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
            if (!$run) { exit 91 }
            """ + "\n" + InitStepShellBuilder.BuildWindowsFragment(checks, "Readiness") + "\nexit 0\n";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Min(840, checks.Sum(s => s.TimeoutSeconds) + 30)));
        await ExecutePrivateWindowsCheckAsync(handle, script, deadline.Token);
    }

    private async Task ExecutePrivateWindowsCheckAsync(string handle, string script, CancellationToken ct, byte[]? input = null)
    {
        var exec = await _client.Exec.ExecCreateContainerAsync(handle, new ContainerExecCreateParameters
        {
            AttachStdout = true, AttachStderr = true, AttachStdin = input is not null,
            Cmd = ["powershell.exe", "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
                Convert.ToBase64String(Encoding.Unicode.GetBytes(script))]
        }, ct);
        using var output = await _client.Exec.StartAndAttachContainerExecAsync(exec.ID, false, ct);
        // Readiness steps can use private per-job material. Never include their output
        // or script/arguments in host logs, receipts or registration requests.
        var drain = output.CopyOutputToAsync(Stream.Null, Stream.Null, Stream.Null, ct);
        try
        {
            if (input is not null)
            {
                await output.WriteAsync(input, 0, input.Length, ct);
                output.CloseWrite();
            }
            await drain;
        }
        catch
        {
            output.Dispose();
            try { await drain; } catch { /* Preserve the original transport failure. */ }
            throw;
        }
        var result = await _client.Exec.InspectContainerExecAsync(exec.ID, ct);
        if (result.Running || result.ExitCode != 0)
            throw new InvalidOperationException("Container readiness/activation operation did not complete successfully.");
    }

    public async Task ActivateRunnerAsync(NativeRunnerOwnership ownership, string handle, string jitConfig, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(jitConfig) || jitConfig.Length > 1024 * 1024)
            throw new InvalidOperationException("Invalid activation payload.");
        var observed = await InspectOwnedAsync(ownership, handle, ct);
        if (observed.State != NativeRunnerState.Running || observed.Handle != handle || ownership.Platform != "windows")
            throw new InvalidOperationException("Prepared native environment is no longer running with its original identity.");
        // Running Hyper-V containers cannot use Docker's archive API. Feed the
        // credential through exec stdin over the host's existing private pipe.
        // Publish only after the complete payload is written; never place it in argv.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        await ExecutePrivateWindowsCheckAsync(handle, BuildActivationWriteScript(ownership.IntentId), deadline.Token,
            BuildActivationInput(ownership, jitConfig));
    }

    internal static string BuildActivationWriteScript(string intentId) => $$"""
            $ErrorActionPreference = 'Stop'
            $root = '{{PreparationPath(intentId)}}'
            $line = [Console]::In.ReadLine()
            if (!$line -or $line.Length -gt 4194304) { throw 'Invalid private activation length' }
            $bytes = [Convert]::FromBase64String($line)
            $pending = Join-Path $root 'activation.pending'
            $file = [IO.File]::Open($pending, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try { $file.Write($bytes, 0, $bytes.Length); $file.Flush($true) } finally { $file.Dispose() }
            $line = $null; $bytes = $null
            [IO.File]::Move($pending, (Join-Path $root 'activation.json'))
            """;

    internal static byte[] BuildActivationInput(NativeRunnerOwnership ownership, string jitConfig)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        { instanceId = ownership.InstanceId, intentId = ownership.IntentId, jitConfig });
        return Encoding.ASCII.GetBytes(Convert.ToBase64String(bytes) + "\n");
    }
}
