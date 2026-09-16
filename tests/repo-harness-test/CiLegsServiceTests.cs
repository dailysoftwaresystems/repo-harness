using RepoHarness.Core.Ci;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>
/// check-ci-legs over job metadata a test supplies. The separation these tests pin is the point of
/// the command: a leg that ran out of its budget and a leg whose tests actually failed have opposite
/// remedies -- re-derive the first, fix the second -- and a check that reports them as one number
/// sends a reader to debug code that never ran.
/// </summary>
public sealed class CiLegsServiceTests
{
    private const string Workflow =
        """
        name: Pipeline
        on: [push]
        """;

    [Fact]
    public async Task ALegThatOverranItsBudget_IsReportedAsAnOverrun_AndNotAsAFailure()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);

        var run = Run(
            Leg("linux-clang-asan", budgetMinutes: 45, testSeconds: 2900, testFailed: true),
            Leg("windows-msvc-release", budgetMinutes: 45, testSeconds: 829, testFailed: true));

        var report = await Service(harness, run).CheckAsync(temp.Path, Request(), cancellationToken);

        var overran = Assert.Single(report.Overran);
        Assert.Equal("linux-clang-asan", overran.Leg);
        Assert.False(overran.Success);
        Assert.Contains("possible budget overrun", Assert.Single(overran.Errors), StringComparison.Ordinal);
        Assert.DoesNotContain("real test failure", Assert.Single(overran.Errors), StringComparison.Ordinal);

        var real = Assert.Single(report.RealFailures);
        Assert.Equal("windows-msvc-release", real.Leg);
        Assert.False(real.Overran);
        Assert.Contains("real test failure", Assert.Single(real.Errors), StringComparison.Ordinal);

        var outcome = CiLegsReports.Render(report, json: false);
        Assert.Equal(CiExit.LegRed, outcome.ExitCode);
        Assert.Contains("1 real failure(s)", outcome.Message, StringComparison.Ordinal);
        Assert.Contains("1 possible budget overrun(s)", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ItsCodes_StayInsideThePerCommandRange()
    {
        // Codes 1-9 belong to a command's own contract; a shared code lives at 10 or above. A code
        // that strayed out of the range would collide with a cross-cutting one and be read as it.
        Assert.InRange(CiExit.LegRed, 1, 9);
        Assert.InRange(CiExit.MatrixDidNotRun, 1, 9);
        Assert.NotEqual(CiExit.LegRed, CiExit.MatrixDidNotRun);
    }

    [Fact]
    public async Task ABuildFailure_IsNeverClassifiedAsAnOverrun()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);

        var job = new CiJob(
            "run-tests (linux-gcc-debug, ubuntu-latest, gcc, g++, Debug, none, 45, 1)",
            CiConclusions.Failure,
            [
                new CiStep("Build", CiConclusions.Failure, Moment(0), Moment(9000)),
                new CiStep("Test", CiConclusions.Skipped, null, null),
            ]);

        var report = await Service(harness, Run(job)).CheckAsync(temp.Path, Request(), cancellationToken);

        var leg = Assert.Single(report.Red);
        Assert.False(leg.Overran);
        Assert.Contains("Build step failed", Assert.Single(leg.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALegWithNoBudgetFromAnySource_IsNotClassified()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, budgetMinutes: 0);

        var job = new CiJob(
            "run-tests (linux-gcc-debug)",
            CiConclusions.Failure,
            [new CiStep("Test", CiConclusions.Failure, Moment(0), Moment(120))]);

        var report = await Service(harness, Run(job)).CheckAsync(temp.Path, Request(), cancellationToken);

        var leg = Assert.Single(report.Red);
        Assert.False(leg.Overran);
        Assert.Null(leg.BudgetSeconds);
        Assert.Contains("invents its denominator", Assert.Single(leg.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDeclaredBudget_AnswersWhenTheJobNameDoesNot()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, budgetMinutes: 2);

        var job = new CiJob(
            "run-tests (linux-gcc-debug)",
            CiConclusions.Failure,
            [new CiStep("Test", CiConclusions.Failure, Moment(0), Moment(180))]);

        var report = await Service(harness, Run(job)).CheckAsync(temp.Path, Request(), cancellationToken);

        var leg = Assert.Single(report.Red);
        Assert.Equal(120, leg.BudgetSeconds);
        Assert.Equal("ci.legBudgetMinutes", leg.BudgetSource);
        Assert.True(leg.Overran);
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("timed_out")]
    [InlineData("neutral")]
    [InlineData("skipped")]
    public async Task OnlyFailureIsRed(string conclusion)
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);

        var job = new CiJob("run-tests (linux-gcc-debug, ubuntu-latest, gcc, g++, Debug, none, 45, 1)", conclusion, []);

        var report = await Service(harness, Run(job)).CheckAsync(temp.Path, Request(), cancellationToken);

        Assert.Empty(report.Red);
        Assert.Equal(HarnessExit.Success, CiLegsReports.Render(report, json: false).ExitCode);
    }

    [Fact]
    public async Task AGreenLegNearItsCap_IsWarnedAboutWithoutBeingRed()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);

        var job = new CiJob(
            "run-tests (linux-clang-asan, ubuntu-latest, clang, clang++, Debug, address, 45, 1)",
            CiConclusions.Success,
            [new CiStep("Test", CiConclusions.Success, Moment(0), Moment(2600))]);

        var report = await Service(harness, Run(job)).CheckAsync(temp.Path, Request(), cancellationToken);

        var leg = Assert.Single(report.Legs);
        Assert.True(leg.Success);
        Assert.Contains("re-derive the budget", Assert.Single(leg.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARunWithNoLegAtAll_IsItsOwnAnswer_AndNeverAPass()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);

        var run = Run(new CiJob("label-check", CiConclusions.Skipped, []));

        var report = await Service(harness, run).CheckAsync(temp.Path, Request(), cancellationToken);
        var outcome = CiLegsReports.Render(report, json: false);

        Assert.True(report.MatrixNeverRan);
        Assert.Equal(CiExit.MatrixDidNotRun, outcome.ExitCode);
        Assert.NotEqual(HarnessExit.Success, outcome.ExitCode);
    }

    [Fact]
    public async Task ARunThatAnsweredNoJobRows_IsFatal_AndNeverAPass()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);

        var refusal = await Assert.ThrowsAsync<HarnessException>(
            () => Service(harness, new CiRun(1, "sha", "main", "now", null, [])).CheckAsync(temp.Path, Request(), cancellationToken));

        Assert.Contains("not a pass", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoRunForTheBranch_IsRefused_RatherThanReportedAsGreen()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);

        var service = new CiLegsService(harness.ContextLoader, new StubJobSource([]), harness.GitClient, harness.FileSystem);

        var refusal = await Assert.ThrowsAsync<HarnessException>(
            () => service.CheckAsync(temp.Path, Request(), cancellationToken));

        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
        Assert.Contains("not a pass", refusal.Message, StringComparison.Ordinal);
    }

    private static CiLegsService Service(HarnessFactory harness, CiRun run)
        => new(harness.ContextLoader, new StubJobSource([run]), harness.GitClient, harness.FileSystem);

    private static CiLegsRequest Request() => new(Branch: null, CiLegsRequest.DefaultLimit, []);

    private static CiRun Run(params CiJob[] jobs)
        => new(4242, "deadbeefcafe", "work", "2026-09-16T00:00:00Z", CiConclusions.Failure, jobs);

    private static CiJob Leg(string leg, int budgetMinutes, double testSeconds, bool testFailed) => new(
        $"run-tests ({leg}, ubuntu-latest, clang, clang++, Debug, none, {budgetMinutes}, 1)",
        testFailed ? CiConclusions.Failure : CiConclusions.Success,
        [
            new CiStep("Build", CiConclusions.Success, Moment(0), Moment(60)),
            new CiStep(
                "Test",
                testFailed ? CiConclusions.Failure : CiConclusions.Success,
                Moment(60),
                Moment(60 + testSeconds)),
        ]);

    private static DateTimeOffset Moment(double seconds)
        => new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero).AddSeconds(seconds);

    private static async Task<HarnessFactory> PrepareAsync(TempDirectory temp, int budgetMinutes = 50)
    {
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;

        await harness.InitializeHarnessAsync(
            temp.Path,
            cancellationToken,
            new HarnessConfig { Ci = new CiSettings { LegBudgetMinutes = budgetMinutes } });

        var workflows = temp.Combine(".github", "workflows");
        Directory.CreateDirectory(workflows);
        File.WriteAllText(Path.Combine(workflows, "pipeline.yml"), Workflow + "\n");

        return harness;
    }

    /// <summary>
    /// Job metadata a test supplies. The forge is not reachable from a test, and reading CI needs the
    /// network and an authenticated tool: a test that needed either would red for a property of the
    /// machine rather than of the code.
    /// </summary>
    private sealed class StubJobSource(IReadOnlyList<CiRun> runs) : ICiJobSource
    {
        public Task<IReadOnlyList<long>> ListRunsAsync(
            string repositoryRoot,
            string workflow,
            string branch,
            int limit,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<long>>([.. runs.Select(run => run.Id)]);

        public Task<CiRun> ReadRunAsync(string repositoryRoot, long runId, CancellationToken cancellationToken = default)
            => Task.FromResult(runs.First(run => run.Id == runId));
    }
}
