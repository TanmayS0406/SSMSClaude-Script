<#
.SYNOPSIS
  Removes the "SSMS Claude Chat" extension from SQL Server Management Studio 21 / 22.

.PARAMETER SsmsPath
  Optional. Path to the SSMS "Common7\IDE" folder, or directly to its VSIXInstaller.exe.

.PARAMETER Quiet
  Uninstall without the confirmation dialog.
#>
[CmdletBinding()]
param(
    [string]$SsmsPath,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

# Must equal the <Identity Id="..."> in source.extension.vsixmanifest.
$ExtensionId = 'SsmsChatExtension.8CAA5A9A-15AF-420A-8646-04F08447673D'

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

    if (Get-Process -Name 'Ssms' -ErrorAction SilentlyContinue) {
        throw "SSMS is running. Please close it and run the uninstaller again."
    }

    Write-Host "SSMS installer : $installer"
    Write-Host "Removing       : $ExtensionId"

    $installerArgs = @()
    if ($Quiet) { $installerArgs += '/quiet' }
    $installerArgs += "/uninstall:$ExtensionId"

    $process = Start-Process -FilePath $installer -ArgumentList $installerArgs -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw "VSIXInstaller exited with code $($process.ExitCode). See the newest log in %TEMP% (VSIXInstaller_*.log) for details."
    }

    Write-Host ""
    Write-Host "Done. The extension is removed; reopen SSMS." -ForegroundColor Green
}
catch {
    Write-Host ""
    Write-Host "Uninstall failed: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
