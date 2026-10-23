<#
.SYNOPSIS
  Installs the "SQL Chat for SSMS" extension (.vsix) into SQL Server Management Studio 21 / 22.

.DESCRIPTION
  SSMS 21+ has no "Manage Extensions" dialog and double-clicking a .vsix opens the wrong installer,
  so this script runs the VSIXInstaller.exe that ships with SSMS itself.

.PARAMETER SsmsPath
  Optional. Path to the SSMS "Common7\IDE" folder, or directly to its VSIXInstaller.exe,
  if SSMS is installed somewhere non-standard.

.PARAMETER VsixPath
  Optional. The .vsix to install. Defaults to the .vsix sitting next to this script.

.PARAMETER Quiet
  Install without the confirmation dialog.

.PARAMETER AllUsers
  Install for all users (requires an elevated PowerShell).
#>
[CmdletBinding()]
param(
    [string]$SsmsPath,
    [string]$VsixPath,
    [switch]$Quiet,
    [switch]$AllUsers
)

$ErrorActionPreference = 'Stop'
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

function Find-VsixInstaller {
    param([string]$Override)

    if ($Override) {
        if (Test-Path -LiteralPath $Override -PathType Leaf) { return (Resolve-Path -LiteralPath $Override).Path }
        $candidate = Join-Path $Override 'VSIXInstaller.exe'
        if (Test-Path -LiteralPath $candidate) { return (Resolve-Path -LiteralPath $candidate).Path }
        throw "VSIXInstaller.exe not found at '$Override'."
    }

    $roots = @($env:ProgramFiles, ${env:ProgramFiles(x86)}) | Where-Object { $_ }
    foreach ($version in 22, 21) {
        foreach ($root in $roots) {
            $candidate = Join-Path $root "Microsoft SQL Server Management Studio $version\Release\Common7\IDE\VSIXInstaller.exe"
            if (Test-Path -LiteralPath $candidate) { return $candidate }
        }
    }
    throw "Could not find SSMS 21 or 22. Re-run with -SsmsPath 'C:\path\to\...\Common7\IDE'."
}

try {
    $installer = Find-VsixInstaller -Override $SsmsPath

    if (-not $VsixPath) {
        $found = Get-ChildItem -LiteralPath $ScriptDir -Filter '*.vsix' | Select-Object -First 1
        if (-not $found) { throw "No .vsix found next to this script. Pass -VsixPath." }
        $VsixPath = $found.FullName
    }
    if (-not (Test-Path -LiteralPath $VsixPath)) { throw "VSIX not found: $VsixPath" }
    $VsixPath = (Resolve-Path -LiteralPath $VsixPath).Path

    if (Get-Process -Name 'Ssms' -ErrorAction SilentlyContinue) {
        throw "SSMS is running. Please close it and run the installer again."
    }

    Write-Host "SSMS installer : $installer"
    Write-Host "Extension      : $VsixPath"

    $installerArgs = @()
    if ($Quiet)    { $installerArgs += '/quiet' }
    if ($AllUsers) { $installerArgs += '/admin' }
    $installerArgs += ('"{0}"' -f $VsixPath)

    $process = Start-Process -FilePath $installer -ArgumentList $installerArgs -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw "VSIXInstaller exited with code $($process.ExitCode). See the newest log in %TEMP% (VSIXInstaller_*.log) for details."
    }

    Write-Host ""
    Write-Host "Done. Start SSMS and open View > SQL Chat." -ForegroundColor Green
}
catch {
    Write-Host ""
    Write-Host "Install failed: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
