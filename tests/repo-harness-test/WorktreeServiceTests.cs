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

        var outcome = await harness.WorktreeService.DeleteAsync(temp.Path, "wt", cancellationToken);

        Assert.True(outcome.Succeeded, outcome.Outcome.Message);
        Assert.False(Directory.Exists(path));

        var worktrees = await harness.GitClient.ListWorktreesAsync(temp.Path, cancellationToken);
        Assert.DoesNotContain(worktrees, worktree => PathAssert.AreSame(path, worktree.Path));
    }

    [Fact]
    public async Task DeleteAsync_RemovesAWorktreeWithUncommittedWork()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var path = HarnessFactory.WorktreePath(temp.Path, "dirty");
        await harness.WorktreeService.CreateAsync(temp.Path, "dirty", useRandomName: false, cancellationToken);
        File.WriteAllText(Path.Combine(path, "scratch.txt"), "uncommitted");
        File.WriteAllText(Path.Combine(path, "README.md"), "modified");

        var outcome = await harness.WorktreeService.DeleteAsync(temp.Path, "dirty", cancellationToken);

        Assert.True(outcome.Succeeded, outcome.Outcome.Message);
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
        await harness.WorktreeService.DeleteAsync(temp.Path, "again", cancellationToken);
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
        var outcome = await harness.WorktreeService.DeleteAsync(temp.Path, "a-longer-name", cancellationToken);

        Assert.True(outcome.Succeeded, outcome.Outcome.Message);
    }

    [Fact]
    public async Task DeleteAsync_RefusesWhenTheWorktreeDoesNotExist()
    {
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);

        var outcome = await harness.WorktreeService.DeleteAsync(temp.Path, "missing", TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Refused, outcome.Outcome.ExitCode);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../..")]
    [InlineData("wt/../../..")]
    public async Task DeleteAsync_RejectsANameThatCouldReachOutsideTheWorktreesDirectory(string name)
    {
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);

        var outcome = await harness.WorktreeService.DeleteAsync(temp.Path, name, TestContext.Current.CancellationToken);

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

        Assert.Equal(["alpha", "beta"], await harness.WorktreeService.ListAsync(temp.Path, cancellationToken));
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
        Assert.Equal(["inner", "outer"], await harness.WorktreeService.ListAsync(outer, cancellationToken));

        var deleted = await harness.WorktreeService.DeleteAsync(outer, "inner", cancellationToken);
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
}
