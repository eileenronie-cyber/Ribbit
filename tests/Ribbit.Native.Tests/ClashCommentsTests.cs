using Ribbit.Native.Revit;
using Xunit;

namespace Ribbit.Native.Tests;

/// <summary>
/// Clash notes - the file Set Clash Status writes and every report reads. It
/// lives in the SHARED clash folder, so two people can have it open at once:
/// each test that matters here is two editors racing.
/// </summary>
public class ClashCommentsTests : IDisposable
{
    private const string Tag = "ClashFederated";
    private const string Title = "Model";

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "ribbit-notes-" + Guid.NewGuid().ToString("N"));

    public ClashCommentsTests() => Directory.CreateDirectory(_dir);

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

    private ClashComments Load() => ClashComments.Load(_dir, Title, Tag);

    private void Save(ClashComments c) => Assert.Null(c.Save(_dir, Title, Tag));

    [Fact]
    public void ANote_RoundTrips()
    {
        ClashComments c = Load();
        c.Set("1|2", "  reroute the drain  ");
        Save(c);

        Assert.Equal("reroute the drain", Load().TextFor("1|2"));
    }

    [Fact]
    public void ABlankNote_RemovesTheEntry()
    {
        ClashComments c = Load();
        c.Set("1|2", "x");
        Save(c);

        ClashComments d = Load();
        d.Set("1|2", "   ");
        Save(d);

        Assert.Equal("", Load().TextFor("1|2"));
        Assert.False(Load().ByClash.ContainsKey("1|2"));
    }

    [Fact]
    public void TwoEditors_AddingDifferentNotes_KeepBoth()
    {
        ClashComments mine = Load();
        ClashComments theirs = Load();

        mine.Set("1|2", "mine");
        theirs.Set("3|4", "theirs");
        Save(mine);
        Save(theirs);

        ClashComments after = Load();
        Assert.Equal("mine", after.TextFor("1|2"));
        Assert.Equal("theirs", after.TextFor("3|4"));
    }

    [Fact]
    public void AnEdit_IsNotUndoneByAColleagueSavingSomethingElse()
    {
        ClashComments seed = Load();
        seed.Set("1|2", "old text");
        Save(seed);

        ClashComments mine = Load();
        ClashComments theirs = Load();    // has "old text" for 1|2, untouched

        mine.Set("1|2", "new text");
        Save(mine);

        theirs.Set("3|4", "their note");
        Save(theirs);                     // must not write its stale "old text" back

        Assert.Equal("new text", Load().TextFor("1|2"));
        Assert.Equal("their note", Load().TextFor("3|4"));
    }

    [Fact]
    public void ADeletion_IsNotUndoneByAColleagueSavingSomethingElse()
    {
        ClashComments seed = Load();
        seed.Set("1|2", "to be removed");
        Save(seed);

        ClashComments mine = Load();
        ClashComments theirs = Load();

        mine.Set("1|2", "");
        Save(mine);

        theirs.Set("3|4", "their note");
        Save(theirs);

        Assert.Equal("", Load().TextFor("1|2"));
    }

    [Fact]
    public void MyOwnDeletion_StaysDeleted_EvenThoughAColleagueStillHadIt()
    {
        ClashComments seed = Load();
        seed.Set("1|2", "x");
        Save(seed);

        ClashComments theirs = Load();
        theirs.Set("3|4", "their note");
        Save(theirs);

        ClashComments mine = Load();
        mine.Set("1|2", "");
        Save(mine);

        Assert.Equal("", Load().TextFor("1|2"));
        Assert.Equal("their note", Load().TextFor("3|4"));
    }

    // The file read fine when this editor opened, then was half-written - a
    // colleague's save, a cloud sync - at the moment this editor saved. "Cannot
    // read" must not be taken as "no notes", or the save writes back only its
    // own change and every other note is gone.
    [Fact]
    public void AFileUnreadableAtSave_KeepsEveryLoadedNote_AndIsKeptAside()
    {
        ClashComments seed = Load();
        seed.Set("1|2", "first");
        seed.Set("3|4", "second");
        Save(seed);

        ClashComments mine = Load();
        string path = Directory.GetFiles(_dir, "*comments.json").Single();
        File.WriteAllText(path, "{ \"ByClash\": { \"1|2\": ");    // half-written

        mine.Set("5|6", "mine");
        Save(mine);

        ClashComments after = Load();
        Assert.Equal("first", after.TextFor("1|2"));
        Assert.Equal("second", after.TextFor("3|4"));
        Assert.Equal("mine", after.TextFor("5|6"));
        Assert.True(File.Exists(path + ".unreadable"));
    }

    [Fact]
    public void AnUnreadableFile_IsMovedAside_NotOverwrittenSilently()
    {
        string path = Directory.GetFiles(_dir).FirstOrDefault()
                      ?? Path.Combine(_dir, $"{Title}_{Tag}_comments.json");
        File.WriteAllText(path, "{ not json");

        ClashComments c = Load();
        Assert.True(c.Corrupt);

        c.Set("1|2", "x");
        Save(c);

        Assert.True(File.Exists(path + ".unreadable"));
        Assert.Equal("x", Load().TextFor("1|2"));
    }
}
