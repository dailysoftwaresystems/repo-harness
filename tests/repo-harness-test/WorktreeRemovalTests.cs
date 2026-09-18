using RepoHarness.Core.Configuration;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Git;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Results;
using RepoHarness.Core.Worktrees;

namespace RepoHarness.Tests;

/// <summary>
/// How delete-worktree removes a worktree once it has decided to: through a linked worktrees
/// directory, and when a delete, an interruption or git's record-keeping goes wrong part way.
/// </summary>
public sealed class WorktreeRemovalTests
{
    private static readonly WorktreeSettings Relaxed = new() { PathBudgetReserve = 5, PathBudgetMargin = 2 };

    [Fact]
    public async Task AWorktreeReachedThroughALinkedWorktreesDirectory_IsDeletedWithoutForce()
    {
        // git reports the worktree's paths with the link resolved, so a check comparing them with
        // the path the harness spelled would refuse every clean worktree here.
        using var temp = new TempDirectory();
        using var elsewhere = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var worktrees = Path.GetDirectoryName(HarnessFactory.WorktreePath(temp.Path, "linked"))!;
        harness.FileSystem.DeleteDirectory(worktrees);
        await LinkDirectoryAsync(harness, worktrees, elsewhere.Path);

        var created = await harness.WorktreeService.CreateAsync(temp.Path, "linked", useRandomName: false, cancellationToken);
        Assert.True(created.Succeeded, created.Outcome.Message);

        var outcome = await harness.WorktreeService.DeleteAsync(temp.Path, "linked", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.True(outcome.Succeeded, outcome.Outcome.Message);
        Assert.False(Directory.Exists(Path.Combine(elsewhere.Path, "linked")));
        Assert.Single(await harness.GitClient.ListWorktreesAsync(temp.Path, cancellationToken));
    }

    [Fact]
    public async Task AFallbackDeleteThatCannotFinish_FailsWithWhatToDoNext()
    {
        // A file another program holds open is no defect in the tool, and part of the worktree may
        // already be gone, which the message has to say.
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "held");

        // Without its .git file git cannot remove the worktree, which leaves the directory to the fallback.
        File.Delete(Path.Combine(path, ".git"));
        var service = Service(harness, harness.GitClient, new UndeletableFileSystem(harness.FileSystem));

        var outcome = await service.DeleteAsync(temp.Path, "held", force: true, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.Equal(HarnessExit.CommandFailed, outcome.Outcome.ExitCode);
        Assert.StartsWith("Could not finish deleting '", outcome.Outcome.Message, StringComparison.Ordinal);
        Assert.Contains("; part of it may already be gone: ", outcome.Outcome.Message, StringComparison.Ordinal);
        Assert.Contains("delete-worktree held --force", outcome.Outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInterruptionAfterTheChecks_DeletesNothing()
    {
        // Every check completes, and the interruption arrives just after them: removal must not start.
        using var temp = new TempDirectory();
        using var interruption = new CancellationTokenSource();
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "stopped");
        var git = new InterceptingGitClient(harness.GitClient) { IgnoreCancellation = true, BeforeEveryCall = interruption.Cancel };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service(harness, git).DeleteAsync(temp.Path, "stopped", force: false, deleteEvidence: false, cancellationToken: interruption.Token));

        Assert.True(Directory.Exists(path));
        Assert.DoesNotContain(git.Runs, run => run.Arguments is ["worktree", "remove", ..]);
        Assert.Equal(2, (await harness.GitClient.ListWorktreesAsync(temp.Path, TestContext.Current.CancellationToken)).Count);
    }

    [Fact]
    public async Task AnInterruptionDuringRemoval_DoesNotStopIt()
    {
        // git stopped halfway would leave the files and git's record partly gone.
        using var temp = new TempDirectory();
        using var interruption = new CancellationTokenSource();
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "finished");
        var git = new InterceptingGitClient(harness.GitClient)
        {
            BeforeRun = arguments =>
            {
                if (arguments is ["worktree", "remove", ..])
                {
                    interruption.Cancel();
                }
            },
        };

        var outcome = await Service(harness, git).DeleteAsync(temp.Path, "finished", force: false, deleteEvidence: false, cancellationToken: interruption.Token);

        Assert.True(outcome.Succeeded, outcome.Outcome.Message);
        Assert.False(Directory.Exists(path));
        Assert.Single(git.Runs, run => run.Arguments is ["worktree", "remove", ..]);
    }

