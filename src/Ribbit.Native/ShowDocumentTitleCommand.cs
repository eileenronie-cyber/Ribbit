using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Ribbit.Native;

[Transaction(TransactionMode.ReadOnly)]
public class ShowDocumentTitleCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument? uiDocument = commandData.Application.ActiveUIDocument;
        if (uiDocument is null)
        {
            message = "There is no active Revit document.";
            return Result.Failed;
        }

        Document document = uiDocument.Document;
        TaskDialog.Show("Ribbit", $"Active document: {document.Title}");

        return Result.Succeeded;
    }
}
