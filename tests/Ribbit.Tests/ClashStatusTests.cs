using Ribbit.Geometry.Clash;
using Xunit;

namespace Ribbit.Tests;

/// <summary>
/// The coordination-tracking layer. A bug here means somebody's "Resolved" flag
/// silently vanishes, or a status from an unrelated search leaks into a report —
/// both of which have happened in this tool's history.
/// </summary>
public class ClashStatusTests
{
    // ------------------------------------------------------- match type

    // The engine returns EXACTLY the threshold only when the Boolean threw, so
    // that one value distinguishes a failed-open pair from a real intersection.
    [Fact]
    public void ExactlyTheThreshold_MeansTheBooleanFailedOpen()
        => Assert.Equal(ClashMatchType.BooleanFailedOpen,
            ClashStatus.ClassifyVolume(ClashStatus.FailOpenVolume));

    [Fact]
    public void AVolumeAboveTheThreshold_IsARealIntersection()
        => Assert.Equal(ClashMatchType.SolidIntersection, ClashStatus.ClassifyVolume(0.5));

    [Fact]
    public void ZeroVolume_IsBoundingBoxOnly()
        => Assert.Equal(ClashMatchType.BoundingBoxOnly, ClashStatus.ClassifyVolume(0.0));

    // --------------------------------------------------------- buckets

    [Theory]
    [InlineData("Resolved", StatusBucket.Resolved)]
    [InlineData("resolve", StatusBucket.Resolved)]
    [InlineData("DONE", StatusBucket.Resolved)]
    [InlineData("Fixed on site", StatusBucket.Resolved)]
    [InlineData("Pending review", StatusBucket.Pending)]
    [InlineData("Still open", StatusBucket.Pending)]
    [InlineData("For coordination - see RFI 12", StatusBucket.Coordination)]
    public void FreeTextStatuses_ClassifyBySubstring(string status, StatusBucket expected)
        => Assert.Equal(expected, ClashStatus.Classify(status));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ask Dave")]
    public void BlankOrUnrecognizedStatuses_HaveNoBucket(string? status)
        => Assert.Null(ClashStatus.Classify(status));

    // The documented, accepted cost of substring matching on a free-text field.
    // Pinned so it is a known tradeoff rather than a surprise later.
    [Fact]
    public void NotResolvedYet_MatchesResolved_TheKnownSubstringTradeoff()
        => Assert.Equal(StatusBucket.Resolved, ClashStatus.Classify("Not resolved yet"));

    // First rule wins, in declaration order.
    [Fact]
    public void WhenSeveralRulesMatch_TheFirstWins()
        => Assert.Equal(StatusBucket.Resolved, ClashStatus.Classify("resolved, pending sign-off"));

    // An unrecognized status must still be VISIBLE - plain text beats vanishing.
    [Fact]
    public void AnUnrecognizedStatus_FallsBackToPlainText()
        => Assert.Equal("ask Dave", ClashStatus.BadgeHtml("ask Dave"));

    [Fact]
    public void ABlankStatus_RendersAsADash()
        => Assert.Equal("-", ClashStatus.BadgeHtml(""));

    [Fact]
    public void ARecognizedStatus_GetsAColouredPill()
    {
        string html = ClashStatus.BadgeHtml("Resolved");

        Assert.Contains("<span", html);
        Assert.Contains("Resolved", html);
        Assert.Contains(ClashStatus.Colors(StatusBucket.Resolved).Background, html);
    }

    // ------------------------------------------------------- filenames

    [Theory]
    [InlineData("Structural Framing", "structural_framing")]
    [InlineData("  Ducts / Pipes  ", "ducts_pipes")]
    [InlineData("55%", "55")]
    [InlineData("", "none")]
    [InlineData("!!!", "none")]
    public void FilenamePartsAreSanitized(string input, string expected)
        => Assert.Equal(expected, ClashStatus.SanitizeFilenamePart(input));

    [Fact]
    public void TheCsvNameCarriesModelToolDateTimeAndTag()
    {
        string name = ClashStatus.BuildCsvFilename(
            "Tower A", "statusupdate", "ClashMEPvsStruc",
            new HashSet<string>(), new DateTime(2026, 8, 8, 14, 35, 0));

        Assert.Equal("Tower_A_ClashMEPvsStruc_20260808-1435_statusupdate1.csv", name);
    }

    // Writing the same thing twice in a minute must never overwrite the first.
    [Fact]
    public void TheCounterSkipsPastNamesAlreadyOnDisk()
    {
        HashSet<string> existing =
        [
            "Tower_A_ClashMEPvsStruc_20260808-1435_statusupdate1.csv",
            "Tower_A_ClashMEPvsStruc_20260808-1435_statusupdate2.csv",
        ];

        string name = ClashStatus.BuildCsvFilename(
            "Tower A", "statusupdate", "ClashMEPvsStruc", existing,
            new DateTime(2026, 8, 8, 14, 35, 0));

        Assert.EndsWith("statusupdate3.csv", name);
    }

