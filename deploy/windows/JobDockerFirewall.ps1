function Get-RunnerRunnerJobDockerFirewallPlan {
    param($Options, [string]$ExecutablePath, [string]$ServiceName)
    if (-not $Options.Enabled) { return $null }
    if ($ServiceName -notmatch '^[A-Za-z0-9_-]+$' -or
        $ExecutablePath -notmatch '^[A-Za-z]:\\' -or
        $ExecutablePath -notmatch '\\RunnerRunner\.HostWorker\.exe$' -or
        -not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) {
        throw 'Job Docker firewall requires the installed worker executable and service identity.'
    }
    $parts = $Options.Subnet.Split('/')
    $gateway = $null; $network = $null; $prefix = 0
    if ($parts.Count -ne 2 -or -not [int]::TryParse($parts[1], [ref]$prefix) -or
        $prefix -lt 16 -or $prefix -gt 28 -or
        -not [Net.IPAddress]::TryParse($Options.Gateway, [ref]$gateway) -or
        -not [Net.IPAddress]::TryParse($parts[0], [ref]$network) -or
        $gateway.AddressFamily -ne 'InterNetwork' -or $network.AddressFamily -ne 'InterNetwork') {
        throw 'Job Docker firewall requires a bounded private IPv4 subnet.'
    }
    $g = $gateway.GetAddressBytes(); $n = $network.GetAddressBytes()
    if (-not ($g[0] -eq 10 -or ($g[0] -eq 172 -and $g[1] -ge 16 -and $g[1] -le 31) -or
        ($g[0] -eq 192 -and $g[1] -eq 168))) { throw 'Gateway must be private IPv4.' }
    for ($i = 0; $i -lt 4; $i++) {
        $bits = [Math]::Max(0, [Math]::Min(8, $prefix - 8 * $i))
        $mask = 256 - [int][Math]::Pow(2, 8 - $bits)
        if (($g[$i] -band $mask) -ne $n[$i]) { throw 'Gateway and canonical subnet differ.' }
    }
    $addresses = @(Get-NetIPAddress -AddressFamily IPv4 -IPAddress $gateway.ToString() -ErrorAction Stop)
    if ($addresses.Count -ne 1 -or $addresses[0].PrefixLength -ne $prefix) { throw 'Gateway interface differs.' }
    $adapter = Get-NetAdapter -InterfaceIndex $addresses[0].InterfaceIndex -ErrorAction Stop
    if ($adapter.HardwareInterface -or $adapter.InterfaceDescription -notlike 'Hyper-V Virtual Ethernet Adapter*') {
        throw 'Job Docker firewall requires the internal Hyper-V adapter.'
    }
    # Container NAT is managed by HNS and need not appear in Get-VMSwitch.
    $networks = @(Get-HnsNetwork | Where-Object {
        $_.Type -eq 'nat' -and ('vEthernet (' + $_.Name + ')') -eq $addresses[0].InterfaceAlias -and
        @($_.Subnets).Count -eq 1 -and $_.Subnets[0].GatewayAddress -eq $gateway.ToString() -and
        $_.Subnets[0].AddressPrefix -eq ($network.ToString() + '/' + $prefix)
    })
    if ($networks.Count -ne 1) { throw 'Exact private container NAT network not found.' }
    return @{
        Name = 'RunnerRunner-JobDocker-' + $ServiceName
        DisplayName = 'RunnerRunner private job Docker gateway'
        Group = 'RunnerRunner.JobDocker.v1'
        Description = 'Private Windows job subnet to HostWorker TLS passthrough only; Linux mTLS remains required.'
        Direction = 'Inbound'; Action = 'Allow'; Enabled = 'True'; Profile = 'Any'
        Protocol = 'TCP'; LocalPort = 2376; RemotePort = 'Any'
        LocalAddress = $gateway.ToString(); RemoteAddress = $network.ToString() + '/' + $prefix
        InterfaceAlias = $addresses[0].InterfaceAlias; Program = $ExecutablePath
        EdgeTraversalPolicy = 'Block'; PolicyStore = 'PersistentStore'
    }
}

