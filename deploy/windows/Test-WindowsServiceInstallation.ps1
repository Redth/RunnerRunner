$ErrorActionPreference = 'Stop'
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Install-HostWorker.ps1'),[ref]$tokens,[ref]$errors)
if ($errors.Count) { throw 'Installer parse failure' }
$helper=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-RunnerRunnerServiceInstallAction'},$false)
if (!$helper) { throw 'Installer decision helper missing' }
. ([scriptblock]::Create($helper.Extent.Text))
$service = [pscustomobject]@{ PathName='"C:\Program Files\RunnerRunner\RunnerRunner.HostWorker.exe"'; StartName='LocalSystem'; State='Running' }
$settings = [pscustomobject]@{ HostWorker=[pscustomobject]@{ HostId='host-1'; ServerUrl='https://control.example.test'; DataRoot='C:\ProgramData\RunnerRunner' } }
$args = @{ BinaryPath=$service.PathName; Account='LocalSystem'; PreserveConfig=$true; Settings=$settings; HostId='host-1'; ServerUrl=$settings.HostWorker.ServerUrl; DataPath=$settings.HostWorker.DataRoot }
function Rejects([scriptblock]$action) { $failed=$false; try { & $action | Out-Null } catch { $failed=$true }; if (!$failed) { throw 'Expected refusal' } }
if ((Get-RunnerRunnerServiceInstallAction -Service $null @args) -ne 'Create') { throw 'Fresh installation refused' }
foreach ($state in @('Running','Start Pending')) {
    $service.State=$state
    if ((Get-RunnerRunnerServiceInstallAction -Service $service @args) -ne 'Preserve') { throw 'Healthy installation not preserved' }
}
$service.State='Stopped'; Rejects { Get-RunnerRunnerServiceInstallAction -Service $service @args }; $service.State='Running'
foreach ($field in @('BinaryPath','Account','HostId','ServerUrl','DataPath')) {
    $before=$args[$field];$args[$field]='foreign';Rejects { Get-RunnerRunnerServiceInstallAction -Service $service @args };$args[$field]=$before
}
$args.PreserveConfig=$false;Rejects { Get-RunnerRunnerServiceInstallAction -Service $service @args };$args.PreserveConfig=$true
$args.Settings=$null;Rejects { Get-RunnerRunnerServiceInstallAction -Service $service @args }
Write-Output 'PASS: fresh install, running/starting retry, stopped/foreign/missing configuration refusal; no services changed.'
