# Ribbit

A Revit add-in (2024/2025/2026): MEP routing, fire-protection piping, sheet and annotation tools, and Federated Clash. See `README.md` for the tools.

## Layout

- `src/Ribbit.Geometry`: netstandard2.0. Pure logic with **no Revit reference** (solvers, planners, clash records, xlsx). Keep it that way so it stays testable without Revit.
- `src/Ribbit.Native`: the add-in.
  - `RibbitApplication.cs` builds the ribbon.
  - `Commands/`, `Tools/`, `Revit/` (engines that touch the document) and `Ui/` (WPF built in code) hold the tools.
- `tests/Ribbit.Tests`: xUnit over Ribbit.Geometry. `tests/Ribbit.Native.Tests`: xUnit over the clash code in Ribbit.Native.
- `packaging/`: `pack.ps1` builds an installable folder; `INSTALL.md`. `.github/workflows/`: CI, and a release on `v*` tags.

## Build and test

```
dotnet build src/Ribbit.Native -c Release -p:RevitVersion=2026    # net10.0-windows
dotnet build src/Ribbit.Native -c Release -p:RevitVersion=2025    # net8.0-windows (the default)
dotnet build src/Ribbit.Native -c Release -p:RevitVersion=2024    # net48
dotnet test Ribbit.sln
powershell -ExecutionPolicy Bypass -File ci\analyze.ps1          # static analysis vs ci\analyzers-baseline.txt
powershell -ExecutionPolicy Bypass -File ci\coverage.ps1 -Report # coverage floors, Connect/Align/Elbow maths
```

- **`ci\analyze.ps1`:** the .NET analyzers. Nothing new may appear beyond the baseline, which is a ratchet: after fixing findings, run `-UpdateBaseline`; never use it to let new ones in.
- **`ci\coverage.ps1`:** keeps `MoveConnectMath`, `AlignBranchMath`, `ElbowMath`, `ElbowDial` and `Vec3` at their floors (100%, except one unreachable branch). Add tests before changing those files. CI runs both.

- **Where the Revit API comes from:** the installed Revit's `RevitAPI.dll` when present, otherwise the Nice3point reference packages, so it builds without Revit.
- **Shared output folder:** all versions build into the same `bin\<Configuration>\`. Delete `bin`/`obj` between versions when packaging.

## Code rules

- **Revit API differences between versions:** only in `src/Ribbit.Native/Revit/RevitCompat.cs`.
- **.NET Framework 4.8 gaps:** shims go in `src/Ribbit.Native/Compat/NetFrameworkShims.cs` (net48 only, with the same contract as the .NET method).
- **Dialogs:** go through `Ui/RibbitUi`.
- **Tools that change the model:** preview first, one transaction, one Ctrl+Z.

## Note for maintainers

This repository is a published copy of the author's working repository and is refreshed from it. A change made only here can be overwritten by the next refresh unless it is also made upstream.
