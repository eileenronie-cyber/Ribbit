using System.IO;
using System.Text;
using Ribbit.Geometry.Clash;
using Xunit;

namespace Ribbit.Tests;

/// <summary>
/// The CSV ledger, round-tripped against real files in a temp folder. This is
/// where a coordination status physically lives, so losing one here is losing
/// somebody's work.
/// </summary>
public class StatusLedgerTests : IDisposable
{
    private readonly string _folder;

    public StatusLedgerTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), $"ribbit_ledger_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private const string Tag = "ClashMEPvsStruc";

    private string WriteCsv(string name, string contents, DateTime? mtime = null)
    {
        string path = Path.Combine(_folder, name);
        File.WriteAllText(path, contents, new UTF8Encoding(true));
        if (mtime is not null)
            File.SetLastWriteTimeUtc(path, mtime.Value);
        return path;
    }

    private static string Csv(params string[] lines) => string.Join("\n", lines) + "\n";

    // -------------------------------------------------------------- reading

    [Fact]
    public void RowsAreKeyedByHeader()
    {
        string path = WriteCsv("a.csv", Csv("Element Id,Link,Status", "101,,Resolved"));

        Dictionary<string, string> row = Assert.Single(StatusLedger.ReadRows(path));

        Assert.Equal("101", row["Element Id"]);
        Assert.Equal("Resolved", row["Status"]);
    }

    // A clash comment routinely contains a comma. A naive split shifts every
    // later column - including Status - so quoted fields have to be handled.
    [Fact]
    public void QuotedFieldsContainingCommasDoNotShiftTheColumns()
    {
        string path = WriteCsv("a.csv",
            Csv("Element Id,Comment,Status", "101,\"Clashes with duct, see RFI 12\",Resolved"));

        Dictionary<string, string> row = Assert.Single(StatusLedger.ReadRows(path));

        Assert.Equal("Clashes with duct, see RFI 12", row["Comment"]);
        Assert.Equal("Resolved", row["Status"]);
    }

    [Fact]
    public void DoubledQuotesAreUnescaped()
    {
        string path = WriteCsv("a.csv",
            Csv("Element Id,Comment", "101,\"He said \"\"move it\"\"\""));

        Assert.Equal("He said \"move it\"", StatusLedger.ReadRows(path).Single()["Comment"]);
    }

    [Fact]
    public void EmbeddedNewlinesInsideQuotesStayInOneField()
    {
        string path = WriteCsv("a.csv", "Element Id,Comment\n101,\"line one\nline two\"\n");

        Dictionary<string, string> row = Assert.Single(StatusLedger.ReadRows(path));

        Assert.Equal("line one\nline two", row["Comment"]);
    }

    [Fact]
    public void AMissingFileYieldsNoRowsRatherThanThrowing()
        => Assert.Empty(StatusLedger.ReadRows(Path.Combine(_folder, "nope.csv")));

    // -------------------------------------------------------------- writing

    [Fact]
    public void WrittenRowsReadBackIdentically()
    {
        string path = Path.Combine(_folder, "out.csv");
        List<string> fields = ["Element Id", "Comment", "Status"];

        StatusLedger.Write(path, fields,
        [
            new Dictionary<string, string>
            {
                ["Element Id"] = "101",
                ["Comment"] = "Clashes with duct, see \"RFI 12\"",
                ["Status"] = "Resolved",
            },
        ]);

        Dictionary<string, string> row = Assert.Single(StatusLedger.ReadRows(path));

        Assert.Equal("Clashes with duct, see \"RFI 12\"", row["Comment"]);
        Assert.Equal("Resolved", row["Status"]);
    }

    // Excel reads a BOM-less UTF-8 file as the system codepage, mangling any
    // non-ASCII in a level name or comment.
    [Fact]
    public void FilesAreWrittenWithAUtf8Bom()
    {
        string path = Path.Combine(_folder, "bom.csv");
        StatusLedger.Write(path, ["A"], [new Dictionary<string, string> { ["A"] = "x" }]);

        byte[] head = File.ReadAllBytes(path).Take(3).ToArray();

        Assert.Equal([0xEF, 0xBB, 0xBF], head);
    }

    [Fact]
    public void MissingValuesAreWrittenAsBlanks()
    {
        string path = Path.Combine(_folder, "sparse.csv");
        StatusLedger.Write(path, ["A", "B"],
            [new Dictionary<string, string> { ["A"] = "1" }]);

        Assert.Equal("", StatusLedger.ReadRows(path).Single()["B"]);
    }

    // --------------------------------------------------------------- ledger

    [Fact]
    public void OnlyThisModelsFilesAreMerged()
    {
        WriteCsv($"TowerA_{Tag}_1.csv", Csv("Element Id,Link,Status", "101,,Resolved"));
        WriteCsv($"TowerB_{Tag}_1.csv", Csv("Element Id,Link,Status", "999,,Pending"));

        ClashLedger ledger = StatusLedger.LoadLedger(_folder, "TowerA", Tag);

        Assert.Equal("Resolved", ledger.Lookup(null, new StatusKey("101", "")));
        Assert.Equal(1, ledger.Count);
    }

    // Two tools sharing this folder must not merge each other's ledgers.
    [Fact]
    public void OnlyThisToolsFilesAreMerged()
    {
        WriteCsv($"TowerA_{Tag}_1.csv", Csv("Element Id,Link,Status", "101,,Resolved"));
        WriteCsv("TowerA_OtherTool_1.csv", Csv("Element Id,Link,Status", "999,,Pending"));

        Assert.Equal(1, StatusLedger.LoadLedger(_folder, "TowerA", Tag).Count);
    }

    // Ordered by MTIME, not filename. The filename encodes a search description,
    // not a timestamp, so two searches on the same day cannot be ordered by name
    // - and a later edit to the same pair must win.
    [Fact]
    public void LaterEditsWin_OrderedByModificationTimeNotFilename()
    {
        // "zzz" sorts last by name but was written FIRST.
        WriteCsv($"TowerA_{Tag}_zzz.csv", Csv("Element Id,Link,Status", "101,,Pending"),
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        WriteCsv($"TowerA_{Tag}_aaa.csv", Csv("Element Id,Link,Status", "101,,Resolved"),
            new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        ClashLedger ledger = StatusLedger.LoadLedger(_folder, "TowerA", Tag);

        Assert.Equal("Resolved", ledger.Lookup(null, new StatusKey("101", "")));
    }

    [Fact]
    public void RowsWithNoStatusOrNoIdAreIgnored()
    {
        WriteCsv($"TowerA_{Tag}_1.csv",
            Csv("Element Id,Link,Status", "101,,", ",,Resolved", "202,,Pending"));

        ClashLedger ledger = StatusLedger.LoadLedger(_folder, "TowerA", Tag);

        Assert.Equal(1, ledger.Count);
        Assert.Equal("Pending", ledger.Lookup(null, new StatusKey("202", "")));
    }

    [Fact]
    public void AMissingFolderYieldsAnEmptyLedger()
        => Assert.Equal(0, StatusLedger.LoadLedger(Path.Combine(_folder, "nope"), "TowerA", Tag).Count);

    // -------------------------------------------------- latest full report

    [Fact]
    public void TheLatestFullReportIsTheMostRecentlyModified()
    {
        WriteCsv($"TowerA_{Tag}_old.csv", Csv("Element Id", "1"),
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        WriteCsv($"TowerA_{Tag}_new.csv", Csv("Element Id", "2"),
            new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        string? latest = StatusLedger.FindLatestFullReport(_folder, "TowerA", Tag);

        Assert.EndsWith("_new.csv", latest);
    }

    // The asymmetry that matters: a statusupdate file is by design the most
    // recently modified thing in the folder, but it is NOT the last full set of
    // results - it only has three columns and fewer rows.
    [Fact]
    public void StatusUpdateFilesAreNeverChosenAsTheLatestFullReport()
    {
        WriteCsv($"TowerA_{Tag}_20260101_report1.csv", Csv("Element Id,Name", "1,Duct"),
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        WriteCsv($"TowerA_{Tag}_20260601_statusupdate1.csv", Csv("Element Id,Link,Status", "1,,Resolved"),
            new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        string? latest = StatusLedger.FindLatestFullReport(_folder, "TowerA", Tag);

        Assert.EndsWith("_report1.csv", latest);
    }

    // ...but the LEDGER must still read them, or a saved status would never be
    // picked up at all.
    [Fact]
    public void TheLedgerStillReadsStatusUpdateFiles()
    {
        WriteCsv($"TowerA_{Tag}_20260601_statusupdate1.csv",
            Csv("Element Id,Link,Status", "101,,Resolved"));

        Assert.Equal("Resolved", StatusLedger
            .LoadLedger(_folder, "TowerA", Tag)
            .Lookup(null, new StatusKey("101", "")));
    }

    [Fact]
    public void NoReportYetReturnsNull()
        => Assert.Null(StatusLedger.FindLatestFullReport(_folder, "TowerA", Tag));

    // ------------------------------------------------------------- writing

    [Fact]
    public void AFullStatusSaveRoundTripsAndBecomesTheNewLatest()
    {
        WriteCsv($"TowerA_{Tag}_20260101_report1.csv", Csv("Element Id,Name,Status", "101,Duct,"),
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        string saved = StatusLedger.WriteFullStatus(_folder, "TowerA", Tag,
            ["Element Id", "Name", "Status"],
            [new Dictionary<string, string>
            {
                ["Element Id"] = "101", ["Name"] = "Duct", ["Status"] = "Resolved",
            }]);

        Assert.Equal("Resolved", StatusLedger.ReadRows(saved).Single()["Status"]);
        Assert.Equal(saved, StatusLedger.FindLatestFullReport(_folder, "TowerA", Tag));
    }

    [Fact]
    public void AStatusUpdateWriteIsTaggedSoItIsDistinguishable()
    {
        string path = StatusLedger.WriteStatusUpdates(_folder, "TowerA", Tag,
            [("101", "", "Resolved")]);

        Assert.Contains("_statusupdate", Path.GetFileName(path));
        Assert.Equal("Resolved", StatusLedger.ReadRows(path).Single()["Status"]);
    }

    // The link name is part of the key: the same raw id in two links is two
    // different physical elements.
    [Fact]
    public void TheSameIdInDifferentLinksAreSeparateEntries()
    {
        WriteCsv($"TowerA_{Tag}_1.csv",
            Csv("Element Id,Link,Status", "101,Arch.rvt,Resolved", "101,Struct.rvt,Pending"));

        ClashLedger ledger = StatusLedger.LoadLedger(_folder, "TowerA", Tag);

        Assert.Equal(2, ledger.Count);
        Assert.Equal("Resolved", ledger.Lookup(null, new StatusKey("101", "Arch.rvt")));
        Assert.Equal("Pending", ledger.Lookup(null, new StatusKey("101", "Struct.rvt")));
    }

    // ------------------------------------------------- atomic publishing
    //
    // The csv folder is SHARED. Writing straight to the final path truncates it
    // at open, so a colleague reading in that window sees an empty ledger - and
    // if they then compact, they write that emptiness back as the new base.

    [Fact]
    public void Write_LeavesNoTempFileBehind()
    {
        string path = Path.Combine(_folder, "atomic.csv");

        StatusLedger.Write(path, ["Element Id", "Status"],
            [new Dictionary<string, string> { ["Element Id"] = "7", ["Status"] = "Resolved" }]);

        Assert.True(File.Exists(path));
        Assert.Empty(Directory.GetFiles(_folder, "*.tmp-*"));
    }

    // A rewrite must replace the file wholesale, not leave the old rows behind.
    [Fact]
    public void Write_OverAnExistingFile_ReplacesItCompletely()
    {
        string path = Path.Combine(_folder, "atomic.csv");

        StatusLedger.Write(path, ["Element Id", "Status"],
        [
            new Dictionary<string, string> { ["Element Id"] = "1", ["Status"] = "Pending" },
            new Dictionary<string, string> { ["Element Id"] = "2", ["Status"] = "Pending" },
        ]);

        StatusLedger.Write(path, ["Element Id", "Status"],
            [new Dictionary<string, string> { ["Element Id"] = "3", ["Status"] = "Resolved" }]);

        List<Dictionary<string, string>> rows = StatusLedger.ReadRows(path);

        Assert.Single(rows);
        Assert.Equal("3", rows[0]["Element Id"]);
        Assert.Empty(Directory.GetFiles(_folder, "*.tmp-*"));
    }

    // While a temp file exists it must not look like ledger content: both
    // listings match on the prefix, and a temp name starts with a dot.
    [Fact]
    public void TempNames_DoNotMatchTheLedgerOrDeltaListings()
    {
        string ledger = StatusLedger.LedgerPath(_folder, "TowerA", Tag);
        string temp = Path.GetFileName(AtomicFile.TempPathFor(ledger));
        string prefix = ClashStatus.ModelCsvPrefix("TowerA", Tag);

        Assert.StartsWith(".", temp);
        Assert.False(temp.StartsWith(prefix, StringComparison.Ordinal));
        Assert.False(temp.EndsWith(".csv", StringComparison.OrdinalIgnoreCase));
        Assert.False(temp.EndsWith(StatusLedger.DeltaExtension, StringComparison.Ordinal));
    }

    // A delta written through the swap still merges on load.
    [Fact]
    public void AppendDelta_StillLoadsAfterTheAtomicSwap()
    {
        StatusLedger.AppendDelta(_folder, "TowerA", Tag,
            [("pair-1", "101", "", "Resolved")]);

        ClashLedger ledger = StatusLedger.LoadLedger(_folder, "TowerA", Tag);

        Assert.Equal("Resolved", ledger.Lookup("pair-1", new StatusKey("101", "")));
        Assert.Empty(Directory.GetFiles(_folder, "*.tmp-*"));
    }
}
