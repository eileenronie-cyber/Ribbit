using Ribbit.Native.Settings;
using Xunit;

namespace Ribbit.Native.Tests;

/// <summary>
/// The usernames a model title is normalised against. Getting one WRONG is worse
/// than missing one: a name that is really part of a model's title would fold
/// that model's ledger into another model's.
/// </summary>
[Collection("KnownUsers")]
public class KnownUsersTests : IDisposable
{
    private const string Tag = "ClashFederated";
    private const string Base = "PRJ01-ABC-ZZ-ZZ-ZZ-M3-N-000001-09-02-2026";

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "ribbit-users-" + Guid.NewGuid().ToString("N"));

    public KnownUsersTests()
    {
        Directory.CreateDirectory(_dir);
        KnownUsers.PathOverride = Path.Combine(_dir, "store", "known_users.txt");
    }

    public void Dispose()
    {
        KnownUsers.PathOverride = null;
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    private void Touch(string name) => File.WriteAllText(Path.Combine(_dir, name), "");

    [Fact]
    public void TheCurrentUser_IsFirst_AndRemembered()
    {
        Assert.Equal(["me"], KnownUsers.For("me"));
        Assert.Equal(["you", "me"], KnownUsers.For("you"));
        Assert.Equal(["me", "you"], KnownUsers.For("me"));
    }

    [Fact]
    public void NoCurrentUser_StillGivesTheRememberedOnes()
    {
        KnownUsers.For("me");
        Assert.Equal(["me"], KnownUsers.For(null));
    }

    // The real folder: a colleague's local was migrated onto the plain name.
    [Fact]
    public void AColleague_IsRecoveredFromARetiredFile()
    {
        Touch($"{Base}_{Tag}_ledger.csv");
        Touch($"{Base}_jdoe1234ABCD_{Tag}_ledger.csv.migrated");
        Touch($"{Base}_jdoe1234ABCD_detached_{Tag}_seen.json.migrated");

        Assert.Equal(["jdoe1234ABCD"], KnownUsers.FromRetiredFiles(_dir, Tag));
        Assert.Contains("jdoe1234ABCD", KnownUsers.For("asmith5678WXYZ", _dir, Tag));

        // Remembered, so other folders benefit.
        Assert.Contains("jdoe1234ABCD", KnownUsers.For("asmith5678WXYZ"));
    }

    [Fact]
    public void ADetachOnlyRetirement_TeachesNoUser()
    {
        // "Plant_Annex_detached" was merged onto "Plant_Annex", which is a model
        // of its own beside "Plant". "Annex" is not a person.
        Touch($"Plant_{Tag}_ledger.csv");
        Touch($"Plant_Annex_{Tag}_ledger.csv");
        Touch($"Plant_Annex_detached_{Tag}_ledger.csv.migrated");

        Assert.Empty(KnownUsers.FromRetiredFiles(_dir, Tag));
    }

    [Fact]
    public void ARetiredNameWithNothingLiveBeneathIt_TeachesNoUser()
    {
        Touch($"Plant_bob_{Tag}_ledger.csv.migrated");

        Assert.Empty(KnownUsers.FromRetiredFiles(_dir, Tag));
    }

    [Fact]
    public void LiveVariantFiles_TeachNothing()
    {
        Touch($"{Base}_{Tag}_ledger.csv");
        Touch($"{Base}_bob_{Tag}_ledger.csv");

        Assert.Empty(KnownUsers.FromRetiredFiles(_dir, Tag));
    }

    [Fact]
    public void AMissingFolder_IsNoUsers()
        => Assert.Empty(KnownUsers.FromRetiredFiles(Path.Combine(_dir, "nope"), Tag));

    // The store is cached between reads; a name another Revit on this PC wrote
    // must still be seen.
    [Fact]
    public void AnotherSessionsWrite_IsSeen()
    {
        KnownUsers.For("me");
        File.WriteAllLines(KnownUsers.StorePath, ["me", "colleague"]);
        File.SetLastWriteTimeUtc(KnownUsers.StorePath, DateTime.UtcNow.AddMinutes(1));

        Assert.Equal(["me", "colleague"], KnownUsers.For("me"));
    }

    [Fact]
    public void WithoutAProductTag_NothingIsRecovered()
    {
        Touch($"{Base}_{Tag}_ledger.csv");
        Touch($"{Base}_bob_{Tag}_ledger.csv.migrated");

        Assert.Empty(KnownUsers.FromRetiredFiles(_dir, null));
        Assert.Equal(["bob"], KnownUsers.FromRetiredFiles(_dir, Tag));
    }
}
