<#
.SYNOPSIS
    Uses the checked shared uninstaller instead of deleting every catalog-name match.
#>
[CmdletBinding()]
param(
    [string] $InstallerPath = (Join-Path $PSScriptRoot 'setup\bin\x64\Release\DesktopSplitterSetup.exe'),
    [switch] $RemoveCertificate
)
$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this script as administrator.' }
if (-not (Test-Path -LiteralPath $InstallerPath -PathType Leaf)) { throw "Verified uninstaller missing: $InstallerPath. Use the installed DesktopSplitterSetup.exe." }
$arguments = @('/uninstall', '/quiet')
# This switch is an explicit user request, never a default trust-store change.
if ($RemoveCertificate) { $arguments += '/removecert' }
$process = Start-Process -FilePath $InstallerPath -ArgumentList $arguments -Wait -PassThru
if ($process.ExitCode -eq 3010) { Write-Warning 'Removal requires a restart.'; exit 3010 }
if ($process.ExitCode -ne 0) { throw "Removal failed/incomplete: installer exit $($process.ExitCode). See ProgramData\DesktopSplitter\Logs; recovery files were retained where possible." }
Write-Host 'Verified DesktopSplitter removal completed.' -ForegroundColor Green
exit 0
