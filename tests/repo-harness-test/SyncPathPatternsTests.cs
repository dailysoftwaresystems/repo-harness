using RepoHarness.Core.Configuration;
using RepoHarness.Core.Sync;

namespace RepoHarness.Tests;

/// <summary>
/// What sync.neverTransfer and sync.exclude cover. One matcher decides it for both sides of a sync,
/// because a file protected by the plan and listed by the host's walk is a file one side deletes
/// and the other keeps.
/// </summary>
public sealed class SyncPathPatternsTests
{
    /// <summary>
    /// An entry is rooted unless it says otherwise, which is what every configuration written
    /// before this means. A bare name covering that name at any depth would quietly stop
    /// transferring nested directories to every host, and a build needing one would start failing
    /// for a reason nothing in the file changed to cause.
    /// </summary>
    [Theory]
    [InlineData("build", "build", true)]
    [InlineData("build", "build/app.o", true)]
    [InlineData("build", "src/build", false)]
    [InlineData("build", "src/build/app.o", false)]
    [InlineData("build", "rebuild", false)]
    public void ABareEntry_IsRooted(string pattern, string path, bool covered)
        => Assert.Equal(covered, SyncPathPatterns.Matches([pattern], path));

    /// <summary>
    /// A cache directory appears wherever its language put it, and a list that can only name one
    /// path at a time is a list nobody can keep correct. Measured on a consumer's host: two
    /// directories a deletion wave emptied survived because __pycache__ remained in them.
    /// </summary>
    [Theory]
    [InlineData("**/__pycache__", "__pycache__", true)]
    [InlineData("**/__pycache__", "scripts/a/__pycache__", true)]
    [InlineData("**/__pycache__", "scripts/a/__pycache__/x.pyc", true)]
    [InlineData("**/__pycache__", "scripts/a/x.py", false)]
    [InlineData("**/node_modules", "web/ui/node_modules/left-pad/index.js", true)]
    public void AnAnyDepthEntry_CoversThatNameWhereverItIs(string pattern, string path, bool covered)
        => Assert.Equal(covered, SyncPathPatterns.Matches([pattern], path));

    /// <summary>
    /// On whole segments. Without that, '**/cache' would cover 'src/mycache', a different directory
    /// with a similar name, and the reader would never see it go.
    /// </summary>
    [Theory]
    [InlineData("src/mycache")]
    [InlineData("src/mycache/x")]
    [InlineData("cacheable")]
    public void AnAnyDepthEntry_MatchesWholeSegmentsOnly(string path)
        => Assert.False(SyncPathPatterns.Matches(["**/cache"], path));

    /// <summary>
    /// A pattern that looks like a glob and is compared as text matches nothing. Refused where the
    /// configuration is read, so the reader gets the line rather than a list that silently protects
    /// nothing.
    /// </summary>
    [Theory]
    [InlineData("src/**/cache")]
    [InlineData("*.pyc")]
    [InlineData("**/")]
    [InlineData("")]
    public void APatternThisCannotUnderstand_IsRefused(string pattern)
        => Assert.NotNull(SyncPathPatterns.Problem(pattern));

    [Theory]
    [InlineData("build")]
    [InlineData("**/__pycache__")]
    [InlineData(".harness-config")]
    public void APatternThisUnderstands_IsAccepted(string pattern)
        => Assert.Null(SyncPathPatterns.Problem(pattern));

    /// <summary>Both spellings arrive, and a Windows source must match a Linux copy.</summary>
    [Theory]
    [InlineData(@"scripts\a\__pycache__")]
    [InlineData("./scripts/a/__pycache__")]
    [InlineData("/scripts/a/__pycache__/")]
    public void APathSpelledAnyWay_IsTheSamePath(string path)
        => Assert.True(SyncPathPatterns.Matches(["**/__pycache__"], path));
}

/// <summary>
/// A protective rule that silently matches nothing is worse than no rule: the name in the file
/// reads as evidence the thing is protected, and any reader stops there. Measured on a consumer's
/// tree, of seventeen entries three protected nothing — one of them '.secrets', while a second
/// .secrets three levels down, the directory this tool's own design names as where a host
/// credential goes, was covered by nothing.
/// </summary>
public sealed class SyncExclusionsReachTests
{
    [Fact]
    public void ARootedEntryThatMatchesNothing_WhileTheNameExistsDeeper_IsNamed()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();

        // No .secrets at the root; one three levels down, exactly the shape that was measured.
        Directory.CreateDirectory(temp.Combine(".harness-config", "sshItems", "vps", ".secrets"));
        Directory.CreateDirectory(temp.Combine("build"));

        var exclusions = new SyncExclusions(
            new SyncConfig { NeverTransfer = [".secrets", "build"] },
            worktreesRoot: ".worktrees");

        var named = exclusions.RootedEntriesMatchingNothing(
            harness.FileSystem, temp.Path, TestContext.Current.CancellationToken);

        // '.secrets' protects nothing and is named; 'build' is at the root and is not.
        Assert.Equal([".secrets"], named);
    }

    /// <summary>
    /// An entry already written for any depth is doing what it says, and a name that exists nowhere
    /// at all is not evidence of anything — neither is worth a line somebody has to read past.
    /// </summary>
    [Fact]
    public void AnEntryThatSaysAnyDepth_AndOneThatMatchesNothingAnywhere_AreBothLeftAlone()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();

        Directory.CreateDirectory(temp.Combine("scripts", "a", "__pycache__"));

        var exclusions = new SyncExclusions(
            new SyncConfig { NeverTransfer = ["**/__pycache__", "nothing-like-this"] },
            worktreesRoot: ".worktrees");

        Assert.Empty(exclusions.RootedEntriesMatchingNothing(
            harness.FileSystem, temp.Path, TestContext.Current.CancellationToken));
    }
}