    [Fact]
    public void TheTestNameGoesAfterTheProductTagAndBeforeTheTag()
    {
        string name = ClashStatus.BuildCsvFilename(
            "Tower A", "saved", "ClashFederated", new HashSet<string>(),
            new DateTime(2026, 8, 8, 14, 35, 0), "52 Series vs Structure");

        Assert.Equal(
            "Tower_A_ClashFederated_20260808-1435_52_series_vs_structure_saved1.csv", name);
    }

    // The prefix is how the ledger finds this model's files. A test name in
    // front of it would orphan every report already written.
    [Fact]
    public void ATestNameNeverDisturbsThePrefix()
    {
        string prefix = ClashStatus.ModelCsvPrefix("Tower A", "ClashFederated");
        string name = ClashStatus.BuildCsvFilename(
            "Tower A", "saved", "ClashFederated", new HashSet<string>(), null, "Anything At All");

        Assert.StartsWith(prefix, name);
    }

    [Fact]
    public void NoTestNameLeavesTheNameUnchanged()
    {
        string withNull = ClashStatus.BuildCsvFilename(
            "Tower A", "saved", "ClashFederated", new HashSet<string>(),
            new DateTime(2026, 8, 8, 14, 35, 0));

        string withBlank = ClashStatus.BuildCsvFilename(
            "Tower A", "saved", "ClashFederated", new HashSet<string>(),
            new DateTime(2026, 8, 8, 14, 35, 0), "   ");

        Assert.Equal("Tower_A_ClashFederated_20260808-1435_saved1.csv", withNull);
        Assert.Equal(withNull, withBlank);
    }

    // An unbounded test name plus a long model title plus a path is how the
    // 260-character limit gets met - and it fails at the write, after the run.
    [Fact]
    public void ALongTestNameIsCapped()
    {
        string name = ClashStatus.BuildCsvFilename(
            "Tower A", "saved", "ClashFederated", new HashSet<string>(), null,
            new string('x', 200));

        Assert.Contains(new string('x', ClashStatus.MaxTestNameLength), name);
        Assert.DoesNotContain(new string('x', ClashStatus.MaxTestNameLength + 1), name);
    }

    // The status editor finds a test's runs by looking for this token inside the
    // filename. Writer and reader MUST agree, so they share this one function -
    // these pin the contract between them.
    [Fact]
    public void TheTokenInTheFilenameIsTheOneTheReaderLooksFor()
    {
        string name = ClashStatus.BuildCsvFilename(
            "Tower A", "saved", "ClashFederated", new HashSet<string>(), null,
            "Pipes vs Trays");

        Assert.Contains($"_{ClashStatus.TestNamePart("Pipes vs Trays")}_", name);
    }

    [Fact]
    public void ALongTestNameIsCappedTheSameWayOnBothSides()
    {
        string testName = new('x', 200);

        string name = ClashStatus.BuildCsvFilename(
            "Tower A", "saved", "ClashFederated", new HashSet<string>(), null, testName);

        Assert.Contains($"_{ClashStatus.TestNamePart(testName)}_", name);
    }

    // Blank must NOT come back as "none": that is a legitimate token elsewhere,
    // and here it would invent a test named "none" on every adhoc run.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoTestNameProducesNoToken(string? testName)
        => Assert.Equal("", ClashStatus.TestNamePart(testName));

    // yyyyMMdd-HHmm sorts chronologically as plain text, which is the whole
    // reason for that format over a friendlier one.
    [Fact]
    public void NamesSortChronologicallyAsText()
    {
        string earlier = ClashStatus.BuildCsvFilename(
            "T", "saved", "P", new HashSet<string>(), new DateTime(2026, 8, 8, 9, 5, 0));
        string later = ClashStatus.BuildCsvFilename(
            "T", "saved", "P", new HashSet<string>(), new DateTime(2026, 8, 8, 14, 35, 0));
        string nextDay = ClashStatus.BuildCsvFilename(
            "T", "saved", "P", new HashSet<string>(), new DateTime(2026, 8, 9, 1, 0, 0));

        Assert.True(string.CompareOrdinal(earlier, later) < 0);
        Assert.True(string.CompareOrdinal(later, nextDay) < 0);
    }

    // The prefix is what Load uses to decide a file belongs to this model AND
    // this tool - it has to sanitize identically to the filename builder.
    [Fact]
    public void ThePrefixMatchesTheStartOfTheFilenameItBuilds()
    {
        string prefix = ClashStatus.ModelCsvPrefix("Tower A", "ClashMEPvsStruc");
        string name = ClashStatus.BuildCsvFilename(
            "Tower A", "tag", "ClashMEPvsStruc", new HashSet<string>());

        Assert.StartsWith(prefix, name);
    }

    [Fact]
    public void AMissingModelTitleBecomesUntitled()
        => Assert.StartsWith("Untitled_", ClashStatus.ModelCsvPrefix(null, "Tool"));

    // Two tools sharing this module must never merge each other's ledgers.
    [Fact]
    public void DifferentProductTagsProduceDifferentPrefixes()
        => Assert.NotEqual(
            ClashStatus.ModelCsvPrefix("Tower A", "ClashMEPvsStruc"),
            ClashStatus.ModelCsvPrefix("Tower A", "ClashMEPLink"));

