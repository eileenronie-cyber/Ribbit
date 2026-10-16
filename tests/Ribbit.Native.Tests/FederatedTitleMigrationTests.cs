using Ribbit.Geometry.Clash;
using Ribbit.Native.Revit;
using Xunit;

namespace Ribbit.Native.Tests;

/// <summary>
/// Folding a model's earlier names onto its normalised one.
///
/// The ledger is months of somebody's triage, so the property guarded here is
/// that the MOST RECENT decision about a clash survives the merge, whichever
/// name it was made under - including when the normalised name is the newer one,
/// and including decisions that only exist as delta files, which is where every
/// status save actually lands.
///
/// Every test ends by running the migration a SECOND time. The first version of
/// this migration found its own output and ran forever; a leftover variant file
/// does the same thing more quietly.
/// </summary>
public class FederatedTitleMigrationTests : IDisposable
{
    private const string Tag = "ClashFederated";
    private const string Normal = "Model";
    private const string Variant = "Model_detached";

    private static readonly DateTime Day1 = new(2026, 8, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Day2 = new(2026, 8, 2, 9, 0, 0, DateTimeKind.Utc);

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "ribbit-title-migration-" + Guid.NewGuid().ToString("N"));

    public FederatedTitleMigrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    private void Ledger(string title, DateTime when, params (string Key, string Status)[] pairs)
    {
        ClashLedger ledger = new();
        foreach ((string key, string status) in pairs)
            ledger.SetPair(key, status);

        string path = StatusLedger.WriteLedger(_dir, title, Tag, ledger);
        File.SetLastWriteTimeUtc(path, when);
    }

    private void Delta(string title, DateTime when, string key, string status)
    {
        string path = StatusLedger.AppendDelta(_dir, title, Tag, [(key, "", "", status)]);
        File.SetLastWriteTimeUtc(path, when);
    }

    private string StatusOf(string key)
        => StatusLedger.LoadLedger(_dir, Normal, Tag).Lookup(key, new StatusKey("", ""));

    private string? Migrate() => FederatedTitleMigration.Run(_dir, Normal, "", Tag);

    // ---------------------------------------------------------------- ledgers

    [Fact]
    public void NewerStatusUnderTheNormalisedName_IsNotOverwrittenByAnOlderVariant()
    {
        Ledger(Variant, Day1, ("1|2", "Active"));
        Ledger(Normal, Day2, ("1|2", "Resolved"));

        Assert.NotNull(Migrate());

        Assert.Equal("Resolved", StatusOf("1|2"));
        Assert.Null(Migrate());
    }

    [Fact]
    public void NewerStatusUnderAVariant_BeatsAnOlderDeltaUnderTheNormalisedName()
    {
        // The normalised name's decision lives in a DELTA, which replays on top
        // of the base ledger on every load - so writing the merge into the base
        // file lets the older delta win.
        Ledger(Normal, Day1);
        Delta(Normal, Day1, "1|2", "Active");
        Ledger(Variant, Day2, ("1|2", "Resolved"));

        Assert.NotNull(Migrate());

        Assert.Equal("Resolved", StatusOf("1|2"));
        Assert.Null(Migrate());
    }

    [Fact]
    public void AnOlderVariant_StillFillsClashesTheNormalisedNameNeverFlagged()
    {
        Ledger(Variant, Day1, ("1|2", "Active"), ("3|4", "Approved"));
        Ledger(Normal, Day2, ("1|2", "Resolved"));

        Migrate();

        Assert.Equal("Resolved", StatusOf("1|2"));
        Assert.Equal("Approved", StatusOf("3|4"));
    }

