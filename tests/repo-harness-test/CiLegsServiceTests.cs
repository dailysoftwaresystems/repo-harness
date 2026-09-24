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

    /// <summary>
    /// How these tests' workflow names a leg's job: <c>unit (leg, ..., budget, attempt)</c>, the budget the
    /// second-to-last matrix value, where the job carries one. Declared, as a repository declares its own.
    /// </summary>
    private const string LegJobPattern = @"^unit \((?<leg>[^,)]+)(?:.*,\s*(?<budget>[0-9]+)\s*,\s*[0-9]+\s*\)\s*$)?";

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
            "unit (linux-gcc-debug, ubuntu-latest, gcc, g++, Debug, none, 45, 1)",
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
            "unit (linux-gcc-debug)",
            CiConclusions.Failure,
            [new CiStep("Test", CiConclusions.Failure, Moment(0), Moment(120))]);

        var report = await Service(harness, Run(job)).CheckAsync(temp.Path, Request(), cancellationToken);

        var leg = Assert.Single(report.Red);
        Assert.False(leg.Overran);
        Assert.True(leg.Unclassified);
        Assert.Null(leg.BudgetSeconds);
        Assert.Contains("invents its denominator", Assert.Single(leg.Errors), StringComparison.Ordinal);

        // Counted as what it is: its own line calls it neither, and neither does the summary.
        Assert.Empty(report.RealFailures);
        Assert.Contains(
            "0 real failure(s) and 0 possible budget overrun(s), besides 1 whose test step failed with no budget to measure it against",
            CiLegsReports.Render(report, json: false).Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A budget group that captures something other than a whole number of minutes is refused, never read as no
    /// budget: the next source would answer for it without a word, and a pattern that reads every budget wrong would
    /// pass unnoticed.
    /// </summary>
    [Fact]
    public async Task ABudgetThatIsNoWholeNumberOfMinutes_IsRefused_NeverReadAsNone()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, ci: new CiSettings
        {
            LegJobPattern = @"^unit \((?<leg>[^,)]+), (?<budget>[^)]+)\)",
            BuildStep = "Build",
            TestStep = "Test",
            LegBudgetMinutes = 2,
        });

        var run = Run(new CiJob("unit (linux-gcc-debug, 15m)", CiConclusions.Failure, []));

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => Service(harness, run).CheckAsync(temp.Path, Request(), cancellationToken));

        Assert.Equal(HarnessExit.ConfigInvalid, refusal.ExitCode);
        Assert.Contains("ci.legJobPattern's budget group captured '15m' from job 'unit (linux-gcc-debug, 15m)'", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDeclaredBudget_AnswersWhenTheJobNameDoesNot()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, budgetMinutes: 2);

        var job = new CiJob(
            "unit (linux-gcc-debug)",
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

        var job = new CiJob("unit (linux-gcc-debug, ubuntu-latest, gcc, g++, Debug, none, 45, 1)", conclusion, []);

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
            "unit (linux-clang-asan, ubuntu-latest, clang, clang++, Debug, address, 45, 1)",
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

    /// <summary>
    /// A leg is whatever job the declared pattern matches, named by its leg group, and read through the
    /// steps the settings name: nothing about a workflow is assumed. A job the pattern does not match, or
    /// whose leg it captures empty, is no leg.
    /// </summary>
    [Fact]
    public async Task ALegIsWhatTheDeclaredPatternMatches_ReadThroughTheStepsItNames()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, ci: new CiSettings
        {
            LegJobPattern = @"^(?:.* / )?check \[(?<leg>[a-z-]*)\]$",
            BuildStep = "Compile",
            TestStep = "Verify",
            LegBudgetMinutes = 2,
        });

        var run = Run(
            new CiJob(
                "ci / check [mac-arm]",
                CiConclusions.Failure,
                [
                    new CiStep("Compile", CiConclusions.Success, Moment(0), Moment(60)),
                    new CiStep("Verify", CiConclusions.Failure, Moment(60), Moment(240)),
                ]),
            new CiJob("check []", CiConclusions.Failure, []),
            new CiJob("lint", CiConclusions.Failure, []));

        var report = await Service(harness, run).CheckAsync(temp.Path, Request(), cancellationToken);

        var leg = Assert.Single(report.Legs);
        Assert.Equal("mac-arm", leg.Leg);
        Assert.True(leg.Overran);
        Assert.StartsWith("the Verify step failed at 180s of a 120s budget", Assert.Single(leg.Errors), StringComparison.Ordinal);
    }

    /// <summary>
    /// A leg whose job name gave no budget takes it from its workflow, where the declared pattern finds it
    /// with the leg's own name, as itself - a name with regular-expression characters in it among them - and
    /// never another leg's whose name begins the same.
    /// </summary>
    [Fact]
    public async Task TheWorkflowGivesALegItsBudget_WhereItsJobNameDoesNot()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(
            temp,
            budgetMinutes: 0,
            workflow: "    - { leg: mac-arm-x, minutes: 9 }\n    - { leg: mac-arm, minutes: 3 }\n    - { leg: clang++-asan, minutes: 2 }\n",
            workflowBudget: "leg: {leg}, minutes: (?<budget>[0-9]+)");

        var run = Run(
            new CiJob("unit (mac-arm)", CiConclusions.Failure, [new CiStep("Test", CiConclusions.Failure, Moment(0), Moment(200))]),
            new CiJob("unit (clang++-asan)", CiConclusions.Failure, [new CiStep("Test", CiConclusions.Failure, Moment(0), Moment(30))]));

        var report = await Service(harness, run).CheckAsync(temp.Path, Request(), cancellationToken);

        var mac = Assert.Single(report.Legs, leg => leg.Leg == "mac-arm");
        Assert.Equal(180, mac.BudgetSeconds);
        Assert.Equal("workflow", mac.BudgetSource);
        Assert.True(mac.Overran);

        var clang = Assert.Single(report.Legs, leg => leg.Leg == "clang++-asan");
        Assert.Equal(120, clang.BudgetSeconds);
        Assert.False(clang.Overran);
    }

    /// <summary>
    /// A repository whose settings do not say how its workflows name legs and steps is refused, naming what
    /// to set, and never read by names this tool made up: those are some other workflow's, and would find
    /// no leg here, or the wrong ones.
    /// </summary>
    [Theory]
    [InlineData(null, null, null, "ci.legJobPattern, ci.buildStep, ci.testStep are not set")]
    [InlineData("^unit \\((?<leg>[^,)]+)", "Build", null, "ci.testStep is not set")]
    [InlineData("^unit \\((?<leg>[^,)]+)", null, "Test", "ci.buildStep is not set")]
    public async Task AWorkflowWhoseLegsAreNotDeclared_IsRefused_NamingWhatToSet(string? pattern, string? build, string? test, string expected)
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, ci: new CiSettings { LegJobPattern = pattern, BuildStep = build, TestStep = test });

        var refusal = await Assert.ThrowsAsync<HarnessException>(
            () => Service(harness, Run(Leg("linux-gcc-debug", budgetMinutes: 45, testSeconds: 60, testFailed: true)))
                .CheckAsync(temp.Path, Request(), cancellationToken));

        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
        Assert.Contains(expected, refusal.Message, StringComparison.Ordinal);
        Assert.Contains("This is not a pass.", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("'DssHarness help ci'", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A pattern that cannot be evaluated in time is refused as that, never read as no match: the job it could not
    /// read would be missing from the report, and a red leg with it.
    /// </summary>
    [Fact]
    public async Task APatternThatRunsOutOfTime_IsRefused_NeverReadAsNoMatch()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, ci: new CiSettings
        {
            LegJobPattern = "^(?<leg>(?:a|aa)+)$",
            BuildStep = "Build",
            TestStep = "Test",
            LegBudgetMinutes = 2,
        });

        var run = Run(new CiJob(new string('a', 48) + "!", CiConclusions.Failure, []));

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => Service(harness, run).CheckAsync(temp.Path, Request(), cancellationToken));

        Assert.Equal(HarnessExit.ConfigInvalid, refusal.ExitCode);
        Assert.Contains("ci.legJobPattern took longer than 1s", refusal.Message, StringComparison.Ordinal);
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
        $"unit ({leg}, ubuntu-latest, clang, clang++, Debug, none, {budgetMinutes}, 1)",
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

    /// <summary>A repository whose one workflow is <see cref="Workflow"/> and <paramref name="workflow"/>, with ci settings as given.</summary>
    /// <param name="temp">Where the repository is.</param>
    /// <param name="budgetMinutes">ci.legBudgetMinutes.</param>
    /// <param name="workflow">What the workflow holds besides its name.</param>
    /// <param name="workflowBudget">ci.workflowBudgetPattern.</param>
    /// <param name="ci">The ci settings, where a test declares its own: in place of the conventions above, budgetMinutes and workflowBudget; the workflow is written all the same.</param>
    private static async Task<HarnessFactory> PrepareAsync(
        TempDirectory temp,
        int budgetMinutes = 50,
        string workflow = "",
        string? workflowBudget = null,
        CiSettings? ci = null)
    {
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;

        await harness.InitializeHarnessAsync(
            temp.Path,
            cancellationToken,
            new HarnessConfig
            {
                Ci = ci ?? new CiSettings
                {
                    LegBudgetMinutes = budgetMinutes,
                    LegJobPattern = LegJobPattern,
                    BuildStep = "Build",
                    TestStep = "Test",
                    WorkflowBudgetPattern = workflowBudget,
                },
            });

        var workflows = temp.Combine(".github", "workflows");
        Directory.CreateDirectory(workflows);
        File.WriteAllText(Path.Combine(workflows, "pipeline.yml"), Workflow + "\n" + workflow);

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
