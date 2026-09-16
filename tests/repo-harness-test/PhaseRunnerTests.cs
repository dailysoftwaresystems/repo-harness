using System.Diagnostics;
using System.Text;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>
/// A phase is where a green result stops meaning anything: an exit code read through a wrapper, a
/// pattern that witnessed the harness's own log header, a hung command killed by a time budget
/// somebody guessed. Each rule is exercised here against a child whose output this test controls.
/// </summary>
public sealed class PhaseRunnerTests
{
    [Fact]
    public async Task APhasePasses_OnItsExitCodeAndItsOwnOutput()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();

        var result = await Runner(factory).RunAsync(
            Child("echo-args", temp.Combine("build.log"), "ready", "OK-MARKER") with { SuccessPattern = "OK-MARKER" },
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Witnessed);
        Assert.True(result.Passed);
        Assert.Equal(LegVerdict.Passed, result.Verdict().Verdict);
        Assert.False(result.Stalled);
    }

    [Fact]
    public async Task APatternThatOnlyMatchesWhatTheHarnessWrote_LeavesThePhaseUnwitnessed()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var log = temp.Combine("build.log");

        // "exec" is in the command line the log header echoes, and nowhere in the child's own
        // output: the child prints nothing at all. A pattern matched against the log would pass
        // this phase, which is how a command that never ran witnesses itself.
        var result = await Runner(factory).RunAsync(
            Child("exit", log, "0") with { SuccessPattern = "exec" },
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.Witnessed);
        Assert.False(result.Passed);
        Assert.Equal(LegVerdict.Unwitnessed, result.Verdict().Verdict);
        Assert.Equal(LegExit.Unwitnessed, Verdicts.ExitCodeFor(result.Verdict().Verdict));

        Assert.Contains("exec", await File.ReadAllTextAsync(log, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptySuccessPattern_IsRefused()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => Runner(factory).RunAsync(
            Child("exit", temp.Combine("build.log"), "0") with { SuccessPattern = "  " },
            TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.ConfigInvalid, refusal.ExitCode);
        Assert.Contains("matches anything", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStallBound_StopsAPhaseThatGoesQuiet()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var quiet = new QuietRunner(TimeSpan.FromSeconds(30));

        // A phase that writes one line and then says nothing. Nothing about its total duration is
        // wrong; its silence is.
        //
        // Measured against a runner rather than a real child, for the reason PacedRunner below gives:
        // the stall clock starts before the child is spawned, so with a bound of one second this once
        // raced `dotnet` starting up, and on a loaded machine over a slow filesystem the race was
        // sometimes lost. That made the test report on how fast a process launches. Stopping a real
        // process tree is covered where it belongs, in ProcessRunnerTests.
        var result = await new PhaseRunner(quiet, factory.FileSystem, factory.Output).RunAsync(
            Child("stream", temp.Combine("test.log"), temp.Combine("never")) with { StallSeconds = 1 },
            TestContext.Current.CancellationToken);

        Assert.True(result.Stalled, "the phase went quiet and was not stopped");
        Assert.False(result.Passed);
        Assert.Contains("hung", result.Verdict().Detail, StringComparison.Ordinal);

        // The claim without a clock in it: the phase was told to stop rather than left to finish on
        // its own. Had the bound never fired, the runner would have run its thirty seconds out and
        // reported that it was not stopped.
        Assert.True(quiet.Stopped, "the bound fired and the phase was not actually stopped");
    }

    [Fact]
    public async Task AStallBound_NeverFiresWhileOutputKeepsArriving()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();

        // Eight lines, 150 milliseconds apart: no gap reaches the bound, and the whole phase runs
        // for longer than the bound. A wall-clock budget would have killed it; a stall bound does
        // not, which is the entire reason the bound is on silence rather than on duration.
        var runner = new PhaseRunner(
            new PacedRunner(TimeSpan.FromMilliseconds(150), lines: 8),
            factory.FileSystem,
            factory.Output);

        var result = await runner.RunAsync(
            Child("echo-args", temp.Combine("paced.log")) with { StallSeconds = 1 },
            TestContext.Current.CancellationToken);

        Assert.False(result.Stalled, "output was flowing and the phase was stopped anyway");
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Duration >= TimeSpan.FromSeconds(1), $"the phase ran for only {result.Duration}");
    }

    [Fact]
    public async Task AStallBoundOfZero_BoundsNothing()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();

        var result = await Runner(factory).RunAsync(
            Child("sleep", temp.Combine("sleep.log"), "300") with { StallSeconds = 0 },
            TestContext.Current.CancellationToken);

        Assert.False(result.Stalled);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task TimingPatterns_PullEveryMatchOutOfTheOutput()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();

        var result = await Runner(factory).RunAsync(
            Child("echo-args", temp.Combine("time.log"), "compile took 1.5s", "link took 12.25s") with
            {
                TimingPatterns = ["took ([0-9.]+)s"],
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(["1.5", "12.25"], result.Timings.Select(timing => timing.Value));
        Assert.All(result.Timings, timing => Assert.Equal("took ([0-9.]+)s", timing.Pattern));
    }

    [Fact]
    public async Task ChildOutput_GoesToTheLog_AndToTheConsoleOnlyWhenVerbose()
    {
        using var temp = new TempDirectory();
        var quiet = new HarnessFactory();
        var loud = new HarnessFactory(verbose: true);

        var quietLog = temp.Combine("quiet.log");
        var loudLog = temp.Combine("loud.log");

        await Runner(quiet).RunAsync(Child("echo-args", quietLog, "CHILD-LINE"), TestContext.Current.CancellationToken);
        await Runner(loud).RunAsync(Child("echo-args", loudLog, "CHILD-LINE"), TestContext.Current.CancellationToken);

        Assert.Contains("CHILD-LINE", await File.ReadAllTextAsync(quietLog, TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Contains("CHILD-LINE", await File.ReadAllTextAsync(loudLog, TestContext.Current.CancellationToken), StringComparison.Ordinal);

        // Progress is one line per leg transition, not a stream of child output.
        Assert.DoesNotContain("CHILD-LINE", quiet.StandardOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("CHILD-LINE", loud.StandardOutput.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailingExitCode_IsReadFromTheProcess()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();

        var result = await Runner(factory).RunAsync(
            Child("exit", temp.Combine("fail.log"), "7"),
            TestContext.Current.CancellationToken);

        Assert.Equal(7, result.ExitCode);
        Assert.Equal(LegVerdict.Failed, result.Verdict().Verdict);
        Assert.Null(result.Witnessed);
    }

    [Fact]
    public async Task AnHonestPhase_RecordsNoClockStep()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();

        var result = await Runner(factory).RunAsync(
            Child("echo-args", temp.Combine("clock.log"), "quick") with { ClockStepToleranceMilliseconds = 2000 },
            TestContext.Current.CancellationToken);

        // Both readings cover the same window, so on a machine whose clock is honest they agree.
        // The other side of this rule — a clock that steps by 25 seconds mid-phase — cannot be
        // exercised without moving this machine's clock, which a test must not do.
        Assert.False(result.ClockStepped);
        Assert.True(result.ClockDrift < TimeSpan.FromSeconds(1), $"wall and monotonic time disagreed by {result.ClockDrift}");
    }

    private static PhaseRunner Runner(HarnessFactory factory)
        => new(factory.ProcessRunner, factory.FileSystem, factory.Output);

    /// <summary>A phase that runs this assembly as a child, exactly as the process tests do.</summary>
    private static PhaseRequest Child(string mode, string logFile, params string[] arguments)
    {
        var request = TestHost.ChildRequest(mode, arguments);

        return new PhaseRequest
        {
            Leg = "win-msvc-release",
            Phase = "build",
            FileName = request.FileName,
            Arguments = request.Arguments,
            Environment = request.Environment,
            LogFile = logFile,
        };
    }

    /// <summary>
    /// A runner that emits lines at a fixed cadence, so the stall bound can be measured against
    /// output that keeps arriving rather than against a child whose timing the machine decides.
    /// </summary>
    /// <summary>
    /// A runner that says one thing and then goes quiet, so a stall is what the bound sees rather
    /// than a machine that was busy. Runs <paramref name="life"/> out if nothing stops it, which is
    /// how a bound that never fires shows up as a failure rather than as a hang.
    /// </summary>
    private sealed class QuietRunner(TimeSpan life) : IProcessRunner
    {
        /// <summary>Whether the phase was stopped, rather than left to finish on its own.</summary>
        public bool Stopped { get; private set; }

        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            var watch = Stopwatch.StartNew();
            const string Line = "starting";

            request.OnOutputLine?.Invoke(Line);

            try
            {
                await Task.Delay(life, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // What the real runner reports when it stopped the child: the exit code is
                // meaningless and the phase is marked as stopped.
                Stopped = true;
                return new ProcessResult(-1, Line + "\n", string.Empty, watch.Elapsed, TimedOut: true);
            }

            return new ProcessResult(0, Line + "\n", string.Empty, watch.Elapsed, TimedOut: false);
        }

        public string? FindExecutable(string command) => command;
    }

    private sealed class PacedRunner(TimeSpan gap, int lines) : IProcessRunner
    {
        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            var captured = new StringBuilder();
            var watch = Stopwatch.StartNew();

            try
            {
                for (var index = 0; index < lines; index++)
                {
                    await Task.Delay(gap, cancellationToken);

                    var line = $"line {index}";
                    captured.Append(line).Append('\n');
                    request.OnOutputLine?.Invoke(line);
                }
            }
            catch (OperationCanceledException)
            {
                // What the real runner reports when it stopped the child: the exit code is
                // meaningless and the phase is marked as stopped.
                return new ProcessResult(-1, captured.ToString(), string.Empty, watch.Elapsed, TimedOut: true);
            }

            return new ProcessResult(0, captured.ToString(), string.Empty, watch.Elapsed, TimedOut: false);
        }

        public string? FindExecutable(string command) => command;
    }
}
