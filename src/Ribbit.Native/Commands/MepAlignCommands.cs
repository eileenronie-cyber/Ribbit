using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Ribbit.Native.Tools;

namespace Ribbit.Native.Commands;

/// <summary>
/// The C# equivalents of the four pushbutton scripts under
/// _Ribbit.tab/Tools.panel. They stay as thin as the .py shims were — the
/// engines hold the logic, and each command exists only to name a mode.
///
/// Transactions are opened inside the engines (each pick/act cycle gets its
/// own), so these are TransactionMode.Manual.
/// </summary>
public abstract class MepAlignCommandBase : IExternalCommand
{
    protected abstract void Invoke(UIDocument uidoc);

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument? uidoc = commandData.Application.ActiveUIDocument;
        if (uidoc is null)
        {
            message = "There is no active Revit document.";
            return Result.Failed;
        }

        Invoke(uidoc);
        return Result.Succeeded;
    }
}

/// <summary>Move Connect Align — single pair, then exits on its own.</summary>
[Transaction(TransactionMode.Manual)]
public class MoveConnectAlignCommand : MepAlignCommandBase
{
    protected override void Invoke(UIDocument uidoc) => MoveConnectEngine.Run(uidoc, multiple: false);
}

/// <summary>Move Connect Align Multi — keeps connecting pairs until Esc.</summary>
[Transaction(TransactionMode.Manual)]
public class MoveConnectAlignMultiCommand : MepAlignCommandBase
{
    protected override void Invoke(UIDocument uidoc) => MoveConnectEngine.Run(uidoc, multiple: true);
}

/// <summary>Align Branch — single pair, then exits on its own.</summary>
[Transaction(TransactionMode.Manual)]
public class AlignBranchCommand : MepAlignCommandBase
{
    protected override void Invoke(UIDocument uidoc) => AlignBranchEngine.Run(uidoc, multiple: false);
}

/// <summary>Align Branch Multi — keeps aligning branches until Esc.</summary>
[Transaction(TransactionMode.Manual)]
public class AlignBranchMultiCommand : MepAlignCommandBase
{
    protected override void Invoke(UIDocument uidoc) => AlignBranchEngine.Run(uidoc, multiple: true);
}

/// <summary>Disconnect — the reverse of Move/Connect/Align.</summary>
[Transaction(TransactionMode.Manual)]
public class DisconnectCommand : MepAlignCommandBase
{
    protected override void Invoke(UIDocument uidoc) => DisconnectEngine.Run(uidoc);
}

/// <summary>Elbow By Click — adds a 45/90-degree elbow in the direction you click.</summary>
[Transaction(TransactionMode.Manual)]
public class ElbowByClickCommand : MepAlignCommandBase
{
    protected override void Invoke(UIDocument uidoc) => ElbowByClickEngine.Run(uidoc);
}

/// <summary>Tee Into Main — connects a branch into a main with a tee fitting.</summary>
[Transaction(TransactionMode.Manual)]
public class TeeIntoMainCommand : MepAlignCommandBase
{
    protected override void Invoke(UIDocument uidoc) => TeeIntoMainEngine.Run(uidoc);
}

/// <summary>
/// Sprig Connect — builds upright sprinkler drops off a branch line.
///
/// Shift+Click opens just the settings screen, matching pyRevit's config.py
/// convention. Revit has no built-in equivalent, so the modifier is read
/// directly at command start.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class SprigConnectCommand : MepAlignCommandBase
{
    protected override void Invoke(UIDocument uidoc)
    {
        bool shift = (System.Windows.Input.Keyboard.Modifiers
                      & System.Windows.Input.ModifierKeys.Shift) != 0;

        if (shift)
            SprigConnectEngine.RunSettingsOnly();
        else
            SprigConnectEngine.Run(uidoc);
    }
}

/// <summary>
/// Drop Connect — builds pendent sprinkler drops off a branch line.
/// Shift+Click opens just the settings screen.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class DropConnectCommand : MepAlignCommandBase
{
    protected override void Invoke(UIDocument uidoc)
    {
        bool shift = (System.Windows.Input.Keyboard.Modifiers
                      & System.Windows.Input.ModifierKeys.Shift) != 0;

        if (shift)
            DropConnectEngine.RunSettingsOnly();
        else
            DropConnectEngine.Run(uidoc);
    }
}

/// <summary>
/// Slope Through — puts one developed slope on a branch line, straight or
/// turning corners. Shift+Click opens just the settings screen (shared with
/// Slope Branch).
/// </summary>
[Transaction(TransactionMode.Manual)]
public class SlopeThroughCommand : MepAlignCommandBase
{
    protected override void Invoke(UIDocument uidoc)
    {
        bool shift = (System.Windows.Input.Keyboard.Modifiers
                      & System.Windows.Input.ModifierKeys.Shift) != 0;

        if (shift)
            SlopeThroughEngine.RunSettingsOnly();
        else
            SlopeThroughEngine.Run(uidoc);
    }
}

/// <summary>
/// Slope Armover — slopes one or several armovers, one pick each. The tee end
/// is detected rather than clicked. Shift+Click opens just the settings screen
/// (shared with Slope Branch).
/// </summary>
[Transaction(TransactionMode.Manual)]
public class SlopeArmoverCommand : MepAlignCommandBase
{
    protected override void Invoke(UIDocument uidoc)
    {
        bool shift = (System.Windows.Input.Keyboard.Modifiers
                      & System.Windows.Input.ModifierKeys.Shift) != 0;

        if (shift)
            SlopeArmoverTool.RunSettingsOnly();
        else
            SlopeArmoverTool.Run(uidoc);
    }
}

