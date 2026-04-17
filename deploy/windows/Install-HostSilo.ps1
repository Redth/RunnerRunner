param(
    [string]$DeployDir = "C:\RunnerRunner",
    [ValidateSet("native", "docker")]
    [string]$Mode = "native",
    [string]$ContainerName = "runnerrunner-host-silo",
    [string]$ImageName = "runnerrunner-host-silo:windows"
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

Start-Process `
    -FilePath $exePath `
    -WorkingDirectory $deployPath `
    -WindowStyle Hidden `
    -RedirectStandardOutput $stdout `
    -RedirectStandardError $stderr | Out-Null

Write-Host "Windows HostSilo started in native mode."
