<#
.SYNOPSIS
    Read-only DesktopSplitter PnP evidence collection. No installation, cleanup or policy changes.
#>
[CmdletBinding()]
param([string] $OutputDirectory = (Join-Path $env:TEMP ('DesktopSplitter-diagnostics-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))))
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$devices = @()
foreach ($device in Get-PnpDevice -Class Display) {
    try { $ids = (Get-PnpDeviceProperty -InstanceId $device.InstanceId -KeyName 'DEVPKEY_Device_HardwareIds').Data }
    catch { continue }
    if ($ids -notcontains 'Root\DesktopSplitterVdd') { continue }
    $evidence = [ordered]@{ InstanceId = $device.InstanceId; Status = $device.Status; FriendlyName = $device.FriendlyName }
    foreach ($key in @('DEVPKEY_Device_ProblemCode', 'DEVPKEY_Device_ProblemStatus', 'DEVPKEY_Device_DriverInfPath', 'DEVPKEY_Device_DriverVersion', 'DEVPKEY_Device_Service', 'DEVPKEY_Device_IsPresent')) {
        try { $evidence[$key] = (Get-PnpDeviceProperty -InstanceId $device.InstanceId -KeyName $key).Data }
        catch { $evidence[$key] = 'Unavailable: ' + $_.Exception.Message }
    }
    $devices += [pscustomobject]$evidence
}
$processes = @(Get-Process DesktopSplitter, dscomp -ErrorAction SilentlyContinue | Select-Object Name, Id, SessionId, Path)
# No command lines: they could contain unrelated sensitive arguments.
[ordered]@{
    CollectedAt = (Get-Date).ToString('o')
    Windows = [Environment]::OSVersion.VersionString
    Devices = $devices
    Processes = $processes
    Cause = 'The cause has not been established; this is evidence, not a hardware diagnosis.'
} | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $OutputDirectory 'device-evidence.json') -Encoding utf8
$setupLog = Join-Path $env:windir 'INF\setupapi.dev.log'
if (Test-Path -LiteralPath $setupLog) {
    Select-String -LiteralPath $setupLog -Pattern 'DesktopSplitterVdd' -Context 15,30 |
        Select-Object -Last 50 | Out-String -Width 4096 |
        Set-Content (Join-Path $OutputDirectory 'setupapi-desktopsplitter-excerpt.txt') -Encoding utf8
}
Write-Host "Read-only evidence written to $OutputDirectory"
Write-Host 'Also retain the matching ProgramData\DesktopSplitter\Logs installation log. Inspect files before sharing.'