    // ----------------------------------------------------------- stale

    private static StatusKey Key(string id, string link = "") => new(id, link);

    /// <summary>A ledger holding element-keyed (legacy) entries.</summary>
    private static ClashLedger ElementLedger(params (string Id, string Link, string Status)[] rows)
    {
        ClashLedger ledger = new();
        foreach ((string id, string link, string status) in rows)
            ledger.SetElement(new StatusKey(id, link), status);
        return ledger;
    }

    private static ClashLedger PairLedger(params (string Key, string Status)[] rows)
    {
        ClashLedger ledger = new();
        foreach ((string key, string status) in rows)
            ledger.SetPair(key, status);
        return ledger;
    }

    [Fact]
    public void AFlaggedPairStillClashing_IsNotStale()
        => Assert.Empty(ClashStatus.FindStale(
            ElementLedger(("101", "", "Resolved")), [], [Key("101")]));

    [Fact]
    public void AFlaggedPairNoLongerClashing_IsStale()
    {
        (string label, string status) = Assert.Single(ClashStatus.FindStale(
            ElementLedger(("101", "", "Resolved")), [], [Key("202")]));

        Assert.Equal("101", label);
        Assert.Equal("Resolved", status);
    }

    // The live bug from 2026-07-24: without a scope, statuses from a completely
    // different search leak into this report purely for being in the shared pool.
    [Fact]
    public void WithoutAScope_AnUnrelatedSearchesStatusLeaksIn()
        => Assert.Single(ClashStatus.FindStale(
            ElementLedger(("999", "", "Resolved")), [], []));

    [Fact]
    public void AScopeRestrictsStaleEntriesToThisSearch()
    {
        ClashLedger ledger = ElementLedger(("101", "", "Resolved"), ("999", "", "Resolved"));
        HashSet<string> scope = ["101", "202"];

        (string label, _) = Assert.Single(
            ClashStatus.FindStale(ledger, [], [Key("202")], scope));

        Assert.Equal("101", label);
    }

    // The same element id in two different links is two different things.
    [Fact]
    public void TheLinkNameIsPartOfTheKey()
    {
        ClashLedger ledger = ElementLedger(
            ("101", "Arch.rvt", "Resolved"),
            ("101", "Struct.rvt", "Pending"));

        (string label, string status) = Assert.Single(
            ClashStatus.FindStale(ledger, [], [Key("101", "Arch.rvt")]));

        Assert.Contains("Struct.rvt", label);
        Assert.Equal("Pending", status);
    }

    // ------------------------------------------------- pair-keyed stale

    [Fact]
    public void APairStillClashing_IsNotStale()
        => Assert.Empty(ClashStatus.FindStale(
            PairLedger(("101|202", "Resolved")), ["101|202"], []));

    [Fact]
    public void APairNoLongerClashing_IsStale()
    {
        (string label, string status) = Assert.Single(ClashStatus.FindStale(
            PairLedger(("101|202", "Resolved")), ["101|303"], []));

        Assert.Contains("101", label);
        Assert.Contains("202", label);
        Assert.Equal("Resolved", status);
    }

    // A pair is this search's business if EITHER side was looked at.
    [Fact]
    public void APairIsInScopeWhenEitherSideWasSearched()
    {
        ClashLedger ledger = PairLedger(("101|202", "Resolved"), ("888|999", "Pending"));

        (string label, _) = Assert.Single(
            ClashStatus.FindStale(ledger, [], [], new HashSet<string> { "202" }));

        Assert.Contains("202", label);
    }

    // The whole point of Accepted: it must never refill the table it exists to
    // empty. "Reviewed, allowed, and not clashing right now" is not news.
    [Fact]
    public void AnAcceptedPairIsNeverStale()
        => Assert.Empty(ClashStatus.FindStale(
            PairLedger(("101|202", "Accepted")), ["999|888"], []));

    [Fact]
    public void AnAcceptedLegacyElementIsNeverStale()
        => Assert.Empty(ClashStatus.FindStale(
            ElementLedger(("101", "", "Accepted")), [], [Key("202")]));

    // --------------------------------------------------------- keywords

    [Fact]
    public void KeywordsAreSplitNormalizedAndDeduplicated()
        => Assert.Equal(["duct", "pipe"], ClashStatus.ParseKeywords(" Duct , pipe ,DUCT, "));

    [Fact]
    public void AnEmptyKeywordStringYieldsNothing()
        => Assert.Empty(ClashStatus.ParseKeywords("  ,  , "));

    [Fact]
    public void KeywordMatchingIsCaseInsensitiveSubstring()
    {
        List<string> kws = ClashStatus.ParseKeywords("duct");

        Assert.True(ClashStatus.ContainsKeyword("Supply DUCTWORK 300mm", kws));
        Assert.False(ClashStatus.ContainsKeyword("Cable Tray", kws));
    }

    [Fact]
    public void NoKeywordsMatchesNothing()
        => Assert.False(ClashStatus.ContainsKeyword("anything", []));
}
