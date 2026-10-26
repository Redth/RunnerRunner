param(
    [string]$DeployDir = "C:\Program Files\RunnerRunner",
    [string]$DataDir = "C:\ProgramData\RunnerRunner",
    [string]$ServiceName = "RunnerRunnerHostWorker",
    [string]$HostId = "",
    [string]$HostName = $env:COMPUTERNAME,
    [string]$ServerUrl = "",
    [string]$EnrollmentToken = "",
    [string]$EnrollmentTokenFile = "",
    [string]$ServiceAccount = "LocalSystem",
    [string]$ServicePassword = "",
    [string]$HttpProxy = "",
    [string]$HttpsProxy = "",
    [string]$NoProxy = "",
    [switch]$PreserveConfig
)

$ErrorActionPreference = "Stop"
function Get-RunnerRunnerServiceInstallAction {
    param($Service, [string]$BinaryPath, [string]$Account, [bool]$PreserveConfig,
        $Settings, [string]$HostId, [string]$ServerUrl, [string]$DataPath)
    if ($null -eq $Service) { return 'Create' }
    if (-not $PreserveConfig -or $Service.PathName -cne $BinaryPath -or
        $Service.StartName -ine $Account -or $null -eq $Settings) {
        throw 'Existing service differs; use a drained upgrade instead of reinstalling.'
    }
    if (($HostId -and $Settings.HostWorker.HostId -cne $HostId) -or
        ($ServerUrl -and $Settings.HostWorker.ServerUrl -cne $ServerUrl) -or
        $Settings.HostWorker.DataRoot -ine $DataPath) {
        throw 'Existing worker identity or data directory differs.'
    }
    if ($Service.State -notin @('Running', 'Start Pending')) {
        throw 'Existing service is not running; inspect its state before attempting recovery.'
    }
    return 'Preserve'
}

if ($ServiceName -notmatch "^[A-Za-z0-9_-]+$") { throw "Invalid service name." }

$deployPath = $DeployDir.Replace('/', '\')
$dataPath = $DataDir.Replace('/', '\')
$logsPath = Join-Path $dataPath "logs"
$exePath = Join-Path $deployPath "RunnerRunner.HostWorker.exe"
$settingsPath = Join-Path $deployPath "appsettings.Production.json"

$binaryPath = "`"$exePath`""
# Inspect before writing configuration or interrupting any existing process.
$existing = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'"
$current = $null
if ($existing -and (Test-Path -LiteralPath $settingsPath -PathType Leaf)) {
    $current = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
}
$action = Get-RunnerRunnerServiceInstallAction -Service $existing -BinaryPath $binaryPath `
    -Account $ServiceAccount -PreserveConfig ([bool]$PreserveConfig) -Settings $current `
    -HostId $HostId -ServerUrl $ServerUrl -DataPath $dataPath
if ($action -eq 'Preserve') {
    Write-Host "RunnerRunner HostWorker already installed and running; preserved."
    return
}

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

    if ($EnrollmentTokenFile) {
        if ($EnrollmentToken) { throw 'Supply only one enrollment token source.' }
        $EnrollmentToken = [IO.File]::ReadAllText($EnrollmentTokenFile).Trim()
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

    # Protect the directory before writing an enrollment token for a SYSTEM service.
    if ($ServiceAccount -eq 'LocalSystem') {
        & icacls.exe $deployPath /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Unable to protect the worker configuration directory.' }
    }
    $settings | ConvertTo-Json -Depth 8 | Set-Content -Path $settingsPath -Encoding UTF8
}

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
if ($LASTEXITCODE -ne 0) { throw "Unable to configure service recovery." }
Start-Service -Name $ServiceName

Write-Host "RunnerRunner HostWorker installed as Windows Service '$ServiceName'."
Write-Host "Logs: $logsPath"
