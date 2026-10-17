using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;

namespace Ribbit.Native;

/// <summary>
/// Builds the ribbon that pyRevit used to generate from the folder layout:
/// _Ribbit.tab / Tools.panel / ConnectAlign.stack + BranchAlign.stack.
///
/// Titles and tooltips are carried over verbatim from each pushbutton's
/// __title__ / __doc__ so the buttons read identically to the ones the users
/// already know.
/// </summary>
public class RibbitApplication : IExternalApplication
{
    internal const string TabName = "Ribbit";

    /// <summary>The id a posted command is looked up by: <c>CustomCtrl_%CustomCtrl_%tab%panel%button</c>.</summary>
    internal static string CommandIdOf(string tab, string panel, string button)
        => $"CustomCtrl_%CustomCtrl_%{tab}%{panel}%{button}";

    public Result OnStartup(UIControlledApplication application)
    {
        application.CreateRibbonTab(TabName);
        RibbonPanel panel = application.CreateRibbonPanel(TabName, "Tools");

        string assemblyPath = Assembly.GetExecutingAssembly().Location;

        // ConnectAlign.stack
        panel.AddStackedItems(
            Button(assemblyPath, "MoveConnectAlign", "Connect",
                typeof(Commands.MoveConnectAlignCommand),
                "Aligns and connects ONE loose MEP element to a fixed one, then exits by itself - no Esc needed.\n\n"
                + "1st click: FIXED target element\n"
                + "2nd click: LOOSE element to move\n\n"
                + "Click near the connector end you want joined.\n"
                + "Use 'Connect Multi' to keep going until Esc."),
            Button(assemblyPath, "MoveConnectAlignMulti", "Connect Multi",
                typeof(Commands.MoveConnectAlignMultiCommand),
                "Aligns and connects loose MEP elements to fixed ones, over and over, until you press Esc.\n\n"
                + "1st click: FIXED target element\n"
                + "2nd click: LOOSE element to move\n"
                + "...then straight on to the next pair.\n\n"
                + "Esc at either prompt exits (one press).\n"
                + "Click near the connector end you want joined."));

        // BranchAlign.stack
        panel.AddStackedItems(
            Button(assemblyPath, "AlignBranch", "Align",
                typeof(Commands.AlignBranchCommand),
                "Aligns ONE loose branch pipe/duct to a fixed reference's centerline in full 3D, "
                + "then exits by itself - no Esc needed.\n\n"
                + "1st click: FIXED reference element\n"
                + "2nd click: BRANCH element to align\n\n"
                + "Rigid centerline-to-centerline translate only - no rotation, no trimming, no connection.\n"
                + "Use 'Align Multi' to keep going until Esc."),
            Button(assemblyPath, "AlignBranchMulti", "Align Multi",
                typeof(Commands.AlignBranchMultiCommand),
                "Aligns loose branch pipes/ducts to a fixed reference's centerline in full 3D, "
                + "over and over, until you press Esc.\n\n"
                + "1st click: FIXED reference element\n"
                + "2nd click: BRANCH element to align\n"
                + "...then straight on to the next pair.\n\n"
                + "Esc at either prompt exits (one press).\n"
                + "Rigid centerline-to-centerline translate only - no rotation, no trimming, no connection."));

        // Disconnect is a large button in the Python layout (its own
        // .pushbutton, not inside a .stack), so it stays unstacked here.
        panel.AddItem(
            Button(assemblyPath, "Disconnect", "Disconnect",
                typeof(Commands.DisconnectCommand),
                "Click on two connected MEP elements and this command will disconnect them."));

        panel.AddItem(
            Button(assemblyPath, "ElbowByClick", "Elbow\n(Click Dir)",
                typeof(Commands.ElbowByClickCommand),
                "Adds a 45 or 90-degree elbow in the direction you click, instead of picking from "
                + "separate per-direction buttons.\n\n"
                + "1st pick: pipe/duct/cable tray/conduit with the open dead-end\n"
                + "2nd click, in a 3D view: a dial on the open end shows every turn as a target - "
                + "teal turns sideways, orange goes up or down (a dot is up, a cross down), a solid "
                + "line is a 90 and a dashed one a 45. Click the one you want.\n"
                + "2nd click, in a plan or section: click toward the side to bend - perpendicular "
                + "for 90 degrees, halfway to straight-ahead for 45. Only the turns lying flat in "
                + "the view are offered; for a drop from a plan, use a 3D view.\n\n"
                + "There is no menu: a click that names no turn just asks again. 'Up' is always "
                + "true world Z, and a riser turns along the building's own axes (the run that "
                + "feeds it, or the grids)."));

        // PH_FP Tools.panel in the Python layout.
        RibbonPanel phfp = application.CreateRibbonPanel(TabName, "PH_FP Tools");

        phfp.AddItem(
            Button(assemblyPath, "TeeIntoMain", "Tee\nInto Main",
                typeof(Commands.TeeIntoMainCommand),
                "Trims/extends a branch into a main and actually places the tee - works on sloped "
                + "mains, where native Trim/Extend silently fails.\n\n"
                + "1st click: the MAIN run (pipe or duct)\n"
                + "2nd click: the BRANCH to connect\n"
                + "Repeats until Esc.\n\n"
                + "The branch's ANGLE is preserved - a plumb riser stays plumb and never picks up "
                + "the main's slope. The branch slides sideways onto the main's centerline and its "
                + "connecting end is trimmed/extended to meet it.\n\n"
                + "Needs 'Allow Slope Adjustments' CHECKED on the tee family's run connectors; if "
                + "Revit refuses the tee, the tool offers to retry with the branch tilted to "
                + "exactly 90 degrees instead."));

        phfp.AddItem(
            Button(assemblyPath, "SprigConnect", "Sprig\nConnect",
                typeof(Commands.SprigConnectCommand),
                "Builds upright sprinkler drops off a branch line.\n\n"
                + "1st pick: BRANCH LINE - the pipe the heads sit over, NOT the cross main "
                + "(one or more straight pipes)\n"
                + "2nd pick: HEADS (upright sprinklers)\n\n"
                + "Reports the tee station, DIRECT/ARMOVER type, armover and sprig lengths and "
                + "off-plumb lean for every head, then asks before building anything.\n\n"
                + "Sprigs are built PLUMB (tee'd directly under each head), even on a pitched "
                + "branch.\n\n"
                + "Cancel at the confirmation to use it as a read-only check.\n\n"
                + "Shift+Click to open just the settings screen."));

        phfp.AddItem(
            Button(assemblyPath, "DropConnect", "Drop\nConnect",
                typeof(Commands.DropConnectCommand),
                "Builds pendent sprinkler drops off a branch line, fed from the top of the pipe "
                + "through a return bend (riser + armover + drop).\n\n"
                + "1st pick: BRANCH LINE - the pipe the drops hang from, NOT the cross main "
                + "(one or more straight pipes)\n"
                + "2nd pick: HEADS (pendent sprinklers)\n\n"
                + "Reports the tee station, DIRECT/RETURN BEND type, riser, armover and drop "
                + "lengths and the clearance checks for every head, then asks before building "
                + "anything.\n\n"
                + "Both vertical pipes are built PLUMB even on a pitched branch.\n\n"
                + "Cancel at the confirmation to use it as a read-only check.\n\n"
                + "Shift+Click to open just the settings screen."));

        phfp.AddItem(
            Button(assemblyPath, "SlopeThrough", "Slope\nThrough",
                typeof(Commands.SlopeThroughCommand),
                "Slopes a whole branch line - straight, or THROUGH its elbows - in one pass.\n\n"
                + "1st pick: the branch. ONE pipe is enough; every leg is found by following the "
                + "run and the elbows. Window-select works too.\n"
                + "2nd pick: the end that must NOT MOVE. Click the cross main or its tee to PIN "
                + "it, or click the branch itself to just name the end - no mode to choose.\n\n"
                + "One developed slope across every corner, rising away from the fixed end. "
                + "Horizontal pipe only; sprigs and drops ride plumb.\n\n"
                + "Reports every elevation first and asks before moving anything - cancel there "
                + "to use it as a read-only check.\n\n"
                + "Shift+Click to open just the settings screen (shared with Slope Branch)."));

        phfp.AddItem(
            Button(assemblyPath, "SlopeArmover", "Slope\nArmover",
                typeof(Commands.SlopeArmoverCommand),
                "Puts a slope on one or several armovers - one pick each.\n\n"
                + "Pick ONE pipe per armover, Esc when done. Each armover's tee end is "
                + "detected automatically and held fixed, so the joint into the branch line "
                + "is undisturbed. The elbow, sprig and head ride along; the sprig stays "
                + "plumb.\n\n"
                + "Slope is typed in the dialog (shared with Slope Through): positive rises "
                + "away from the branch, negative falls toward the head.\n\n"
                + "One report, one confirmation, one transaction - one Ctrl+Z undoes the "
                + "whole batch.\n\n"
                + "Shift+Click to open just the settings screen."));

        RibbonPanel sheets = application.CreateRibbonPanel(TabName, "Sheets");

        sheets.AddItem(
            Button(assemblyPath, "SheetExport", "Export\nSheets",
                typeof(Commands.SheetExportCommand),
                "Write the model's sheets to an Excel sheet list, with the parameters you tick "
                + "here - chosen from the ones the model actually has, so every column matches a "
                + "real parameter.\n\n"
                + "Edit it in Excel (change values, renumber, add rows for new sheets), save, then "
                + "use Sheet Maker to bring the changes back."));

        sheets.AddItem(
            Button(assemblyPath, "SheetMaker", "Sheet\nMaker",
                typeof(Commands.SheetMakerCommand),
                "Create or update Revit sheets from an Excel workbook.\n\n"
                + "A sheet list made by Export Sheets is recognised: every change it would make is "
                + "listed - new sheets, renumbers, each value from and to - and nothing is written "
                + "until you press Apply. Values changed in Revit since the export are kept, not "
                + "overwritten.\n\n"
                + "Any other workbook opens the mapping form as before: pick which Excel column "
                + "fills which parameter, with a dry-run preview and titleblock choices."));

        sheets.AddItem(
            Button(assemblyPath, "PlaceViewsOnSheets", "Place Views\non Sheets",
                typeof(Commands.PlaceViewsOnSheetsCommand),
                "Give each view its sheet in one window (was Placing Views to Sheets v3.dyn).\n\n"
                + "Click a view and the sheets re-order by how well their number and name match it, best "
                + "first; clear matches are suggested and one click accepts them all. Views already on a "
                + "sheet are left out (legends can go on many). Placed at the title block centre or a point "
                + "you type.\n\n"
                + "A change guard rolls the whole run back if placing touches anything it should not. One "
                + "Ctrl+Z undoes it."));

        sheets.AddItem(
            Button(assemblyPath, "CopyViewFilters", "Copy View\nFilters",
                typeof(Commands.CopyViewFiltersCommand),
                "Copy filters and their V/G overrides from one view template to others - in this model, from "
                + "another open model, or from a .rvt on disk (opened detached, closed again after) "
                + "(was Filter v2 and Filter_Revit_to_Revit_v2.dyn).\n\n"
                + "Every filter is shown with what it carries and what it is in this model before anything is "
                + "written. Missing filters and patterns are brought across; a filter defined differently here "
                + "keeps its rules unless you choose to overwrite them. Several target templates at once."));

        sheets.AddItem(
            Button(assemblyPath, "PartSheetsDuplicator", "Part Sheets\nDuplicator",
                typeof(Commands.PartSheetsDuplicatorCommand),
                "Create or update Overall + dependent floor plan views from an Excel workbook "
                + "(Series, Floor Plan View Name, Phase, Template Name).\n\n"
                + "Load the worksheet to get one explicit base-view dropdown per level it "
                + "references, plus a dry-run preview and a skip/update choice for views that "
                + "already exist."));

        sheets.AddItem(
            Button(assemblyPath, "AdjustGrids", "Adjust\nGrids",
                typeof(Commands.AdjustGridsCommand),
                "Tidy grids in many views at once (was the Grid_v2 Dynamo graph).\n\n"
                + "STEP 1 converts grid ends to 2D, STEP 2 stretches them to the crop box (or the "
                + "view's scope box) plus offsets, STEP 3 puts every bubble on the chosen side, "
                + "STEP 4 spreads bubbles that clash onto an elbow. Or copy another view's grid "
                + "set-up, and optionally hide grids that are not in the view's set.\n\n"
                + "Only the grids each view actually draws are touched, and never a grid's 3D "
                + "extent. Scope box grid sets and settings are remembered per model."));

        RibbonPanel annotation = application.CreateRibbonPanel(TabName, "Annotation");

        annotation.AddItem(
            Button(assemblyPath, "NumberTextNotes", "Number\nText Notes",
                typeof(Commands.NumberTextNotesCommand),
                "Auto-number the paragraphs of text notes (general notes) - a Revit list, typed numbers, or "
                + "numbering removed (was NumberTextNotes v6.dyn).\n\n"
                + "Select notes first, or use Pick notes. Header lines are left alone, numbers typed by hand are "
                + "stripped first so re-running is safe, and the preview can be edited per note before anything "
                + "is written. One Ctrl+Z undoes it."));

        annotation.AddItem(
            Button(assemblyPath, "RoomTagChecker", "Room Tag\nChecker",
                typeof(Commands.RoomTagCheckerCommand),
                "Tag the linked rooms a plan view is missing, and delete the extra tags on rooms that carry "
                + "more than one (was Room Tag Checker v3.dyn).\n\n"
                + "Tick views, pick the link the rooms live in, and pair each level with the linked level its rooms "
                + "are on - paired by name where the models agree, never guessed where they do not. Only rooms inside "
                + "each view's crop. Check lists every tag and deletion before Apply writes anything; one Ctrl+Z "
                + "undoes it."));

        // Its own panel, at the end of the tab - the same place the pyRevit
        // extension put it, and where anyone looks for "what is this and who
        // made it?".
        RibbonPanel about = application.CreateRibbonPanel(TabName, "About");

        about.AddItem(
            Button(assemblyPath, "About", "Ribbit",
                typeof(Commands.AboutCommand),
                "Ribbit - MEP routing, sheet and clash-detection tools for Revit.\n\n"
                + "Published by John Ronie Torres.\n\n"
                + "Click to see the version, the build currently loaded, and what is on each "
                + "tab."));

        BuildClashTab(application, assemblyPath);

        return Result.Succeeded;
    }