    [Fact]
    public async Task AFailureReportedWhileClearingTheRecord_IsCheckedAgainstTheRecordItself()
    {
        // With the directory gone, git clears the record; what decides the outcome is whether the
        // record is gone, not what git's exit code said.
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "cleared");
        File.Delete(Path.Combine(path, ".git"));
        var removals = 0;
        var git = new InterceptingGitClient(harness.GitClient)
        {
            AfterRun = (arguments, result) => arguments is ["worktree", "remove", ..] && ++removals == 2
                ? new GitCommandResult(1, string.Empty, "error: reported failure after clearing the record")
                : result,
        };

        var outcome = await Service(harness, git).DeleteAsync(temp.Path, "cleared", force: true, deleteEvidence: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded, outcome.Outcome.Message);
        Assert.Equal(2, removals);
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public async Task ARecordThatSurvivesTheDeletion_IsReported()
    {
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "kept");
        File.Delete(Path.Combine(path, ".git"));
        var removals = 0;
        var git = new InterceptingGitClient(harness.GitClient)
        {
            InsteadOfRun = arguments => arguments is ["worktree", "remove", ..] && ++removals == 2
                ? new GitCommandResult(1, string.Empty, "error: could not clear the record")
                : null,
        };

        var outcome = await Service(harness, git).DeleteAsync(temp.Path, "kept", force: true, deleteEvidence: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.CommandFailed, outcome.Outcome.ExitCode);
        Assert.Contains("git still has it registered", outcome.Outcome.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public async Task ARecordLeftThroughALinkedWorktreesDirectory_IsCleared_SoTheNameCanBeUsedAgain()
    {
        // git lists the record by the link's target; matched against the path as spelled, it
        // would never be found, and the name would stay unusable.
        using var temp = new TempDirectory();
        using var elsewhere = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        await LinkWorktreesDirectoryAsync(harness, temp, elsewhere);
        var path = await CreateAsync(harness, temp, "linked");
        harness.FileSystem.DeleteDirectory(path);

        var outcome = await harness.WorktreeService.DeleteAsync(temp.Path, "linked", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.True(outcome.Succeeded, outcome.Outcome.Message);
        Assert.Single(await harness.GitClient.ListWorktreesAsync(temp.Path, cancellationToken));

        var recreated = await harness.WorktreeService.CreateAsync(temp.Path, "linked", useRandomName: false, cancellationToken);
        Assert.True(recreated.Succeeded, recreated.Outcome.Message);
    }

    [Fact]
    public async Task AForcedDeleteThroughALinkedWorktreesDirectory_ReportsARecordThatSurvives_WithGitsReason()
    {
        // With the .git file gone, git cannot name the record's directory, so the record is looked
        // for in git's list, which names it by the link's target.
        using var temp = new TempDirectory();
        using var elsewhere = new TempDirectory();
        var harness = await PrepareAsync(temp);
        await LinkWorktreesDirectoryAsync(harness, temp, elsewhere);
        var path = await CreateAsync(harness, temp, "linked");
        File.Delete(Path.Combine(path, ".git"));
        var removals = 0;
        var git = new InterceptingGitClient(harness.GitClient)
        {
            InsteadOfRun = arguments => arguments is ["worktree", "remove", ..] && ++removals == 2
                ? new GitCommandResult(1, string.Empty, "error: could not clear the record")
                : null,
        };

        var outcome = await Service(harness, git).DeleteAsync(temp.Path, "linked", force: true, deleteEvidence: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.CommandFailed, outcome.Outcome.ExitCode);
        Assert.Contains("git still has it registered", outcome.Outcome.Message, StringComparison.Ordinal);
        Assert.Contains("error: could not clear the record", outcome.Outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACheckedRemovalGitAbandonsPartWay_SaysWhatIsGone_AndForceFinishesIt()
    {
        // git deletes the .git file and its record before failing on a file it cannot delete, and
        // after that no check can run again.
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "partial");
        var gitDirectory = (await harness.RunGitAsync(path, ["rev-parse", "--absolute-git-dir"], cancellationToken)).StandardOutput.Trim();
        var git = new InterceptingGitClient(harness.GitClient)
        {
            InsteadOfRun = arguments =>
            {
                if (arguments is not ["worktree", "remove", ..])
                {
                    return null;
                }

                File.Delete(Path.Combine(path, ".git"));
                harness.FileSystem.DeleteDirectory(gitDirectory);
                return new GitCommandResult(255, string.Empty, "error: failed to delete 'build.log': Permission denied");
            },
        };

        var failed = await Service(harness, git).DeleteAsync(temp.Path, "partial", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.Equal(HarnessExit.CommandFailed, failed.Outcome.ExitCode);
        Assert.StartsWith("git could not remove worktree 'partial': error: failed to delete 'build.log'", failed.Outcome.Message, StringComparison.Ordinal);
        Assert.Contains("its .git file and git's record of it are already gone", failed.Outcome.Message, StringComparison.Ordinal);

        // Here the checks did run, so finishing with --force is safe, and says so.
        Assert.Contains("Every check passed before removal began", failed.Outcome.Message, StringComparison.Ordinal);
        Assert.Contains("delete-worktree partial --force", failed.Outcome.Message, StringComparison.Ordinal);

        var forced = await harness.WorktreeService.DeleteAsync(temp.Path, "partial", force: true, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.True(forced.Succeeded, forced.Outcome.Message);
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public async Task AnIgnoredFileHeldOpen_LeavesACheckedRemovalPartWay_AndForceFinishesIt()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to delete a file another program holds open.");

        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        File.AppendAllText(temp.Combine(".gitignore"), "\n*.log\n");
        await harness.CommitAllAsync(temp.Path, "ignore logs", cancellationToken);
        var path = await CreateAsync(harness, temp, "held");
        var held = Path.Combine(path, "build.log");
        File.WriteAllText(held, "held open by a build server");

        WorktreeOutcome failed;

        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            failed = await harness.WorktreeService.DeleteAsync(temp.Path, "held", force: false, deleteEvidence: false, cancellationToken: cancellationToken);
        }

        Assert.Equal(HarnessExit.CommandFailed, failed.Outcome.ExitCode);
        Assert.Contains("already gone", failed.Outcome.Message, StringComparison.Ordinal);
        Assert.Contains("delete-worktree held --force", failed.Outcome.Message, StringComparison.Ordinal);

        var forced = await harness.WorktreeService.DeleteAsync(temp.Path, "held", force: true, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.True(forced.Succeeded, forced.Outcome.Message);
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public async Task AnInterruptionDuringRemoval_SaysAtOnceThatTheDeletionIsUnderWay()
    {
        using var temp = new TempDirectory();
        using var interruption = new CancellationTokenSource();
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "noticed");
        string? warnedBeforeGitRan = null;
        var git = new InterceptingGitClient(harness.GitClient)
        {
            BeforeRun = arguments =>
            {
                if (arguments is ["worktree", "remove", ..])
                {
                    interruption.Cancel();
                    warnedBeforeGitRan = harness.StandardError.ToString();
                }
            },
        };

        var outcome = await Service(harness, git).DeleteAsync(temp.Path, "noticed", force: false, deleteEvidence: false, cancellationToken: interruption.Token);

        Assert.True(outcome.Succeeded, outcome.Outcome.Message);
        Assert.False(Directory.Exists(path));
        Assert.Contains("delete-worktree: WARN - Deleting worktree 'noticed' is under way and may be left half done", warnedBeforeGitRan, StringComparison.Ordinal);
        Assert.Contains("delete-worktree noticed --force", warnedBeforeGitRan, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AForcedDeleteThatCannotLookUpARecord_SaysNothingWasDeleted_WithoutOfferingForce()
    {
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "lookup");
        harness.FileSystem.DeleteDirectory(path);
        var git = new InterceptingGitClient(harness.GitClient)
        {
            ListWorktreesFailure = new HarnessException(HarnessExit.CommandFailed, "Could not list the worktrees: simulated failure"),
        };

        var outcome = await Service(harness, git).DeleteAsync(temp.Path, "lookup", force: true, deleteEvidence: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.CommandFailed, outcome.Outcome.ExitCode);
        Assert.StartsWith("Could not list the worktrees: simulated failure", outcome.Outcome.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was deleted", outcome.Outcome.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("--force", outcome.Outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AListingThatFailsAfterTheDeletion_SaysTheWorktreeIsAlreadyDeleted()
    {
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "unlisted");

        // Without its .git file, git cannot name the record's directory, so git's list is asked.
        File.Delete(Path.Combine(path, ".git"));
        var git = new InterceptingGitClient(harness.GitClient)
        {
            ListWorktreesFailure = new HarnessException(HarnessExit.CommandFailed, "Could not list the worktrees: simulated failure"),
        };

        var outcome = await Service(harness, git).DeleteAsync(temp.Path, "unlisted", force: true, deleteEvidence: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.CommandFailed, outcome.Outcome.ExitCode);
        Assert.StartsWith("Worktree 'unlisted' is deleted, but whether git still has it registered could not be confirmed", outcome.Outcome.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public async Task ForcingALockedWorktreeWhoseGitFileIsGone_RemovesTheDirectoryAndTheRecord()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "stuck");
        await harness.RunGitAsync(temp.Path, ["worktree", "lock", "--reason", "on a USB disk", path], cancellationToken);
        File.Delete(Path.Combine(path, ".git"));

        var outcome = await harness.WorktreeService.DeleteAsync(temp.Path, "stuck", force: true, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.True(outcome.Succeeded, outcome.Outcome.Message);
        Assert.False(Directory.Exists(path));
        Assert.Single(await harness.GitClient.ListWorktreesAsync(temp.Path, cancellationToken));
    }

    [Fact]
    public async Task ACheckedRemovalThatFailsWithEverythingStillThere_NeverClaimsNothingWasDeleted()
    {
        // git can delete files before it fails, and those would now read as changes.
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "remains");
        var git = new InterceptingGitClient(harness.GitClient)
        {
            InsteadOfRun = arguments =>
            {
                if (arguments is not ["worktree", "remove", ..])
                {
                    return null;
                }

                File.Delete(Path.Combine(path, "README.md"));
                return new GitCommandResult(255, string.Empty, "error: failed to delete 'build.log': Permission denied");
            },
        };

        var failed = await Service(harness, git).DeleteAsync(temp.Path, "remains", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.Equal(HarnessExit.CommandFailed, failed.Outcome.ExitCode);
        Assert.DoesNotContain("nothing was deleted", failed.Outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("git may already have deleted some files", failed.Outcome.Message, StringComparison.Ordinal);
        Assert.Contains("delete-worktree remains --force", failed.Outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARemovalStillRunningAsTheGraceRunsOut_IsStopped_AndSaysWhatIsLeft()
    {
        // Left to the command line, the process would end mid-deletion without a word.
        using var temp = new TempDirectory();
        using var interruption = new CancellationTokenSource();
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "hung");
        var stopped = false;
        var git = new InterceptingGitClient(harness.GitClient)
        {
            BeforeRun = arguments =>
            {
                if (arguments is ["worktree", "remove", ..])
                {
                    interruption.Cancel();
                }
            },
            RunInstead = async (arguments, token) =>
            {
                if (arguments is not ["worktree", "remove", ..])
                {
                    return null;
                }

                // A git that hangs until it is stopped.
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                catch (OperationCanceledException)
                {
                    stopped = true;
                    throw;
                }

                return null;
            },
        };
        var service = new WorktreeService(harness.ContextLoader, git, harness.FileSystem, harness.PathBudget, harness.Platform, harness.Output)
        {
            InterruptionGrace = TimeSpan.FromMilliseconds(400),
        };

        var outcome = await service.DeleteAsync(temp.Path, "hung", force: false, deleteEvidence: false, cancellationToken: interruption.Token);

        Assert.True(stopped, "git was not stopped before the grace ran out.");
        Assert.Equal(HarnessExit.Cancelled, outcome.Outcome.ExitCode);
        Assert.StartsWith("Deleting worktree 'hung' was stopped part way", outcome.Outcome.Message, StringComparison.Ordinal);
        Assert.Contains("delete-worktree hung --force", outcome.Outcome.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public async Task ARecordWhoseDirectoryCannotBeFound_FailsBeforeAnythingIsDeleted()
    {
        // Without the record's own directory, which HEAD is this worktree's, and which submodule
        // repositories clearing it deletes, cannot be known.
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "unfound");
        harness.FileSystem.DeleteDirectory(path);

        var outcome = await Service(harness, harness.GitClient, new RecordHidingFileSystem(harness.FileSystem))
            .DeleteAsync(temp.Path, "unfound", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.Equal(HarnessExit.CommandFailed, outcome.Outcome.ExitCode);
        Assert.Contains("the directory holding its record could not be found", outcome.Outcome.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was deleted", outcome.Outcome.Message, StringComparison.Ordinal);
        Assert.Equal(2, (await harness.GitClient.ListWorktreesAsync(temp.Path, cancellationToken)).Count);
    }

    [Fact]
    public async Task ALinkLoopInTheWorktreesPath_FailsWithAClearReason()
    {
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);
        var worktrees = Path.GetDirectoryName(HarnessFactory.WorktreePath(temp.Path, "any"))!;
        var loop = Path.Combine(Path.GetDirectoryName(worktrees)!, "loop");
        harness.FileSystem.DeleteDirectory(worktrees);

        // Each link leads to the other. A junction is made to a directory that exists, so the loop
        // is closed only once both links are there.
        Directory.CreateDirectory(loop);
        await LinkDirectoryAsync(harness, worktrees, loop);
        Directory.Delete(loop);
        await LinkDirectoryAsync(harness, loop, worktrees);

        var outcome = await harness.WorktreeService.DeleteAsync(temp.Path, "looped", force: false, deleteEvidence: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.CommandFailed, outcome.Outcome.ExitCode);
        Assert.StartsWith("Could not follow the links in the worktree's path", outcome.Outcome.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was deleted", outcome.Outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInterruptedForcedRemoval_ClaimsNothingAboutChecks()
    {
        // A forced deletion made no check, so telling the reader every check passed would read as
        // reassurance that nothing valuable was at risk.
        using var temp = new TempDirectory();
        using var interruption = new CancellationTokenSource();
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "forced");

        // Without its .git file git leaves the directory, so the stop finds part of it gone.
        File.Delete(Path.Combine(path, ".git"));
        var git = new InterceptingGitClient(harness.GitClient)
        {
            BeforeRun = arguments =>
            {
                if (arguments is ["worktree", "remove", ..])
                {
                    interruption.Cancel();
                }
            },
            RunInstead = async (arguments, token) =>
            {
                if (arguments is not ["worktree", "remove", ..])
                {
                    return null;
                }

                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return null;
            },
        };
        var service = new WorktreeService(harness.ContextLoader, git, harness.FileSystem, harness.PathBudget, harness.Platform, harness.Output)
        {
            InterruptionGrace = TimeSpan.FromMilliseconds(400),
        };

        var outcome = await service.DeleteAsync(temp.Path, "forced", force: true, deleteEvidence: false, cancellationToken: interruption.Token);

        Assert.Equal(HarnessExit.Cancelled, outcome.Outcome.ExitCode);
        Assert.Contains("already gone", outcome.Outcome.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Every check passed", outcome.Outcome.Message, StringComparison.Ordinal);
        Assert.Contains("delete-worktree forced --force", outcome.Outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStopWhileTheRecordIsVerified_ReportsTheDeletionAsDone()
    {
        // Verification deletes nothing. Stopping it would report a deletion that finished as one
        // left half done, and the rerun it advises would answer that there is no such worktree.
        using var temp = new TempDirectory();
        using var interruption = new CancellationTokenSource();
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "verified");

        // Without its .git file git cannot name the record's directory, so git's list is asked.
        File.Delete(Path.Combine(path, ".git"));
        var removals = 0;
        var git = new InterceptingGitClient(harness.GitClient)
        {
            AfterRun = (arguments, result) =>
            {
                if (arguments is ["worktree", "remove", ..] && ++removals == 2)
                {
                    // The interruption arrives once the record is cleared, just before verification.
                    interruption.Cancel();
                    Thread.Sleep(100);
                }

                return result;
            },
        };
        var service = new WorktreeService(harness.ContextLoader, git, harness.FileSystem, harness.PathBudget, harness.Platform, harness.Output)
        {
            InterruptionGrace = TimeSpan.Zero,
        };

        var outcome = await service.DeleteAsync(temp.Path, "verified", force: true, deleteEvidence: false, cancellationToken: interruption.Token);

        // The warning proves the interruption was delivered, which is what arms the stop, so
        // verification ran with the stop already fired rather than before it.
        Assert.Contains("is under way", harness.StandardError.ToString(), StringComparison.Ordinal);
        Assert.True(outcome.Succeeded, outcome.Outcome.Message);
        Assert.False(Directory.Exists(path));
        Assert.Single(await harness.GitClient.ListWorktreesAsync(temp.Path, TestContext.Current.CancellationToken));
    }

    /// <summary>The real file system, except that git's worktree records cannot be listed.</summary>
    private sealed class RecordHidingFileSystem(IFileSystem inner) : IFileSystem
    {
        private static readonly string Records = Path.Combine(".git", "worktrees");

        public bool FileExists(string path) => inner.FileExists(path);

        public bool DirectoryExists(string path) => inner.DirectoryExists(path);

        public string ResolveLinks(string path) => inner.ResolveLinks(path);


        public Stream OpenRead(string path) => inner.OpenRead(path);

        public DateTime LastWriteTimeUtc(string path) => inner.LastWriteTimeUtc(path);


        public Task WriteAllBytesAtomicAsync(string path, byte[] contents, CancellationToken cancellationToken = default)

            => inner.WriteAllBytesAtomicAsync(path, contents, cancellationToken);

        public void CreateDirectory(string path) => inner.CreateDirectory(path);

        public void DeleteFile(string path) => inner.DeleteFile(path);

        public string CopyToTemporaryFile(string path) => inner.CopyToTemporaryFile(path);
        public void CopyFile(string source, string destination, bool overwrite = false)
            => inner.CopyFile(source, destination, overwrite);

        public void DeleteDirectory(string path) => inner.DeleteDirectory(path);

        public IEnumerable<string> EnumerateFiles(string path, bool recursive) => inner.EnumerateFiles(path, recursive);

        public IEnumerable<string> EnumerateDirectoryLinks(string path) => inner.EnumerateDirectoryLinks(path);

        public IEnumerable<string> EnumerateDirectories(string path)
            => Path.TrimEndingDirectorySeparator(path).EndsWith(Records, StringComparison.OrdinalIgnoreCase)
                ? []
                : inner.EnumerateDirectories(path);

        public string ReadAllText(string path) => inner.ReadAllText(path);

        public void WriteAllTextAtomic(string path, string contents) => inner.WriteAllTextAtomic(path, contents);

        public void ProtectSecretFile(string path) => inner.ProtectSecretFile(path);
    }

    /// <summary>Replaces the worktrees directory of <paramref name="temp"/> with a link to <paramref name="target"/>.</summary>
    private static async Task LinkWorktreesDirectoryAsync(HarnessFactory harness, TempDirectory temp, TempDirectory target)
    {
        var worktrees = Path.GetDirectoryName(HarnessFactory.WorktreePath(temp.Path, "any"))!;
        harness.FileSystem.DeleteDirectory(worktrees);
        await LinkDirectoryAsync(harness, worktrees, target.Path);
    }

    private static WorktreeService Service(HarnessFactory harness, IGitClient git, IFileSystem? fileSystem = null)
        => new(harness.ContextLoader, git, fileSystem ?? harness.FileSystem, harness.PathBudget, harness.Platform, harness.Output);

    private static async Task<HarnessFactory> PrepareAsync(TempDirectory temp)
    {
        var harness = new HarnessFactory();

        await harness.InitializeHarnessAsync(
            temp.Path,
            TestContext.Current.CancellationToken,
            new HarnessConfig { Worktrees = Relaxed });

        return harness;
    }

    private static async Task<string> CreateAsync(HarnessFactory harness, TempDirectory temp, string name)
    {
        var outcome = await harness.WorktreeService.CreateAsync(
            temp.Path, name, useRandomName: false, TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded, outcome.Outcome.Message);
        return HarnessFactory.WorktreePath(temp.Path, name);
    }

    /// <summary>
    /// Makes <paramref name="link"/> lead to <paramref name="target"/>: a junction on Windows, which
    /// needs no privilege there, and a symbolic link elsewhere.
    /// </summary>
    private static async Task LinkDirectoryAsync(HarnessFactory harness, string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }

        var result = await harness.ProcessRunner.RunAsync(
            new ProcessRequest { FileName = "cmd", Arguments = ["/c", "mklink", "/J", link, target] },
            TestContext.Current.CancellationToken);

        Assert.True(result.ExitCode == 0, $"mklink /J failed: {result.StandardError}{result.StandardOutput}");
    }
}

/// <summary>The real file system, except that a directory cannot be deleted, as while another program holds a file in it open.</summary>
internal sealed class UndeletableFileSystem(IFileSystem inner) : IFileSystem
{
    public bool FileExists(string path) => inner.FileExists(path);

    public bool DirectoryExists(string path) => inner.DirectoryExists(path);

    public string ResolveLinks(string path) => inner.ResolveLinks(path);


    public Stream OpenRead(string path) => inner.OpenRead(path);

    public DateTime LastWriteTimeUtc(string path) => inner.LastWriteTimeUtc(path);


    public Task WriteAllBytesAtomicAsync(string path, byte[] contents, CancellationToken cancellationToken = default)

        => inner.WriteAllBytesAtomicAsync(path, contents, cancellationToken);

    public void CreateDirectory(string path) => inner.CreateDirectory(path);

    public void DeleteFile(string path) => inner.DeleteFile(path);

    public string CopyToTemporaryFile(string path) => inner.CopyToTemporaryFile(path);
    public void CopyFile(string source, string destination, bool overwrite = false)
        => inner.CopyFile(source, destination, overwrite);

    public void DeleteDirectory(string path)
        => throw new IOException("The process cannot access the file because it is being used by another process.");

    public IEnumerable<string> EnumerateFiles(string path, bool recursive) => inner.EnumerateFiles(path, recursive);

    public IEnumerable<string> EnumerateDirectoryLinks(string path) => inner.EnumerateDirectoryLinks(path);

    public IEnumerable<string> EnumerateDirectories(string path) => inner.EnumerateDirectories(path);

    public string ReadAllText(string path) => inner.ReadAllText(path);

    public void WriteAllTextAtomic(string path, string contents) => inner.WriteAllTextAtomic(path, contents);

    public void ProtectSecretFile(string path) => inner.ProtectSecretFile(path);
}

/// <summary>
/// The real git client, with hooks a test uses to interrupt a command at a chosen moment, to change
/// what git answers, and to see what git was asked and whether that call could still be cancelled.
/// </summary>
internal sealed class InterceptingGitClient(IGitClient inner) : IGitClient
{
    private readonly List<(string[] Arguments, bool Cancellable)> _runs = [];

    /// <summary>Runs at the start of every call.</summary>
    public Action? BeforeEveryCall { get; init; }

    /// <summary>Runs before each arbitrary git command, with its arguments.</summary>
    public Action<IReadOnlyList<string>>? BeforeRun { get; init; }

    /// <summary>An answer to give instead of running a command, or <see langword="null"/> to run it.</summary>
    public Func<IReadOnlyList<string>, GitCommandResult?>? InsteadOfRun { get; init; }

    /// <summary>Replaces the result of each arbitrary git command once it has run.</summary>
    public Func<IReadOnlyList<string>, GitCommandResult, GitCommandResult>? AfterRun { get; init; }

    /// <summary>Whether calls reach git with their cancellation taken away.</summary>
    public bool IgnoreCancellation { get; init; }

    /// <summary>A failure every worktree listing throws instead of asking git.</summary>
    public HarnessException? ListWorktreesFailure { get; init; }

    public Task<int> CountRepositoryCommitsAsync(
        string gitDirectory,
        IReadOnlyList<string> revisions,
        CancellationToken cancellationToken = default)
        => Call(() => inner.CountRepositoryCommitsAsync(gitDirectory, revisions, Token(cancellationToken)));

    public Task<bool> HasStashAsync(string gitDirectory, CancellationToken cancellationToken = default)
        => Call(() => inner.HasStashAsync(gitDirectory, Token(cancellationToken)));

    /// <summary>Every arbitrary git command, and whether its call could still be cancelled.</summary>
    public IReadOnlyList<(string[] Arguments, bool Cancellable)> Runs
    {
        get
        {
            lock (_runs)
            {
                return [.. _runs];
            }
        }
    }

    public bool IsInstalled() => inner.IsInstalled();

    public Task<bool> IsRepositoryAsync(string directory, CancellationToken cancellationToken = default)
        => Call(() => inner.IsRepositoryAsync(directory, Token(cancellationToken)));

    public Task<string?> GetRepositoryRootAsync(string directory, CancellationToken cancellationToken = default)
        => Call(() => inner.GetRepositoryRootAsync(directory, Token(cancellationToken)));

    public Task<GitWorktree?> GetMainWorktreeAsync(string directory, CancellationToken cancellationToken = default)
        => Call(() => inner.GetMainWorktreeAsync(directory, Token(cancellationToken)));

    public Task<bool> IsDirtyAsync(string directory, CancellationToken cancellationToken = default)
        => Call(() => inner.IsDirtyAsync(directory, Token(cancellationToken)));

    public Task<IReadOnlyList<string>> GetStatusAsync(string directory, CancellationToken cancellationToken = default)
        => Call(() => inner.GetStatusAsync(directory, Token(cancellationToken)));

    public Task<IReadOnlyList<GitWorktree>> ListWorktreesAsync(string directory, CancellationToken cancellationToken = default)
        => ListWorktreesFailure is { } failure
            ? Task.FromException<IReadOnlyList<GitWorktree>>(failure)
            : Call(() => inner.ListWorktreesAsync(directory, Token(cancellationToken)));

    public Task<GitLocation?> GetLocationAsync(string directory, CancellationToken cancellationToken = default)
        => Call(() => inner.GetLocationAsync(directory, Token(cancellationToken)));

    public Task<IReadOnlyList<GitIndexEntry>> ListIndexAsync(string directory, CancellationToken cancellationToken = default)
        => Call(() => inner.ListIndexAsync(directory, Token(cancellationToken)));

    public Task<string> GetIndexFileAsync(string directory, CancellationToken cancellationToken = default)
        => Call(() => inner.GetIndexFileAsync(directory, Token(cancellationToken)));

    public Task<IReadOnlyList<string>> FindEditedFilesAsync(
        string directory,
        string indexCopy,
        IReadOnlyList<string> assumedUnchanged,
        IReadOnlyList<string> skipWorktree,
        CancellationToken cancellationToken = default)
        => Call(() => inner.FindEditedFilesAsync(directory, indexCopy, assumedUnchanged, skipWorktree, Token(cancellationToken)));

    public Task<string?> ResolveGitDirectoryAsync(string directory, string path, CancellationToken cancellationToken = default)
        => Call(() => inner.ResolveGitDirectoryAsync(directory, path, Token(cancellationToken)));

    public Task<int> CountCommitsAsync(
        string directory,
        IReadOnlyList<string> revisions,
        CancellationToken cancellationToken = default)
        => Call(() => inner.CountCommitsAsync(directory, revisions, Token(cancellationToken)));

    public Task<bool> IsIgnoredAsync(string directory, string path, CancellationToken cancellationToken = default)
        => Call(() => inner.IsIgnoredAsync(directory, path, Token(cancellationToken)));

    public Task<string?> ResolveCommitAsync(string directory, string reference, CancellationToken cancellationToken = default)
        => Call(() => inner.ResolveCommitAsync(directory, reference, Token(cancellationToken)));

    public Task<string?> ReadFileAtCommitAsync(
        string directory,
        string commit,
        string relativePath,
        CancellationToken cancellationToken = default)
        => Call(() => inner.ReadFileAtCommitAsync(directory, commit, relativePath, Token(cancellationToken)));

    /// <summary>
    /// An answer to give once the call's own token is in hand, or <see langword="null"/> to run the
    /// command: a stand-in for a git that hangs until it is stopped.
    /// </summary>
    public Func<IReadOnlyList<string>, CancellationToken, Task<GitCommandResult?>>? RunInstead { get; init; }

    public async Task<GitCommandResult> RunAsync(
        string directory,
        IReadOnlyList<string> arguments,
        bool echoOutput = false,
        CancellationToken cancellationToken = default)
    {
        BeforeEveryCall?.Invoke();
        BeforeRun?.Invoke(arguments);

        lock (_runs)
        {
            _runs.Add(([.. arguments], cancellationToken.CanBeCanceled));
        }

        if (InsteadOfRun?.Invoke(arguments) is { } answer)
        {
            return answer;
        }

        if (RunInstead is not null && await RunInstead(arguments, cancellationToken) is { } scripted)
        {
            return scripted;
        }

        var result = await inner.RunAsync(directory, arguments, echoOutput, Token(cancellationToken));
        return AfterRun is null ? result : AfterRun(arguments, result);
    }

    private CancellationToken Token(CancellationToken cancellationToken)
        => IgnoreCancellation ? CancellationToken.None : cancellationToken;

    private Task<T> Call<T>(Func<Task<T>> call)
    {
        BeforeEveryCall?.Invoke();
        return call();
    }
}