/// <summary>SheetMaker — create or update sheets from an Excel workbook.</summary>
[Transaction(TransactionMode.Manual)]
public class SheetMakerCommand : MepAlignCommandBase
{
    protected override void Invoke(UIDocument uidoc) => SheetMakerTool.Run(uidoc);
}

/// <summary>Export Sheets — write the sheets and chosen parameters to an Excel sheet list.</summary>
[Transaction(TransactionMode.Manual)]
public class SheetExportCommand : MepAlignCommandBase
{
    protected override void Invoke(UIDocument uidoc) => SheetExportTool.Run(uidoc);
}

/// <summary>Place Views on Sheets — assign a view to each sheet and place them.</summary>
[Transaction(TransactionMode.Manual)]
public class PlaceViewsOnSheetsCommand : MepAlignCommandBase
{
    protected override void Invoke(UIDocument uidoc) => PlaceViewsTool.Run(uidoc);
}

/// <summary>Number Text Notes — auto-number the paragraphs of text notes (was NumberTextNotes v6.dyn).</summary>
[Transaction(TransactionMode.Manual)]
public class NumberTextNotesCommand : MepAlignCommandBase
{
    protected override void Invoke(UIDocument uidoc) => NumberNotesTool.Run(uidoc);
}

/// <summary>Room Tag Checker — tag linked rooms a plan is missing, delete duplicate tags (was Room Tag Checker v3.dyn).</summary>
[Transaction(TransactionMode.Manual)]
public class RoomTagCheckerCommand : MepAlignCommandBase
{
    protected override void Invoke(UIDocument uidoc) => RoomTagTool.Run(uidoc);
}

/// <summary>Copy View Filters — filters and their overrides between templates or models (was Filter v2 / Filter_Revit_to_Revit_v2.dyn).</summary>
[Transaction(TransactionMode.Manual)]
public class CopyViewFiltersCommand : MepAlignCommandBase
{
    protected override void Invoke(UIDocument uidoc) => FilterCopyTool.Run(uidoc);
}

/// <summary>Part Sheets Duplicator — create Overall + dependent views from Excel.</summary>
[Transaction(TransactionMode.Manual)]
public class PartSheetsDuplicatorCommand : MepAlignCommandBase
{
    protected override void Invoke(UIDocument uidoc) => PartSheetsDuplicatorTool.Run(uidoc);
}

/// <summary>Adjust Grids — stretch grids to the crop box, set bubbles (was Grid_v2.dyn).</summary>
[Transaction(TransactionMode.Manual)]
public class AdjustGridsCommand : MepAlignCommandBase
{
    protected override void Invoke(UIDocument uidoc) => GridAdjustTool.Run(uidoc);
}

/// <summary>
/// The three Set Clash Status buttons. Same tool, three products — each reads
/// and writes only its own search button's CSVs.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class SetClashStatusCommand : MepAlignCommandBase
{
    protected override void Invoke(UIDocument uidoc)
        => SetClashStatusTool.Run(uidoc, Settings.ClashProduct.Cdmep);
}

/// <summary>Set Clash Status for the Clash Detect Link results.</summary>
[Transaction(TransactionMode.Manual)]
public class SetClashStatusLinkCommand : MepAlignCommandBase
{
    protected override void Invoke(UIDocument uidoc)
        => SetClashStatusTool.Run(uidoc, Settings.ClashProduct.DetectLink);
}

/// <summary>Set Clash Status for the MEP vs Structure results.</summary>
[Transaction(TransactionMode.Manual)]
public class SetClashStatusMepVsStructureCommand : MepAlignCommandBase
{
    protected override void Invoke(UIDocument uidoc)
        => SetClashStatusTool.Run(uidoc, Settings.ClashProduct.MepVsStructure);
}

/// <summary>Clash Detection — host MEP against linked-model categories.</summary>
[Transaction(TransactionMode.Manual)]
public class ClashMepVsStructureCommand : MepAlignCommandBase
{
    protected override void Invoke(UIDocument uidoc) => ClashMepVsStructureTool.Run(uidoc);
}

/// <summary>Clash Detect Link — host MEP against keyword-matched link MEP.</summary>
[Transaction(TransactionMode.Manual)]
public class ClashDetectLinkCommand : MepAlignCommandBase
{
    protected override void Invoke(UIDocument uidoc) => ClashDetectLinkTool.Run(uidoc);
}

/// <summary>
/// Federated Clash — both sides searched across the host model and every ticked
/// link at once, over the whole model rather than the active view.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class FederatedClashCommand : MepAlignCommandBase
{
    protected override void Invoke(UIDocument uidoc) => FederatedClashTool.Run(uidoc);
}

/// <summary>
/// Set Clash Status for Federated Clash results.
///
/// The same editor as the other three — only the product descriptor differs,
/// which is what points it at Federated's own ledger rather than theirs.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class SetClashStatusFederatedCommand : MepAlignCommandBase
{
    protected override void Invoke(UIDocument uidoc)
        => SetClashStatusTool.Run(uidoc, Settings.ClashProduct.Federated);
}

/// <summary>
/// About — who published this add-in, and which build is loaded.
///
/// Not derived from MepAlignCommandBase: that base bails out when there is no
/// active document, and "who made this?" is answerable with no project open.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class AboutCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        AboutTool.Run();
        return Result.Succeeded;
    }
}

/// <summary>Clash Detect — host MEP against itself, no links involved.</summary>
[Transaction(TransactionMode.Manual)]
public class ClashDetectCommand : MepAlignCommandBase
{
    protected override void Invoke(UIDocument uidoc) => ClashDetectTool.Run(uidoc);
}