    /// <summary>
    /// The clash tools live on their own tab, matching the source extension's
    /// separate _Ribbit[CD] tab — they are a different workflow from the MEP
    /// modelling tools and share nothing but the library underneath.
    /// </summary>
    /// <summary>
    /// Whether the three original clash buttons still appear.
    ///
    /// TURNED OFF 2026-08-16, as the trial step before deleting them. Federated
    /// Clash was measured against Clash Detect and MEP vs Structure on the real
    /// model and covers them: of 8 named sets, 6 build identically and the 2 that
    /// differ are a strict SUPERSET — Federated never misses an element they
    /// find. The difference is insulation, which Federated includes and they do
    /// not, and the duplicate rows that caused are now removed by
    /// <c>FederatedNoiseFilter</c> while the genuine "the pipe fits and its
    /// lagging does not" clashes are kept.
    ///
    /// HIDDEN, NOT DELETED, and this flag is the whole mechanism: a week of real
    /// use is the thing no measurement substitutes for, and flipping this back to
    /// true restores all three panels exactly as they were. Nothing on disk is
    /// touched either way — every CSV those tools ever wrote is still in the
    /// folder, so turning them back on restores access to all of it.
    ///
    /// Safe to hide their Set Clash Status buttons with them: their ledgers were
    /// measured first and hold ZERO statuses, so there is no triage to reopen.
    /// Everything ever flagged in this model belongs to Federated's own ledger.
    ///
    /// When they do go, the code to remove is: ClashDetectTool,
    /// ClashDetectLinkTool, ClashMepVsStructureTool, their three windows, their
    /// commands, and the three ClashProduct descriptors — but NOT ClashSetBuilder
    /// or ClashParameterReader, which Federated uses.
    /// </summary>
    /// <remarks>
    /// <c>static readonly</c> rather than <c>const</c> on purpose: a const false
    /// folds at compile time and the guarded call becomes unreachable code, which
    /// is a standing CS0162 — and a permanent warning is exactly where a real one
    /// goes unnoticed.
    /// </remarks>
    private static readonly bool ShowLegacyClashTools = false;

