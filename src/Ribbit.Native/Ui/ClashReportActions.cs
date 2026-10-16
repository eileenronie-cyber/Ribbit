using System.Text.Json;
using Ribbit.Geometry.Clash;
using Ribbit.Native.Settings;

namespace Ribbit.Native.Ui;

/// <summary>
/// Handles what the clash report asks the host to do: copy an element id, and
/// save status changes made in the results table.
///
/// This is what makes the results report editable WITHOUT displacing the Set
/// Clash Status button. Both write through the same
/// <see cref="StatusLedger.WriteFullStatus"/> and produce the same kind of file,
/// so a status set here is indistinguishable from one set there — the standalone
/// editor stays useful for coming back to a report later, and this covers the
/// common case of flagging while you are already looking at the results.
///
/// One instance per shown report; it holds that report's rows.
/// </summary>
internal sealed class ClashReportActions
{
    private readonly List<Dictionary<string, string>> _csvRows;
    private readonly List<string> _fields;
    private readonly string _csvFolder;
    private readonly string _modelTitle;
    private readonly ClashProduct _product;
    private readonly ClashLedger _ledger;

    /// <summary>
    /// The saved test this report came from, carried onto the re-export.
    ///
    /// Without it, saving a status here renames the run to an anonymous one: the
    /// re-export becomes the newest report for the model, and the run picker —
    /// which reads the test off the FILENAME — can only show it as "(no saved
    /// test)". Triaging a run would quietly cost it its own name.
    ///
    /// Null for the three original clash tools, which have no saved tests; their
    /// filenames are unchanged.
    ///
    /// SETTABLE, because a run can become a test AFTER its report is on screen:
    /// accept "save this search as a test?" and this report now belongs to that
    /// test. Captured once, every status saved from this window would keep
    /// writing files called "adhoc" until the test was next re-run.
    /// </summary>
    public string? TestName { get; set; }

    public ClashReportActions(
        IReadOnlyList<Dictionary<string, string>> csvRows,
        IReadOnlyList<string> fields,
        string csvFolder,
        string modelTitle,
        ClashProduct product,
        ClashLedger ledger,
        string? testName = null)
    {
        TestName = testName;
        _ledger = ledger;
        // Copied, because saving rewrites Status in place and the same instance
        // serves every save the window performs.
        _csvRows = [.. csvRows.Select(r => new Dictionary<string, string>(r))];
        _fields = [.. fields];
        _csvFolder = csvFolder;
        _modelTitle = modelTitle;
        _product = product;
    }

    /// <summary>
    /// The handler to hand <see cref="ReportWindow"/>. Returns a JSON reply.
    /// </summary>
    public string? Handle(string message)
    {
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(message).RootElement;
        }
        catch (Exception)
        {
            return null;
        }

        if (!root.TryGetProperty("action", out JsonElement action))
            return null;

        return action.GetString() switch
        {
            "copy" => Copy(root),
            "save" => Save(root),
            _ => null,
        };
    }

    /// <summary>
    /// Puts text on the clipboard.
    ///
    /// Done host-side rather than with the page's clipboard API: the report is
    /// loaded via NavigateToString, so it has no secure origin and
    /// navigator.clipboard is not reliably available there. This always works.
    /// </summary>
    private static string Copy(JsonElement root)
    {
        string text = root.TryGetProperty("text", out JsonElement t) ? t.GetString() ?? "" : "";
        if (text.Length == 0)
            return Reply(false, "Nothing to copy.");

        string note = root.TryGetProperty("note", out JsonElement n)
            ? n.GetString() ?? "Copied."
            : "Copied " + text + " to the clipboard.";

        try
        {
            System.Windows.Clipboard.SetText(text);
            return Reply(true, note);
        }
        catch (Exception ex)
        {
            // Another process can hold the clipboard open; that is a transient
            // Windows condition, not a reason to break the report.
            return Reply(false, "Could not reach the clipboard: " + ex.Message);
        }
    }

    /// <summary>
    /// Applies the status changes to the ledger and re-exports the full report.
    ///
    /// TWO WRITES, both needed. The LEDGER is what every future run reads, so
    /// that is where the status actually lives. The REPORT is re-exported as a
    /// complete snapshot so the CSV on disk still matches what is on screen —
    /// somebody opening it in Excel should not see stale statuses.
    ///
    /// Keyed by CLASH, not by element: flagging one clash of a three-clash pipe
    /// must not flag the other two.
    /// </summary>
    private string Save(JsonElement root)
    {
        if (!root.TryGetProperty("updates", out JsonElement updates)
            || updates.ValueKind != JsonValueKind.Array)
        {
            return Reply(false, "Nothing to save.");
        }

        Dictionary<string, string> byClash = [];
        foreach (JsonElement u in updates.EnumerateArray())
        {
            string clashKey = Str(u, "clashKey");
            string status = Str(u, "status");

            if (clashKey.Length > 0 && status.Length > 0)
                byClash[clashKey] = status;
        }

        if (byClash.Count == 0)
            return Reply(false, "Nothing to save.");

        try
        {
            foreach (KeyValuePair<string, string> kv in byClash)
                _ledger.SetPair(kv.Key, kv.Value);

            // A DELTA, not the whole ledger. This instance holds the ledger as it
            // was when the report opened, and a report can sit open for hours -
            // writing all of it back would erase every status saved since.
            StatusLedger.AppendDelta(
                _csvFolder, _modelTitle, _product.ProductTag,
                byClash.Select(kv => (kv.Key, "", "", kv.Value)));

            // Both rows of a pair share the clash key, so both sides of each
            // flagged clash pick up the new status together.
            foreach (Dictionary<string, string> row in _csvRows)
            {
                if (byClash.TryGetValue(Field(row, StatusLedger.ClashKeyField), out string? status))
                    row["Status"] = status;
            }

            string path = StatusLedger.WriteFullStatus(
                _csvFolder, _modelTitle, _product.ProductTag, _fields, _csvRows, TestName);

            return "{\"ok\":true,\"saved\":true,\"message\":"
                   + JsonSerializer.Serialize(
                       $"Saved {byClash.Count} clash status(es) to {System.IO.Path.GetFileName(path)}")
                   + "}";
        }
        catch (Exception ex)
        {
            return Reply(false, "Save failed: " + ex.Message);
        }
    }

    private static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out JsonElement v) ? (v.GetString() ?? "").Trim() : "";

    private static string Field(IReadOnlyDictionary<string, string> row, string name)
        => row.TryGetValue(name, out string? v) ? v.Trim() : "";

    private static string Reply(bool ok, string message)
        => $"{{\"ok\":{(ok ? "true" : "false")},\"message\":"
           + JsonSerializer.Serialize(message) + "}";
}
