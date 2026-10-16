# Federated clash — plan for review

**The idea (yours):** a set collects elements without caring whether they live in
the host model or a link, the way Navisworks does. Sets are created once, saved,
and imported into a clash run.

This document separates **what is verified** from **what is still assumption**, so
we argue about the right things before writing code.

Status: **planning only. Nothing built.**

---

## 0. Decision — a FOURTH tool, not a change to the three

The three existing clash buttons are built, deployed and verified in Revit. They
stay exactly as they are. The federated idea ships as **its own pushbutton with
its own product tag and its own ledger**.

This is not a compromise, it is what makes the work safe. Every risk below that
involved *collapsing* the three tools drops off the critical path:

| Risk | Status under "fourth tool" |
|------|----------------------------|
| R5 ledger identity | **Gone.** A fourth `ClashProduct` means a fourth `{Model}_ClashFederated_ledger.csv`. Nothing is orphaned, nothing is migrated. |
| R4 reader parity *across* tools | **Reduced.** The new tool picks one reader combination and states it. The three keep theirs. |
| Q1 view scope | **Answerable without risk** — see below. |
| Rev-5 "one window, three buttons" | **Dropped.** Not needed, and it touched working code. |

### What this unlocks: model scope, for free

The three tools call `ClashGeometry.GetSafeActiveView(doc)` and pass the real
active view into the engine, so a bounding box is view-specific and anything not
visible in that view is skipped. Changing that would move every existing clash
count.

The new tool simply **passes `null`**, which gives model-wide bounding boxes —
true federated semantics, Navisworks-like, and **zero change to the three**.
That is a decision I could not have made safely inside the existing tools.

### What the new tool shares vs what is new

| Shared, already built and tested | New |
|----------------------------------|-----|
| `ClashEngine` (already federated — V1) | One window: federation list + two sides |
| `ClashGeometry`, transforms (V3, V4) | A `ClashProduct` descriptor + its ledger |
| `ClashParameterReader`, set builders | A tool class + ribbon button + icon |
| **The set library** — sets built for the three work here | Source filtering during set building |
| Report table, status pills, CSV, timing | |

The set library being already shared is the reason your idea lands cheaply: a set
is portable by construction.

---

## 1. Verified — checked, not remembered

