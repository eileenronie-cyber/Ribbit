using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Ribbit.Geometry.Views;
using Ribbit.Native.Revit;
using Ribbit.Native.Settings;
using Ribbit.Native.Ui;

namespace Ribbit.Native.Tools;

/// <summary>
/// Copy View Filters: one tool for "Filter v2.dyn" (between templates of this
/// model) and "Filter_Revit_to_Revit_v2.dyn" (from another .rvt). A model this
/// run opened is closed again when the run ends, whatever the outcome - nothing
/// outlives the run holding one of its elements.
/// </summary>
internal static class FilterCopyTool
{
    /// <summary>Tests only: owner for the window, so a test can drive it.</summary>
    internal static System.Windows.Window? TestOwner { get; set; }

    /// <summary>Tests only: answers Browse instead of the file dialog.</summary>
    internal static Func<string?>? BrowseHook { get; set; }

    internal static FilterCopyResult? LastResult { get; private set; }

    /// <summary>The Revit side of the window's questions.</summary>
    internal sealed class Host(Document doc) : IFilterCopyHost, IDisposable
    {
        private readonly List<Document> _open = [.. doc.Application.Documents.Cast<Document>()
            .Where(d => !d.IsLinked && !d.Equals(doc) && !d.IsFamilyDocument)];
        private FilterSource? _opened;

        public Document Target => doc;
        public Document Source { get; private set; } = doc;

        public string ThisModel => doc.Title;
        public IReadOnlyList<string> OpenModels => [.. _open.Select(d => d.Title)];
        public List<TemplateEntry> TargetTemplates { get; } = FilterCopyEngine.Templates(doc);

        public (List<TemplateEntry> Templates, string Status) LoadSource(FilterSourceChoice choice)
        {
            if (choice.Path is { } path)
            {
                if (path.Length == 0)
                    throw new InvalidOperationException("no file chosen");
                FilterSource src = FilterCopyEngine.OpenSource(doc.Application, path);
                if (src.Document.Equals(doc))
                {
                    src.Dispose();
                    throw new InvalidOperationException("that file is this model - choose \"This model\" instead");
                }
                ReleaseOpened();
                _opened = src;
                Source = src.Document;
                return (FilterCopyEngine.Templates(Source), $"{System.IO.Path.GetFileName(path)} ({src.How})");
            }
            ReleaseOpened();
            Source = choice.IsThisModel ? doc : _open[choice.OpenModel];
            return (FilterCopyEngine.Templates(Source), choice.IsThisModel ? "This model" : $"{Source.Title} (already open)");
        }

        public List<FilterOverrideRow> ReadFilters(long templateId)
            => Source.GetElement(new ElementId(templateId)) is View { IsTemplate: true } v ? FilterCopyEngine.Extract(v) : [];

        public Dictionary<string, string> TargetStatus(IReadOnlyList<FilterOverrideRow> rows)
            => FilterCopyEngine.TargetStatus(doc, Source, rows);

        public string? BrowseFile(string current)
        {
            if (BrowseHook is not null)
                return BrowseHook();
            Microsoft.Win32.OpenFileDialog dlg = new()
            {
                Title = "Model to copy view filters from",
                Filter = "Revit project (*.rvt)|*.rvt",
                FileName = System.IO.File.Exists(current) ? current : "",
            };
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        }

        private void ReleaseOpened()
        {
            _opened?.Dispose();
            _opened = null;
            Source = doc;
        }

        public void Dispose() => ReleaseOpened();
    }

    public static void Run(UIDocument uidoc) => Run(uidoc.Document);

    internal static void Run(Document doc)
    {
        RevitLinks.EnsureInitialized();
        LastResult = null;

        using Host host = new(doc);
        if (host.TargetTemplates.Count == 0)
        {
            RibbitUi.Alert("This model has no view templates to copy filters into.", FilterCopyWindow.DialogTitle);
            return;
        }

        FilterCopyWindow window = new(host, FilterCopySettings.Load());
        if (TestOwner is not null)
            window.Owner = TestOwner;
        bool ok = window.ShowDialog() == true;
        window.SettingsToSave().Save();
        if (!ok || window.SourceTemplate is null)
            return;

        List<View> targets = [.. window.TargetIds.Select(id => doc.GetElement(new ElementId(id))).OfType<View>()];
        FilterCopyResult result = FilterCopyEngine.Apply(doc, targets, host.Source, window.SourceTemplate.Id, window.ChosenFilters, window.Options);
        LastResult = result;
        RibbitUi.ShowReport($"{FilterCopyWindow.DialogTitle} - {result.Written.Count} written, {result.Skipped.Count} skipped", Report(result));
    }

    private static string Report(FilterCopyResult r)
    {
        StringBuilder sb = new();
        List<IReadOnlyList<string>> rows = [];
        rows.AddRange(r.Skipped.Select(s => (IReadOnlyList<string>)new[] { "<span class=\"skip\">skipped</span>", ReportHtml.Escape(s) }));
        rows.AddRange(r.Written.Select(s => (IReadOnlyList<string>)new[]
            { s.Contains("(without") ? "<span class=\"warn\">written</span>" : "<span class=\"ok\">written</span>", ReportHtml.Escape(s) }));
        ReportHtml.Table(sb, "Filters", ["Result", "Filter"], rows);
        if (r.Differences.Count > 0)
            ReportHtml.Table(sb, "Defined differently in this model", ["Filter and how it differs"],
                [.. r.Differences.Select(d => (IReadOnlyList<string>)new[] { ReportHtml.Escape(d) })]);
        if (r.Log.Count > 0)
            ReportHtml.Note(sb, string.Join("<br>", r.Log.Select(ReportHtml.Escape)));
        if (r.Committed)
            ReportHtml.Note(sb, "Ctrl+Z in Revit undoes the whole run.");
        return sb.ToString();
    }
}
