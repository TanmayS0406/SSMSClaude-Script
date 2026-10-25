<#
.SYNOPSIS
  Lists the findings in SARIF files (log and job summary) and fails when any reach -FailOn.

.DESCRIPTION
  Every scanner in security.yml writes SARIF, so this one script gives them the same report and the same gate.
  A finding's severity comes from its rule's security-severity score (CVSS-style, 0-10) when the tool sets one,
  otherwise from its level: error = high, warning = medium, note = low. Suppressed findings are skipped.

.PARAMETER Path
  SARIF files, or folders searched for *.sarif.

.PARAMETER FailOn
  Lowest severity that fails the step. 'none' only reports.

.PARAMETER SecurityOnly
  Only findings whose rule has a security-severity score can fail the step (e.g. CodeQL's code-quality queries
  are listed but do not fail it).

.PARAMETER JsonPath
  Also writes the number of findings per severity (only those that could fail the step) to this JSON file, for
  the public security report.

.PARAMETER UniqueRules
  With -JsonPath, counts each rule once at its highest severity: several scanners report the same CVE.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string[]]$Path,
    [ValidateSet('critical', 'high', 'medium', 'low', 'none')][string]$FailOn = 'high',
    [string]$Title = 'Findings',
    [switch]$SecurityOnly,
    [string]$JsonPath,
    [switch]$UniqueRules
)

$ErrorActionPreference = 'Stop'
$rank = @{ none = 0; low = 1; medium = 2; high = 3; critical = 4 }

$files = foreach ($p in $Path) {
    if (Test-Path -LiteralPath $p -PathType Container) { Get-ChildItem -LiteralPath $p -Recurse -Filter *.sarif }
    elseif (Test-Path -LiteralPath $p) { Get-Item -LiteralPath $p }
}
if (-not $files) { throw "No SARIF found at: $($Path -join ', ')" }

$findings = foreach ($file in $files) {
    $sarif = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
    foreach ($run in $sarif.runs) {
        $rules = @{}
        foreach ($rule in @($run.tool.driver.rules) + @($run.tool.extensions | ForEach-Object { $_.rules })) {
            if ($rule -and $rule.id -and -not $rules.ContainsKey($rule.id)) { $rules[$rule.id] = $rule }
        }
        foreach ($result in $run.results) {
            if ($result.suppressions) { continue }
            $ruleId = if ($result.ruleId) { $result.ruleId } else { $result.rule.id }
            $rule = $rules[$ruleId]
            $score = $null
            foreach ($props in $result.properties, $rule.properties) {
                $value = $props.'security-severity' -as [double]
                if ($props.'security-severity' -and $null -ne $value) { $score = $value; break }
            }
            $severity = if ($null -ne $score) {
                if ($score -ge 9) { 'critical' } elseif ($score -ge 7) { 'high' } elseif ($score -ge 4) { 'medium' } elseif ($score -gt 0) { 'low' } else { 'none' }
            } else {
                $level = if ($result.level) { $result.level } elseif ($rule.defaultConfiguration.level) { $rule.defaultConfiguration.level } else { 'warning' }
                switch ($level) { 'error' { 'high' } 'warning' { 'medium' } 'note' { 'low' } default { 'none' } }
            }
            $location = $result.locations | Select-Object -First 1
            $where = $location.physicalLocation.artifactLocation.uri
            if ($location.physicalLocation.region.startLine) { $where += ":$($location.physicalLocation.region.startLine)" }
            [pscustomobject]@{
                Tool     = $run.tool.driver.name
                Severity = $severity
                Gates    = -not $SecurityOnly -or $null -ne $score
                Rule     = $ruleId
                Location = $where
                Message  = (($result.message.text -split "`n")[0]).Trim()
            }
        }
    }
}
$findings = @($findings | Sort-Object { -$rank[$_.Severity] }, Tool, Location)
$failing = @($findings | Where-Object { $FailOn -ne 'none' -and $_.Gates -and $rank[$_.Severity] -ge $rank[$FailOn] })

Write-Host "$Title`: $($findings.Count) finding(s), $($failing.Count) at or above '$FailOn'."
foreach ($f in $findings) { Write-Host ("[{0}] {1} {2} {3} - {4}" -f $f.Severity.ToUpper(), $f.Tool, $f.Rule, $f.Location, $f.Message) }

if ($env:GITHUB_STEP_SUMMARY) {
    $md = @("### $Title", '', "$($findings.Count) finding(s); $($failing.Count) at or above **$FailOn** (fails the check).", '')
    if ($findings) {
        $md += '| Severity | Tool | Rule | Location | Message |', '|---|---|---|---|---|'
        $md += $findings | Select-Object -First 100 | ForEach-Object {
            '| {0} | {1} | {2} | {3} | {4} |' -f $_.Severity, $_.Tool, $_.Rule, $_.Location, ($_.Message -replace '\|', '\|')
        }
        if ($findings.Count -gt 100) { $md += '', "_...and $($findings.Count - 100) more; see the SARIF artifact._" }
    }
    $md -join "`n" | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding utf8
}

if ($JsonPath) {
    $counted = @($findings | Where-Object Gates)
    # $findings is sorted most severe first, so the first of each rule is its highest severity.
    if ($UniqueRules) { $counted = @($counted | Group-Object Rule | ForEach-Object { $_.Group[0] }) }
    $counts = [ordered]@{}
    foreach ($level in 'critical', 'high', 'medium', 'low') {
        $counts[$level] = @($counted | Where-Object Severity -eq $level).Count
    }
    $counts | ConvertTo-Json | Set-Content -LiteralPath $JsonPath -Encoding utf8
}

if ($failing) { exit 1 }