    private static void BuildClashTab(UIControlledApplication application, string assemblyPath)
    {
        const string clashTab = "Ribbit CD";
        application.CreateRibbonTab(clashTab);

        const string statusTooltip =
            "Flag clashes from the most recent report as Resolved, Pending or Coordination - "
            + "without opening Excel.\n\n"
            + "Reads the last full report written for this model and re-exports it with your "
            + "changes applied, so the saved file is always a complete current snapshot.\n\n"
            + "Never touches the model - CSV only. There is no way to clear a status back to "
            + "blank; change it to one of the other three instead.";

        if (ShowLegacyClashTools)
            BuildLegacyClashPanels(application, assemblyPath, clashTab, statusTooltip);

        // Its own panel, and now the only one: see ShowLegacyClashTools.
        RibbonPanel federated = application.CreateRibbonPanel(clashTab, "Federated");

        federated.AddItem(
            Button(assemblyPath, "FederatedClash", "Federated\nClash",
                typeof(Commands.FederatedClashCommand),
                "Clash across the host model AND its links at once, the way Navisworks "
                + "searches an appended scene.\n\n"
                + "Both sets are searched over every model you tick - host and links "
                + "together - so an element is found wherever it lives. Choose named sets "
                + "from the shared Set Library for each side, or type a one-off keyword.\n\n"
                + "Searches the whole model. Clearance is real here: a pair within the pad "
                + "but not touching is reported as a clearance clash.\n\n"
                + "Insulation is included in a set alongside the pipe or duct it wraps, so a "
                + "service that fits while its lagging does not is still found. Where both "
                + "clash with the same thing, only the host is listed.\n\n"
                + "Never modifies the model."));

        // Without this, statuses set inside a Federated report were unreachable
        // once the report window was closed — the other three panels each have
        // this button, and its absence here was the one real hole in the
        // workflow rather than a missing feature.
        federated.AddItem(
            Button(assemblyPath, "SetClashStatusFederated", "Set\nClash\nStatus",
                typeof(Commands.SetClashStatusFederatedCommand), statusTooltip));
    }

