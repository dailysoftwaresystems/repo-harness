using RepoHarness.Core.Configuration;
using RepoHarness.Core.Results;
using RepoHarness.Core.Sync;

namespace RepoHarness.Tests;

/// <summary>
/// What a sync decides before it touches a host: what to write, what to delete, what it must never
/// touch, and when it refuses to go on.
/// </summary>
public sealed class SyncPlanTests
{
    private static readonly SyncExclusions Default = new(new SyncConfig(), ".harness-config/worktrees");

    /// <summary>
    /// A file the copy already had, holding something else, is a loss; a file it never had is not.
    /// Both are writes, and telling them apart is what lets a refusal say which is which.
    /// </summary>
    [Fact]
    public void APathTheCopyAlreadyHeld_IsAnOverwrite_AndOneItNeverHadIsNot()
    {
        var source = Manifest(("kept.c", "one"), ("fresh.c", "two"));
        var destination = Manifest(("kept.c", "something else"));

        var plan = SyncPlan.Between(source, destination, Default);

        Assert.Equal(["kept.c"], plan.Overwrites);
        Assert.Contains(plan.Writes, entry => entry.Path == "fresh.c");
        Assert.Equal(2, plan.Writes.Count);

        // And the loss report names it as what it is, ahead of the deletions.
        Assert.Contains("overwrite kept.c", plan.DescribeLoss());

        // So does the listing a refusal sends the reader to for the whole of it. Spelled the same as
        // a write there, the lossy half would arrive looking like the half that costs nothing.
        var described = plan.Describe(SyncVerb.Planned);
        Assert.Contains("would overwrite  kept.c", described);
        Assert.Contains("would write  fresh.c", described);
    }

    [Fact]
    public void AFileWithTheSameContent_IsNeitherWrittenNorDeleted()
    {
        // An unchanged file that is rewritten gets a new modification time, and an incremental build
        // decides what is stale by ordering timestamps. This is the rule that keeps a remote
        // incremental build correct after a sync.
        var source = Manifest(("src/a.c", "aaa"), ("src/b.c", "bbb"));
        var destination = Manifest(("src/a.c", "aaa"), ("src/b.c", "xxx"));

        var plan = SyncPlan.Between(source, destination, Default);

        Assert.Equal(1, plan.Unchanged);
        Assert.Equal("src/b.c", Assert.Single(plan.Writes).Path);
        Assert.Empty(plan.Deletes);
    }

    [Fact]
    public void AFileTheSourceNoLongerHas_IsDeletedFromTheCopy()
    {
        // A copy that only ever gains files is not a copy of the tree: a deleted source file keeps
        // compiling and a renamed one exists twice, and the leg's verdict then describes a tree that
        // no longer exists.
        var plan = SyncPlan.Between(Manifest(("src/a.c", "aaa")), Manifest(("src/a.c", "aaa"), ("src/gone.c", "zzz")), Default);

        Assert.Equal("src/gone.c", Assert.Single(plan.Deletes));
        Assert.Empty(plan.Writes);
    }

