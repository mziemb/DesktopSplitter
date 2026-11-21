<#
.SYNOPSIS
    Runs the verified DesktopSplitter installer; no direct pnputil/devcreate shortcut.
.DESCRIPTION
    Success means the shared native engine passed all mandatory checks. 3010 means
    restart and /verify-installed are still required. This script never changes
    signing policy or installs a certificate on its own.
#>
[CmdletBinding()]
param(
    [string] $InstallerPath = (Join-Path $PSScriptRoot 'setup\bin\x64\Release\DesktopSplitterSetup.exe'),
    [string] $InstallDir,
    [switch] $VerifyOnly,
    [string] $InfPath,
    [switch] $SkipTestSigningCheck
)
$ErrorActionPreference = 'Stop'
if ($InfPath) { throw '-InfPath cannot bypass post-install verification. Build a DesktopSplitterSetup.exe embedding that exact package and pass -InstallerPath.' }
if ($SkipTestSigningCheck) { Write-Warning '-SkipTestSigningCheck is obsolete; this script never changes signing policy.' }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this script as administrator.' }
if (-not (Test-Path -LiteralPath $InstallerPath -PathType Leaf)) { throw "Verified installer missing: $InstallerPath. Build the complete package first." }
$arguments = @()
if ($VerifyOnly) { $arguments += '/verify-installed' }
if ($InstallDir) {
    if ($VerifyOnly) { throw '-InstallDir is not used by read-only installed verification.' }
    if ($InstallDir.Contains('"')) { throw 'Invalid install directory.' }
    $arguments += @('/quiet', '/dir', ('"' + $InstallDir + '"'))
}
if ($arguments.Count) { $process = Start-Process -FilePath $InstallerPath -ArgumentList $arguments -Wait -PassThru }
else { $process = Start-Process -FilePath $InstallerPath -Wait -PassThru }
if ($process.ExitCode -eq 3010) {
    Write-Warning 'Restart required. Driver readiness is not certified. Reboot and rerun with -VerifyOnly.'
    exit 3010
}
if ($process.ExitCode -ne 0) { throw "Installer failed/incomplete (installer exit $($process.ExitCode), not a PnP problem code). See ProgramData\DesktopSplitter\Logs." }
Write-Host 'DesktopSplitter installation/runtime verification passed.' -ForegroundColor Green
exit 0
