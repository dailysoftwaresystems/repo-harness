using RepoHarness.Core.Configuration;
using RepoHarness.Core.Output;
using RepoHarness.Core.Results;
using RepoHarness.Core.Runners;

namespace RepoHarness.Tests;

/// <summary>What has to be confirmed before an expected exception excuses anything.</summary>
public sealed class RunCheckGateTests
{
    [Fact]
    public async Task ConfirmAsync_ExcusesNothing_WhenACheckFails()
    {
        // The whole point of the gate. Unconfirmed, the failure stays genuine: an unconditional
        // excusal hides the regression it was written to explain.
        var entry = ExpectedExceptionMatcherTests.Entry(
            messages: ["busy"],
            runChecks: [Check("probe", new RunCheckExpectation { Success = true })]);

        var result = await ConfirmAsync(entry, _ => RunOutcome.Failed(20, "the probe failed too"));

        Assert.False(result.Confirmed);
        Assert.False(result.Unconditional);
        Assert.Contains("expected success True but reported False", Assert.Single(result.Reasons), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmAsync_Confirms_WhenEveryCheckAgrees()
    {
        var entry = ExpectedExceptionMatcherTests.Entry(
            messages: ["busy"],
            runChecks:
            [
                Check("probe", new RunCheckExpectation { Success = true }),
                Check("probe-two", new RunCheckExpectation { ResultCode = 0, Message = "quiet" }),
            ]);

        var result = await ConfirmAsync(entry, _ => RunOutcome.Ok("the device was quiet"));

        Assert.True(result.Confirmed);
        Assert.Equal(2, result.Checks.Count);
        Assert.All(result.Checks, check => Assert.True(check.Passed));
    }

    [Fact]
    public async Task ConfirmAsync_RequiresTheSameFourFields_WhenSameExceptionIsAsked()
    {
        var entry = ExpectedExceptionMatcherTests.Entry(
            messages: ["busy"],
            message: "excused: the device was busy",
            runChecks: [Check("probe", new RunCheckExpectation { SameException = true })]);

        var same = await ConfirmAsync(
            entry,
            _ => new RunOutcome(false, true, 0, "excused: the device was busy"));

        Assert.True(same.Confirmed);

        // One field differing is a different failure, and a check that passed on it would confirm
        // an entry measured on something else.
        var different = await ConfirmAsync(
            entry,
            _ => new RunOutcome(false, true, 0, "excused: the device was missing"));

        Assert.False(different.Confirmed);
        Assert.Contains("expected the same exception as the entry", Assert.Single(different.Reasons), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmAsync_CountsStepsInsideTheFailingUnitsOwnWindow_AndNowhereElse()
    {
        var entry = ExpectedExceptionMatcherTests.Entry(
            messages: ["busy"],
            runChecks: [Check("probe", new RunCheckExpectation(), minSteps: 2, minStepSeconds: 5)]);

        var window = FailureWindow.Extract(FailureWindowTests.Output(), "corpus/case-7");

        Assert.NotNull(window);

        RunStep[] inside =
        [
            new(FailureWindowTests.At(15), TimeSpan.FromSeconds(30), "inside"),
            new(FailureWindowTests.At(35), TimeSpan.FromSeconds(30), "inside"),
        ];

        Assert.True((await ConfirmAsync(entry, _ => RunOutcome.Ok("quiet"), window, inside)).Confirmed);

        // Two steps of the same length, recorded during the same run, but outside the window of the
        // unit that failed. They excuse nothing: that is exactly the once-per-run sample this gate
        // refuses to be.
        RunStep[] outside =
        [
            new(FailureWindowTests.At(5), TimeSpan.FromSeconds(30), "before the unit started"),
            new(FailureWindowTests.At(60), TimeSpan.FromSeconds(30), "after the verdict"),
        ];

        var result = await ConfirmAsync(entry, _ => RunOutcome.Ok("quiet"), window, outside);

        Assert.False(result.Confirmed);
        Assert.Contains("but counted 0", Assert.Single(result.Reasons), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmAsync_ExcusesNothing_WhenNoWindowCouldBeEstablished()
    {
        // An unfinished window is not evidence of a quiet machine; it is the absence of evidence.
        var entry = ExpectedExceptionMatcherTests.Entry(
            messages: ["busy"],
            runChecks: [Check("probe", new RunCheckExpectation(), minSteps: 1, minStepSeconds: 1)]);

        var result = await ConfirmAsync(
            entry,
            _ => RunOutcome.Ok("quiet"),
            window: null,
            steps: [new RunStep(FailureWindowTests.At(15), TimeSpan.FromSeconds(30), "somewhere")]);

        Assert.False(result.Confirmed);
        Assert.Contains("could not be established", Assert.Single(result.Reasons), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmAsync_ExcusesNothing_WhenTheCheckedRunnerCouldNotBeRun()
    {
        var entry = ExpectedExceptionMatcherTests.Entry(
            messages: ["busy"],
            runChecks: [Check("probe", new RunCheckExpectation { Success = true })]);

        var result = await new RunCheckGate(Output()).ConfirmAsync(
            Scope(),
            entry,
            window: null,
            steps: [],
            (_, _) => throw new HarnessException(HarnessExit.HostUnavailable, "the host was unreachable"),
            TestContext.Current.CancellationToken);

        Assert.False(result.Confirmed);
        Assert.Contains("could not be run", Assert.Single(result.Reasons), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmAsync_Refuses_ACheckThatNamesItsOwnRunner()
    {
        var entry = ExpectedExceptionMatcherTests.Entry(
            messages: ["busy"],
            runChecks: [Check("corpus", new RunCheckExpectation { Success = true })]);

        var exception = await Assert.ThrowsAsync<HarnessException>(
            () => ConfirmAsync(entry, _ => RunOutcome.Ok("quiet")));

        Assert.Equal(HarnessExit.Refused, exception.ExitCode);
        Assert.Contains("would either recurse or confirm itself", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmAsync_ReportsAnEntryWithNoChecksAsUnconditional()
    {
        var result = await ConfirmAsync(
            ExpectedExceptionMatcherTests.Entry(messages: ["busy"]),
            _ => RunOutcome.Ok("quiet"));

        Assert.True(result.Confirmed);
        Assert.True(result.Unconditional);
        Assert.Empty(result.Checks);
    }

    [Fact]
    public async Task ConfirmAsync_InvokesEveryCheckedRunnerByName()
    {
        var invoked = new List<string>();
        var entry = ExpectedExceptionMatcherTests.Entry(
            messages: ["busy"],
            runChecks:
            [
                Check("probe", new RunCheckExpectation()),
                Check("probe-two", new RunCheckExpectation()),
            ]);

        await new RunCheckGate(Output()).ConfirmAsync(
            Scope(),
            entry,
            window: null,
            steps: [],
            (name, _) =>
            {
                invoked.Add(name);
                return Task.FromResult(RunOutcome.Ok("quiet"));
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(["probe", "probe-two"], invoked);
    }

    private static Task<RunCheckGateResult> ConfirmAsync(
        ExpectedException entry,
        Func<string, RunOutcome> respond,
        FailureWindow? window = null,
        IReadOnlyList<RunStep>? steps = null)
        => new RunCheckGate(Output()).ConfirmAsync(
            Scope(),
            entry,
            window,
            steps ?? [],
            (name, _) => Task.FromResult(respond(name)),
            TestContext.Current.CancellationToken);

    private static RunCheck Check(
        string runner,
        RunCheckExpectation expects,
        int minSteps = 0,
        double minStepSeconds = 0)
        => new()
        {
            PredefinedRunner = runner,
            Expects = expects,
            MinStepsInFailureWindow = minSteps,
            MinStepSeconds = minStepSeconds,
        };

    private static RunnerScope Scope() => new("corpus", ["linux-arm64-qemu"], []);

    private static IHarnessOutput Output()
        => new ConsoleHarnessOutput(new StringWriter(), new StringWriter(), verbose: false);
}
