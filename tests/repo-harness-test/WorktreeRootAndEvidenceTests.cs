using RepoHarness.Core.Configuration;
using RepoHarness.Core.Results;
using RepoHarness.Core.Worktrees;

namespace RepoHarness.Tests;

/// <summary>
/// The configured worktrees root, the evidence a deletion refuses to take, and the commit a
/// worktree records that it was made from.
/// </summary>
public sealed class WorktreeRootAndEvidenceTests
{
    /// <summary>
    /// A budget any temporary directory fits. The default reserve assumes a real build tree below
    /// the worktree, which a directory under the system temp path cannot always satisfy on Windows.
    /// </summary>
    private const int Reserve = 5;

    private const int Margin = 2;

    [Fact]
    public async Task AConfiguredRoot_IsWhereWorktreesAreMadeAndFound()
    {
        // The default root spends 22 characters of the Windows path budget before a worktree's own
        // name. A repository whose build paths are long has no name left that fits, and a shorter
        // root is what buys those characters back.
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, new WorktreeSettings
        {
            Root = ".wt",
            PathBudgetReserve = Reserve,
            PathBudgetMargin = Margin,
        });

        var outcome = await harness.WorktreeService.CreateAsync(temp.Path, "feature", useRandomName: false, cancellationToken);

        Assert.True(outcome.Succeeded, outcome.Outcome.Message);
        PathAssert.Same(Path.Combine(temp.Path, ".wt", "feature"), outcome.Path);
        Assert.True(Directory.Exists(Path.Combine(temp.Path, ".wt", "feature")));

        // Listed and deleted through the same root, so the three commands cannot disagree about
        // where a worktree is.
        var listed = await harness.WorktreeService.ListAsync(temp.Path, cancellationToken);
        Assert.Single(listed, worktree => worktree.Name == "feature");

        var deleted = await harness.WorktreeService.DeleteAsync(
            temp.Path, "feature", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.True(deleted.Succeeded, deleted.Outcome.Message);
        Assert.False(Directory.Exists(Path.Combine(temp.Path, ".wt", "feature")));
    }

