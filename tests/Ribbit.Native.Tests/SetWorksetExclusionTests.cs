using Ribbit.Native.Settings;
using Xunit;

namespace Ribbit.Native.Tests;

/// <summary>
/// A set's "Worksets left out" must survive every path a set travels: the
/// library file, an edit, a duplicate, an export and an import.
///
/// The paths that copy a set's rules each copied them FIELD BY FIELD, so a new
/// field is lost by whichever path nobody remembered - an edit made in Clash
/// Detect, which never shows the list, would have wiped it. Every one of those
/// paths is here.
///
/// The library file is pointed at scratch (<see cref="ClashSetLibrary.PathOverride"/>):
/// the first version of the set tests wrote into the user's real library.
/// Nothing here calls <c>ClashSet.Resolved</c> or <c>Describe</c>, which need
/// RevitAPI loaded - those are covered by the in-Revit suite ("fedws").
/// </summary>
[Collection("set library file")]
public class SetWorksetExclusionTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "ribbit-ws-exclusion-" + Guid.NewGuid().ToString("N"));

    public SetWorksetExclusionTests()
    {
        Directory.CreateDirectory(_dir);
        ClashSetLibrary.PathOverride = Path.Combine(_dir, "clash_sets.json");
    }

    public void Dispose()
    {
        ClashSetLibrary.PathOverride = null;

        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    private static ClashSet Set(string name, params string[] excluded) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = name,
        Values = [new SetValue { Param = "System Type", Value = "52 - Vent" }],
        Keywords = ["52"],
        Categories = ["OST_PipeCurves"],
        ExcludedWorksets = [.. excluded],
    };

    // ------------------------------------------------------------ the library

    [Fact]
    public void SavedAndLoaded_TheListComesBack()
    {
        ClashSetLibrary lib = new() { Sets = [Set("52 Series", "MEP_ZZ_Typical", "MEP_01_BWIC")] };
        lib.Save();

        ClashSet loaded = Assert.Single(ClashSetLibrary.Load().Sets);
        Assert.Equal(["MEP_ZZ_Typical", "MEP_01_BWIC"], loaded.ExcludedWorksets);
    }

    [Fact]
    public void AFileWrittenBeforeTheList_LoadsAsLeavingNothingOut()
    {
        File.WriteAllText(ClashSetLibrary.PathOverride!,
            """{ "Sets": [ { "Id": "a1", "Name": "Old", "Keywords": ["52"] } ] }""");

        Assert.Empty(Assert.Single(ClashSetLibrary.Load().Sets).ExcludedWorksets);
    }

    [Fact]
    public void AHandEditedNull_LoadsAsEmpty_NotNull()
    {
        File.WriteAllText(ClashSetLibrary.PathOverride!,
            """{ "Sets": [ { "Id": "a1", "Name": "Old", "Keywords": ["52"], "ExcludedWorksets": null } ] }""");

        ClashSet s = Assert.Single(ClashSetLibrary.Load().Sets);
        Assert.NotNull(s.ExcludedWorksets);
        Assert.Empty(s.ExcludedWorksets);
    }

    // ------------------------------------------------- edit, duplicate, copy

    [Fact]
    public void AnEdit_CarriesTheList_ThroughCopyRulesFrom()
    {
        ClashSet inLibrary = Set("52 Series");
        ClashSet edited = Set("52 Series", "MEP_ZZ_Typical");
        edited.Keywords = ["52", "53"];

        string id = inLibrary.Id;
        inLibrary.CopyRulesFrom(edited);

        Assert.Equal(["MEP_ZZ_Typical"], inLibrary.ExcludedWorksets);
        Assert.Equal(["52", "53"], inLibrary.Keywords);
        Assert.Equal(id, inLibrary.Id);
    }

    [Fact]
    public void AnEditThatClearsTheList_Clears_It()
    {
        ClashSet inLibrary = Set("52 Series", "MEP_ZZ_Typical");
        inLibrary.CopyRulesFrom(Set("52 Series"));

        Assert.Empty(inLibrary.ExcludedWorksets);
    }

    [Fact]
    public void ADuplicate_CarriesTheList_UnderANewId()
    {
        ClashSet original = Set("52 Series", "MEP_ZZ_Typical");
        ClashSet copy = original.Copy();

        Assert.Equal(["MEP_ZZ_Typical"], copy.ExcludedWorksets);
        Assert.NotEqual(original.Id, copy.Id);

        // A copy, not a shared list: editing one must not edit the other.
        copy.ExcludedWorksets.Add("X");
        Assert.Single(original.ExcludedWorksets);
    }

    // ------------------------------------------------------ export and import

    private string Path_(string name) => Path.Combine(_dir, name);

    [Fact]
    public void ExportThenImport_CarriesTheList_IncludingAwkwardNames()
    {
        string path = Path_("sets.txt");
        ClashSetTransferIo.Export(path, [Set("52 Series", "MEP_ZZ_Typical", "Zone=A", "Level: 02")]);

        ClashSetTransfer back = Assert.Single(ClashSetTransferIo.Import(path));

        Assert.Equal(["MEP_ZZ_Typical", "Zone=A", "Level: 02"], back.ExcludedWorksets);

        // "Zone=A" must not have turned into a VALUE of a parameter called
        // "exclude workset: Zone" - the prefix is read before the '=' split.
        Assert.Single(back.Values);
        Assert.Equal("System Type", back.Values[0].Param);
    }

    [Fact]
    public void TheWrittenLine_IsTheDocumentedShape()
    {
        string path = Path_("sets.txt");
        ClashSetTransferIo.Export(path, [Set("52 Series", "MEP_ZZ_Typical")]);

        Assert.Contains(
            File.ReadAllLines(path),
            l => l.Trim() == "exclude workset: MEP_ZZ_Typical");
    }

    [Fact]
    public void AnOlderListing_SaysNothingAboutWorksets_NotThatThereAreNone()
    {
        string path = Path_("old.txt");
        File.WriteAllText(path, "[52 Series]\n  System Type = 52 - Vent\n");

        Assert.Null(Assert.Single(ClashSetTransferIo.Import(path)).ExcludedWorksets);
    }

    [Fact]
    public void ImportUpdate_FromAnOlderFile_KeepsTheLocalList()
    {
        ClashSet existing = Set("52 Series", "MEP_ZZ_Typical");
        ClashSetLibrary lib = new() { Sets = [existing] };

        string path = Path_("old.txt");
        File.WriteAllText(path, "[52 Series]\n  System Type = 52 - Vent\n  keyword: 53\n");

        lib.Import(ClashSetTransferIo.Import(path), ClashSetLibrary.ImportClash.Update);

        ClashSet after = Assert.Single(lib.Sets);
        Assert.Equal(["MEP_ZZ_Typical"], after.ExcludedWorksets);   // kept
        Assert.Equal(["53"], after.Keywords);                       // the rules did update
    }

    [Fact]
    public void ImportUpdate_FromAFileThatCanSay_ClearsTheList_WhenItListsNone()
    {
        // Exported by this build: it carries the format line, so a set with no
        // "exclude workset" lines genuinely leaves nothing out.
        string path = Path_("new.txt");
        ClashSetTransferIo.Export(path, [Set("52 Series")]);

        ClashSet existing = Set("52 Series", "MEP_ZZ_Typical");
        ClashSetLibrary lib = new() { Sets = [existing] };
        lib.Import(ClashSetTransferIo.Import(path), ClashSetLibrary.ImportClash.Update);

        Assert.Empty(Assert.Single(lib.Sets).ExcludedWorksets);
    }

    [Theory]
    [InlineData("format: 2")]
    [InlineData("format:2")]
    [InlineData("FORMAT: 3")]       // a later build's file still carries the list
    public void AnyFormatFromTwoUp_CarriesWorksets_SoNoneMeansNone(string marker)
    {
        string path = Path_("fmt.txt");
        File.WriteAllText(path, $"{marker}\n[52 Series]\n  keyword: 52\n");

        Assert.Empty(Assert.Single(ClashSetTransferIo.Import(path)).ExcludedWorksets!);
    }

    [Theory]
    [InlineData("format: 1")]
    [InlineData("format: two")]
    public void AnOlderOrUnreadableFormat_DoesNotClaimToCarryThem(string marker)
    {
        string path = Path_("fmt.txt");
        File.WriteAllText(path, $"{marker}\n[52 Series]\n  keyword: 52\n");

        Assert.Null(Assert.Single(ClashSetTransferIo.Import(path)).ExcludedWorksets);
    }

    [Fact]
    public void AHandWrittenFile_WithoutTheFormatLine_StillCarriesTheLinesItHas()
    {
        string path = Path_("hand.txt");
        File.WriteAllText(path, "[A]\n  keyword: 52\n  exclude workset: MEP_ZZ_Typical\n[B]\n  keyword: 53\n");

        List<ClashSetTransfer> sets = ClashSetTransferIo.Import(path);

        Assert.Equal(["MEP_ZZ_Typical"], sets[0].ExcludedWorksets);
        Assert.Null(sets[1].ExcludedWorksets);
    }

    [Fact]
    public void AJsonBundleWithoutTheProperty_SaysNothingAboutWorksets()
    {
        string path = Path_("old.json");
        File.WriteAllText(path, """{ "Version": 1, "Sets": [ { "Name": "52 Series", "Keywords": ["52"] } ] }""");

        Assert.Null(Assert.Single(ClashSetTransferIo.Import(path)).ExcludedWorksets);
    }

    [Fact]
    public void ASetOfExclusionsAlone_IsNotASet()
    {
        string path = Path_("only.txt");
        File.WriteAllText(path, "[Nothing]\n  exclude workset: MEP_ZZ_Typical\n[Real]\n  keyword: 52\n");

        ClashSetTransfer s = Assert.Single(ClashSetTransferIo.Import(path));
        Assert.Equal("Real", s.Name);
    }

    [Fact]
    public void ImportUpdate_ReplacesTheList_AndKeepsTheId()
    {
        ClashSet existing = Set("52 Series", "Old_WS");
        ClashSetLibrary lib = new() { Sets = [existing] };
        string id = existing.Id;

        ClashSetTransfer incoming = new()
        {
            Name = "52 Series",
            Keywords = ["52"],
            ExcludedWorksets = ["MEP_ZZ_Typical"],
        };

        lib.Import([incoming], ClashSetLibrary.ImportClash.Update);

        ClashSet after = Assert.Single(lib.Sets);
        Assert.Equal(id, after.Id);
        Assert.Equal(["MEP_ZZ_Typical"], after.ExcludedWorksets);
    }

    [Fact]
    public void ImportAsNew_CarriesTheList()
    {
        ClashSetLibrary lib = new();

        lib.Import(
            [new ClashSetTransfer { Name = "52 Series", Keywords = ["52"], ExcludedWorksets = [" MEP_01_BWIC "] }],
            ClashSetLibrary.ImportClash.KeepBoth);

        Assert.Equal(["MEP_01_BWIC"], Assert.Single(lib.Sets).ExcludedWorksets);
    }
}
