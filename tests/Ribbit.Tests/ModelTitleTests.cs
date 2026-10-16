using Ribbit.Geometry.Clash;
using Xunit;

namespace Ribbit.Tests;

/// <summary>
/// One model opened three ways must resolve to one name, or its triage splits
/// across as many ledgers as there are ways of opening it.
/// </summary>
public class ModelTitleTests
{
    private const string User = "jdoe1234ABCD";
    private const string Base = "PRJ01-ABC-ZZ-ZZ-ZZ-M3-N-000001-09-02-2026";

    // The four titles one model actually produced in a single afternoon.
    [Theory]
    [InlineData(Base)]
    [InlineData($"{Base}_{User}")]
    [InlineData($"{Base}_detached")]
    [InlineData($"{Base}_{User}_detached")]
    public void EveryOpenMethodResolvesToTheSameName(string title)
        => Assert.Equal(Base, ModelTitle.Normalize(title, User));

    // Order is not guaranteed, so both stackings must come apart.
    [Fact]
    public void SuffixesComeOffInEitherOrder()
        => Assert.Equal(Base, ModelTitle.Normalize($"{Base}_detached_{User}", User));

    [Fact]
    public void TheSuffixMustBeItsOwnUnderscoredPart()
        => Assert.Equal("PlantDetached", ModelTitle.Normalize("PlantDetached", User));

    [Fact]
    public void CaseDoesNotMatter()
        => Assert.Equal(Base, ModelTitle.Normalize($"{Base}_DETACHED", User));

    // A prefix of "" would collide with every other model in the folder.
    [Fact]
    public void ATitleThatIsNothingButTheSuffixKeepsItsName()
        => Assert.Equal("_detached", ModelTitle.Normalize("_detached", User));

    [Fact]
    public void NoUsernameStillStripsDetached()
        => Assert.Equal(Base, ModelTitle.Normalize($"{Base}_detached", (string?)null));

    // Without this the migration would find the current title as a variant of
    // itself and merge the folder into the file it is already using.
    [Fact]
    public void TheNormalizedTitleIsNotAVariantOfItself()
        => Assert.False(ModelTitle.IsVariantOf(Base, Base, User));

    [Fact]
    public void ASuffixedTitleIsAVariant()
        => Assert.True(ModelTitle.IsVariantOf($"{Base}_{User}_detached", Base, User));

    [Fact]
    public void ADifferentModelIsNotAVariant()
        => Assert.False(ModelTitle.IsVariantOf($"OTHER-MODEL_{User}", Base, User));

    // The migration reads titles back out of FILENAMES, which are sanitised, so
    // the two have to agree on what the title looks like.
    [Fact]
    public void TheSanitizedTitleMatchesTheStartOfThePrefix()
        => Assert.StartsWith(
            ClashStatus.SanitizeModelTitle(Base),
            ClashStatus.ModelCsvPrefix(Base, "ClashFederated"));

    [Fact]
    public void SanitizingSurvivesNormalizing()
        => Assert.Equal(
            ClashStatus.SanitizeModelTitle(Base),
            ModelTitle.Normalize(ClashStatus.SanitizeModelTitle($"{Base}_{User}_detached"), User));

    // The property FederatedSources.Matches relies on for a test pinned to the
    // host by a DIFFERENT person: each side strips only ITS OWN username, but
    // both land on the same canonical title regardless of whose username was
    // active when. Without this, a test saved by a colleague under their
    // username could never match the host again on anyone else's machine.
    [Fact]
    public void TwoUsersNormalizingTheirOwnOpenTitle_ConvergeOnTheSameName()
    {
        const string colleague = "asmith5678WXYZ";

        string savedByColleague = ModelTitle.Normalize($"{Base}_{colleague}_detached", colleague);
        string seenByMe = ModelTitle.Normalize($"{Base}_{User}", User);

        Assert.Equal(Base, savedByColleague);
        Assert.Equal(Base, seenByMe);
    }

    // ---------------------------------------------------- numbered detaches

    // Detaching again numbers the copy. Both of these are in the field.
    [Theory]
    [InlineData($"{Base}_detached2")]
    [InlineData($"{Base}_detached26")]
    [InlineData($"{Base}_detached_detached2")]
    [InlineData($"{Base}_{User}_detached26")]
    [InlineData($"{Base}_{User}_DETACHED26")]
    public void NumberedDetaches_Normalize(string title)
        => Assert.Equal(Base, ModelTitle.Normalize(title, User));

    [Fact]
    public void ANumberWithoutTheDetachedSuffix_IsKept()
        => Assert.Equal($"{Base}_26", ModelTitle.Normalize($"{Base}_26", User));

    [Fact]
    public void DetachedJoinedToTheNumberWithoutAnUnderscore_IsKept()
        => Assert.Equal("Plantdetached2", ModelTitle.Normalize("Plantdetached2", User));

    [Fact]
    public void ATitleThatIsOnlyANumberedSuffix_KeepsItsName()
        => Assert.Equal("_detached2", ModelTitle.Normalize("_detached2", User));

    // ------------------------------------------------------ several users

    // The real file: detached from a colleague's local, opened by someone else.
    [Fact]
    public void AColleaguesNameIsStripped_WhenTheyAreKnown()
    {
        const string me = "asmith5678WXYZ";
        string title = $"{Base}_{User}_detached26";

        Assert.Equal($"{Base}_{User}", ModelTitle.Normalize(title, me));
        Assert.Equal(Base, ModelTitle.Normalize(title, [me, User]));
        Assert.True(ModelTitle.IsVariantOf(title, Base, [me, User]));
        Assert.False(ModelTitle.IsVariantOf(title, Base, me));
    }

    [Fact]
    public void UsernamesStackInAnyOrder()
        => Assert.Equal(Base, ModelTitle.Normalize($"{Base}_a_detached_b_detached3", ["b", "a"]));

    [Fact]
    public void NullAndBlankUsers_AreIgnored()
        => Assert.Equal(Base, ModelTitle.Normalize($"{Base}_detached2", [null, " ", ""]));
}
