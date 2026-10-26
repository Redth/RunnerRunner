$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'JobDockerFirewall.ps1')
# Exercise policy bounds and ownership without changing the test machine firewall.
$script:rules = @(); $script:creates = 0; $script:hardware = $false; $script:networkType = 'nat'
$script:rebinds = 0; $script:oldAdapterPresent = $false
function Test-Path { param($LiteralPath, $PathType) return $true }
function Get-NetIPAddress { param($AddressFamily, $IPAddress, $ErrorAction)
    [pscustomobject]@{ PrefixLength = 20; InterfaceIndex = 28; InterfaceAlias = 'vEthernet (nat)' }
}
function Get-NetAdapter { param($InterfaceIndex, [switch]$IncludeHidden, $ErrorAction)
    if ($IncludeHidden) {
        if ($script:oldAdapterPresent) { [pscustomobject]@{InterfaceGuid = 'a12e10c3-8fd8-49ae-8492-70c4698543df'} }
        return
    }
    [pscustomobject]@{ HardwareInterface = $script:hardware; InterfaceDescription = 'Hyper-V Virtual Ethernet Adapter' }
}
function Get-HnsNetwork { [pscustomobject]@{ Name = 'nat'; Type = $script:networkType;
    Subnets = @([pscustomobject]@{GatewayAddress = '10.90.16.1'; AddressPrefix = '10.90.16.0/20'}) } }
function Get-NetFirewallRule { param($PolicyStore, $ErrorAction) $script:rules }
function New-NetFirewallRule {
    param($Name,$DisplayName,$Group,$Description,$Direction,$Action,$Enabled,$Profile,$Protocol,
        $LocalPort,$RemotePort,$LocalAddress,$RemoteAddress,$InterfaceAlias,$Program,$EdgeTraversalPolicy,$PolicyStore,$ErrorAction)
    $script:creates++; $script:rules = @([pscustomobject]$PSBoundParameters)
}
function Get-NetFirewallAddressFilter { param([Parameter(ValueFromPipeline)]$InputObject) process { $InputObject } }
function Get-NetFirewallPortFilter { param([Parameter(ValueFromPipeline)]$InputObject) process { $InputObject } }
function Get-NetFirewallApplicationFilter { param([Parameter(ValueFromPipeline)]$InputObject) process { $InputObject } }
function Get-NetFirewallInterfaceFilter { param([Parameter(ValueFromPipeline)]$InputObject) process { $InputObject } }
function Set-NetFirewallInterfaceFilter { param([Parameter(ValueFromPipeline)]$InputObject,$InterfaceAlias)
    process { $script:rebinds++; $InputObject.InterfaceAlias = $InterfaceAlias }
}
function Rejects($Action) { $rejected = $false; try { & $Action | Out-Null } catch { $rejected = $true }; if (-not $rejected) { throw 'Expected unsafe policy rejection.' } }
$options = [pscustomobject]@{ Enabled = $true; Gateway = '10.90.16.1'; Subnet = '10.90.16.0/20' }
$exe = 'C:\ProgramData\RunnerRunner\releases\hostworker-v3\RunnerRunner.HostWorker.exe'
$argsForRule = @{ Options = $options; ExecutablePath = $exe; ServiceName = 'RunnerRunnerHostWorker' }
$r = Set-RunnerRunnerJobDockerFirewall @argsForRule
if ($r.present -or $script:creates) { throw 'Default inspection mutated policy.' }
$r = Set-RunnerRunnerJobDockerFirewall @argsForRule -Mode Install
if (-not $r.present -or -not $r.created -or $script:creates -ne 1) { throw 'Missing private rule was not installed once.' }
$r = Set-RunnerRunnerJobDockerFirewall @argsForRule -Mode Install
if ($r.created -or $script:creates -ne 1) { throw 'Matching retry duplicated rule.' }
$script:rules[0].RemoteAddress = '10.90.16.0/255.255.240.0'
$r = Set-RunnerRunnerJobDockerFirewall @argsForRule
if (-not $r.present -or $script:creates -ne 1) { throw 'Native dotted subnet mask was not recognized.' }
$script:rules[0].RemoteAddress = '10.90.16.0/255.255.0.0'
Rejects { Set-RunnerRunnerJobDockerFirewall @argsForRule -Mode Install }
$script:rules[0].RemoteAddress = 'Any'
Rejects { Set-RunnerRunnerJobDockerFirewall @argsForRule -Mode Install }
$script:rules[0].RemoteAddress = $options.Subnet
$script:rules[0].Program = 'C:\foreign.exe'
Rejects { Set-RunnerRunnerJobDockerFirewall @argsForRule -Mode Install }
$script:rules[0].Program = $exe
$script:rules[0].InterfaceAlias = 'a12e10c3-8fd8-49ae-8492-70c4698543df'
Rejects { Set-RunnerRunnerJobDockerFirewall @argsForRule }
Rejects { Set-RunnerRunnerJobDockerFirewall @argsForRule -Mode Install }
$script:rules[0].RemoteAddress = 'Any'
Rejects { Set-RunnerRunnerJobDockerFirewall @argsForRule -Mode Rebind }
if ($script:rebinds) { throw 'Broad rule was changed.' }
$script:rules[0].RemoteAddress = $options.Subnet
$script:oldAdapterPresent = $true
Rejects { Set-RunnerRunnerJobDockerFirewall @argsForRule -Mode Rebind }
if ($script:rebinds) { throw 'Live foreign adapter was rebound.' }
$script:oldAdapterPresent = $false
$r = Set-RunnerRunnerJobDockerFirewall @argsForRule -Mode Rebind
if (-not $r.rebound -or $script:rebinds -ne 1 -or $script:creates -ne 1) { throw 'Orphaned adapter was not rebound exactly once.' }
$r = Set-RunnerRunnerJobDockerFirewall @argsForRule -Mode Rebind
if ($r.rebound -or $script:rebinds -ne 1) { throw 'Healthy rebind retry changed policy.' }
$script:rules[0].InterfaceAlias = 'Any'
Rejects { Set-RunnerRunnerJobDockerFirewall @argsForRule -Mode Rebind }
$script:rules[0].InterfaceAlias = @('vEthernet (nat)', 'Ethernet')
Rejects { Set-RunnerRunnerJobDockerFirewall @argsForRule -Mode Rebind }
$script:rules = @()
Rejects { Set-RunnerRunnerJobDockerFirewall @argsForRule -Mode Rebind }
$options.Subnet = '0.0.0.0/0'; Rejects { Set-RunnerRunnerJobDockerFirewall @argsForRule -Mode Install }
$options.Subnet = '10.90.16.1/20'; Rejects { Set-RunnerRunnerJobDockerFirewall @argsForRule -Mode Install }
$options.Subnet = '10.90.16.0/20'; $script:hardware = $true
Rejects { Set-RunnerRunnerJobDockerFirewall @argsForRule -Mode Install }
$script:hardware = $false; $script:networkType = 'transparent'
Rejects { Set-RunnerRunnerJobDockerFirewall @argsForRule -Mode Install }
$script:networkType = 'nat'; $options.Enabled = $false
$r = Set-RunnerRunnerJobDockerFirewall @argsForRule -Mode Install
if ($r.enabled -or $script:creates -ne 1) { throw 'Disabled gateway altered policy.' }
'PASS: inspection, bounded rule, idempotence, orphan-only adapter rebind, live/foreign/broad/multiple rule rejection, subnet and internal-interface guards.'
