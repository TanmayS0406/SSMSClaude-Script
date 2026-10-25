<#
.SYNOPSIS
  Builds the public security report (security.json) and its README badge (badge.json) from a Security run.

.DESCRIPTION
  Publishes only what is safe to make public: each check's result and its number of findings per severity.
  Finding details (files, rules, messages) stay in this private repository's run, so nobody learns of an
  unfixed problem from the report.

.PARAMETER Needs
  The workflow's needs context as JSON (each job's result).

.PARAMETER SarifRoot
  Folder with the downloaded sarif-* artifacts, one subfolder per artifact.

.PARAMETER OutDir
  Where security.json and badge.json are written.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Needs,
    [Parameter(Mandatory)][string]$SarifRoot,
    [Parameter(Mandatory)][string]$OutDir,
    [string]$Release,
    [switch]$Signed
)

$ErrorActionPreference = 'Stop'
$jobs = $Needs | ConvertFrom-Json
New-Item -ItemType Directory -Force $OutDir | Out-Null

# Job id, plain-language name and description, the SARIF artifacts it produces, and when it fails.
$checks = @(
    @{ Job = 'codeql'; Name = 'Code analysis'; Tools = 'GitHub CodeQL'; Sarif = 'sarif-codeql-*'; SecurityOnly = $true
       Gate = 'high'; About = 'Reads the source code looking for security bugs such as injection or unsafe data handling.' }
    @{ Job = 'sast'; Name = 'Security rules'; Tools = 'Semgrep, Microsoft DevSkim'; Sarif = 'sarif-sast'
       Gate = 'high'; About = 'Checks the code against thousands of known insecure patterns.' }
    @{ Job = 'secrets'; Name = 'Leaked passwords and keys'; Tools = 'Gitleaks, TruffleHog'
       Gate = 'any'; About = 'Searches the whole history of the code for passwords, keys or tokens left in by mistake.' }
    @{ Job = 'dependencies'; Name = 'Vulnerable libraries'; Tools = 'OSV-Scanner, Trivy'; Sarif = 'sarif-dependencies'; UniqueRules = $true
       Gate = 'critical'; About = 'Compares every library used with public vulnerability databases.'
       Note = 'Includes libraries used only while building, which are not part of the installer. What ships is checked by the installer scan below.' }
    @{ Job = 'scan-build'; Name = 'Installer scan'; Tools = 'ClamAV, Grype, Syft'; Sarif = 'sarif-build-output'; UniqueRules = $true
       Gate = 'medium'; About = 'Scans the finished installer for viruses and for vulnerable components, and lists everything inside it.' }
    @{ Job = 'workflows'; Name = 'Build pipeline safety'; Tools = 'actionlint, zizmor'
       Gate = 'any'; About = 'Checks that the automated build cannot be tampered with or leak its credentials.' }
    @{ Job = 'powershell'; Name = 'Build script checks'; Tools = 'PSScriptAnalyzer'
       Gate = 'error'; About = 'Checks the build scripts for unsafe or error-prone code.' }
    @{ Job = 'scorecard'; Name = 'Security best practices'; Tools = 'OpenSSF Scorecard'; Sarif = 'sarif-scorecard'
       Gate = 'none'; About = 'Grades how the project is run: reviews, pinned dependencies, token permissions and more.' }
)

$results = foreach ($check in $checks) {
    $result = switch ($jobs.($check.Job).result) {
        'success' { 'pass' } 'failure' { 'fail' } 'cancelled' { 'cancelled' } default { 'skipped' }
    }
    $findings = $null
    if ($check.Sarif -and $result -ne 'skipped') {
        $dirs = @(Get-ChildItem -LiteralPath $SarifRoot -Directory -Filter $check.Sarif -ErrorAction SilentlyContinue)
        if ($dirs) {
            $json = Join-Path $OutDir "$($check.Job).counts.json"
            try {
                & (Join-Path $PSScriptRoot 'Assert-Sarif.ps1') -Path $dirs.FullName -FailOn none -Title $check.Name `
                    -SecurityOnly:([bool]$check.SecurityOnly) -UniqueRules:([bool]$check.UniqueRules) -JsonPath $json | Out-Null
                $findings = Get-Content -LiteralPath $json -Raw | ConvertFrom-Json
                Remove-Item -LiteralPath $json
            }
            catch { Write-Warning "$($check.Name): $($_.Exception.Message)" }
        }
    }
    [ordered]@{
        id = $check.Job; name = $check.Name; tools = $check.Tools; about = $check.About
        fails_on = $check.Gate; note = $check.Note; result = $result; findings = $findings
    }
}

$failed = @($results | Where-Object { $_.result -in 'fail', 'cancelled' })
$report = [ordered]@{
    generated = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    status    = if ($failed) { 'fail' } else { 'pass' }
    commit    = "$env:GITHUB_SHA".Substring(0, [Math]::Min(7, "$env:GITHUB_SHA".Length))
    release   = $Release
    trigger   = $env:GITHUB_EVENT_NAME
    signed    = [bool]$Signed
    checks    = @($results)
}
$report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutDir 'security.json') -Encoding utf8

# shields.io endpoint badge.
[ordered]@{
    schemaVersion = 1
    label         = 'security scan'
    message       = if ($failed) { 'issues found' } else { 'passing' }
    color         = if ($failed) { 'red' } else { 'brightgreen' }
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutDir 'badge.json') -Encoding utf8

Write-Host "Security report: $($report.status) ($($failed.Count) failing check(s))"
