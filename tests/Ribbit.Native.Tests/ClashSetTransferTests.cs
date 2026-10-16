using Ribbit.Native.Settings;
using Xunit;

namespace Ribbit.Native.Tests;

/// <summary>
/// Export and import of clash sets.
///
/// Lives here rather than in Ribbit.Tests because ClashSetLibrary is in
/// Ribbit.Native. Nothing below touches a Revit type: categories travel as enum
/// NAMES (plain strings), and none of these paths call ClashSet.Resolved, which
/// is the one member that would need RevitAPI loaded.
///
/// The invariant worth guarding is <see cref="UpdatingAnExistingSet_KeepsItsId"/>.
/// Everything that references a set does so by id, so an import that replaced
/// ids would silently orphan every saved test pointing at those sets — the exact
/// failure the id was introduced to prevent.
/// </summary>
public class ClashSetTransferTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "ribbit-set-transfer-" + Guid.NewGuid().ToString("N"));

    public ClashSetTransferTests() => Directory.CreateDirectory(_dir);

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

    private string Path_(string name) => Path.Combine(_dir, name);

    private static ClashSet Set(string name, string id, params string[] values)
        => new()
        {
            Id = id,
            Name = name,
            Values = [.. values.Select(v => new SetValue { Param = "System Type", Value = v })],
        };

    // ------------------------------------------------------------ round trip

    [Fact]
    public void ExportThenImport_CarriesTheRulesAndNotTheId()
    {
        string path = Path_("bundle.txt");

        ClashSet source = Set("52 Series", "aaaaaaaaaaaa", "52 - Vent", "52 - Waste");
        source.Keywords.Add("52");
        source.Categories.Add("OST_PipeCurves");

        ClashSetTransferIo.Export(path, [source]);

        string text = File.ReadAllText(path);
        Assert.DoesNotContain("aaaaaaaaaaaa", text);

        // The point of the format: a person can read it.
        Assert.Contains("[52 Series]", text);
        Assert.Contains("System Type = 52 - Vent", text);
        Assert.Contains("keyword: 52", text);
        Assert.Contains("category: OST_PipeCurves", text);
        Assert.DoesNotContain("{", text);

        List<ClashSetTransfer> back = ClashSetTransferIo.Import(path);

        ClashSetTransfer one = Assert.Single(back);
        Assert.Equal("52 Series", one.Name);
        Assert.Equal(["52 - Vent", "52 - Waste"], one.Values.Select(v => v.Value));
        Assert.Equal("System Type", one.Values[0].Param);
        Assert.Equal(["52"], one.Keywords);
        Assert.Equal(["OST_PipeCurves"], one.Categories);
    }

    [Fact]
    public void ImportedSets_GetAFreshLocalId()
    {
        ClashSetLibrary lib = new();
        ClashSetLibrary.ImportOutcome outcome = lib.Import(
            [new ClashSetTransfer { Name = "New", Keywords = ["k"] }],
            ClashSetLibrary.ImportClash.KeepBoth);

        Assert.Equal(1, outcome.Added);

        ClashSet added = Assert.Single(lib.Sets);
        Assert.Equal(32, added.Id.Length);
        Assert.NotEqual("", added.Id);
    }

    // -------------------------------------------------------- name clashes

    /// <summary>
    /// The load-bearing one: an update must NOT re-identify the set, or every
    /// saved test pointing at it is orphaned.
    /// </summary>
    [Fact]
    public void UpdatingAnExistingSet_KeepsItsId()
    {
        ClashSetLibrary lib = new();
        lib.Sets.Add(Set("52 Series", "originalid00000000000000000000000", "old"));

        lib.Import(
            [new ClashSetTransfer
            {
                Name = "52 Series",
                Values = [new SetValue { Param = "System Type", Value = "new" }],
            }],
            ClashSetLibrary.ImportClash.Update);

        ClashSet only = Assert.Single(lib.Sets);
        Assert.Equal("originalid00000000000000000000000", only.Id);
        Assert.Equal(["new"], only.Values.Select(v => v.Value));
    }

    [Fact]
    public void SkippingAnExistingSet_ChangesNothing()
    {
        ClashSetLibrary lib = new();
        lib.Sets.Add(Set("52 Series", "keepme", "old"));

        ClashSetLibrary.ImportOutcome outcome = lib.Import(
            [new ClashSetTransfer
            {
                Name = "52 Series",
                Values = [new SetValue { Param = "System Type", Value = "new" }],
            }],
            ClashSetLibrary.ImportClash.Skip);

        Assert.Equal(1, outcome.Skipped);
        Assert.Equal(0, outcome.Added);

        ClashSet only = Assert.Single(lib.Sets);
        Assert.Equal("keepme", only.Id);
        Assert.Equal(["old"], only.Values.Select(v => v.Value));
    }

    [Fact]
    public void KeepingBoth_RenamesTheIncomingOneAndLeavesTheOriginal()
    {
        ClashSetLibrary lib = new();
        lib.Sets.Add(Set("52 Series", "keepme", "old"));

        lib.Import(
            [new ClashSetTransfer { Name = "52 Series", Keywords = ["new"] }],
            ClashSetLibrary.ImportClash.KeepBoth);

        Assert.Equal(2, lib.Sets.Count);
        Assert.Equal(["52 Series", "52 Series (2)"], lib.Sets.Select(s => s.Name));

        // The original is untouched, including its id.
        Assert.Equal("keepme", lib.Sets[0].Id);
        Assert.Equal(["old"], lib.Sets[0].Values.Select(v => v.Value));
    }

    [Fact]
    public void KeepingBothTwice_KeepsCounting()
    {
        ClashSetLibrary lib = new();
        lib.Sets.Add(Set("A", "id1"));

        for (int i = 0; i < 2; i++)
        {
            lib.Import(
                [new ClashSetTransfer { Name = "A", Keywords = ["k"] }],
                ClashSetLibrary.ImportClash.KeepBoth);
        }

        Assert.Equal(["A", "A (2)", "A (3)"], lib.Sets.Select(s => s.Name));
    }

    // ------------------------------------------------------------- cleaning

    [Fact]
    public void SetsWithNoNameOrNoRules_AreDropped()
    {
        string path = Path_("mixed.json");
        File.WriteAllText(path, """
            {
              "Version": 1,
              "Sets": [
                { "Name": "keeps", "Keywords": ["k"] },
                { "Name": "", "Keywords": ["k"] },
                { "Name": "no rules at all", "Keywords": [], "Values": [], "Categories": [] }
              ]
            }
            """);

        List<ClashSetTransfer> got = ClashSetTransferIo.Import(path);

        Assert.Equal(["keeps"], got.Select(s => s.Name));
    }

    [Fact]
    public void EmptyValuesAreDropped_SoOneCannotMatchEverything()
    {
        string path = Path_("empties.json");
        File.WriteAllText(path, """
            {
              "Sets": [
                { "Name": "s",
                  "Values": [
                    { "Param": "System Type", "Value": "real" },
                    { "Param": "System Type", "Value": "   " }
                  ] }
              ]
            }
            """);

        ClashSetTransfer one = Assert.Single(ClashSetTransferIo.Import(path));
        Assert.Equal(["real"], one.Values.Select(v => v.Value));
    }

    /// <summary>Someone will hand over a bare array; being strict helps nobody.</summary>
    [Fact]
    public void ABareArrayOfSets_IsAccepted()
    {
        string path = Path_("bare.json");
        File.WriteAllText(path, """[ { "Name": "loose", "Keywords": ["k"] } ]""");

        Assert.Equal(["loose"], ClashSetTransferIo.Import(path).Select(s => s.Name));
    }

    /// <summary>A live clash_sets.json carries ids; they must be ignored, not adopted.</summary>
    [Fact]
    public void AWholeLibraryFile_ImportsAsRulesWithItsIdsIgnored()
    {
        string path = Path_("library.json");
        File.WriteAllText(path, """
            {
              "Sets": [
                { "Id": "theirid", "Name": "Theirs", "Keywords": ["k"],
                  "Values": [], "Categories": [] }
              ]
            }
            """);

        ClashSetLibrary lib = new();
        lib.Import(ClashSetTransferIo.Import(path), ClashSetLibrary.ImportClash.KeepBoth);

        ClashSet only = Assert.Single(lib.Sets);
        Assert.Equal("Theirs", only.Name);
        Assert.NotEqual("theirid", only.Id);
    }

    // -------------------------------------------------- the written listing

    [Fact]
    public void AHandWrittenListing_Imports()
    {
        string path = Path_("hand.txt");
        File.WriteAllText(path, """
            # a note from a colleague
            Ribbit clash sets

            [Chilled Water]
              System Type = 55 - CHW Flow
              System Type = 55 - CHW Return
              keyword: CHW
              category: OST_PipeCurves

            [Trays]
              Description = Refrigerant Pipework Tray
            """);

        List<ClashSetTransfer> got = ClashSetTransferIo.Import(path);

        Assert.Equal(["Chilled Water", "Trays"], got.Select(s => s.Name));
        Assert.Equal(["55 - CHW Flow", "55 - CHW Return"], got[0].Values.Select(v => v.Value));
        Assert.Equal("System Type", got[0].Values[0].Param);
        Assert.Equal(["CHW"], got[0].Keywords);
        Assert.Equal(["OST_PipeCurves"], got[0].Categories);
        Assert.Equal(["Refrigerant Pipework Tray"], got[1].Values.Select(v => v.Value));
    }

    /// <summary>A value may contain '='; only the first one separates.</summary>
    [Fact]
    public void AValueContainingAnEquals_SurvivesARoundTrip()
    {
        string path = Path_("eq.txt");
        ClashSet s = Set("odd", "id", "W=100 H=50");

        ClashSetTransferIo.Export(path, [s]);

        ClashSetTransfer back = Assert.Single(ClashSetTransferIo.Import(path));
        Assert.Equal("System Type", back.Values[0].Param);
        Assert.Equal("W=100 H=50", back.Values[0].Value);
    }

    /// <summary>Hand editing is expected, so junk lines are skipped, not fatal.</summary>
    [Fact]
    public void LinesThatMeanNothing_AreIgnoredRatherThanFailing()
    {
        string path = Path_("junky.txt");
        File.WriteAllText(path, """
            [Set]
              this line has no separator at all
              = value with no parameter
              System Type = good
              keyword:
            """);

        ClashSetTransfer one = Assert.Single(ClashSetTransferIo.Import(path));
        Assert.Equal(["good"], one.Values.Select(v => v.Value));
        Assert.Empty(one.Keywords);
    }

    [Fact]
    public void RulesBeforeAnyHeading_AreIgnored()
    {
        string path = Path_("orphan.txt");
        File.WriteAllText(path, """
            System Type = orphaned
            [Real]
              System Type = kept
            """);

        ClashSetTransfer one = Assert.Single(ClashSetTransferIo.Import(path));
        Assert.Equal("Real", one.Name);
        Assert.Equal(["kept"], one.Values.Select(v => v.Value));
    }

    /// <summary>The header this writes must not read back as a set.</summary>
    [Fact]
    public void TheExportedHeaderIsAllComments()
    {
        string path = Path_("header.txt");
        ClashSetTransferIo.Export(path, [Set("Only", "id", "v")]);

        Assert.Equal(["Only"], ClashSetTransferIo.Import(path).Select(s => s.Name));
    }

    /// <summary>Files exported before the format changed must still import.</summary>
    [Fact]
    public void TheOldJsonBundle_StillImports()
    {
        string path = Path_("legacy.json");
        File.WriteAllText(path, """
            {
              "Version": 1,
              "Note": "Exported from Ribbit on 2026-08-16.",
              "Sets": [
                { "Name": "52 Series", "Categories": [], "Keywords": ["52"],
                  "Values": [ { "Param": "System Type", "Value": "52 - Vent" } ] }
              ]
            }
            """);

        ClashSetTransfer one = Assert.Single(ClashSetTransferIo.Import(path));
        Assert.Equal("52 Series", one.Name);
        Assert.Equal(["52 - Vent"], one.Values.Select(v => v.Value));
        Assert.Equal(["52"], one.Keywords);
    }

    [Fact]
    public void AFileThatIsNotSets_FailsWithSomethingWorthShowing()
    {
        string path = Path_("junk.json");
        File.WriteAllText(path, """{ "something": "else" }""");

        InvalidDataException ex =
            Assert.Throws<InvalidDataException>(() => ClashSetTransferIo.Import(path));

        Assert.Contains("clash sets", ex.Message);
    }
}
