param(
    [string]$DeployDir = "C:\RunnerRunner",
    [ValidateSet("native", "docker")]
    [string]$Mode = "native",
    [string]$ContainerName = "runnerrunner-host-silo",
    [string]$ImageName = "runnerrunner-host-silo:windows",
    [string]$ServiceUser = "Administrator",
    [string]$ServicePassword = ""
)

$ErrorActionPreference = "Stop"

$deployPath = $DeployDir.Replace('/', '\')
$logsPath = Join-Path $deployPath "logs"
$exePath = Join-Path $deployPath "RunnerRunner.HostSilo.exe"

New-Item -ItemType Directory -Force -Path $deployPath | Out-Null
New-Item -ItemType Directory -Force -Path $logsPath | Out-Null

# Stop only the HostSilo process started from this deploy directory.
Get-CimInstance Win32_Process -Filter "Name = 'RunnerRunner.HostSilo.exe'" |
    Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($deployPath, [System.StringComparison]::OrdinalIgnoreCase) } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

# Tear down any previously registered scheduled task so we redeploy cleanly.
Unregister-ScheduledTask -TaskName "RunnerRunnerHostSilo" -Confirm:$false -ErrorAction SilentlyContinue

if (Get-Command docker -ErrorAction SilentlyContinue) {
    & cmd.exe /c "docker rm -f $ContainerName 1>nul 2>nul" | Out-Null
}

if ($Mode -eq "docker") {
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
        throw "Docker is required for WINDOWS_MODE=docker."
    }

    Push-Location $deployPath
    try {
        docker build -t $ImageName -f "$deployPath\Dockerfile.windows" $deployPath
        docker run -d `
            --name $ContainerName `
            --restart unless-stopped `
            --isolation process `
            -p 11111:11111 `
            -p 30000:30000 `
            --mount "type=npipe,source=\\.\pipe\docker_engine,target=\\.\pipe\docker_engine" `
            $ImageName | Out-Null
    }
    finally {
        Pop-Location
    }

    Write-Host "Windows HostSilo started in Docker mode."
    exit 0
}

if (-not (Test-Path $exePath)) {
    throw "RunnerRunner.HostSilo.exe not found in $deployPath"
}

$stdout = Join-Path $logsPath "hostsilo.out.log"
$stderr = Join-Path $logsPath "hostsilo.err.log"
$wrapper = Join-Path $deployPath "Run-HostSilo.ps1"
$taskName = "RunnerRunnerHostSilo"

# Wrapper script: redirects stdout/stderr to logs, exits non-zero if the silo dies
# so the scheduled task's restart-on-failure policy kicks in.
@"
`$ErrorActionPreference = 'Continue'
Set-Location -Path '$deployPath'
`$out = '$stdout'
`$err = '$stderr'
# Rotate logs > 50MB
foreach (`$f in @(`$out, `$err)) {
    if ((Test-Path `$f) -and ((Get-Item `$f).Length -gt 50MB)) {
        Move-Item `$f "`$f.1" -Force
    }
}
`$p = Start-Process -FilePath '$exePath' -WorkingDirectory '$deployPath' ``
    -NoNewWindow -RedirectStandardOutput `$out -RedirectStandardError `$err -PassThru
Wait-Process -Id `$p.Id
exit `$p.ExitCode
"@ | Set-Content -Path $wrapper -Encoding UTF8

# Stop & remove any existing scheduled task with this name
Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue

if (-not $ServicePassword) {
    throw "ServicePassword is required to register the scheduled task as $ServiceUser."
}

# Register the task entirely through the Schedule.Service COM API, which is
# the only way to register a task that runs as a specific user with a stored
# password AND with RunLevel=Highest. The PowerShell cmdlet's parameter sets
# are mutually exclusive between -Principal (which gives RunLevel) and
# -User/-Password (which doesn't accept RunLevel), so we build the task XML
# definition with both and register via the COM API.

$svc = New-Object -ComObject "Schedule.Service"
$svc.Connect()
$folder = $svc.GetFolder("\")

$def = $svc.NewTask(0)
$def.RegistrationInfo.Description = "RunnerRunner Host Silo (auto-restart)"
$def.RegistrationInfo.Author = "RunnerRunner"

# Principal: run as $ServiceUser with full admin rights, password logon.
$def.Principal.UserId = $ServiceUser
$def.Principal.LogonType = 1     # TASK_LOGON_PASSWORD
$def.Principal.RunLevel  = 1     # TASK_RUNLEVEL_HIGHEST

# Settings (mirror the New-ScheduledTaskSettingsSet block above).
$s = $def.Settings
$s.AllowDemandStart = $true
$s.StartWhenAvailable = $true
$s.DisallowStartIfOnBatteries = $false
$s.StopIfGoingOnBatteries = $false
$s.RunOnlyIfNetworkAvailable = $false
$s.MultipleInstances = 2          # IgnoreNew
$s.ExecutionTimeLimit = "PT0S"    # unlimited
$s.RestartCount = 999
$s.RestartInterval = "PT1M"

# Trigger 1: at boot.
$tBoot = $def.Triggers.Create(8) # TASK_TRIGGER_BOOT
$tBoot.Id = "BootTrigger"
$tBoot.Enabled = $true

# Trigger 2: 5 seconds from now (one-shot kickoff).
$tOnce = $def.Triggers.Create(1) # TASK_TRIGGER_TIME
$tOnce.Id = "OnceTrigger"
$tOnce.StartBoundary = (Get-Date).AddSeconds(5).ToString("yyyy-MM-ddTHH:mm:ss")
$tOnce.Enabled = $true

# Action: run powershell.exe with the wrapper script.
$act = $def.Actions.Create(0) # TASK_ACTION_EXEC
$act.Path = "powershell.exe"
$act.Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$wrapper`""
$act.WorkingDirectory = $deployPath

$folder.RegisterTaskDefinition(
    $taskName,
    $def,
    6,                # TASK_CREATE_OR_UPDATE
    $ServiceUser,
    $ServicePassword,
    1,                # TASK_LOGON_PASSWORD
    $null
) | Out-Null

# Kick it off immediately rather than waiting for the 5-second trigger
Start-ScheduledTask -TaskName $taskName

Start-Sleep -Seconds 4
$task = Get-ScheduledTask -TaskName $taskName
$info = $task | Get-ScheduledTaskInfo
Write-Host "Windows HostSilo scheduled task '$taskName' registered. State=$($task.State) LastRunResult=0x$('{0:X}' -f $info.LastTaskResult)"
