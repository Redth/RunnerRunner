param([Parameter(Mandatory)][string]$Gateway,[Parameter(Mandatory)][string]$Subnet,
    [Parameter(Mandatory)][int]$WorkerProcessId,[Parameter(Mandatory)][string]$ExecutablePath)
$ErrorActionPreference='Stop'
if([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -ne 'S-1-5-18'){
    throw 'Private gateway startup requires the installed SYSTEM worker.'
}
$services=@(Get-CimInstance Win32_Service -Filter "ProcessId=$WorkerProcessId" -ErrorAction Stop)
$process=Get-CimInstance Win32_Process -Filter "ProcessId=$WorkerProcessId" -ErrorAction Stop
if($WorkerProcessId -le 0 -or $services.Count -ne 1 -or $services[0].StartName -ne 'LocalSystem' -or
    $process.ExecutablePath -ine $ExecutablePath -or
    $services[0].PathName -notlike ('"'+$ExecutablePath+'" *')){
    throw 'Installed worker process/service binding differs.'
}
. (Join-Path $PSScriptRoot 'JobDockerFirewall.ps1')
$options=[pscustomobject]@{Enabled=$true;Gateway=$Gateway;Subnet=$Subnet}
$result=Set-RunnerRunnerJobDockerFirewall -Options $options -ExecutablePath $ExecutablePath -ServiceName $services[0].Name -Mode Rebind
if(-not $result.present){throw 'Private gateway firewall unavailable.'}
[Console]::Out.Write('RR_DOCKER_FIREWALL_READY')
