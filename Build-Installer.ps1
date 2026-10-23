<#
.SYNOPSIS
  Builds the extension and its setup .exe under .\dist

.DESCRIPTION
  1. Optionally stamps -Version into the VSIX manifest, AssemblyInfo.cs and SsmsChatPackage.cs.
  2. Finds MSBuild (Visual Studio 2022/2026 or Build Tools) with vswhere.
  3. Restores NuGet packages and builds the Release VSIX.
  4. Compiles installer\SsmsClaudeChat.iss with Inno Setup into dist\SsmsClaudeChat-Setup-<version>.exe
     and copies the .vsix next to it.

.PARAMETER Version
  Optional. Version to build, e.g. 1.5.0. Defaults to the version already in source.extension.vsixmanifest.

.PARAMETER Configuration
  Build configuration. Default: Release.
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$Root     = Split-Path -Parent $MyInvocation.MyCommand.Path
$Project  = Join-Path $Root 'src\SsmsChatExtension'
$Manifest = Join-Path $Project 'source.extension.vsixmanifest'

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

function Find-Iscc {
    $onPath = Get-Command iscc.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    foreach ($root in @(${env:ProgramFiles(x86)}, $env:ProgramFiles, (Join-Path $env:LOCALAPPDATA 'Programs'))) {
        if (-not $root) { continue }
        $candidate = Join-Path $root 'Inno Setup 6\ISCC.exe'
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    throw "Inno Setup 6 not found. Install it (winget install JRSoftware.InnoSetup) and run this script again."
}

function Set-FileVersion {
    param([string]$Path, [string]$Pattern, [string]$Replacement)
    $text = [IO.File]::ReadAllText($Path)
    $updated = [regex]::Replace($text, $Pattern, $Replacement)
    if ($updated -ne $text) { [IO.File]::WriteAllText($Path, $updated) }
}

try {
    if ($Version) {
        if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.5.0 (got '$Version')." }
        Set-FileVersion $Manifest '(<Identity [^>]*Version=")[^"]+' "`${1}$Version"
        Set-FileVersion (Join-Path $Project 'Properties\AssemblyInfo.cs') '(Assembly(File)?Version\(")[^"]+' "`${1}$Version.0"
        Set-FileVersion (Join-Path $Project 'SsmsChatPackage.cs') '(InstalledProductRegistration\([^)]*")[\d.]+("\))' "`${1}$Version`${2}"
    }
    else {
        $Version = ([xml](Get-Content -LiteralPath $Manifest -Raw)).PackageManifest.Metadata.Identity.Version
    }
    Write-Host "Version : $Version"

    $msbuild = Find-MSBuild
    $iscc    = Find-Iscc
    Write-Host "MSBuild : $msbuild"
    Write-Host "ISCC    : $iscc"

    & $msbuild (Join-Path $Root 'SsmsChatExtension.sln') /restore /t:Rebuild "/p:Configuration=$Configuration" /v:minimal /nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed (exit code $LASTEXITCODE)." }

    $vsix = Join-Path $Project "bin\$Configuration\SsmsChatExtension.vsix"
    if (-not (Test-Path -LiteralPath $vsix)) { throw "Build succeeded but the VSIX was not produced at: $vsix" }

    $dist = Join-Path $Root 'dist'
    if (Test-Path -LiteralPath $dist) { Remove-Item -LiteralPath $dist -Recurse -Force }
    New-Item -ItemType Directory -Path $dist -Force | Out-Null

    & $iscc /Q "/DAppVersion=$Version" "/DVsixPath=$vsix" (Join-Path $Root 'installer\SsmsClaudeChat.iss')
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed (exit code $LASTEXITCODE)." }

    $setup = Join-Path $dist "SsmsClaudeChat-Setup-$Version.exe"
    Copy-Item -LiteralPath $vsix -Destination (Join-Path $dist "SsmsClaudeChat-$Version.vsix")

    Write-Host ""
    Write-Host "Setup : $setup" -ForegroundColor Green
    Write-Host "Next: run it on any machine with SSMS 21 or 22."
}
catch {
    Write-Host ""
    Write-Host "Build failed: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