| # | Fact | How verified |
|---|------|--------------|
| V1 | **The clash engine is already federated.** `ClashEngine.Run` looks up `e.Document` in the link map for **Set A** and transforms its bounding box by each instance transform, exactly as it does for Set B via `InstancesFor`. | Read `Revit/ClashEngine.cs` lines 96–130 and 177–185 |
| V2 | **Set A being host-only is a set-*building* limit, not an engine limit.** Nothing in the engine assumes A is host. | Same as V1 |
| V3 | **We use `GetTotalTransform()`**, which includes the true-north rotation. `GetTransform()` would not, and a link with a different true north would land geometry in the wrong place. | `Revit/ClashLinks.cs:215`; [GetTotalTransform docs](https://www.revitapidocs.com/2024/8c8aff2b-5ff9-e43a-3b5c-308cd0174f1f.htm) |
| V4 | **The same linked file placed twice is already handled** — the transform map stores a *list* of (instance, transform) per document, and the engine loops it, producing a box per placement. | `Revit/ClashLinks.cs:219–221`, `ClashEngine.cs:126` |
| V5 | **Navisworks search sets are criteria-based, dynamic, and importable/exportable across projects.** Selection sets are static and project-bound. Our sets are search sets. | [Understanding Navisworks Search Sets](https://forums.autodesk.com/t5/community-blog-aec-english/understanding-navisworks-search-sets/ba-p/13992613) |
| V6 | **Navisworks clash *tests* are themselves exportable as XML** and reused across projects — which is the "saved tests" idea, and it is standard practice, not an invention. | [All About Clash Detection with Navisworks](https://www.united-bim.com/get-to-know-all-about-clash-detection-with-navisworks/) |
| V7 | **Category filters are cheap; parameter reads are not.** Revit "quick" filters execute on element header information; "slow" filters must load the full element. Native filters should be applied before any LINQ. | [Quick, Slow and LINQ Element Filtering](https://jeremytammik.github.io/tbc/a/1382_filter_shortcuts.html), [Filtered Element Collectors](https://jeremytammik.github.io/tbc/a/0463_filtered_element_collector.htm) |
| V8 | **View-based collectors have a real first-use cost**, especially the first time for a given view or after its display settings change. | [Benchmarking Element Iteration Collectors](https://jeremytammik.github.io/tbc/a/0333_collector_benchmark.htm) |

**What V1 + V2 mean:** the expensive part of "federated" is already built and
tested. This is a smaller job than it looks.

---

## 2. Not verified — the actual risk list

| # | Risk | Why it matters | How to settle it |
|---|------|----------------|------------------|
| R1 | **Active-view scoping.** `ClashEngine.Run` takes `activeView` and passes it to `GetBoundingBox`. A view-specific bounding box is null for anything not visible in that view, and those elements are counted `aUnknown` and skipped. | A "federated" run that silently only clashes what is visible in the current view is not what anyone means by federated. This may already be surprising today. | Decide the semantics (see Q1). Test: run the same sets from a 3D view showing everything vs a cropped view, compare counts. |
| R2 | **Scan cost across many links is unmeasured.** Value and keyword rules need a parameter read per candidate element, per document. | "Everywhere by default" could turn a 5-second Browse into a minute on a big federation. | Measure before defaulting to everywhere. The report already prints Scan/Clash/Total. |
| R3 | **Two placements of one link — do they stay distinct in the report?** The engine makes a box per placement, but the clash pair key includes the link *instance*, not obviously the placement. | If they collapse, a genuine clash at one of two placements could be silently halved or mis-attributed. | Find a model with a twice-placed link, or place one deliberately, and check the row count. |
| R4 | **Reader parity.** MEP vs Struc reads its match parameters through the paired readers (System Type type-first, Description instance-first); the other two use `FindParameter` for both. A set does not currently record which reader it wants. | A value picked in one tool may silently match nothing in another once one set is shared by both. | Store the reader **with the rule**. Until then, do not share a value set across tools that read differently. |
| R5 | **Ledger identity.** Status lives in `{Model}_{Tag}_ledger.csv`, one per product, merged by prefix. | Any collapse of the three tools has to answer "which ledger" without orphaning existing triage or the CSVs the Python tools wrote. | Saved tests carry the tag. Until then, keep the three products. |
| R6 | **Unloaded and workset-closed links.** A link can be present, tickable and contribute nothing. | Already dimmed in the UI, but a federated default means it matters more — "everywhere" quietly means "everywhere that happens to be loaded". | State the loaded count in the read-back and the report. |

---

## 3. Design decisions proposed

**D1 — Source is a filter, not a mode.** A set is evaluated across the federation.
An optional source filter inside it: *everywhere* (default), *host only*, or
*named links*. This replaces the per-side scope dropdown from rev 5.

**D2 — Do NOT copy Navisworks' default.** Defaulting both sides to "everywhere"
produces architecture-vs-structure clashes nobody asked for, plus every link
against every other link. Default **A = this model, B = everything else**; make
"everywhere" a deliberate choice.

**D3 — Cost follows the rule kind.** Category rules are quick filters (V7) and can
span every loaded link cheaply. Value and keyword rules are the expensive half and
stay lazily scanned and cached per document, as they already are. The window shows
how many sources a run will scan.

**D4 — The federation list lives at the top of the window, once.** "This model +
4 loaded links", ticked, unloaded ones dimmed — the way Navisworks shows appended
files. Not duplicated per side.

**D5 — Source filters are expressed by ROLE, not by file name.** *host* / *any
link* / *named link*. A set that names a link still travels to another project; it
just warns instead of silently matching nothing. This is what makes V5-style
portability real.

**D6 — Noise filtering becomes per-pair.** "Same document → drop self-pairs,
insulation-host pairs, connector-joined pairs, and collapse mirrored duplicates."
The engine already knows each element's document, so this stops being a per-run
mode you could get wrong by pressing the wrong button.

---

## 3b. DECISIONS TAKEN (2026-08-12)

| # | Decision |
|---|----------|
| Name | **Federated Clash** |
| Sources | **Everywhere on both sides.** Rationale (user): sets constrain *what* is selected, so "everywhere" widens the SOURCES, not the selection. Accepted — the noise I warned about only appears with unconstrained sets. D2 is overruled deliberately. |
| Link-vs-link | Falls out of everywhere-on-both-sides. No extra UI. |
| Status editor | Not now. The report edits status inline. |

### The consequence of "everywhere on both sides"

Both sides now draw from overlapping pools, so the same element can appear on
both — exactly the condition that made Clash Detect need a noise filter. **The
federated tool needs noise filtering from day one**, not as a later step. It moves
from step 4 to step 1.

### V9 — FOUND WHILE PLANNING: the noise filter is not document-safe

`ClashNoiseFilter` keys on the **raw ElementId with no document component**
(`RevitCompat.IdKey(cp.A.Id)`, lines 142–143 and 156). Correct for Clash Detect,
which is single-document. Under federation it fails in the dangerous direction:

- `aId == bId` treats a host element and a **different** linked element that
  happens to share an ElementId as "the same element", and **silently drops a real
  clash**. ElementIds are per-document, so collisions are routine.
- `ClashPairKey.Symmetric(aId, bId)` dedup has the same flaw — two genuinely
  different cross-document pairs can collide, and one is discarded.
- `InsulationHostId` and `ConnectedOwnerIds` compare ids across documents, where
  neither relationship can structurally exist.

**Fix:** a document-aware variant keyed on `(Document, ElementId)` — the same key
`ClashSetBuilder.DedupeById` already uses, whose comment says never to use a bare
id. The insulation and connector tests must additionally be **skipped unless both
elements are in the same document**, because those relationships cannot span one.

Do NOT "fix" the existing `ClashNoiseFilter` in place — Clash Detect is verified
and single-document, and a shared change is a change to a working tool. The
federated tool gets its own.

---

## 4. Open questions — smaller now

**Q1 — RESOLVED by section 0.** The new tool is model-scoped (`activeView: null`).
The three keep their view-scoped behaviour. No existing count moves.

**Q2 — Default sides.** Proposed: `A = this model`, `B = every loaded link`. Both
overridable. Copying Navisworks' "everywhere on both sides" default would produce
architecture-vs-structure clashes nobody asked for, plus every link against every
other link — so we deliberately do not.

**Q3 — Link-vs-link.** The engine already allows it (V1) and the UI costs nothing
extra once each side picks its sources. Proposed: allow it, do not feature it.

**Q4 — Name and ribbon home.** It needs a button label and a panel. Candidates:
*Clash All Models*, *Federated Clash*, *Clash Anywhere*. Which panel — the
existing Ribbit CD tab, presumably a fourth panel.

**Q5 — Status editor.** The three each have a *Set Clash Status* button. The new
tool's report already edits status inline, so a standalone editor can wait. Add a
fourth status button later, or not at all?

---

## 5. Build order

Each step independently deployable and revertable. **None of these touch the three
existing tools**, so a rollback is always "delete the new button".

| Step | Work | Risk to existing tools |
|------|------|------------------------|
| 1 | Fourth `ClashProduct` + tool + ribbon button. Window = federation list + two sides (sets + keywords). Model-scoped. Collect from host **and** every ticked link for BOTH sides. **Document-aware noise filter (V9) ships here** — not optional, since both sides overlap. | **None** — new files only |
| 2 | Import / export sets as JSON (V5 parity). Shared, so the three benefit too. | Additive only |
| 3 | Optional per-set source filter by role (D1/D5), if "everywhere" proves too broad in practice. | None |
| 4 | Store the reader with the rule (R4), if sharing sets across tools proves lossy. | Deferred until measured |

---

## 6. What to test in Revit, per step

- **Step 1:** the new tool with `A = this model, B = links` should roughly
  reproduce a Clash Detect Link run — **but not exactly**, because it is
  model-scoped where the old one is view-scoped. Expect the new count to be
  **higher**, and confirm the difference is elements hidden in the active view.
  That comparison is the single best check that model scope works.
- **Step 2:** same set as host-only vs everywhere — counts differ only by the link
  contribution. Then a twice-placed link (R3).
- **Step 3:** export a set, delete it, re-import, run — identical count.
- **Step 4:** a host-vs-host run reports each clash **once**; a host-vs-link run
  does not lose mirrored pairs that are genuinely two different elements.

---

## 7. Still-live risks for the NEW tool

R1 is resolved. R4 and R5 are off the critical path. These remain:

- **R2 scan cost** across many links — unmeasured. Mitigate by scanning lazily per
  document with progress, and read the Scan/Clash/Total line the report prints.
- **R3 twice-placed link** — verify the two placements report distinctly.
- **R6 unloaded links** — "everywhere" quietly means "everywhere loaded". State the
  loaded count in the read-back and in the report.

---

## 9. Scan performance — the federated multiplier

Federation multiplies the scan by the number of sources, so this is the thing most
likely to make the tool unusable. What is already fast, what is not, and what to
do — ordered by expected payoff, **not** by how clever it is.

### Already optimised (verified in code, do not redo)

| | |
|---|---|
| **Category collection** | `ClashCategories.CollectByCategoryFast` already uses ONE `ElementMulticategoryFilter` pass and buckets afterwards, instead of 24 collectors. `OfCategory` is a *quick* filter (header-only, V7), so collection is cheap even across many links. |
| **Reader caches are document-aware** | Every cache key in `ClashParameterReader` carries the Document, never a bare id — the exact bug V9 found in the noise filter. |
| **Type-level caching** | A genuine type-parameter value, and "name absent", are cached per type. Only a value found by the instance scan is never type-cached, because it can differ per instance. |
| **Description scoping** | The cable-tray-only rule already skips the Description read on most elements. This was a real saving, not just a semantic fix. |
| **Progress throttling** | `ProgressThrottle` caps repaints at ~100 per run. A per-element `Dispatcher.Invoke` was previously the dominant cost of a "scan". |

### The one that cannot be done

**No parallelism.** The Revit API is single-threaded; element access must happen on
the main thread. This is the instinctive fix and it is not available. Anything
promising a threaded scan is wrong.

### Proposed optimisations, highest payoff first

**P1 — Read only the parameters the rules actually name.**
Today the run reads BOTH System Type and Description off every element. A set made
only of `[System Type]` values — which is exactly the "52 Series" shape — needs
System Type alone. Halves the reads for value-only runs.
*Caveat: keyword rules match against either parameter, so a keyword forces both.
The Browse picker also legitimately needs both, since it offers you the choice.*

**P2 — Skip the parameter scan entirely when no rule needs it.**
A category-only set needs zero parameter reads. Decide before scanning whether any
side references a value or keyword; if not, collect and go straight to the clash.

**P3 — Fast-path the name lookup.**
`ParamNameMap` builds a normalized-name dictionary of *every* parameter on an
element. For two known names, try `LookupParameter(exactName)` first and fall back
to the normalized map only on a miss. Preserves the tolerant matching that exists
for a reason, while paying for it only when it is needed.

**P4 — Collect each document once, reuse for both sides.**
Set A and Set B both draw from every ticked source. Bucket per document once and
share — the existing tools already cache buckets this way; the federated one must
not regress it by collecting per side.

**P5 — Scan lazily, per document, and cache per (document, rule).**
Already the pattern for Browse. Extend it so a link that no rule touches is never
scanned at all.

### Measure before and after — non-negotiable

The report already prints **Scan / Clash / Total**. For the federated tool that is
not enough: one slow link would be invisible inside one number. Add **per-source
timing** to the report:

```
Scan:  Host 0.8s · ST-Structure 2.1s · AR-Architecture 6.4s   Total 9.3s
```

That line tells you which link is expensive, which is the only way to know whether
P1–P3 helped or whether the real cost is one badly-modelled link.

**Order of work: instrument first (per-source timing), measure a real federation,
then apply P1–P3 against numbers.** Optimising before measuring is how the wrong
half gets optimised — and P1 and P2 could be worth more than everything else
combined, or nothing at all, depending entirely on whether your sets use keywords.

---

## 9b. BUILT — 2026-08-12, step 1 complete

New files only. The three existing clash tools were not modified, except for one
provably behaviour-preserving optimisation (below).

| File | What |
|------|------|
| `Settings/ClashSettings.cs` | `ClashProduct.Federated` — tag `ClashFederated`, its own ledger |
| `Revit/FederatedNoiseFilter.cs` | **V9 fix.** Document-aware: every key is (Document, ElementId); insulation and connector tests skipped unless both elements share a document |
| `Revit/FederatedSources.cs` | `ClashSource` / `FederatedSources` — discovery, per-source bucket cache, **per-source timing** |
| `Ui/FederatedClashWindow.cs` | Federation list at the top, two identical set cards below |
| `Tools/FederatedClashTool.cs` | Orchestration, model-scoped (`activeView: null`) |
| `Commands/MepAlignCommands.cs` | `FederatedClashCommand` |
| `RibbitApplication.cs` | "Federated" panel on the Ribbit CD tab |
| `Resources/Icons/FederatedClash.png` | Placeholder — copied from Clash Detect Link, worth drawing properly |

### P1 applied — the one shared-code change

`ClashLinkSetBuilder.MatchKeywords` now reads a match parameter only when some
rule can use it: any keyword exists, or an exact pair names that parameter. A
value-only set of `[System Type]` values — the "52 Series" shape — halves its
parameter reads.

Behaviour-preserving by construction: a skipped value could only have reached
`ContainsKeyword` with an empty keyword list (always false) or a per-parameter
lookup that is never performed. All 362 tests still pass.

**P2 turned out to be already implemented** — `MatchKeywords` returns early when
there are no keywords and no exact pairs, so a category-only set already does zero
parameter reads. Nothing to do.

### Known limitation, shipped deliberately

`ClashSetBuilder.ScopeIdsFor` keys the stale-status table on **bare** ElementIds.
That is consistent with the ledger's own `Element Id` column, so it is not a new
bug — but in a federated run a host element and a link element sharing a raw id
can be conflated, making the stale table slightly imprecise. It affects **only**
which rows the stale table lists, never which clashes are found.

Not fixed here on purpose: `ScopeIdsFor` is shared with three verified tools, and
changing it would alter their stale tables for no benefit to them.

---

## 10. Note

This repository is still not a git repo, so every step above needs its own
`_backup` snapshot. For a sequence of five result-changing steps, `git init` first
would pay for itself immediately.

