<#
.SYNOPSIS
    Unit-test coverage gate for the modeling tools' Revit-free maths. No Revit needed.

.DESCRIPTION
    Runs tests\Ribbit.Tests with coverlet (coverlet.collector, Cobertura format) and checks, per
    source file, that line and branch coverage are at or above the floors below. The floors are a
    RATCHET: raise one when tests improve, never lower it to let a change through.

    Covered here (the maths behind Connect / Align / Elbow, all in Ribbit.Geometry):
        MoveConnectMath  - Move Connect Align: which ends pair, where the loose element goes
        AlignBranchMath  - Align Branch: closest points between the runs, the move that aligns
        ElbowMath        - Elbow (Click Dir): turn candidates, the click-to-turn choice, menu labels
        ElbowDial        - Elbow (Click Dir): the on-screen direction dial
        Vec3             - the vector type all of them use
        PickLoop         - Connect Multi / Align Multi vs single: when to stop, what the prompt says

    The Revit-touching halves (MoveConnectEngine, AlignBranchEngine, ElbowByClickEngine and the dial
    painter) cannot run outside Revit; they are covered by the in-Revit suites connect / align /
    elbow and their planted-bug runs, which are not part of this repository.

    MoveConnectMath's branch floor is below 100 on purpose: ChoosePair's "no mover can join the
    chosen target" exit is unreachable (a target is only chosen when some mover can join it) and is
    kept as a guard, not tested by contortion.

.PARAMETER Report
    Also print every line/branch not covered in the gated files.

MUST STAY ASCII (Windows PowerShell 5.1 reads this file as ANSI).
#>
[CmdletBinding()]
param([switch] $Report)

$floors = [ordered]@{
    #  file                 lines%  branches%
    'MoveConnectMath.cs' = @(100,  98)
    'AlignBranchMath.cs' = @(100, 100)
    'ElbowMath.cs'       = @(100, 100)
    'ElbowDial.cs'       = @(100, 100)
    'Vec3.cs'            = @(100, 100)
    'PickLoop.cs'        = @(100, 100)
}

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
$work = Join-Path ([IO.Path]::GetTempPath()) 'Ribbit-coverage'
if (Test-Path $work) { Remove-Item $work -Recurse -Force }

Write-Host "-- unit-test coverage of the modeling maths (Connect / Align / Elbow) --" -ForegroundColor Cyan
& dotnet test (Join-Path $root 'tests\Ribbit.Tests') --nologo -v q --collect:"XPlat Code Coverage" --results-directory $work 2>&1 |
    Where-Object { "$_" -match 'Passed!|Failed!| error ' } | ForEach-Object { Write-Host "    $_" }
if ($LASTEXITCODE -ne 0) { Write-Host "COVERAGE FAILED: the unit tests did not pass" -ForegroundColor Red; exit 1 }

$xmlPath = Get-ChildItem $work -Recurse -Filter coverage.cobertura.xml | Select-Object -First 1
if (-not $xmlPath) { Write-Host "COVERAGE FAILED: no coverage report was written" -ForegroundColor Red; exit 1 }
[xml] $xml = Get-Content $xmlPath.FullName -Raw

# One file can hold several classes (records, nested types, lambdas); merge their lines by number.
$files = @{}
foreach ($cls in $xml.SelectNodes('//class')) {
    $name = [IO.Path]::GetFileName($cls.filename)
    if (-not $floors.Contains($name)) { continue }
    if (-not $files.ContainsKey($name)) { $files[$name] = @{} }
    foreach ($ln in $cls.SelectNodes('lines/line')) {
        $n = [int]$ln.number
        $hits = [int]$ln.hits
        $bCov = 0; $bAll = 0
        if ($ln.'condition-coverage' -match '\((\d+)/(\d+)\)') { $bCov = [int]$Matches[1]; $bAll = [int]$Matches[2] }
        if ($files[$name].ContainsKey($n)) {
            $o = $files[$name][$n]
            $files[$name][$n] = @{ Hits = [Math]::Max($o.Hits, $hits); BCov = [Math]::Max($o.BCov, $bCov); BAll = [Math]::Max($o.BAll, $bAll) }
        } else {
            $files[$name][$n] = @{ Hits = $hits; BCov = $bCov; BAll = $bAll }
        }
    }
}

$fail = 0
foreach ($name in $floors.Keys) {
    if (-not $files.ContainsKey($name)) { Write-Host ("    {0,-20} not in the coverage report" -f $name) -ForegroundColor Red; $fail++; continue }
    $lines = $files[$name].Values
    $lineAll = @($lines).Count
    $lineHit = @($lines | Where-Object { $_.Hits -gt 0 }).Count
    # Summed by hand: the entries are hashtables, which Measure-Object -Property cannot read.
    $bAll = 0; $bCov = 0
    foreach ($v in $lines) { $bAll += $v.BAll; $bCov += $v.BCov }
    $lp = if ($lineAll) { 100.0 * $lineHit / $lineAll } else { 100.0 }
    $bp = if ($bAll) { 100.0 * $bCov / $bAll } else { 100.0 }
    $ok = ($lp -ge $floors[$name][0]) -and ($bp -ge $floors[$name][1])
    if (-not $ok) { $fail++ }
    Write-Host ("    {0,-20} lines {1,6:N1}% (floor {2})   branches {3,6:N1}% (floor {4})   {5}" -f $name, $lp, $floors[$name][0], $bp, $floors[$name][1], $(if ($ok) { 'ok' } else { 'BELOW FLOOR' })) -ForegroundColor $(if ($ok) { 'Gray' } else { 'Red' })
    if ($Report -or -not $ok) {
        foreach ($k in ($files[$name].Keys | Sort-Object)) {
            $v = $files[$name][$k]
            if ($v.Hits -eq 0) { Write-Host "        line $k never runs" }
            elseif ($v.BAll -gt 0 -and $v.BCov -lt $v.BAll) { Write-Host "        line $k branches $($v.BCov)/$($v.BAll)" }
        }
    }
}

if ($fail) { Write-Host "COVERAGE FAILED: $fail file(s) below their floor" -ForegroundColor Red; exit 1 }
Write-Host "COVERAGE PASSED (Connect / Align / Elbow maths at or above their floors)" -ForegroundColor Green
exit 0
