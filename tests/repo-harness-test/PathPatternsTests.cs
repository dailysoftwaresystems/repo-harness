using RepoHarness.Core.Configuration;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Sync;

namespace RepoHarness.Tests;

/// <summary>
/// What sync.neverTransfer and sync.exclude cover. One matcher decides it for both sides of a sync,
/// because a file protected by the plan and listed by the host's walk is a file one side deletes
/// and the other keeps.
/// </summary>
public sealed class PathPatternsTests
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
        => Assert.Equal(covered, PathPatterns.Matches([pattern], path));

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
        => Assert.Equal(covered, PathPatterns.Matches([pattern], path));

    /// <summary>
    /// On whole segments. Without that, '**/cache' would cover 'src/mycache', a different directory
    /// with a similar name, and the reader would never see it go.
    /// </summary>
    [Theory]
    [InlineData("src/mycache")]
    [InlineData("src/mycache/x")]
    [InlineData("cacheable")]
    public void AnAnyDepthEntry_MatchesWholeSegmentsOnly(string path)
        => Assert.False(PathPatterns.Matches(["**/cache"], path));

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
    [InlineData("build/./x")]
    [InlineData("build//x")]
    [InlineData("**/./x")]
    public void APatternThisCannotUnderstand_IsRefused(string pattern)
        => Assert.NotNull(PathPatterns.Problem(pattern));

    [Theory]
    [InlineData("build")]
    [InlineData("**/__pycache__")]
    [InlineData(".harness-config")]
    [InlineData("./build")]
    [InlineData("tools/build/")]
    public void APatternThisUnderstands_IsAccepted(string pattern)
        => Assert.Null(PathPatterns.Problem(pattern));

    /// <summary>
    /// A path spelled with a '.' segment or a doubled separator inside it names, to the file system, the
    /// path without them - and compared as written, matches nothing. Said with the one spelling to write;
    /// a leading './' and a trailing separator are read past everywhere, and are not counted.
    /// </summary>
    [Theory]
    [InlineData(".harness-config/./worktrees", ".harness-config/worktrees")]
    [InlineData("wt//lanes", "wt/lanes")]
    [InlineData("**/./x", "**/x")]
    public void APathSpelledTwoWays_IsNamedWithTheOneSpelling(string path, string spelling)
    {
        Assert.EndsWith($"write '{spelling}'", PathPatterns.Misspelling(path), StringComparison.Ordinal);
        Assert.EndsWith($"write '{spelling}'", PathPatterns.Problem(path), StringComparison.Ordinal);
    }

    /// <summary>Both spellings arrive, and a Windows source must match a Linux copy.</summary>
    [Theory]
    [InlineData(@"scripts\a\__pycache__")]
    [InlineData("./scripts/a/__pycache__")]
    [InlineData("/scripts/a/__pycache__/")]
    public void APathSpelledAnyWay_IsTheSamePath(string path)
        => Assert.True(PathPatterns.Matches(["**/__pycache__"], path));
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
            harness.FileSystem,
            temp.Path,
            harness.Platform.PathComparison,
            TestContext.Current.CancellationToken);

        // '.secrets' protects nothing and is named; 'build' is at the root and is not.
        Assert.Equal([".secrets"], named.MatchingNothing);
        Assert.Null(named.Incomplete);
    }

    /// <summary>
    /// What a sync.neverTransfer entry covers, and the worktrees root, are not searched: what they hold
    /// is kept from every sync already, by an entry the configuration's author wrote, and each worktree
    /// is a checkout of its own. Measured on a consumer's tree, nearly every directory lay in there, and a
    /// search that went in spent its whole budget before it reached the rest, saying so on every sync.
    /// Left out, the search finishes, and still names the entry a name outside them shows protects
    /// nothing - never one whose name is found only in there.
    /// </summary>
    [Fact]
    public void WhatAnotherEntryWithholds_IsNotSearched_SoTheSearchFinishes()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();

        for (var index = 0; index < 20; index++)
        {
            Directory.CreateDirectory(temp.Combine("build", $"b{index}"));
            Directory.CreateDirectory(temp.Combine(".worktrees", $"w{index}", "node_modules"));
        }

        Directory.CreateDirectory(temp.Combine("build", "lib", ".secrets"));
        Directory.CreateDirectory(temp.Combine("scripts", "a", "__pycache__"));

        var exclusions = new SyncExclusions(
            new SyncConfig { NeverTransfer = ["build", "__pycache__", ".secrets", "node_modules"] },
            worktreesRoot: ".worktrees");

        var report = exclusions.RootedEntriesMatchingNothing(
            harness.FileSystem,
            temp.Path,
            harness.Platform.PathComparison,
            mostDirectoriesRead: 10,
            TestContext.Current.CancellationToken);

        Assert.Equal(["__pycache__"], report.MatchingNothing);
        Assert.Null(report.Incomplete);
    }

    /// <summary>
    /// The harness's own directory is still searched, though git ignores most of it by design - a host's
    /// directory under sshItems among it, as git lists it to a sync under init's own rules, or all of it
    /// in a tree that ignores the whole directory - since a .secrets there is what this was written to
    /// find. The worktrees root inside it, where it is by default, is not gone into, however many
    /// directories its worktrees hold.
    /// </summary>
    [Theory]
    [InlineData(".harness-config/sshItems/vps")]
    [InlineData(".harness-config")]
    public void TheHarnessDirectory_IsSearched_ButNotTheWorktreesRootInsideIt(string ignored)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();

        Directory.CreateDirectory(temp.Combine(".harness-config", "sshItems", "vps", ".secrets"));
        Directory.CreateDirectory(temp.Combine(".harness-config", "worktrees", "lane", "node_modules"));

        for (var index = 0; index < 20; index++)
        {
            Directory.CreateDirectory(temp.Combine(".harness-config", "worktrees", "lane", $"src{index}"));
        }

        var exclusions = new SyncExclusions(
            new SyncConfig { NeverTransfer = [".secrets", "node_modules"] },
            worktreesRoot: ".harness-config/worktrees",
            gitIgnored: [ignored, ".harness-config/worktrees"]);

        var report = exclusions.RootedEntriesMatchingNothing(
            harness.FileSystem,
            temp.Path,
            harness.Platform.PathComparison,
            mostDirectoriesRead: 10,
            TestContext.Current.CancellationToken);

        Assert.Equal([".secrets"], report.MatchingNothing);
        Assert.Null(report.Incomplete);
    }

    /// <summary>
    /// Where what is searched holds more directories than the search may read, it says so - never
    /// "found none" - and says what it did not count.
    /// </summary>
    [Fact]
    public void ASearchThatRunsOutOfDirectories_SaysSo_AndWhatItDidNotCount()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();

        for (var index = 0; index < 20; index++)
        {
            Directory.CreateDirectory(temp.Combine("src", $"s{index}"));
        }

        var exclusions = new SyncExclusions(new SyncConfig { NeverTransfer = [".secrets"] }, worktreesRoot: ".worktrees");

        var report = exclusions.RootedEntriesMatchingNothing(
            harness.FileSystem,
            temp.Path,
            harness.Platform.PathComparison,
            mostDirectoriesRead: 5,
            TestContext.Current.CancellationToken);

        Assert.Empty(report.MatchingNothing);
        Assert.Equal(
            $"more than 5 directories under '{temp.Path}' would have had to be read, though none a sync "
            + "withholds is read outside the harness's own directory",
            report.Incomplete);
    }

    /// <summary>
    /// A name counts only where nothing the configuration writes covers it: an entry for any depth, as the
    /// warning advises writing, and the worktrees root. Counted, the author who wrote **/.env as advised
    /// would be told to write it on every sync.
    /// </summary>
    [Fact]
    public void ANameTheConfigurationAlreadyCovers_DoesNotCount()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();

        temp.WriteFile(Path.Combine("services", "api", ".env"), "KEY=value\n");
        Directory.CreateDirectory(temp.Combine(".harness-config", "worktrees", "lane"));

        var exclusions = new SyncExclusions(
            new SyncConfig { NeverTransfer = [".env", "**/.env", "worktrees"] },
            worktreesRoot: ".harness-config/worktrees");

        var report = exclusions.RootedEntriesMatchingNothing(
            harness.FileSystem,
            temp.Path,
            harness.Platform.PathComparison,
            TestContext.Current.CancellationToken);

        Assert.Empty(report.MatchingNothing);
        Assert.Null(report.Incomplete);
    }

    /// <summary>
    /// An entry covers its own path and nothing that shares its name deeper: build/ at the root is not
    /// searched, while tools/build, which a sync carries, is - and a .secrets there counts.
    /// </summary>
    [Fact]
    public void ADirectoryASyncCarries_IsSearched_ThoughAnEntryNamesItsNameAtTheRoot()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();

        Directory.CreateDirectory(temp.Combine("build"));
        Directory.CreateDirectory(temp.Combine("tools", "build", ".secrets"));

        var exclusions = new SyncExclusions(new SyncConfig { NeverTransfer = ["build", ".secrets"] }, worktreesRoot: ".worktrees");

        var report = exclusions.RootedEntriesMatchingNothing(
            harness.FileSystem,
            temp.Path,
            harness.Platform.PathComparison,
            TestContext.Current.CancellationToken);

        Assert.Equal([".secrets"], report.MatchingNothing);
    }

    /// <summary>
    /// A directory git ignores outside the harness's own is not gone into - what it holds is generated or
    /// fetched, and it is where a tree's size is - though its own name, seen from the directory holding
    /// it, still counts.
    /// </summary>
    [Fact]
    public void WhatGitIgnores_IsNotGoneInto_ThoughItsOwnNameCounts()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();

        for (var index = 0; index < 20; index++)
        {
            Directory.CreateDirectory(temp.Combine("web", "node_modules", $"p{index}"));
        }

        temp.WriteFile(Path.Combine("web", "node_modules", "p0", ".env"), "KEY=value\n");

        var exclusions = new SyncExclusions(
            new SyncConfig { NeverTransfer = ["node_modules", ".env"] },
            worktreesRoot: ".worktrees",
            gitIgnored: ["web/node_modules"]);

        var report = exclusions.RootedEntriesMatchingNothing(
            harness.FileSystem,
            temp.Path,
            harness.Platform.PathComparison,
            mostDirectoriesRead: 10,
            TestContext.Current.CancellationToken);

        Assert.Equal(["node_modules"], report.MatchingNothing);
        Assert.Null(report.Incomplete);
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
            harness.FileSystem,
            temp.Path,
            harness.Platform.PathComparison,
            TestContext.Current.CancellationToken).MatchingNothing);
    }
}