    [Fact]
    public void AStaleEntryInAFreshlyTouchedVariant_DoesNotOverwriteANewerTargetDecision()
    {
        // The variant's ONLY write to "1|2" is on Day1. The normalised name then
        // decides "1|2" = Resolved on Day2. The variant is then written to AGAIN
        // on Day3, but that write only touches an UNRELATED clash "3|4" - it must
        // not drag the Day1 "1|2" entry along with it as if it were a Day3
        // decision, or the merge undoes what the normalised name just decided.
        Ledger(Variant, Day1, ("1|2", "Active"));
        Ledger(Normal, Day2, ("1|2", "Resolved"));
        Delta(Variant, new DateTime(2026, 8, 3, 9, 0, 0, DateTimeKind.Utc), "3|4", "Approved");

        Migrate();

        Assert.Equal("Resolved", StatusOf("1|2"));
        Assert.Equal("Approved", StatusOf("3|4"));
    }

    [Fact]
    public void AVariantsDeltas_AreMergedAndRetired_SoTheSecondRunFindsNothing()
    {
        Ledger(Variant, Day1);
        Delta(Variant, Day1, "1|2", "Resolved");

        Assert.NotNull(Migrate());
        Assert.Equal("Resolved", StatusOf("1|2"));

        // A delta left under the old name keeps the old name alive as a
        // "variant", so every later run reports a merge that moves nothing.
        Assert.Null(Migrate());
        Assert.Empty(Directory.GetFiles(_dir, $"{Variant}_{Tag}_*.delta"));
    }

    // ------------------------------------------------------------------ notes

    [Fact]
    public void AVariantsNotes_ArriveUnderTheNormalisedName()
    {
        Ledger(Variant, Day1);
        ClashComments variant = ClashComments.Load(_dir, Variant, Tag);
        variant.Set("1|2", "reroute the drain");
        Assert.Null(variant.Save(_dir, Variant, Tag));

        ClashComments normal = ClashComments.Load(_dir, Normal, Tag);
        normal.Set("3|4", "already here");
        Assert.Null(normal.Save(_dir, Normal, Tag));

        Assert.NotNull(Migrate());

        ClashComments after = ClashComments.Load(_dir, Normal, Tag);
        Assert.Equal("reroute the drain", after.TextFor("1|2"));
        Assert.Equal("already here", after.TextFor("3|4"));
        Assert.Null(Migrate());
    }

    // -------------------------------------------------------------- baselines

    [Fact]
    public void NewerBaselineUnderTheNormalisedName_IsNotOverwrittenByAnOlderVariant()
    {
        SeenLedger variant = new();
        variant.Record("t1", ["old"]);
        variant.Save(_dir, Variant, Tag);

        SeenLedger normal = new();
        normal.Record("t1", ["new"]);
        normal.Save(_dir, Normal, Tag);

        foreach (string f in SeenLedger.FilesFor(_dir, Variant, Tag))
            File.SetLastWriteTimeUtc(f, Day1);
        foreach (string f in SeenLedger.FilesFor(_dir, Normal, Tag))
            File.SetLastWriteTimeUtc(f, Day2);

        // The variant needs a ledger too, or nothing marks it as a variant.
        Ledger(Variant, Day1);

        Migrate();

        Assert.Equal(["new"], SeenLedger.Load(_dir, Normal, Tag).Previous("t1"));
        Assert.Null(Migrate());
    }

    [Fact]
    public void ANewerVariantBaseline_Wins()
    {
        SeenLedger normal = new();
        normal.Record("t1", ["old"]);
        normal.Save(_dir, Normal, Tag);

        SeenLedger variant = new();
        variant.Record("t1", ["new"]);
        variant.Record("t2", ["only-in-variant"]);
        variant.Save(_dir, Variant, Tag);

        foreach (string f in SeenLedger.FilesFor(_dir, Normal, Tag))
            File.SetLastWriteTimeUtc(f, Day1);
        foreach (string f in SeenLedger.FilesFor(_dir, Variant, Tag))
            File.SetLastWriteTimeUtc(f, Day2);

        Migrate();

        SeenLedger merged = SeenLedger.Load(_dir, Normal, Tag);
        Assert.Equal(["new"], merged.Previous("t1"));
        Assert.Equal(["only-in-variant"], merged.Previous("t2"));
        Assert.Null(Migrate());
    }
}
