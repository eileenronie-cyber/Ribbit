using Ribbit.Geometry.Sheets;
using Xunit;

namespace Ribbit.Tests;

/// <summary>
/// Resolving the Titleblock column. The load-bearing rule is that an ambiguous
/// or unknown name resolves to NOTHING — the planner turns that into a row error
/// rather than stamping the wrong titleblock on a real drawing.
/// </summary>
public class TitleblockResolverTests
{
    private static readonly List<string> Names =
    [
        "ABC Titleblock : A1 Landscape",
        "ABC Titleblock : A3 Portrait",
        "Legacy Titleblock : A1 Landscape",
    ];

    [Fact]
    public void FullDisplayName_Resolves()
        => Assert.Equal(1, TitleblockResolver.Resolve("ABC Titleblock : A3 Portrait", Names));

    // What people actually type in a drawing list.
    [Fact]
    public void UniqueTypeName_Resolves()
        => Assert.Equal(1, TitleblockResolver.Resolve("A3 Portrait", Names));

    [Fact]
    public void TypeNameOnTwoFamilies_IsAmbiguousAndResolvesToNothing()
        => Assert.Null(TitleblockResolver.Resolve("A1 Landscape", Names));

    [Fact]
    public void FamilyNameWithOneType_Resolves()
        => Assert.Equal(2, TitleblockResolver.Resolve("Legacy Titleblock", Names));

    [Fact]
    public void FamilyNameWithSeveralTypes_ResolvesToNothing()
        => Assert.Null(TitleblockResolver.Resolve("ABC Titleblock", Names));

    [Fact]
    public void MatchingIgnoresCaseAndSpacing()
        => Assert.Equal(1, TitleblockResolver.Resolve("  a3   portrait ", Names));

    [Fact]
    public void UnknownName_ResolvesToNothing()
        => Assert.Null(TitleblockResolver.Resolve("A0 Landscape", Names));

    [Fact]
    public void BlankCell_ResolvesToNothing()
        => Assert.Null(TitleblockResolver.Resolve("   ", Names));
}
