using RepoHarness.Core.Configuration;
using RepoHarness.Core.Results;
using RepoHarness.Core.Worktrees;

namespace RepoHarness.Tests;

public sealed class WorktreeServiceTests
{
    /// <summary>
    /// A budget any temporary directory fits. The default reserve assumes a real build tree
    /// below the worktree, which a directory under the system temp path cannot always
    /// satisfy on Windows; the tests of the budget itself set their own.
    /// </summary>
    private static readonly WorktreeSettings Relaxed = new() { PathBudgetReserve = 5, PathBudgetMargin = 2 };

    [Fact]
    public async Task CreateAsync_CreatesADetachedWorktreeGitKnowsAbout()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var expected = HarnessFactory.WorktreePath(temp.Path, "fix-auth");

        var outcome = await harness.WorktreeService.CreateAsync(temp.Path, "fix-auth", useRandomName: false, cancellationToken);

        Assert.True(outcome.Succeeded, outcome.Outcome.Message);
        Assert.Equal("fix-auth", outcome.Name);
        PathAssert.Same(expected, outcome.Path);
        Assert.True(Directory.Exists(expected));

        var worktrees = await harness.GitClient.ListWorktreesAsync(temp.Path, cancellationToken);
        var created = Assert.Single(worktrees, worktree => PathAssert.AreSame(expected, worktree.Path));
        Assert.Null(created.Branch);
    }

    [Fact]
    public async Task CreateAsync_RefusesADuplicate()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);

        var first = await harness.WorktreeService.CreateAsync(temp.Path, "wt", useRandomName: false, cancellationToken);
        var second = await harness.WorktreeService.CreateAsync(temp.Path, "wt", useRandomName: false, cancellationToken);

        Assert.True(first.Succeeded, first.Outcome.Message);
        Assert.Equal(HarnessExit.Refused, second.Outcome.ExitCode);
    }

    [Fact]
    public async Task CreateAsync_RejectsAMalformedName_AsAUsageError()
    {
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);

        var outcome = await harness.WorktreeService.CreateAsync(
            temp.Path, "Not_Valid", useRandomName: false, TestContext.Current.CancellationToken);

        // A malformed name is the caller's mistake, which is a different class of
        // failure from a precondition refusing an otherwise valid request.
        Assert.Equal(HarnessExit.UsageError, outcome.Outcome.ExitCode);
    }

    [Theory]
    [InlineData("Not_Valid", false)]
    [InlineData("fix-auth", true)]
    public async Task CreateAsync_ReportsAUsageMistake_BeforeLookingForARepository(string name, bool random)
    {
        // Checked the other way round, the caller's mistake would be reported as a missing
        // repository or configuration, sending them after the wrong problem.
        using var temp = new TempDirectory();

        var outcome = await new HarnessFactory().WorktreeService.CreateAsync(
            temp.Path, name, useRandomName: random, TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.UsageError, outcome.Outcome.ExitCode);
    }

    [Fact]
    public async Task CreateAsync_WithRandom_ReportsTheGeneratedName()
    {
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);

        var outcome = await harness.WorktreeService.CreateAsync(
            temp.Path, name: null, useRandomName: true, TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded, outcome.Outcome.Message);
        Assert.Equal(WorktreeName.RandomLength, outcome.Name.Length);

        // A generated name must be one the validator would also accept; asserting
        // only its length would pass for a name create-worktree would reject.
        Assert.True(WorktreeName.Validate(outcome.Name).IsValid, outcome.Name);
        PathAssert.Same(HarnessFactory.WorktreePath(temp.Path, outcome.Name), outcome.Path);
    }

    [Fact]
    public async Task CreateAsync_WithRandom_RespectsAShorterConfiguredLimit()
    {
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp, WithRelaxedBudget(maxNameLength: 6));

        var outcome = await harness.WorktreeService.CreateAsync(
            temp.Path, name: null, useRandomName: true, TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded, outcome.Outcome.Message);
        Assert.Equal(6, outcome.Name.Length);
    }

    [Fact]
    public async Task CreateAsync_AppliesTheConfiguredNameLimit()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, WithRelaxedBudget(maxNameLength: 4));

        var tooLong = await harness.WorktreeService.CreateAsync(temp.Path, "abcde", useRandomName: false, cancellationToken);
        var fits = await harness.WorktreeService.CreateAsync(temp.Path, "abcd", useRandomName: false, cancellationToken);

        Assert.Equal(HarnessExit.UsageError, tooLong.Outcome.ExitCode);
        Assert.Contains("the limit is 4", tooLong.Outcome.Message, StringComparison.Ordinal);
        Assert.True(fits.Succeeded, fits.Outcome.Message);
    }

    [Fact]
    public async Task CreateAsync_RefusesWhenThePathLimitCannotBeMet_OnEveryPlatform()
    {
        // A configured limit replaces the platform's, so this refusal is exercised on every
        // operating system rather than only where Windows imposes one.
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp, new WorktreeSettings { PathBudgetReserve = 5, PathBudgetMargin = 2, PathLimit = 20 });

        var outcome = await harness.WorktreeService.CreateAsync(
            temp.Path, "wt", useRandomName: false, TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Refused, outcome.Outcome.ExitCode);
        Assert.Contains("the limit is 20", outcome.Outcome.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(HarnessFactory.WorktreePath(temp.Path, "wt")));
    }

    /// <summary>
    /// The reserve is what the build system generates below build/&lt;variant&gt;, and the budget adds
    /// that directory itself, sized to the longest variant this machine builds. Folded into the
    /// reserve, it went stale the day build directories were keyed by variant: a consumer's check
    /// believed it had twenty characters to spare where it had four. A leg of another operating
    /// system, and one naming a host of its own, never builds here and adds nothing, however long.
    /// </summary>
    [Fact]
    public async Task CreateAsync_AddsTheLongestBuildDirectoryThisMachineBuilds_ToTheBudget()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var platform = harness.Platform;
        var other = platform.PlatformKey == RepoHarness.Core.Platform.PlatformNames.Linux
            ? RepoHarness.Core.Platform.PlatformNames.MacOs
            : RepoHarness.Core.Platform.PlatformNames.Linux;

        var path = HarnessFactory.WorktreePath(temp.Path, "wt");
        const int Reserve = 5;
        const int Margin = 2;

        await harness.InitializeHarnessAsync(temp.Path, TestContext.Current.CancellationToken, new HarnessConfig
        {
            // Exactly enough for the worktree and the reserve, and nothing for a build directory.
            Worktrees = new WorktreeSettings { PathBudgetReserve = Reserve, PathBudgetMargin = Margin, PathLimit = path.Length + Reserve + Margin },
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Hosts = new HostsConfig { Ssh = { ["box"] = new SshHostConfig { RepositoryPath = "/srv/repo" } } },
            SshItems = { "box" },
            Legs =
            {
                ["short"] = new LegConfig { Os = platform.PlatformKey, Processor = "arm64", Config = "debug" },
                ["long"] = new LegConfig { Os = platform.PlatformKey, Processor = "x86_64", Config = "debug" },
                ["elsewhere"] = new LegConfig { Os = other, Processor = "loongarch64", Config = "debug" },
                ["remote"] = new LegConfig { Os = platform.PlatformKey, Processor = "loongarch64", Config = "debug", Ssh = "box" },
            },
        });

        var outcome = await harness.WorktreeService.CreateAsync(
            temp.Path, "wt", useRandomName: false, TestContext.Current.CancellationToken);

        // '/build/x86_64-none-debug/': the long leg's, not the remote or the foreign one's.
        var added = "build".Length + "x86_64-none-debug".Length + 3;

        Assert.Equal(HarnessExit.Refused, outcome.Outcome.ExitCode);
        Assert.Contains($"needs {path.Length + added + Reserve + Margin} characters", outcome.Outcome.Message, StringComparison.Ordinal);
    }

    /// <summary>A machine that builds no leg grows no build directory, and nothing is added for one.</summary>
    [Fact]
    public async Task CreateAsync_AddsNothing_WhenNoLegBuildsOnThisMachine()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var other = harness.Platform.PlatformKey == RepoHarness.Core.Platform.PlatformNames.Linux
            ? RepoHarness.Core.Platform.PlatformNames.MacOs
            : RepoHarness.Core.Platform.PlatformNames.Linux;

        var path = HarnessFactory.WorktreePath(temp.Path, "wt");

        await harness.InitializeHarnessAsync(temp.Path, TestContext.Current.CancellationToken, new HarnessConfig
        {
            Worktrees = new WorktreeSettings { PathBudgetReserve = 5, PathBudgetMargin = 2, PathLimit = path.Length + 5 + 2 },
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Legs = { ["elsewhere"] = new LegConfig { Os = other, Processor = "x86_64", Config = "debug" } },
        });

        var outcome = await harness.WorktreeService.CreateAsync(
            temp.Path, "wt", useRandomName: false, TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded, outcome.Outcome.Message);
    }

    /// <summary>
    /// A worktree is what git records as one. Every directory under the root was counted, so a data
    /// directory a lane script keeps there read as a sixth worktree beside git's five.
    /// </summary>
    [Fact]
    public async Task ListAsync_CountsWhatGitRecords_NotEveryDirectoryUnderTheRoot()
    {
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);
        var token = TestContext.Current.CancellationToken;

        var created = await harness.WorktreeService.CreateAsync(temp.Path, "wt", useRandomName: false, token);
        Assert.True(created.Succeeded, created.Outcome.Message);

        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(created.Path)!, ".manifests"));

        var listed = await harness.WorktreeService.ListAsync(temp.Path, token);

        Assert.Equal(["wt"], listed.Select(listing => listing.Name));

        // A data directory is passed over without a word unless asked: it was never a worktree.
        Assert.DoesNotContain(".manifests", harness.StandardError.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A directory holding a .git entry that git no longer records is what a removal leaves when git's
    /// record went and a file in use did not. It is not listed - git does not count it - and it is
    /// said, without being asked: its name is still taken, and dropped from the listing silently it
    /// looks free.
    /// </summary>
    [Fact]
    public async Task ListAsync_WarnsOfALeftoverThatStillHoldsAGitEntry()
    {
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);
        var token = TestContext.Current.CancellationToken;

        var created = await harness.WorktreeService.CreateAsync(temp.Path, "wt", useRandomName: false, token);
        Assert.True(created.Succeeded, created.Outcome.Message);

        var leftover = Path.Combine(Path.GetDirectoryName(created.Path)!, "gone");
        Directory.CreateDirectory(leftover);
        File.WriteAllText(Path.Combine(leftover, ".git"), "gitdir: /nowhere/.git/worktrees/gone\n");

        var listed = await harness.WorktreeService.ListAsync(temp.Path, token);

        Assert.Equal(["wt"], listed.Select(listing => listing.Name));
        Assert.Contains(
            "list-worktree: WARN - 'gone' holds a .git entry, and git records no worktree there",
            harness.StandardError.ToString(),
            StringComparison.Ordinal);
        Assert.Contains($"then delete '{leftover}'", harness.StandardError.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAsync_AcceptsTheDefaultReserve_WhenThePathLimitIsRaised()
    {
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp, new WorktreeSettings { PathLimit = 4096 });

        var outcome = await harness.WorktreeService.CreateAsync(
            temp.Path, "wt", useRandomName: false, TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded, outcome.Outcome.Message);
    }

    [Fact]
    public async Task DeleteAsync_RemovesTheWorktree_AndItsRegistration()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var path = HarnessFactory.WorktreePath(temp.Path, "wt");
        await harness.WorktreeService.CreateAsync(temp.Path, "wt", useRandomName: false, cancellationToken);

        var outcome = await harness.WorktreeService.DeleteAsync(temp.Path, "wt", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.True(outcome.Succeeded, outcome.Outcome.Message);
        Assert.False(Directory.Exists(path));

        var worktrees = await harness.GitClient.ListWorktreesAsync(temp.Path, cancellationToken);
        Assert.DoesNotContain(worktrees, worktree => PathAssert.AreSame(path, worktree.Path));
    }

    [Fact]
    public async Task DeleteAsync_RefusesAWorktreeWithAModifiedFile()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var path = HarnessFactory.WorktreePath(temp.Path, "modified");
        await harness.WorktreeService.CreateAsync(temp.Path, "modified", useRandomName: false, cancellationToken);
        File.WriteAllText(Path.Combine(path, "README.md"), "modified");

        var outcome = await harness.WorktreeService.DeleteAsync(temp.Path, "modified", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        await AssertRefusedOverUncommittedWorkAsync(harness, outcome, path, "README.md");
    }

    [Fact]
    public async Task DeleteAsync_RefusesAWorktreeWithAStagedChange()
    {
        // Once staged, the work tree matches the index, and only the index differs from the
        // commit: a check that compared the work tree with the index would find nothing.
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var path = HarnessFactory.WorktreePath(temp.Path, "staged");
        await harness.WorktreeService.CreateAsync(temp.Path, "staged", useRandomName: false, cancellationToken);
        File.WriteAllText(Path.Combine(path, "README.md"), "staged");
        await harness.RunGitAsync(path, ["add", "README.md"], cancellationToken);

        var outcome = await harness.WorktreeService.DeleteAsync(temp.Path, "staged", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        await AssertRefusedOverUncommittedWorkAsync(harness, outcome, path, "README.md");
    }

    [Fact]
    public async Task DeleteAsync_RefusesAWorktreeWithAnUntrackedFile()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var path = HarnessFactory.WorktreePath(temp.Path, "untracked");
        await harness.WorktreeService.CreateAsync(temp.Path, "untracked", useRandomName: false, cancellationToken);
        File.WriteAllText(Path.Combine(path, "notes.txt"), "never committed");

        var outcome = await harness.WorktreeService.DeleteAsync(temp.Path, "untracked", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        await AssertRefusedOverUncommittedWorkAsync(harness, outcome, path, "notes.txt");
    }

    [Fact]
    public async Task DeleteAsync_Refusal_NamesAFewChanges_AndCountsTheRest()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var path = HarnessFactory.WorktreePath(temp.Path, "many");
        await harness.WorktreeService.CreateAsync(temp.Path, "many", useRandomName: false, cancellationToken);

        foreach (var file in new[] { "a.txt", "b.txt", "c.txt", "d.txt", "e.txt" })
        {
            File.WriteAllText(Path.Combine(path, file), "never committed");
        }

        var outcome = await harness.WorktreeService.DeleteAsync(temp.Path, "many", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.Equal(
            "Worktree 'many' was not deleted, because it has 5 uncommitted change(s) that would be lost: a.txt, b.txt, c.txt and 2 more (commit them to a branch, or run 'git stash -u'); fix that, or pass --force to delete it anyway.",
            outcome.Outcome.Message);
    }

    [Fact]
    public async Task DeleteAsync_RemovesAWorktreeHoldingOnlyIgnoredFiles()
    {
        // Build output is ignored, and a build makes it again. Counting it would refuse every
        // worktree that has ever been built.
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        File.AppendAllText(temp.Combine(".gitignore"), "\nbuild/\n*.log\n");
        await harness.CommitAllAsync(temp.Path, "ignore build output", cancellationToken);
        var path = HarnessFactory.WorktreePath(temp.Path, "built");
        await harness.WorktreeService.CreateAsync(temp.Path, "built", useRandomName: false, cancellationToken);
        Directory.CreateDirectory(Path.Combine(path, "build"));
        File.WriteAllText(Path.Combine(path, "build", "app.dll"), "binary");
        File.WriteAllText(Path.Combine(path, "test.log"), "log");

        var outcome = await harness.WorktreeService.DeleteAsync(temp.Path, "built", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.True(outcome.Succeeded, outcome.Outcome.Message);
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public async Task DeleteAsync_WithForce_RemovesAWorktreeWithUncommittedWork()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var path = HarnessFactory.WorktreePath(temp.Path, "dirty");
        await harness.WorktreeService.CreateAsync(temp.Path, "dirty", useRandomName: false, cancellationToken);
        File.WriteAllText(Path.Combine(path, "scratch.txt"), "uncommitted");
        File.WriteAllText(Path.Combine(path, "README.md"), "modified");

        var outcome = await harness.WorktreeService.DeleteAsync(temp.Path, "dirty", force: true, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.True(outcome.Succeeded, outcome.Outcome.Message);
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public async Task DeleteAsync_FailsForAWorktreeWhoseStatusGitCannotRead_DeletingNothing_UntilForced()
    {
        // git cannot read this worktree's index, so nothing is known about what it holds. That is
        // git failing to answer rather than a precondition, and --force must still get past it, or
        // the worktree could never be deleted.
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var path = HarnessFactory.WorktreePath(temp.Path, "broken");
        await harness.WorktreeService.CreateAsync(temp.Path, "broken", useRandomName: false, cancellationToken);
        var gitDirectory = await harness.RunGitAsync(path, ["rev-parse", "--absolute-git-dir"], cancellationToken);
        File.WriteAllText(Path.Combine(gitDirectory.StandardOutput.Trim(), "index"), "not an index");

        var failed = await harness.WorktreeService.DeleteAsync(temp.Path, "broken", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.Equal(HarnessExit.CommandFailed, failed.Outcome.ExitCode);
        Assert.StartsWith("Could not read the repository status", failed.Outcome.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was deleted", failed.Outcome.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(path));

        var forced = await harness.WorktreeService.DeleteAsync(temp.Path, "broken", force: true, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.True(forced.Succeeded, forced.Outcome.Message);
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public async Task DeleteAsync_RefusesADirectoryThatIsNotAWorktreeOfItsOwn_UntilForced()
    {
        // Without its .git file, git answers for the main checkout around the directory, which
        // ignores everything under the worktrees directory. With that checkout committed and
        // clean, the answer would pass off the untracked file here as nothing at all.
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        await harness.CommitAllAsync(temp.Path, "harness", cancellationToken);
        var path = HarnessFactory.WorktreePath(temp.Path, "orphan");
        await harness.WorktreeService.CreateAsync(temp.Path, "orphan", useRandomName: false, cancellationToken);
        File.Delete(Path.Combine(path, ".git"));
        File.WriteAllText(Path.Combine(path, "notes.txt"), "never committed");

        var refused = await harness.WorktreeService.DeleteAsync(temp.Path, "orphan", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.Equal(HarnessExit.Refused, refused.Outcome.ExitCode);
        Assert.Contains("is not a worktree git can find", refused.Outcome.Message, StringComparison.Ordinal);
        Assert.Contains("'git -C ", refused.Outcome.Message, StringComparison.Ordinal);
        Assert.Contains(" worktree repair'", refused.Outcome.Message, StringComparison.Ordinal);
        Assert.EndsWith("or pass --force to delete it anyway.", refused.Outcome.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(path, "notes.txt")), "The refused delete removed uncommitted work.");

        var forced = await harness.WorktreeService.DeleteAsync(temp.Path, "orphan", force: true, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.True(forced.Succeeded, forced.Outcome.Message);
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public async Task DeleteAsync_ThenCreateAsync_CanReuseTheName()
    {
        // A registration left behind by the delete makes git refuse the name forever.
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);

        await harness.WorktreeService.CreateAsync(temp.Path, "again", useRandomName: false, cancellationToken);
        await harness.WorktreeService.DeleteAsync(temp.Path, "again", force: false, deleteEvidence: false, cancellationToken: cancellationToken);
        var recreated = await harness.WorktreeService.CreateAsync(temp.Path, "again", useRandomName: false, cancellationToken);

        Assert.True(recreated.Succeeded, recreated.Outcome.Message);
    }

    [Fact]
    public async Task DeleteAsync_RemovesAWorktreeNamedUnderAHigherLimit_AfterTheLimitIsLowered()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, WithRelaxedBudget(maxNameLength: 20));
        await harness.WorktreeService.CreateAsync(temp.Path, "a-longer-name", useRandomName: false, cancellationToken);

        harness.WriteConfig(temp.Path, new HarnessConfig { Worktrees = WithRelaxedBudget(maxNameLength: 10) });
        var outcome = await harness.WorktreeService.DeleteAsync(temp.Path, "a-longer-name", force: false, deleteEvidence: false, cancellationToken: cancellationToken);

        Assert.True(outcome.Succeeded, outcome.Outcome.Message);
    }

    [Fact]
    public async Task DeleteAsync_RefusesWhenTheWorktreeDoesNotExist()
    {
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);

        var outcome = await harness.WorktreeService.DeleteAsync(temp.Path, "missing", force: false, deleteEvidence: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Refused, outcome.Outcome.ExitCode);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../..")]
    [InlineData("wt/../../..")]
    public async Task DeleteAsync_RejectsANameThatCouldReachOutsideTheWorktreesDirectory(string name)
    {
        // Forced, so no check of uncommitted work stands between the name and the delete.
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);

        var outcome = await harness.WorktreeService.DeleteAsync(temp.Path, name, force: true, deleteEvidence: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.UsageError, outcome.Outcome.ExitCode);
        Assert.True(File.Exists(HarnessFactory.ConfigPath(temp.Path)), "Something outside the worktrees directory was deleted.");
    }

    [Fact]
    public async Task ListAsync_ReportsNamesInOrder_AndIgnoresThePlaceholder()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);

        await harness.WorktreeService.CreateAsync(temp.Path, "beta", useRandomName: false, cancellationToken);
        await harness.WorktreeService.CreateAsync(temp.Path, "alpha", useRandomName: false, cancellationToken);

        Assert.Equal(["alpha", "beta"], (await harness.WorktreeService.ListAsync(temp.Path, cancellationToken)).Select(worktree => worktree.Name));
    }

    [Fact]
    public async Task ListAsync_IsEmpty_BeforeAnyWorktreeExists()
    {
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);

        Assert.Empty(await harness.WorktreeService.ListAsync(temp.Path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Worktrees_BelongToTheMainCheckout_WhenManagedFromInsideAWorktree()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);

        await harness.WorktreeService.CreateAsync(temp.Path, "outer", useRandomName: false, cancellationToken);
        var outer = HarnessFactory.WorktreePath(temp.Path, "outer");

        // Creating from inside a worktree must add a sibling, never nest one, or the
        // worktrees directory becomes a tree rather than a flat list.
        var inner = await harness.WorktreeService.CreateAsync(outer, "inner", useRandomName: false, cancellationToken);

        Assert.True(inner.Succeeded, inner.Outcome.Message);
        PathAssert.Same(HarnessFactory.WorktreePath(temp.Path, "inner"), inner.Path);
        Assert.False(Directory.Exists(HarnessFactory.WorktreePath(outer, "inner")));
        Assert.Equal(["inner", "outer"], (await harness.WorktreeService.ListAsync(outer, cancellationToken)).Select(worktree => worktree.Name));

        var deleted = await harness.WorktreeService.DeleteAsync(outer, "inner", force: false, deleteEvidence: false, cancellationToken: cancellationToken);
        Assert.True(deleted.Succeeded, deleted.Outcome.Message);
    }

    [Fact]
    public async Task EveryOperation_ReportsNotInitialised_BeforeInit()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = new HarnessFactory();
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);

        var create = await Assert.ThrowsAsync<HarnessException>(() =>
            harness.WorktreeService.CreateAsync(temp.Path, "wt", useRandomName: false, cancellationToken));
        var list = await Assert.ThrowsAsync<HarnessException>(() =>
            harness.WorktreeService.ListAsync(temp.Path, cancellationToken));

        Assert.Equal(HarnessExit.NotInitialized, create.ExitCode);
        Assert.Equal(HarnessExit.NotInitialized, list.ExitCode);
    }

    private static async Task<HarnessFactory> PrepareAsync(TempDirectory temp, WorktreeSettings? settings = null)
    {
        var harness = new HarnessFactory();

        await harness.InitializeHarnessAsync(
            temp.Path,
            TestContext.Current.CancellationToken,
            new HarnessConfig { Worktrees = settings ?? Relaxed });

        return harness;
    }

    private static WorktreeSettings WithRelaxedBudget(int maxNameLength) => new()
    {
        MaxNameLength = maxNameLength,
        PathBudgetReserve = Relaxed.PathBudgetReserve,
        PathBudgetMargin = Relaxed.PathBudgetMargin,
    };

    /// <summary>
    /// Asserts a delete was refused over uncommitted work, naming the changed file and the way
    /// past, and left the worktree as it was: the change still on disk, and git still tracking it.
    /// </summary>
    private static async Task AssertRefusedOverUncommittedWorkAsync(
        HarnessFactory harness,
        WorktreeOutcome outcome,
        string path,
        string changedFile)
    {
        Assert.Equal(HarnessExit.Refused, outcome.Outcome.ExitCode);
        Assert.Contains(changedFile, outcome.Outcome.Message, StringComparison.Ordinal);
        Assert.Contains("--force", outcome.Outcome.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(path, changedFile)), "The refused delete removed uncommitted work.");

        var worktrees = await harness.GitClient.ListWorktreesAsync(path, TestContext.Current.CancellationToken);
        Assert.Contains(worktrees, worktree => PathAssert.AreSame(path, worktree.Path));
    }
}