    /// <summary>
    /// A deletion empties a directory only where the source keeps nothing in it, at any depth: one still
    /// holding the source's own files is not emptied however many of its files went, and a file at the
    /// root empties no directory at all.
    /// </summary>
    [Fact]
    public void ADeletion_EmptiesOnlyADirectoryTheSourceKeepsNothingIn()
    {
        var source = Manifest(("docs/kept.md", "k"), ("lib/deep/kept.c", "d"));
        var destination = Manifest(
            ("docs/kept.md", "k"),
            ("docs/gone.md", "g"),
            ("lib/gone.c", "g"),
            ("lib/deep/kept.c", "d"),
            ("retired/one.c", "1"),
            ("retired/nested/two.c", "2"),
            ("top.txt", "t"));

        var plan = SyncPlan.Between(source, destination, Default);

        Assert.Equal(5, plan.Deletes.Count);
        Assert.Equal(["retired", "retired/nested"], plan.Emptied.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(".git/config")]
    [InlineData(".harness-config/config.json")]
    [InlineData("build/release/main.o")]
    [InlineData(".harness-config/worktrees/feature/src/a.c")]
    public void TheNeverTransferFloor_IsAlsoANeverDeleteFloor(string path)
    {
        // A path the harness will not write is one it cannot know the source lacks. Deleting it
        // would remove the host's own state, not a file the source gave up.
        var plan = SyncPlan.Between(Manifest(), Manifest((path, "zzz")), Default);

        Assert.Empty(plan.Deletes);
        Assert.False(Default.IsWithheldFromTransfer("src/a.c"));
        Assert.True(Default.IsWithheldFromTransfer(path));
        Assert.True(Default.IsProtectedFromDeletion(path));
    }

    [Fact]
    public void AnExcludedPath_IsNotTransferred_ButIsStillDeletable()
    {
        // sync.exclude says the source chooses not to send a path, which is not the same as saying
        // the host owns it. A copy that keeps an excluded file for ever is a copy of a tree that no
        // longer exists.
        var exclusions = new SyncExclusions(
            new SyncConfig { Exclude = ["docs/generated"] },
            ".harness-config/worktrees");

        Assert.True(exclusions.IsWithheldFromTransfer("docs/generated/index.html"));
        Assert.False(exclusions.IsProtectedFromDeletion("docs/generated/index.html"));

        var plan = SyncPlan.Between(Manifest(), Manifest(("docs/generated/index.html", "zzz")), exclusions);

        Assert.Equal("docs/generated/index.html", Assert.Single(plan.Deletes));
    }

    [Fact]
    public void ASyncThatWouldEmptyTheCopy_StopsAndSaysSo()
    {
        // A mistyped repository path makes the source look empty, which is indistinguishable from a
        // source that deleted everything. Without the bound, that empties a host.
        var destination = Manifest(
            ("a.c", "1"), ("b.c", "2"), ("c.c", "3"), ("d.c", "4"),
            ("e.c", "5"), ("f.c", "6"), ("g.c", "7"), ("h.c", "8"));

        var plan = SyncPlan.Between(Manifest(("a.c", "1")), destination, Default);

        var refusal = Assert.Throws<HarnessException>(
            () => plan.RefuseWhenDeletingTooMuch(destination, SyncConfig.DefaultMaxDeleteFraction));

        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
        Assert.Contains("would delete 7", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was changed", refusal.Message, StringComparison.Ordinal);

        // Which of the two mistakes this is decides what to check. For a copy this tool already
        // owns, the source is what is wrong; the takeover's remedy would send the reader to inspect
        // a tree they never doubted.
        Assert.Contains("source tree is the one", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("directory you meant to take over", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeletionInsideTheBound_IsAllowed()
    {
        var destination = Manifest(("a.c", "1"), ("b.c", "2"), ("c.c", "3"), ("d.c", "4"));
        var plan = SyncPlan.Between(Manifest(("a.c", "1"), ("b.c", "2"), ("c.c", "3")), destination, Default);

        plan.RefuseWhenDeletingTooMuch(destination, SyncConfig.DefaultMaxDeleteFraction);

        Assert.Single(plan.Deletes);
    }

    [Fact]
    public void AFractionOfZero_AllowsAnyDeletion()
    {
        var destination = Manifest(("a.c", "1"), ("b.c", "2"));
        var plan = SyncPlan.Between(Manifest(), destination, Default);

        plan.RefuseWhenDeletingTooMuch(destination, 0);

        Assert.Equal(2, plan.Deletes.Count);
    }

    [Fact]
    public void AFirstSyncToAnEmptyCopy_IsNeverOverTheBound()
    {
        // Nothing to delete, and nothing to measure a share against. A bound that refused here would
        // make a host impossible to set up.
        var destination = SyncManifest.Empty("/host/repo");
        var plan = SyncPlan.Between(Manifest(("a.c", "1")), destination, Default);

        plan.RefuseWhenDeletingTooMuch(destination, SyncConfig.DefaultMaxDeleteFraction);

        Assert.Single(plan.Writes);
    }

    [Fact]
    public void ADryRun_ListsEveryDeletionByName()
    {
        // What a sync removed from a host is the one thing running it again cannot recover, so a
        // deletion is never summarised away.
        var plan = SyncPlan.Between(
            Manifest(("keep.c", "1")),
            Manifest(("keep.c", "1"), ("gone-a.c", "2"), ("gone-b.c", "3")),
            Default);

        var lines = plan.Describe(SyncVerb.Planned);

        Assert.Contains("would delete gone-a.c", lines);
        Assert.Contains("would delete gone-b.c", lines);
        Assert.Contains("unchanged 1 file(s)", lines);
    }

    [Theory]
    [InlineData("build")]
    [InlineData("build/")]
    [InlineData("./build")]
    [InlineData("build\\nested")]
    public void PathsAreComparedInOneSpelling_BecauseTheTwoSidesRarelyAgree(string path)
    {
        // A Windows source and a Linux copy otherwise share no spelling, and every comparison misses:
        // the whole tree is rewritten on every run and nothing is ever deleted.
        Assert.True(Default.IsWithheldFromTransfer(path));
    }

    [Fact]
    public void APathThatMerelyStartsWithAWithheldName_IsNotWithheld()
    {
        // "buildings" is not "build". Matching by prefix rather than by path segment would withhold
        // a source directory nobody excluded.
        Assert.False(Default.IsWithheldFromTransfer("buildings/plan.md"));
        Assert.False(Default.IsWithheldFromTransfer(".gitattributes"));
    }

    private static SyncManifest Manifest(params (string Path, string Content)[] files)
    {
        var entries = files.ToDictionary(
            file => file.Path,
            file => new SyncEntry(file.Path, file.Content.Length, Hash(file.Content)),
            StringComparer.Ordinal);

        return new SyncManifest("/tree", entries);
    }

    private static string Hash(string content)
        => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(content)));
}
