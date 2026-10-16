<#
.SYNOPSIS
    Static analysis gate: the .NET code analyzers at their recommended level, against a recorded
    baseline. No Revit needed (the build falls back to the Nice3point reference packages).

.DESCRIPTION
    Rebuilds Ribbit.Native and Ribbit.Geometry once (Revit 2026, Debug, so the dev-only folders are
    analysed too) with the Microsoft .NET analyzers at AnalysisLevel=latest-recommended, into a
    throwaway output folder, and collects every distinct finding.

    Two rules decide pass or fail:

      1. ZERO in the categories that can hide a real defect: reliability (CA2000-CA2099),
         security (CA2100-CA2199, CA2300-CA2399, CA3000-CA3999, CA5000-CA5999), plus CA1001
         (a type that owns a disposable and is not disposable) and CA1806 (an ignored result,
         e.g. a TryParse whose failure nobody handles) - EXCEPT where ci\analyzers-baseline.txt
         already records them. A deliberate exception is suppressed IN THE CODE with a written
         justification, where a reviewer sees it.

      2. NO NEW findings of any rule, per file. ci\analyzers-baseline.txt records how many findings
         each (rule, file) pair had when the gate was introduced; a count may go down (fix things,
         then run with -UpdateBaseline to ratchet), never up. Counting per file rather than per
         line means an edit that moves code does not churn the baseline.

    Adapted from LiveClash's ci\analyze.ps1. Ribbit had no analyzers before 2026-10-16, so the
    baseline records what was already there; the gate stops anything new from getting in.

.PARAMETER UpdateBaseline
    Rewrite ci\analyzers-baseline.txt from this run. Only after fixing findings, never to admit new ones.

.PARAMETER Focus
    Also list every current finding in the files whose path contains this text (e.g. MoveConnect).

MUST STAY ASCII (Windows PowerShell 5.1 reads this file as ANSI).
#>
[CmdletBinding()]
param([switch] $UpdateBaseline, [string] $Focus = '')

$ErrorActionPreference = 'Stop'
$root     = Split-Path -Parent $PSScriptRoot
$baseline = Join-Path $PSScriptRoot 'analyzers-baseline.txt'
$work     = Join-Path ([System.IO.Path]::GetTempPath()) 'Ribbit-analyze'
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Force $work | Out-Null
$log = Join-Path $work 'build.log'
$project = Join-Path $root 'src\Ribbit.Native\Ribbit.Native.csproj'

Write-Host "-- static analysis (.NET analyzers, latest-recommended, Revit 2026 Debug) --" -ForegroundColor Cyan
# -t:Rebuild is load-bearing: an incremental build re-runs no analyzers and reports nothing.
& dotnet build $project -p:RevitVersion=2026 -c Debug -t:Rebuild -v q --nologo `
    -p:EnableNETAnalyzers=true -p:AnalysisLevel=latest-recommended `
    "-p:OutputPath=$work\bin\" "-flp:logfile=$log;verbosity=normal" | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Host "ANALYSIS BUILD FAILED (see $log)" -ForegroundColor Red; exit 1 }

