using System.IO;
using System.Text;
using Ribbit.Geometry.Clash;
using Xunit;

namespace Ribbit.Tests;

/// <summary>
/// Status is keyed by CLASH PAIR, with element-keyed entries from older reports
/// still honoured. These pin both halves of that, plus the one-file ledger and
/// its migration.
/// </summary>
public class ClashLedgerTests : IDisposable
{
    private const string Tag = "TestTool";
    private readonly string _folder;

    public ClashLedgerTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), "RibbitLedger_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private void WriteCsv(string name, params string[] lines)
        => File.WriteAllText(Path.Combine(_folder, name),
            string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(true));

    // ------------------------------------------------------------ pair key

    // The bug this whole change exists to fix: a pipe clashing with two beams is
    // TWO clashes, and they must be separately flaggable.
    [Fact]
    public void OneElementInTwoClashesHasTwoKeys()
        => Assert.NotEqual(
            ClashPairKey.For("100", "", "200", ""),
            ClashPairKey.For("100", "", "300", ""));

    // The engine can report a pair from either side; both must be one clash.
    [Fact]
    public void TheKeyIgnoresWhichSideIsA()
        => Assert.Equal(
            ClashPairKey.For("200", "", "100", ""),
            ClashPairKey.For("100", "", "200", ""));

    // A raw id is only unique within one document.
    [Fact]
    public void TheSameIdInTwoLinksIsTwoDifferentClashes()
        => Assert.NotEqual(
            ClashPairKey.For("100", "", "200", "Arch.rvt"),
            ClashPairKey.For("100", "", "200", "Struct.rvt"));

    [Fact]
    public void BothSideIdsAreRecoverableForScopeChecks()
    {
        string key = ClashPairKey.For("100", "", "200", "Arch.rvt");
        Assert.Equal(["100", "200"], ClashPairKey.IdsOf(key).OrderBy(s => s));
    }

    // ------------------------------------------------------------- lookup

    [Fact]
    public void APairEntryWinsOverALegacyElementEntry()
    {
        ClashLedger ledger = new();
        ledger.SetElement(new StatusKey("100", ""), "Pending");
        ledger.SetPair("100|200", "Resolved");

        // Re-flagging one clash of a multi-clash element must appear to take,
        // rather than being overridden by the old element-wide flag.
        Assert.Equal("Resolved", ledger.Lookup("100|200", new StatusKey("100", "")));
    }

    [Fact]
    public void ALegacyElementEntryIsUsedWhenThePairHasNone()
    {
        ClashLedger ledger = new();
        ledger.SetElement(new StatusKey("100", ""), "Pending");

        Assert.Equal("Pending", ledger.Lookup("100|999", new StatusKey("100", "")));
    }

    [Fact]
    public void AnUnknownClashHasNoStatus()
        => Assert.Equal("", new ClashLedger().Lookup("1|2", new StatusKey("1", "")));

    [Fact]
    public void ABlankStatusIsNeverStored()
    {
        ClashLedger ledger = new();
        ledger.SetPair("1|2", "   ");
        Assert.Equal(0, ledger.Count);
    }

    // ------------------------------------------------------------ accepted

    [Theory]
    [InlineData("Accepted", StatusBucket.Accepted)]
    [InlineData("accepted by structure", StatusBucket.Accepted)]
    // "accept" is tested before "coord", so the stronger claim wins the overlap.
    [InlineData("Accepted - coordinated with struct", StatusBucket.Accepted)]
    [InlineData("Coordination", StatusBucket.Coordination)]
    [InlineData("Resolved", StatusBucket.Resolved)]
    public void AcceptedClassifiesAheadOfCoordination(string text, StatusBucket expected)
        => Assert.Equal(expected, ClashStatus.Classify(text));

    [Fact]
    public void AnAcceptedPairIsReportedAsAccepted()
    {
        ClashLedger ledger = new();
        ledger.SetPair("1|2", "Accepted");
        Assert.True(ledger.IsAccepted("1|2", new StatusKey("1", "")));
    }

    [Fact]
    public void AResolvedPairIsNotAccepted()
    {
        ClashLedger ledger = new();
        ledger.SetPair("1|2", "Resolved");
        Assert.False(ledger.IsAccepted("1|2", new StatusKey("1", "")));
    }

    // -------------------------------------------------------- ledger file

    [Fact]
    public void TheLedgerRoundTripsBothKeySpaces()
    {
        ClashLedger written = new();
        written.SetPair("100|200", "Accepted");
        written.SetElement(new StatusKey("300", "Arch.rvt"), "Pending");

        StatusLedger.WriteLedger(_folder, "TowerA", Tag, written);
        ClashLedger read = StatusLedger.LoadLedger(_folder, "TowerA", Tag);

        Assert.Equal("Accepted", read.Lookup("100|200", new StatusKey("x", "")));
        Assert.Equal("Pending", read.Lookup(null, new StatusKey("300", "Arch.rvt")));
    }

    // The upgrade path: an existing folder of report CSVs must not lose its
    // triage just because the ledger file did not exist yet.
    [Fact]
    public void TheFirstLoadMigratesExistingReportsAndWritesTheLedger()
    {
        WriteCsv($"TowerA_{Tag}_old.csv",
            "Element Id,Link,Status", "101,,Resolved");

        Assert.False(File.Exists(StatusLedger.LedgerPath(_folder, "TowerA", Tag)));

        ClashLedger ledger = StatusLedger.LoadLedger(_folder, "TowerA", Tag);

        Assert.Equal("Resolved", ledger.Lookup(null, new StatusKey("101", "")));
        Assert.True(File.Exists(StatusLedger.LedgerPath(_folder, "TowerA", Tag)));
    }

    // The whole point of #3: once migrated, a run reads the ledger and NOT the
    // pile of report CSVs. Proven by adding a report afterwards and showing it
    // is ignored.
    [Fact]
    public void AfterMigrationTheReportCsvsAreNoLongerRead()
    {
        WriteCsv($"TowerA_{Tag}_old.csv", "Element Id,Link,Status", "101,,Resolved");
        StatusLedger.LoadLedger(_folder, "TowerA", Tag);

        WriteCsv($"TowerA_{Tag}_new.csv", "Element Id,Link,Status", "999,,Pending");

        ClashLedger ledger = StatusLedger.LoadLedger(_folder, "TowerA", Tag);

        Assert.Equal("Resolved", ledger.Lookup(null, new StatusKey("101", "")));
        Assert.Equal("", ledger.Lookup(null, new StatusKey("999", "")));
    }

    [Fact]
    public void MigrationPicksUpPairKeysWhenTheReportHasThem()
    {
        WriteCsv($"TowerA_{Tag}_old.csv",
            "Clash Key,Element Id,Link,Status", "100|200,100,,Accepted");

        ClashLedger ledger = StatusLedger.LoadLedger(_folder, "TowerA", Tag);

        Assert.Equal("Accepted", ledger.Lookup("100|200", new StatusKey("100", "")));
    }

    // The ledger is a status store, not a report - it has no descriptive
    // columns, and it is always the newest file in the folder.
    [Fact]
    public void TheLedgerIsNeverChosenAsTheLatestFullReport()
    {
        WriteCsv($"TowerA_{Tag}_report1.csv", "Element Id,Link,Status", "101,,Resolved");
        StatusLedger.LoadLedger(_folder, "TowerA", Tag);

        string? latest = StatusLedger.FindLatestFullReport(_folder, "TowerA", Tag);

        Assert.NotNull(latest);
        Assert.EndsWith("_report1.csv", latest);
    }
}