    /// <summary>
    /// The three original clash tools. Built only when
    /// <see cref="ShowLegacyClashTools"/> is on — kept whole and unedited so
    /// turning them back on is one word, not a reconstruction.
    /// </summary>
    private static void BuildLegacyClashPanels(
        UIControlledApplication application,
        string assemblyPath,
        string clashTab,
        string statusTooltip)
    {
        RibbonPanel detection = application.CreateRibbonPanel(clashTab, "Clash Detection");

        detection.AddItem(
            Button(assemblyPath, "ClashDetect", "Clash\nDetect",
                typeof(Commands.ClashDetectCommand),
                "Clash the host model against itself - no Revit Links involved.\n\n"
                + "Both sets are keyword searches over this model. Type a keyword, or use "
                + "Browse to pick exact values that exist. The force-include checkboxes add "
                + "whole categories into Set B regardless of keywords - Set A is never "
                + "affected.\n\n"
                + "Pairs that overlap by design are suppressed: an element against itself, "
                + "against its own insulation, or against something it is directly joined "
                + "to.\n\n"
                + "Writes a CSV report that doubles as the status ledger the Set Clash Status "
                + "button reads. Never modifies the model."));

        detection.AddItem(
            Button(assemblyPath, "SetClashStatus", "Set\nClash\nStatus",
                typeof(Commands.SetClashStatusCommand), statusTooltip));

        RibbonPanel detectionLink = application.CreateRibbonPanel(clashTab, "Clash Detection Link");

        detectionLink.AddItem(
            Button(assemblyPath, "ClashDetectLink", "Clash\nDetect\nLink",
                typeof(Commands.ClashDetectLinkCommand),
                "Clash host MEP against MEP inside one or more Revit Links.\n\n"
                + "BOTH sets are keyword searches here - Set A over the host model, Set B "
                + "inside the link(s) you check. Type a keyword, or use Browse to pick exact "
                + "values that exist: Set A browses the host, Set B browses whichever links "
                + "are ticked.\n\n"
                + "The force-include checkboxes add whole categories from the checked links "
                + "into Set B regardless of keywords - Set A is never affected.\n\n"
                + "Writes a CSV report that doubles as the status ledger the Set Status Link "
                + "button reads. Never modifies the model."));

        detectionLink.AddItem(
            Button(assemblyPath, "SetClashStatusLink", "Set\nStatus\nLink",
                typeof(Commands.SetClashStatusLinkCommand), statusTooltip));

        RibbonPanel mepVsStruct = application.CreateRibbonPanel(clashTab, "MEP vs Structure");

        mepVsStruct.AddItem(
            Button(assemblyPath, "ClashMepVsStruct", "Clash\nMEP vs\nStruc",
                typeof(Commands.ClashMepVsStructureCommand),
                "Clash host MEP against categories inside one or more Revit Links.\n\n"
                + "Set A is matched on System Type or Description - type a keyword, or use "
                + "Browse to pick exact values that exist in the model.\n"
                + "Set B is chosen as link/category combinations, so several categories from "
                + "one link and different categories from two links both work.\n\n"
                + "Writes a CSV report that doubles as the status ledger the Set Clash Status "
                + "button reads. Never modifies the model."));

        mepVsStruct.AddItem(
            Button(assemblyPath, "SetClashStatusMepVsStruct", "Set\nClash\nStatus",
                typeof(Commands.SetClashStatusMepVsStructureCommand), statusTooltip));
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        return Result.Succeeded;
    }