    [Fact]
    public async Task ADeclaredEvidenceDirectoryHoldingMeasurements_RefusesTheDeletion()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, Settings(evidence: ["scratchpad", ".temp"]));

        var created = await harness.WorktreeService.CreateAsync(temp.Path, "feature", useRandomName: false, cancellationToken);
        Assert.True(created.Succeeded, created.Outcome.Message);

        // Ignored by git, so the deletion would take it without a word and git would report
        // nothing missing afterwards.
        Directory.CreateDirectory(Path.Combine(created.Path, "scratchpad", "run-1"));
        await File.WriteAllTextAsync(
            Path.Combine(created.Path, "scratchpad", "run-1", "timings.txt"), "42\n", cancellationToken);

        var refused = await harness.WorktreeService.DeleteAsync(
            temp.Path, "feature", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.Equal(HarnessExit.Refused, refused.Outcome.ExitCode);
        Assert.Contains("scratchpad", refused.Outcome.Message, StringComparison.Ordinal);
        Assert.Contains("--delete-evidence", refused.Outcome.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(created.Path), "The worktree was deleted despite the refusal.");
    }

    [Fact]
    public async Task AnEmptyEvidenceDirectory_DoesNotRefuse()
    {
        // The refusal is about measurements, not about the directory existing. A worktree that took no
        // measurements must still be deletable without a flag.
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, Settings(evidence: ["scratchpad"]));

        var created = await harness.WorktreeService.CreateAsync(temp.Path, "feature", useRandomName: false, cancellationToken);
        Directory.CreateDirectory(Path.Combine(created.Path, "scratchpad"));

        var deleted = await harness.WorktreeService.DeleteAsync(
            temp.Path, "feature", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.True(deleted.Succeeded, deleted.Outcome.Message);
    }

    [Fact]
    public async Task DeleteEvidence_ProceedsWhileEveryOtherCheckStillRuns()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, Settings(evidence: ["scratchpad"]));

        var created = await harness.WorktreeService.CreateAsync(temp.Path, "feature", useRandomName: false, cancellationToken);
        Directory.CreateDirectory(Path.Combine(created.Path, "scratchpad"));
        await File.WriteAllTextAsync(Path.Combine(created.Path, "scratchpad", "timings.txt"), "42\n", cancellationToken);

        // A tracked-file change must still refuse, so --delete-evidence is shown to waive exactly
        // one check rather than to stand in for --force.
        await File.WriteAllTextAsync(Path.Combine(created.Path, "notes.txt"), "work\n", cancellationToken);
        await harness.RunGitAsync(created.Path, ["add", "notes.txt"], cancellationToken);

        var stillRefused = await harness.WorktreeService.DeleteAsync(
            temp.Path, "feature", force: false, deleteEvidence: true, cancellationToken: cancellationToken);

        Assert.Equal(HarnessExit.Refused, stillRefused.Outcome.ExitCode);

        // Refused for the change, not for the evidence: the evidence check was waived, so the
        // remedy it offers is not the one being shown.
        Assert.Contains("notes.txt", stillRefused.Outcome.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("--delete-evidence", stillRefused.Outcome.Message, StringComparison.Ordinal);

        await harness.RunGitAsync(created.Path, ["rm", "-f", "--quiet", "notes.txt"], cancellationToken);

        var deleted = await harness.WorktreeService.DeleteAsync(
            temp.Path, "feature", force: true, deleteEvidence: true, cancellationToken: cancellationToken);

        Assert.True(deleted.Succeeded, deleted.Outcome.Message);
        Assert.False(Directory.Exists(created.Path));
    }

    [Fact]
    public async Task EvidenceStillRefusesTheDeletion_WhenUncommittedChangesAreToBeDiscarded()
    {
        // --discard-uncommitted is about what git reports; evidence is what git was never told about.
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, Settings(evidence: ["scratchpad"]));

        var created = await harness.WorktreeService.CreateAsync(temp.Path, "feature", useRandomName: false, cancellationToken);
        Directory.CreateDirectory(Path.Combine(created.Path, "scratchpad"));
        await File.WriteAllTextAsync(Path.Combine(created.Path, "scratchpad", "timings.txt"), "42\n", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(created.Path, "notes.txt"), "never committed\n", cancellationToken);

        var refused = await harness.WorktreeService.DeleteAsync(
            temp.Path, "feature", force: false, deleteEvidence: false, discardUncommitted: true, cancellationToken: cancellationToken);

        Assert.Equal(HarnessExit.Refused, refused.Outcome.ExitCode);
        Assert.Contains("scratchpad", refused.Outcome.Message, StringComparison.Ordinal);
        Assert.Contains("--delete-evidence", refused.Outcome.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(created.Path, "scratchpad", "timings.txt")), "The refused delete removed the evidence.");
        Assert.True(File.Exists(Path.Combine(created.Path, "notes.txt")), "The refused delete discarded the uncommitted change.");
    }

    [Fact]
    public async Task AWorktree_RecordsTheCommitItWasMadeFrom_AndForgetsItWhenDeleted()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, Settings());

        var head = await harness.GitClient.RunAsync(temp.Path, ["rev-parse", "HEAD"], cancellationToken: cancellationToken);
        var baseCommit = head.StandardOutput.Trim();

        var created = await harness.WorktreeService.CreateAsync(temp.Path, "feature", useRandomName: false, cancellationToken);
        Assert.True(created.Succeeded, created.Outcome.Message);

        // A commit inside the worktree moves its HEAD. After it, nothing but the record says what
        // tree the worktree started from, which is the whole reason the record exists.
        await File.WriteAllTextAsync(Path.Combine(created.Path, "work.txt"), "work\n", cancellationToken);
        await harness.RunGitAsync(created.Path, ["add", "work.txt"], cancellationToken);
        await harness.RunGitAsync(created.Path, ["commit", "-m", "work"], cancellationToken);

        var listed = await harness.WorktreeService.ListAsync(temp.Path, cancellationToken);
        var feature = Assert.Single(listed, worktree => worktree.Name == "feature");

        Assert.Equal(baseCommit, feature.BaseCommit);
        Assert.Contains("base " + baseCommit[..12], feature.ToString(), StringComparison.Ordinal);

        var deleted = await harness.WorktreeService.DeleteAsync(
            temp.Path, "feature", force: true, deleteEvidence: false, cancellationToken: cancellationToken);
        Assert.True(deleted.Succeeded, deleted.Outcome.Message);

        // Left behind, the record would answer for a later worktree of the same name with the
        // commit an earlier one started from.
        var record = await harness.GitClient.RunAsync(
            temp.Path,
            ["rev-parse", "--verify", "--quiet", WorktreeService.BaseCommitRefPrefix + "feature"],
            cancellationToken: cancellationToken);

        Assert.True(string.IsNullOrWhiteSpace(record.StandardOutput), "The base commit record outlived its worktree.");
    }

    [Fact]
    public async Task TheBaseCommitRecord_DoesNotMakeLostCommitsLookKept()
    {
        // The record is a ref, and the deletion check counts branches, tags, remote-tracking refs,
        // the newest stash and other worktrees' HEADs. If the record counted too, every commit made
        // in a worktree would look safe and the check would pass over all of it.
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, Settings());

        var created = await harness.WorktreeService.CreateAsync(temp.Path, "feature", useRandomName: false, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(created.Path, "work.txt"), "work\n", cancellationToken);
        await harness.RunGitAsync(created.Path, ["add", "work.txt"], cancellationToken);
        await harness.RunGitAsync(created.Path, ["commit", "-m", "work"], cancellationToken);

        var refused = await harness.WorktreeService.DeleteAsync(
            temp.Path, "feature", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.Equal(HarnessExit.Refused, refused.Outcome.ExitCode);
        Assert.Contains("on no branch", refused.Outcome.Message, StringComparison.Ordinal);
    }

    private static WorktreeSettings Settings(IEnumerable<string>? evidence = null) => new()
    {
        PathBudgetReserve = Reserve,
        PathBudgetMargin = Margin,
        EvidenceRoots = [.. evidence ?? []],
    };

    private static async Task<HarnessFactory> PrepareAsync(TempDirectory temp, WorktreeSettings settings)
    {
        var harness = new HarnessFactory();

        await harness.InitializeHarnessAsync(
            temp.Path,
            TestContext.Current.CancellationToken,
            new HarnessConfig { Worktrees = settings });

        return harness;
    }
}
