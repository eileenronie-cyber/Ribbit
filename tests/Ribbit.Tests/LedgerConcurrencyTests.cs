using System.IO;
using Ribbit.Geometry.Clash;
using Xunit;

namespace Ribbit.Tests;

/// <summary>
/// Two people triaging one model must not erase each other.
///
/// The old save was read-modify-write over a file both were writing: whoever
/// saved second silently dropped everything the first had done. These pin the
/// property that replaced it — a save contributes only its own changes, so
/// nothing a save did not touch can be lost by it.
/// </summary>
public class LedgerConcurrencyTests : IDisposable
{
    private const string Tag = "ClashTest";
    private const string Model = "Tower";

    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "ribbit-ledger-" + Guid.NewGuid().ToString("N"));

    public LedgerConcurrencyTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    private ClashLedger Load() => StatusLedger.LoadLedger(_folder, Model, Tag);

    private void Save(string clashKey, string status)
        => StatusLedger.AppendDelta(_folder, Model, Tag, [(clashKey, "", "", status)]);

    // The exact scenario that used to lose work: both load, both save, neither
    // knows about the other.
    [Fact]
    public void ConcurrentSavesBothSurvive()
    {
        Load();   // person A opens
        Load();   // person B opens

        Save("a|b", "Resolved");    // A saves
        Save("c|d", "Pending");     // B saves, from a ledger that predates A's

        ClashLedger merged = Load();

        Assert.Equal("Resolved", merged.Lookup("a|b", new StatusKey("", "")));
        Assert.Equal("Pending", merged.Lookup("c|d", new StatusKey("", "")));
    }

    // Same clash, two decisions. Someone has to win, and it must be the later.
    [Fact]
    public void TheLastDecisionOnOneClashWins()
    {
        Save("a|b", "Pending");
        Save("a|b", "Resolved");

        Assert.Equal("Resolved", Load().Lookup("a|b", new StatusKey("", "")));
    }

    [Fact]
    public void ADeltaDoesNotDisturbTheBaseLedger()
    {
        ClashLedger seed = new();
        seed.SetPair("old|pair", "Accepted");
        StatusLedger.WriteLedger(_folder, Model, Tag, seed);

        Save("new|pair", "Resolved");

        ClashLedger merged = Load();
        Assert.Equal("Accepted", merged.Lookup("old|pair", new StatusKey("", "")));
        Assert.Equal("Resolved", merged.Lookup("new|pair", new StatusKey("", "")));
    }

    // Deltas must be invisible to anything hunting for reports, which is why
    // they do not end in .csv.
    [Fact]
    public void DeltasAreNotMistakenForReports()
    {
        Save("a|b", "Resolved");

        Assert.Empty(StatusLedger.ListFullReports(_folder, Model, Tag));
        Assert.Null(StatusLedger.FindLatestFullReport(_folder, Model, Tag));
    }

    [Fact]
    public void OneModelsDeltasDoNotReachAnother()
    {
        StatusLedger.AppendDelta(_folder, "Other", Tag, [("a|b", "", "", "Resolved")]);

        Assert.Equal("", Load().Lookup("a|b", new StatusKey("", "")));
    }

    // Compaction folds the deltas away. The answer must not change - that is the
    // whole contract, since it can run at any time.
    [Fact]
    public void CompactionPreservesEveryStatus()
    {
        for (int i = 0; i < 30; i++)
            Save($"pair{i}|x", i % 2 == 0 ? "Resolved" : "Pending");

        ClashLedger before = Load();   // crosses the threshold and compacts
        ClashLedger after = Load();    // reads whatever compaction left behind

        for (int i = 0; i < 30; i++)
        {
            StatusKey none = new("", "");
            Assert.Equal(before.Lookup($"pair{i}|x", none), after.Lookup($"pair{i}|x", none));
            Assert.NotEqual("", after.Lookup($"pair{i}|x", none));
        }

        Assert.Empty(Directory.GetFiles(_folder, "*" + StatusLedger.DeltaExtension));
    }
}
