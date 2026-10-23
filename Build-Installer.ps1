<#
.SYNOPSIS
  Builds the extension and assembles a ready-to-run installer folder (and zip) under .\dist

.DESCRIPTION
  1. Finds MSBuild (Visual Studio 2022/2026 or Build Tools) with vswhere.
  2. Restores NuGet packages and builds the Release VSIX.
  3. Copies the .vsix plus Install.cmd / Uninstall.cmd / *.ps1 into .\dist\SsmsChatExtension-Installer
     and zips it, so you can copy it to any machine that has SSMS.

.PARAMETER Configuration
  Build configuration. Default: Release.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path

function Find-MSBuild {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) {
        $found = & $vswhere -latest -prerelease -products * -requires Microsoft.Component.MSBuild `
                            -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
        if ($found) { return $found }
    }
    $onPath = Get-Command msbuild.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    throw "MSBuild not found. Install Visual Studio 2022 (or newer) with the 'Visual Studio extension development' workload, or the Build Tools."
}

try {
    $msbuild  = Find-MSBuild
    $solution = Join-Path $Root 'SsmsChatExtension.sln'
    Write-Host "MSBuild : $msbuild"

    & $msbuild $solution /restore /t:Rebuild "/p:Configuration=$Configuration" /v:minimal /nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed (exit code $LASTEXITCODE)." }

    $vsix = Join-Path $Root "src\SsmsChatExtension\bin\$Configuration\SsmsChatExtension.vsix"
    if (-not (Test-Path -LiteralPath $vsix)) { throw "Build succeeded but the VSIX was not produced at: $vsix" }

    $dist   = Join-Path $Root 'dist'
    $bundle = Join-Path $dist 'SsmsChatExtension-Installer'
    if (Test-Path -LiteralPath $bundle) { Remove-Item -LiteralPath $bundle -Recurse -Force }
    New-Item -ItemType Directory -Path $bundle -Force | Out-Null

    Copy-Item -LiteralPath $vsix -Destination $bundle
    Copy-Item -Path (Join-Path $Root 'installer\*') -Destination $bundle -Recurse

    $zip = Join-Path $dist 'SsmsChatExtension-Installer.zip'
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    Compress-Archive -Path (Join-Path $bundle '*') -DestinationPath $zip

    Write-Host ""
    Write-Host "Installer folder : $bundle" -ForegroundColor Green
    Write-Host "Installer zip    : $zip" -ForegroundColor Green
    Write-Host "Next: close SSMS, then run Install.cmd from the installer folder."
}
catch {
    Write-Host ""
    Write-Host "Build failed: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
