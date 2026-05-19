param(
    [string]$DeployDir = "C:\Program Files\RunnerRunner",
    [string]$DataDir = "C:\ProgramData\RunnerRunner",
    [string]$ServiceName = "RunnerRunnerHostWorker",
    [string]$HostId = "",
    [string]$HostName = $env:COMPUTERNAME,
    [string]$ServerUrl = "",
    [string]$EnrollmentToken = "",
    [string]$ServiceAccount = "LocalSystem",
    [string]$ServicePassword = "",
    [string]$HttpProxy = "",
    [string]$HttpsProxy = "",
    [string]$NoProxy = "",
    [switch]$PreserveConfig
)

$ErrorActionPreference = "Stop"

$deployPath = $DeployDir.Replace('/', '\')
$dataPath = $DataDir.Replace('/', '\')
$logsPath = Join-Path $dataPath "logs"
$exePath = Join-Path $deployPath "RunnerRunner.HostWorker.exe"
$settingsPath = Join-Path $deployPath "appsettings.Production.json"

if ([string]::IsNullOrWhiteSpace($HostId)) {
    $HostId = $HostName
}

New-Item -ItemType Directory -Force -Path $deployPath | Out-Null
New-Item -ItemType Directory -Force -Path $logsPath | Out-Null

if (-not (Test-Path $exePath)) {
    throw "RunnerRunner.HostWorker.exe not found in $deployPath"
}

if ($PreserveConfig) {
    if (-not (Test-Path $settingsPath)) {
        throw "PreserveConfig requires an existing $settingsPath"
    }
}
else {
    if ([string]::IsNullOrWhiteSpace($ServerUrl)) {
        throw "ServerUrl is required."
    }

    if ([string]::IsNullOrWhiteSpace($EnrollmentToken)) {
        throw "EnrollmentToken is required."
    }

    $settings = @{
        HostWorker = @{
            ServerUrl = $ServerUrl
            EnrollmentToken = $EnrollmentToken
            HostId = $HostId
            HostName = $HostName
            Platform = "Windows"
            DataRoot = $dataPath
            LogRoot = $logsPath
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($HttpProxy)) {
        $settings["HostWorker"]["HttpProxy"] = $HttpProxy
    }

    if (-not [string]::IsNullOrWhiteSpace($HttpsProxy)) {
        $settings["HostWorker"]["HttpsProxy"] = $HttpsProxy
    }

    if (-not [string]::IsNullOrWhiteSpace($NoProxy)) {
        $settings["HostWorker"]["NoProxy"] = $NoProxy
    }

    $settings | ConvertTo-Json -Depth 8 | Set-Content -Path $settingsPath -Encoding UTF8
}

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne "Stopped") {
        Stop-Service -Name $ServiceName -Force -ErrorAction Stop
        $service.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
    }
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

$binaryPath = "`"$exePath`""

if ($ServiceAccount -eq "LocalSystem") {
    New-Service `
        -Name $ServiceName `
        -DisplayName "RunnerRunner HostWorker" `
        -Description "RunnerRunner authenticated host worker for Docker, Tart, and native runner execution." `
        -BinaryPathName $binaryPath `
        -StartupType Automatic `
        -ErrorAction Stop | Out-Null
}
else {
    $securePassword = ConvertTo-SecureString $ServicePassword -AsPlainText -Force
    $credential = [System.Management.Automation.PSCredential]::new($ServiceAccount, $securePassword)
    New-Service `
        -Name $ServiceName `
        -DisplayName "RunnerRunner HostWorker" `
        -Description "RunnerRunner authenticated host worker for Docker, Tart, and native runner execution." `
        -BinaryPathName $binaryPath `
        -StartupType Automatic `
        -Credential $credential `
        -ErrorAction Stop | Out-Null
}

sc.exe failure $ServiceName reset= 60 actions= restart/5000/restart/5000/restart/30000 | Out-Null
Start-Service -Name $ServiceName

Write-Host "RunnerRunner HostWorker installed as Windows Service '$ServiceName'."
Write-Host "Logs: $logsPath"
