using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Platform;

namespace RepoHarness.Tests;

/// <summary>
/// Containment guards the one recursive delete the harness performs, so every doubtful
/// case must answer no.
/// </summary>
public sealed class PathContainmentTests
{
    private static readonly string Root = Path.Combine(TestHost.TemporaryRoot, "containment", "worktrees");

    private static readonly StringComparison Comparison = new HostPlatform().PathComparison;

    [Fact]
    public void AChild_IsInside()
    {
        Assert.True(IsInside(Path.Combine(Root, "wt")));
    }

    [Fact]
    public void ADeeperDescendant_IsInside()
    {
        Assert.True(IsInside(Path.Combine(Root, "wt", "src", "main.c")));
    }

    [Fact]
    public void TheRoot_IsNotInsideItself_WithOrWithoutATrailingSeparator()
    {
        Assert.False(IsInside(Root));
        Assert.False(IsInside(Root + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void ASiblingThatSharesThePrefix_IsNotInside()
    {
        // A bare prefix test would accept this, and delete a directory beside the target.
        Assert.False(IsInside(Root + "-other" + Path.DirectorySeparatorChar + "wt"));
    }

    [Fact]
    public void ParentSegments_CannotEscape()
    {
        Assert.False(IsInside(Path.Combine(Root, "..", "outside")));
        Assert.False(IsInside(Path.Combine(Root, "wt", "..", "..", "outside")));
        Assert.False(IsInside(Path.Combine(Root, "wt", "..")));
    }

    [Fact]
    public void ParentSegmentsThatStayInside_AreInside()
    {
        Assert.True(IsInside(Path.Combine(Root, "a", "..", "wt")));
    }

    [Fact]
    public void AFilesystemRoot_ContainsEverythingBelowIt_ButNotItself()
    {
        var filesystemRoot = Path.GetPathRoot(Root)!;

        Assert.True(PathContainment.IsStrictlyInside(filesystemRoot, Root, Comparison));
        Assert.False(PathContainment.IsStrictlyInside(filesystemRoot, filesystemRoot, Comparison));
    }

    [Fact]
    public void Case_FollowsTheComparisonGiven()
    {
        var differentCase = Path.Combine(Root.ToUpperInvariant(), "wt");

        Assert.True(PathContainment.IsStrictlyInside(Root, differentCase, StringComparison.OrdinalIgnoreCase));
        Assert.False(PathContainment.IsStrictlyInside(Root, differentCase, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AMissingPath_IsRejected(string blank)
    {
        Assert.ThrowsAny<ArgumentException>(() => PathContainment.IsStrictlyInside(blank, Root, Comparison));
        Assert.ThrowsAny<ArgumentException>(() => PathContainment.IsStrictlyInside(Root, blank, Comparison));
    }

    private static bool IsInside(string candidate) => PathContainment.IsStrictlyInside(Root, candidate, Comparison);
}