    private static PushButtonData Button(
        string assemblyPath, string name, string text, Type commandType, string tooltip)
        => new(name, text, assemblyPath, commandType.FullName)
        {
            ToolTip = tooltip,

            // Both sizes come from the one 96x96 source. Revit picks LargeImage
            // for a normal button and Image for a stacked or small one, and a
            // button with only one of them set falls back to a blank slot in the
            // other layout - which is exactly what the stacked Connect/Align
            // pairs would hit.
            LargeImage = LoadIcon(name, 32),
            Image = LoadIcon(name, 16),
        };

    /// <summary>
    /// The embedded icon for a button, downscaled from the 96x96 source, or null
    /// if there isn't one.
    ///
    /// Returning null is deliberate and must stay that way: this runs inside
    /// OnStartup, so letting an icon problem throw would cost the whole ribbon —
    /// every button, over one missing PNG. A button with no icon is a cosmetic
    /// fault; a button that never appears is not.
    /// </summary>
    private static System.Windows.Media.ImageSource? LoadIcon(string name, int pixels)
    {
        try
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            string resource = $"Ribbit.Native.Resources.Icons.{name}.png";

            using Stream? stream = assembly.GetManifestResourceStream(resource);
            if (stream is null)
                return null;

            BitmapImage image = new();
            image.BeginInit();

            // The stream is disposed as soon as this method returns, so the
            // bitmap has to finish reading it here rather than lazily.
            image.CacheOption = BitmapCacheOption.OnLoad;

            // Decoding straight to the target size scales far better than
            // letting the ribbon squash a 96px bitmap into a 16px slot.
            image.DecodePixelWidth = pixels;
            image.DecodePixelHeight = pixels;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();

            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
