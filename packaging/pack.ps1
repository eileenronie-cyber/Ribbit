<#
.SYNOPSIS
    Builds Release and assembles a copyable Ribbit package under dist\.

.DESCRIPTION
    Produces the layout Revit expects when the manifest uses relative paths:

        dist\Ribbit-<date>\
            Ribbit.Native.addin          <- relative <Assembly> paths
            INSTALL.md
            Ribbit\
                Ribbit.Native.dll
                Ribbit.Geometry.dll
                Microsoft.Web.WebView2.*.dll
                Ribbit.Native.deps.json
                runtimes\win-x64\native\WebView2Loader.dll

    The WebView2 files are NOT optional - the clash report is a WebView2 page,
    and the native loader has to keep its runtimes\ subfolder because that is
    where the managed wrapper looks for it.

    Leaves a development setup alone: a manifest installed in
    %APPDATA% still points at bin\Debug, so the normal build-and-test loop is
    untouched by packaging.

.NOTES
    Symbols (.pdb) and IntelliSense docs (.xml) are excluded. Add the .pdb back
    if you ever need a readable stack trace from another machine.
#>

[CmdletBinding()]
param(
    # Which Revit this package is for. One package PER version: 2024 is .NET
    # Framework 4.8, 2025 is .NET 8, and 2026 is .NET 10 - three different
    # runtimes, so three different binaries. (2025 and 2026 were both assumed
    # to be .NET 8 here until 2026-08-18, when a REAL installed Revit 2026
    # (build 26.5) failed to build against net8.0-windows: its own RevitAPI.dll
    # now references System.Runtime 10.0.0.0. Re-check this boundary against
    # each new Revit release rather than trusting it forward.)
    # Needs that Revit installed to reference - the build says so if it is not.
    [ValidateSet("2024", "2025", "2026")]
    [string] $RevitVersion = "2025",

    [string] $Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "src\Ribbit.Native\bin\$Configuration"
$stamp = Get-Date -Format "yyyyMMdd"
$dist = Join-Path $root "dist\Ribbit-$RevitVersion-$stamp"
$payload = Join-Path $dist "Ribbit"

Write-Host "Building $Configuration for Revit $RevitVersion..."

# The project, not the solution: the test project has no Revit leg and does not
# need building to package.
dotnet build (Join-Path $root "src\Ribbit.Native\Ribbit.Native.csproj") `
    -c $Configuration -p:RevitVersion=$RevitVersion -v q --nologo
if ($LASTEXITCODE -ne 0) { throw "build failed" }

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory $payload -Force | Out-Null

# Everything the add-in needs at runtime, and nothing else.
#
# Roslyn and its dependencies are never part of a package; filtered out
# here in case a future reference brings them in.
$devOnly = "Microsoft.CodeAnalysis*", "System.Reflection.Metadata*", "System.Collections.Immutable*"

Get-ChildItem $out -Recurse -File |
    Where-Object { $_.Extension -notin @(".pdb", ".xml") } |
    Where-Object { $name = $_.Name; -not ($devOnly | Where-Object { $name -like $_ }) } |
    ForEach-Object {
        $rel = $_.FullName.Substring($out.Length).TrimStart("\")
        $target = Join-Path $payload $rel
        $dir = Split-Path -Parent $target
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory $dir -Force | Out-Null }
        Copy-Item $_.FullName $target -Force
    }

Copy-Item (Join-Path $PSScriptRoot "Ribbit.Native.addin") $dist -Force
Copy-Item (Join-Path $PSScriptRoot "INSTALL.md") $dist -Force

$count = (Get-ChildItem $payload -Recurse -File | Measure-Object).Count
$size = (Get-ChildItem $dist -Recurse -File | Measure-Object Length -Sum).Sum / 1MB

Write-Host ""
Write-Host ("Packaged {0} file(s), {1:N1} MB for Revit {2}" -f $count, $size, $RevitVersion)
Write-Host $dist
Write-Host ("Install into %APPDATA%\Autodesk\Revit\Addins\{0}\" -f $RevitVersion)