# file(line,col): warning CAxxxx: message [project]  - the build log repeats each warning, so de-duplicate.
$rx = '^\s*(?<file>[A-Za-z]:\\[^(]+)\((?<l>\d+),(?<c>\d+)\): warning (?<id>CA\d{4}): (?<msg>.*?)(?: \[[^\]]+\])?\s*$'
$seen = @{}
$findings = @()
foreach ($line in (Get-Content $log)) {
    $m = [regex]::Match($line, $rx)
    if (-not $m.Success) { continue }
    $file = $m.Groups['file'].Value
    $i = $file.IndexOf($root, [StringComparison]::OrdinalIgnoreCase)
    if ($i -ge 0) { $file = $file.Substring($i + $root.Length).TrimStart('\') }
    $key = $m.Groups['id'].Value + '|' + $file + '|' + $m.Groups['l'].Value + '|' + $m.Groups['c'].Value
    if ($seen.ContainsKey($key)) { continue }
    $seen[$key] = $true
    $findings += [pscustomobject]@{ Id = $m.Groups['id'].Value; File = $file; Line = [int]$m.Groups['l'].Value; Msg = $m.Groups['msg'].Value }
}

function Is-Critical([string]$id) {
    $n = [int]$id.Substring(2)
    return ($n -ge 2000 -and $n -le 2199) -or ($n -ge 2300 -and $n -le 2399) -or ($n -ge 3000 -and $n -le 3999) `
        -or ($n -ge 5000 -and $n -le 5999) -or $id -eq 'CA1001' -or $id -eq 'CA1806'
}
# CA2211, CA2249, CA2263, CA2254 sit in the CA2xxx range but are design/usage/performance advice, not defects.
$notCritical = @('CA2211', 'CA2249', 'CA2263', 'CA2254')

$counts = @{}
foreach ($f in $findings) { $k = $f.Id + '|' + $f.File; if ($counts.ContainsKey($k)) { $counts[$k]++ } else { $counts[$k] = 1 } }

if ($UpdateBaseline) {
    $lines = @('# ci\analyzers-baseline.txt - findings per (rule, file) allowed by ci\analyze.ps1. A RATCHET: counts',
               '# may only go down. Regenerate with ci\analyze.ps1 -UpdateBaseline after FIXING findings, never to admit new ones.')
    $lines += ($counts.Keys | Sort-Object | ForEach-Object { $_ + '|' + $counts[$_] })
    Set-Content -Path $baseline -Value $lines -Encoding ASCII
    Write-Host ("    baseline written: {0} (rule, file) pairs, {1} findings" -f $counts.Count, $findings.Count) -ForegroundColor Yellow
}

$allowed = @{}
if (Test-Path $baseline) {
    foreach ($l in (Get-Content $baseline)) {
        if ($l -match '^#' -or $l.Trim() -eq '') { continue }
        $p = $l.Split('|')
        if ($p.Count -eq 3) { $allowed[$p[0] + '|' + $p[1]] = [int]$p[2] }
    }
}

# A critical finding fails unless its (rule, file) pair is in the baseline at that count or more.
$critical = @($findings | Where-Object { (Is-Critical $_.Id) -and ($notCritical -notcontains $_.Id) -and -not $allowed.ContainsKey($_.Id + '|' + $_.File) })

$grown = @()
foreach ($k in $counts.Keys) {
    $was = if ($allowed.ContainsKey($k)) { $allowed[$k] } else { 0 }
    if ($counts[$k] -gt $was) { $grown += [pscustomobject]@{ Key = $k; Now = $counts[$k]; Was = $was } }
}
$shrunk = 0
foreach ($k in $allowed.Keys) { $now = if ($counts.ContainsKey($k)) { $counts[$k] } else { 0 }; if ($now -lt $allowed[$k]) { $shrunk += $allowed[$k] - $now } }

$byRule = $findings | Group-Object Id | Sort-Object Count -Descending | Select-Object -First 8 | ForEach-Object { $_.Name + ' x' + $_.Count }
$critAll = @($findings | Where-Object { (Is-Critical $_.Id) -and ($notCritical -notcontains $_.Id) })
Write-Host ("    {0} findings in {1} (rule, file) pairs ({2} in must-be-zero categories); most common: {3}" -f $findings.Count, $counts.Count, $critAll.Count, ($byRule -join ', '))

if ($Focus) {
    $mine = @($findings | Where-Object { $_.File -like "*$Focus*" } | Sort-Object File, Line)
    Write-Host ("    findings in files matching '{0}': {1}" -f $Focus, $mine.Count)
    $mine | ForEach-Object { Write-Host ("      {0} {1}:{2}  {3}" -f $_.Id, $_.File, $_.Line, $_.Msg) }
}

$fail = 0
if ($critical.Count -gt 0) {
    Write-Host ("STATIC ANALYSIS FAILED: {0} finding(s) in a must-be-zero category:" -f $critical.Count) -ForegroundColor Red
    $critical | Select-Object -First 20 | ForEach-Object { Write-Host ("      {0} {1}:{2}  {3}" -f $_.Id, $_.File, $_.Line, $_.Msg) }
    $fail++
}
if ($grown.Count -gt 0) {
    Write-Host ("STATIC ANALYSIS FAILED: {0} (rule, file) pair(s) have NEW findings beyond the baseline:" -f $grown.Count) -ForegroundColor Red
    foreach ($g in ($grown | Select-Object -First 20)) {
        Write-Host ("      {0}: {1} now, {2} allowed" -f $g.Key, $g.Now, $g.Was)
        $id, $file = $g.Key.Split('|')
        $findings | Where-Object { $_.Id -eq $id -and $_.File -eq $file } | Select-Object -First 3 | ForEach-Object { Write-Host ("          line {0}: {1}" -f $_.Line, $_.Msg) }
    }
    $fail++
}
if ($fail -eq 0) {
    $note = if ($shrunk -gt 0) { " ($shrunk fewer than the baseline - run with -UpdateBaseline to ratchet it down)" } else { "" }
    Write-Host ("STATIC ANALYSIS PASSED: 0 critical, 0 new{0}" -f $note) -ForegroundColor Green
}
exit $fail
