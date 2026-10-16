# Ribbit

*by John Ronie B. Torres*

A native C# add-in for Autodesk Revit 2024, 2025 and 2026: MEP routing, fire-protection piping, sheet and annotation tools, and federated clash detection. It began as a pyRevit extension and a set of Dynamo graphs. This is the compiled version of those tools, so there's no pyRevit or Dynamo to install.

## What's on the ribbon

### Ribbit tab

| Panel | Tools |
|---|---|
| **Tools** | Connect, Connect Multi, Align, Align Multi, Disconnect, Elbow (Click Dir) |
| **PH_FP Tools** | Tee Into Main, Sprig Connect, Drop Connect, Slope Through, Slope Armover |
| **Sheets** | Export Sheets (to Excel and back), Sheet Maker, Place Views on Sheets, Copy View Filters, Part Sheets Duplicator, Adjust Grids |
| **Annotation** | Number Text Notes, Room Tag Checker |
| **About** | Version and build information |

### Ribbit Clash tab

| Panel | Tools |
|---|---|
| **Federated** | **Federated Clash** checks the host model and its links in one pass, much like searching an appended Navisworks scene. **Set Clash Status** reopens the triage ledger. |

Federated Clash highlights:

- Clash sets come from a shared **Set Library**. They're named rules that can be used on either side, or you can type a one-off keyword.
- A pair that's within the clearance pad but not touching is reported as a **clearance** clash, separate from **hard** clashes.
- **Insulation** goes into a set together with the pipe or duct it wraps.
- **Equipment hosted in walls** is still reported, not filtered out as noise.
- The **workset audit** shows which worksets each library set leaves out.
- Triage (status, comments, who set it) is kept in a CSV ledger for each model, and more than one person can work from the same ledger.
- It never modifies the model.

Most tools show a preview before they write anything, and each run is a single Ctrl+Z.

## Installing

Download the zip for your Revit version from [Releases](../../releases). Unzip it and copy `Ribbit.Native.addin` **and** the `Ribbit` folder beside it into `%APPDATA%\Autodesk\Revit\Addins\<year>\`, then start Revit. Full steps are in [packaging/INSTALL.md](packaging/INSTALL.md).

The 2025 and 2026 builds are the ones in daily use. The 2024 build compiles from the same source but has had less testing inside Revit 2024.

## Building

You need the .NET SDK 10 (it builds all three targets).

```
dotnet build src/Ribbit.Native -c Release -p:RevitVersion=2026   # net10.0-windows
dotnet build src/Ribbit.Native -c Release -p:RevitVersion=2025   # net8.0-windows (default)
dotnet build src/Ribbit.Native -c Release -p:RevitVersion=2024   # net48
```

If that Revit version is installed, the build uses its `RevitAPI.dll`. If not, it falls back to the `Nice3point.Revit.Api` reference packages.

To build an installable package, run `packaging/pack.ps1 -RevitVersion 2026`. Install instructions are in [packaging/INSTALL.md](packaging/INSTALL.md).

## Tests

```
dotnet test Ribbit.sln
```

- `tests/Ribbit.Tests` covers the pure geometry, solver, planner and Excel code in `Ribbit.Geometry`. It needs no Revit.
- `tests/Ribbit.Native.Tests` covers the clash ledger, set transfer, title migration and workset exclusion logic.

GitHub Actions (`.github/workflows/ci.yml`) builds all three Revit versions and runs both test projects on every push. Pushing a `v*` tag runs `.github/workflows/release.yml`, which packages each version and publishes the zips as a release.

## Layout

```
src/Ribbit.Geometry   Revit-free logic: solvers, planners, clash records, xlsx read/write
src/Ribbit.Native     the add-in: ribbon, commands, Revit engines, WPF windows
tests/                unit tests for both
packaging/            release packaging and install guide
docs/                 design notes (Federated Clash plan)
```

## License

MIT. See [LICENSE](LICENSE).
