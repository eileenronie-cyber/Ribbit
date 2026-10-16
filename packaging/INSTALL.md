# Installing Ribbit

Windows. **One package per Revit version** — the folder name says which one
(`Ribbit-2025-...` is for Revit 2025). They are different binaries, not the same
files in different folders: Revit 2024 runs on .NET Framework 4.8, Revit 2025 on
.NET 8, and Revit 2026 on .NET 10 — three different runtimes, and nothing spans
any of them.

Installing the wrong one gives you no Ribbit tab and no error.

## Install

Copy **both** of these into your Revit add-ins folder:

- `Ribbit.Native.addin`
- the `Ribbit\` folder beside it

The destination is the folder for **your** Revit version:

```
%APPDATA%\Autodesk\Revit\Addins\2024\
%APPDATA%\Autodesk\Revit\Addins\2025\
%APPDATA%\Autodesk\Revit\Addins\2026\
```

Paste that path into File Explorer's address bar and it will take you there.
When you are done it should look like:

```
Addins\2025\
    Ribbit.Native.addin
    Ribbit\
        Ribbit.Native.dll
        Ribbit.Geometry.dll
        ...
```

Start Revit. A **Ribbit** tab appears on the ribbon.

**Keep the two together.** The `.addin` file finds the add-in through a path
relative to itself, so the `Ribbit\` folder must sit beside it and keep its
name. Copying only the `.addin` file installs nothing at all.

## Uninstall

Delete `Ribbit.Native.addin` and the `Ribbit\` folder, then restart Revit.

Your settings, sets, saved tests and clash reports are stored elsewhere and are
NOT removed by uninstalling — see below.

## Where your data lives

| What | Where |
|---|---|
| Sets and saved tests | `%APPDATA%\Ribbit\` |
| Clash reports, status ledger, notes | the CSV folder you choose in the tool |

## Upgrading

Replace the `Ribbit\` folder with the new one and restart Revit. Your settings,
sets, saved tests and triage are untouched — none of it lives with the add-in.

Revit holds the add-in files open while it is running, so **close Revit first**
or the copy will be refused.

## Sharing a CSV folder

Two people **can** work against the same CSV folder. Statuses and notes merge:
each save writes only its own changes, so nobody's work is overwritten by
somebody else's save. The same holds for the New-badge baselines, which are kept
one file per saved test.

Two things still do not travel:

- **Sets and saved tests are per person**, in `%APPDATA%\Ribbit`. Your colleague
  starts with the six built-in sets and no tests. Use **Manage sets ▸ Export...**
  to hand them a plain-text list of your sets' rules (no ids — importing creates
  sets their library owns) and **Import...** on their side to bring them in;
  Update keeps a same-named set's id so any saved test already pointing at it
  picks up the new rules. Copying `clash_sets.json` / `clash_tests.json` directly
  also works, but the two copies then drift with no way to reconcile them —
  Export/Import is the supported path.
- **Running the same saved test** updates that test's "last run" baseline for
  everyone using the folder. Whoever ran it last decides what counts as new. That
  is the honest answer for a shared test, but it means your New badges can be
  cleared by somebody else's run.

## Known limits

- **The other three clash tools split by model title.** Federated Clash resolves
  a model opened as a local, detached, or straight from central to one name.
  Clash Detect, Clash Detect Link and MEP vs Structure do not, so opening the
  same model two different ways gives those three separate ledgers.
- **Updates are a manual copy** with Revit closed. There is no auto-update.
  Check **Ribbit ▸ About** for the exact build someone is running.
