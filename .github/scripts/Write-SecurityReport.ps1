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

# Banner for the top of the public README: overall status, scan time and one mark per check.
# Symbols as character codes so the script reads the same in any PowerShell, whatever the file's encoding.
$tick, $cross, $dash, $dot, $arrow = [char]0x2713, [char]0x2717, [char]0x2013, [char]0xB7, [char]0x2192
function Escape([string]$text) { [Security.SecurityElement]::Escape($text) }
$ok = -not $failed
$fill = if ($ok) { '#f0fdf4' } else { '#fffbeb' }
$edge = if ($ok) { '#16a34a' } else { '#d97706' }
$headline = if ($ok) { 'All security checks passed' } else { 'Some security checks need attention' }
$when = (Get-Date $report.generated).ToUniversalTime().ToString('d MMM yyyy, HH:mm') + ' UTC'
$sub = "Last scan $when" + $(if ($Release) { " $dot latest release $Release" }) + " $dot scanned on every change and daily"
$marks = for ($i = 0; $i -lt $results.Count; $i++) {
    $c = $results[$i]
    $symbol, $color = switch ($c.result) { 'pass' { $tick, '#16a34a' } 'skipped' { $dash, '#9ca3af' } default { $cross, '#dc2626' } }
    $x = 24 + ($i % 4) * 200; $y = 104 + [Math]::Floor($i / 4) * 24
    "<text x='$x' y='$y' font-size='14'><tspan fill='$color' font-weight='700'>$symbol</tspan><tspan fill='#374151' dx='6'>$(Escape $c.name)</tspan></text>"
}
$height = 104 + [Math]::Ceiling($results.Count / 4) * 24
@"
<svg xmlns='http://www.w3.org/2000/svg' width='840' height='$height' viewBox='0 0 840 $height' font-family='-apple-system,Segoe UI,Helvetica,Arial,sans-serif' role='img' aria-label='$(Escape "SSMS Claude Chat security scan: $headline. $sub")'>
  <rect x='1' y='1' width='838' height='$($height - 2)' rx='12' fill='$fill' stroke='$edge' stroke-width='2'/>
  <text x='24' y='38' font-size='20' font-weight='700' fill='#111827'><tspan fill='$edge'>$(if ($ok) { $tick } else { '!' })</tspan> SSMS Claude Chat security scan: $(Escape $headline)</text>
  <text x='24' y='64' font-size='14' fill='#4b5563'>$(Escape $sub)</text>
  $($marks -join "`n  ")
  <text x='816' y='38' font-size='14' fill='$edge' text-anchor='end'>Full report $arrow</text>
</svg>
"@ | Set-Content -LiteralPath (Join-Path $OutDir 'banner.svg') -Encoding utf8

Write-Host "Security report: $($report.status) ($($failed.Count) failing check(s))"
