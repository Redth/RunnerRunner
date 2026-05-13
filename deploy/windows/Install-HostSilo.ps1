param(
    [string]$DeployDir = "C:\Program Files\RunnerRunner",
    [string]$DataDir = "C:\ProgramData\RunnerRunner",
    [string]$ServiceName = "RunnerRunnerHostSilo",
    [string]$HostId = "",
    [string]$HostName = $env:COMPUTERNAME,
    [string]$DatabaseConnectionString = "",
    [string]$AdvertisedIPAddress = "",
    [string]$ServiceAccount = "LocalSystem",
    [string]$ServicePassword = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($DatabaseConnectionString)) {
    throw "DatabaseConnectionString is required for HostSilo trusted-network cluster mode."
}

$deployPath = $DeployDir.Replace('/', '\')
$dataPath = $DataDir.Replace('/', '\')
$logsPath = Join-Path $dataPath "logs"
$exePath = Join-Path $deployPath "RunnerRunner.HostSilo.exe"
$settingsPath = Join-Path $deployPath "appsettings.Production.json"

if ([string]::IsNullOrWhiteSpace($HostId)) {
    if ([string]::IsNullOrWhiteSpace($AdvertisedIPAddress)) {
        $HostId = $HostName
    }
    else {
        $HostId = "windows-host-$AdvertisedIPAddress"
    }
}

New-Item -ItemType Directory -Force -Path $deployPath | Out-Null
New-Item -ItemType Directory -Force -Path $logsPath | Out-Null

if (-not (Test-Path $exePath)) {
    throw "RunnerRunner.HostSilo.exe not found in $deployPath"
}

$settings = @{
    HostSilo = @{
        HostId = $HostId
        HostName = $HostName
        Platform = "Windows"
    }
    Database = @{
        ConnectionString = $DatabaseConnectionString
    }
    Orleans = @{
        AdvertisedIPAddress = $AdvertisedIPAddress
    }
}

$settings | ConvertTo-Json -Depth 8 | Set-Content -Path $settingsPath -Encoding UTF8

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
        -DisplayName "RunnerRunner HostSilo" `
        -Description "RunnerRunner host-local Orleans worker for Docker, Tart, and native runner execution." `
        -BinaryPathName $binaryPath `
        -StartupType Automatic `
        -ErrorAction Stop | Out-Null
}
else {
    $securePassword = ConvertTo-SecureString $ServicePassword -AsPlainText -Force
    $credential = [System.Management.Automation.PSCredential]::new($ServiceAccount, $securePassword)
    New-Service `
        -Name $ServiceName `
        -DisplayName "RunnerRunner HostSilo" `
        -Description "RunnerRunner host-local Orleans worker for Docker, Tart, and native runner execution." `
        -BinaryPathName $binaryPath `
        -StartupType Automatic `
        -Credential $credential `
        -ErrorAction Stop | Out-Null
}

sc.exe failure $ServiceName reset= 60 actions= restart/5000/restart/5000/restart/30000 | Out-Null
Start-Service -Name $ServiceName

Write-Host "RunnerRunner HostSilo installed as Windows Service '$ServiceName'."
Write-Host "Logs: $logsPath"