function Assert-RunnerRunnerJobDockerFirewallRule {
    param($Rule, $Plan)
    $address = $Rule | Get-NetFirewallAddressFilter
    $port = $Rule | Get-NetFirewallPortFilter
    $app = $Rule | Get-NetFirewallApplicationFilter
    $interface = $Rule | Get-NetFirewallInterfaceFilter
    # NetSecurity returns a dotted subnet mask even when creation used CIDR.
    $subnet = $Plan.RemoteAddress.Split('/')
    $mask = (0..3 | ForEach-Object {
        $bits = [Math]::Max(0, [Math]::Min(8, [int]$subnet[1] - 8 * $_))
        256 - [int][Math]::Pow(2, 8 - $bits)
    }) -join '.'
    $remote = @($address.RemoteAddress) -join ','
    if ($Rule.Group -ne $Plan.Group -or $Rule.Direction -ne 'Inbound' -or $Rule.Action -ne 'Allow' -or
        $Rule.Enabled -ne 'True' -or $Rule.Profile -ne 'Any' -or $Rule.EdgeTraversalPolicy -ne 'Block' -or
        (@($address.LocalAddress) -join ',') -ne $Plan.LocalAddress -or
        $remote -notin @($Plan.RemoteAddress, ($subnet[0] + '/' + $mask)) -or
        (@($port.LocalPort) -join ',') -ne '2376' -or (@($port.RemotePort) -join ',') -ne 'Any' -or
        $port.Protocol -notin @('TCP', '6') -or $app.Program -ne $Plan.Program -or
        (@($interface.InterfaceAlias) -join ',') -ne $Plan.InterfaceAlias) {
        throw 'Existing job Docker firewall rule differs; a drained, explicit upgrade is required.'
    }
}

function Set-RunnerRunnerJobDockerFirewall {
    param($Options, [string]$ExecutablePath, [string]$ServiceName,
        [ValidateSet('Inspect', 'Install', 'Rebind')][string]$Mode = 'Inspect')
    $plan = Get-RunnerRunnerJobDockerFirewallPlan $Options $ExecutablePath $ServiceName
    if ($null -eq $plan) { return @{ enabled = $false; present = $false; created = $false } }
    $rules = @(Get-NetFirewallRule -PolicyStore PersistentStore -ErrorAction Stop | Where-Object Name -eq $plan.Name)
    if ($rules.Count -gt 1) { throw 'Duplicate job Docker firewall ownership.' }
    $created = $false; $rebound = $false
    if ($rules.Count -eq 1) {
        if ($Mode -eq 'Rebind') {
            $filter = $rules[0] | Get-NetFirewallInterfaceFilter
            $aliases = @($filter.InterfaceAlias)
            if ($aliases.Count -eq 1 -and $aliases[0] -ne $plan.InterfaceAlias) {
                # HNS may replace its NAT adapter during a host reboot. Windows
                # retains the now-unresolved interface GUID in the existing rule.
                # Rebind only that orphaned GUID, with every other restriction
                # intact, to the independently verified private HNS interface.
                $orphan = [guid]::Empty
                if (-not [guid]::TryParseExact([string]$aliases[0], 'D', [ref]$orphan)) {
                    throw 'Existing firewall interface is not an orphaned adapter GUID.'
                }
                $bound = @(Get-NetAdapter -IncludeHidden -ErrorAction Stop | Where-Object {
                    [guid]$_.InterfaceGuid -eq $orphan
                })
                if ($bound.Count) { throw 'Existing firewall interface still belongs to an adapter; no rebind.' }
                $prior = $plan.Clone(); $prior.InterfaceAlias = $aliases[0]
                Assert-RunnerRunnerJobDockerFirewallRule $rules[0] $prior
                $filter | Set-NetFirewallInterfaceFilter -InterfaceAlias $plan.InterfaceAlias -ErrorAction Stop | Out-Null
                $rebound = $true
                $rules = @(Get-NetFirewallRule -PolicyStore PersistentStore -ErrorAction Stop | Where-Object Name -eq $plan.Name)
                if ($rules.Count -ne 1) { throw 'Firewall ownership changed while rebinding.' }
            }
        }
        Assert-RunnerRunnerJobDockerFirewallRule $rules[0] $plan
    }
    elseif ($Mode -eq 'Install') {
        New-NetFirewallRule @plan -ErrorAction Stop | Out-Null
        $created = $true
    }
    elseif ($Mode -eq 'Rebind') { throw 'Startup requires the existing installed private firewall rule.' }
    else { return @{ enabled = $true; present = $false; created = $false; name = $plan.Name } }
    $effective = @(Get-NetFirewallRule -PolicyStore ActiveStore -ErrorAction Stop | Where-Object Name -eq $plan.Name)
    if ($effective.Count -ne 1) { throw 'Installed job Docker rule is not effective.' }
    Assert-RunnerRunnerJobDockerFirewallRule $effective[0] $plan
    return @{ enabled = $true; present = $true; created = $created; rebound = $rebound; name = $plan.Name;
        gateway = $plan.LocalAddress; subnet = $plan.RemoteAddress; interface_alias = $plan.InterfaceAlias;
        program = $plan.Program; port = 2376 }
}
