using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;
using RepoHarness.Core.Runners;

namespace RepoHarness.Tests;

/// <summary>
/// A predefined runner gets the same witnesses, bounds and verdicts every other leg-running command
/// gets. These pin the three places that stop being true quietly: a program nobody declared running
/// halfway through a file, a credential reaching a log or an argument list, and an excusal that
/// nothing re-measured turning a regression green.
/// </summary>
public sealed class RunnerRunServiceTests
{
    private const string Leg = "lin-gcc-release";
    private const string RunId = "20260916-100000-0a1b2c3d";
    private const string Secret = "not-a-real-credential-9f3a";

    [Fact]
    public async Task AnActionFileNamingAnUndeclaredProgram_IsRefusedWithNothingRun()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();

        WriteAction(temp, """
            name: corpus
            steps:
              - name: first
                run: |
                  dotnet --version
              - name: second
                run: |
                  curl https://example.invalid
            """);

        var config = Config();
        config.Tools.Add(new ToolConfig { Name = "dotnet" });

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => Service(factory).RunAsync(
            config,
            Request(temp, new RunnerConfig { Action = "corpus.yaml" }),
            TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
        Assert.Contains("curl", refusal.Message, StringComparison.Ordinal);

        // The refusal covers the whole file, so the first step — which is allowed — never ran
        // either. A file that fails on its fourth step has already changed the tree.
        Assert.False(Directory.Exists(temp.Combine(".harness-config", "runs", RunId)));
    }

    [Fact]
    public async Task AStepThatPutsASecretInItsArguments_IsRefusedWithoutQuotingIt()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        WriteSecret(temp);

        var runner = new RunnerConfig
        {
            Phases =
            [
                Phase("harmless", "echo-args", ["one"]),
                Phase("leak", "echo-args", [Secret]),
            ],
        };

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => Service(factory).RunAsync(
            Config(),
            Request(temp, runner),
            TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);

        // Checked over the whole runner, so the harmless step before it never ran either: a run
        // that refuses on its second step has already changed the tree.
        Assert.False(Directory.Exists(temp.Combine(".harness-config", "runs", RunId)));

        // The argument list reaches the log header, the machine's process table and every error
        // that quotes the command. A refusal that named the value would be the leak it prevents.
        Assert.DoesNotContain(Secret, refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, factory.StandardOutput.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, factory.StandardError.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASecretACommandPrinted_ReachesTheChildAndNothingElse()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        WriteSecret(temp);

        // The child prints the variable it was handed, which is the one path a secret can still
        // take into a log: a harness cannot stop a program echoing its own environment.
        var runner = new RunnerConfig
        {
            Phases = [Phase("print", "print-env", ["HARNESS_TOKEN"], successPattern: "nothing matches this")],
        };

        var result = await Service(factory).RunAsync(
            Config(),
            Request(temp, runner),
            TestContext.Current.CancellationToken);

        var log = await File.ReadAllTextAsync(result.Phases[0].LogFile, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(Secret, log, StringComparison.Ordinal);
        Assert.Contains(ActionValues.Mask, log, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, factory.StandardOutput.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, factory.StandardError.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, result.Outcome.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, result.Verdict.Detail, StringComparison.Ordinal);

        // The child did receive the real value; a masked credential would fail later and somewhere
        // else, where the failure says nothing about what was wrong.
        Assert.DoesNotContain("<unset>", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExpectedExceptionWhoseCheckIsNotConfirmed_LeavesTheFailureGenuine()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();

        var runner = Excusable();
        var request = Request(temp, runner) with
        {
            InvokeRunner = (_, _) => Task.FromResult(RunOutcome.Failed(20, "the other runner failed too")),
        };

        var result = await Service(factory).RunAsync(Config(), request, TestContext.Current.CancellationToken);

        Assert.NotNull(result.ExpectedException);
        Assert.NotNull(result.Gate);
        Assert.False(result.Gate.Confirmed);

        // Unconfirmed, the failure stays genuine. An unconditional excusal hides the regression it
        // was written to explain, and hides it best on the day that regression appears.
        Assert.Equal(LegVerdict.Failed, result.Verdict.Verdict);
        Assert.False(result.Outcome.Success);
        Assert.DoesNotContain("a known confound", result.Verdict.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExpectedExceptionWhoseCheckConfirmsIt_ReportsTheDeclaredOutcome()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();

        var request = Request(temp, Excusable()) with
        {
            InvokeRunner = (_, _) => Task.FromResult(RunOutcome.Ok("the other runner reproduced it")),
        };

        var result = await Service(factory).RunAsync(Config(), request, TestContext.Current.CancellationToken);

        Assert.NotNull(result.Gate);
        Assert.True(result.Gate.Confirmed, string.Join("; ", result.Gate.Reasons));
        Assert.False(result.Gate.Unconditional);

        // The step counted is the failing step's own, inside its own window. A once-per-run sample
        // was measured excusing and charging the same failure on the same day.
        Assert.Contains("inside the window", result.Gate.Reasons[0], StringComparison.Ordinal);

        Assert.Equal(LegVerdict.Passed, result.Verdict.Verdict);
        Assert.True(result.Outcome.Success);
        Assert.True(result.Outcome.Warning);
        Assert.Equal("a known confound", result.Outcome.Message);
    }

    [Fact]
    public async Task AnExpectedExceptionWithNothingWiredToInvokeItsCheck_ExcusesNothing()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();

        // No InvokeRunner: the check cannot be run, so it cannot pass, so the failure stays genuine.
        var result = await Service(factory).RunAsync(
            Config(),
            Request(temp, Excusable()),
            TestContext.Current.CancellationToken);

        Assert.False(result.Gate!.Confirmed);
        Assert.Equal(LegVerdict.Failed, result.Verdict.Verdict);
    }

    [Fact]
    public async Task ARunResumedInASecondSegment_ReportsTheUnionAcrossBoth()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var service = Service(factory);
        var token = TestContext.Current.CancellationToken;

        var aborting = new RunnerConfig
        {
            Phases =
            [
                Phase("step-1", "echo-args", ["one"]),
                Phase("step-2", "exit", ["7"]),
                Phase("step-3", "echo-args", ["three"]),
            ],
        };

        var first = await service.RunAsync(Config(), Request(temp, aborting), token);

        Assert.Equal(LegVerdict.Failed, first.Verdict.Verdict);
        Assert.Equal(2, first.Phases.Count);

        // The second attempt skips what the first carried to an outcome, failures included: a suite
        // that ran two thirds of itself, aborted and finished the rest did the whole suite once.
        var second = await service.RunAsync(
            Config(),
            Request(temp, aborting) with { SegmentId = "s2" },
            token);

        Assert.Single(second.Phases);
        Assert.Equal("step-3", second.Phases[0].Phase);

        Assert.Equal(["step-1", "step-2", "step-3"], second.Union.Keys.Order(StringComparer.Ordinal));
        Assert.True(second.Union["step-1"].Success);
        Assert.False(second.Union["step-2"].Success);
        Assert.True(second.Union["step-3"].Success);

        // Every step this attempt ran passed, and the run is still red: reporting only the third of
        // the suite that happened to run last is how a resumed run reports a failure as green.
        Assert.Equal(LegVerdict.Failed, second.Verdict.Verdict);
        Assert.Contains("step-2", second.Verdict.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnActionFilesStepsRun_AndItsPredefinedActionsAreNamedRatherThanPassedOver()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();

        // The program is the bare name of a declared tool; the assembly it runs is an argument, and
        // it is quoted because the splitter honours double quotes and nothing else, so a path with a
        // space in it stays one token.
        WriteAction(temp, $"""
            name: corpus
            steps:
              - name: fetch
                uses: harness/checkout
              - name: measure
                successPattern: measured
                run: |
                  dotnet exec "{TestHost.AssemblyPath.Replace('\\', '/')}" measured
            """);

        var config = Config();
        config.Tools.Add(new ToolConfig { Name = "dotnet" });

        var runner = new RunnerConfig
        {
            Action = "corpus.yaml",
            Env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [TestHost.ChildModeVariable] = "echo-args",
            },
        };

        var result = await Service(factory).RunAsync(config, Request(temp, runner), TestContext.Current.CancellationToken);

        Assert.Equal(LegVerdict.Passed, result.Verdict.Verdict);
        Assert.Single(result.Phases);
        Assert.Equal(["fetch (harness/checkout)"], result.PerformedActions);
    }

    [Fact]
    public async Task RequireBuild_IsReportedRatherThanActedOn()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();

        var runner = new RunnerConfig
        {
            RequireBuild = true,
            Phases = [Phase("measure", "echo-args", ["measured"], successPattern: "measured")],
        };

        var result = await Service(factory).RunAsync(Config(), Request(temp, runner), TestContext.Current.CancellationToken);

        // Syncing and building belong to the orchestrator: a service that did either itself would do
        // it once per leg on a tree that legs share.
        Assert.True(result.RequireBuild);
        Assert.Equal(LegVerdict.Passed, result.Verdict.Verdict);
    }

    [Fact]
    public async Task TwoStepsSharingAName_AreRefused()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();

        var runner = new RunnerConfig
        {
            Phases =
            [
                Phase("measure", "echo-args", ["one"]),
                Phase("measure", "echo-args", ["two"]),
            ],
        };

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => Service(factory).RunAsync(
            Config(),
            Request(temp, runner),
            TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.ConfigInvalid, refusal.ExitCode);
        Assert.Contains("measure", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AStepNamedWithASlash_StillWritesItsOwnLogFile()
    {
        // A step whose run block holds several lines is named "step (1/2)", and that slash is a
        // directory separator on every platform this tool runs on.
        Assert.Equal("build (1-2)", RunnerRunService.LogNameFor("build (1/2)"));
        Assert.Equal("build (2-2)", RunnerRunService.LogNameFor("build (2/2)"));
    }

    [Fact]
    public void AFailureThatNamedNoException_CarriesANameAnEntryCanDeclare()
    {
        Assert.Equal(RunnerRunService.StepFailureType, RunnerRunService.FailureTypeIn("ctest exited 7"));
        Assert.Equal("System.IO.IOException", RunnerRunService.FailureTypeIn("Unhandled: System.IO.IOException: gone"));
    }

    private static RunnerRunService Service(HarnessFactory factory)
        => new(
            new PhaseRunner(factory.ProcessRunner, factory.FileSystem, factory.Output),
            new ActionFileParser(factory.FileSystem, factory.Output),
            new ActionToolPolicy(factory.Platform),
            new ActionValuesReader(factory.FileSystem, factory.Output),
            new ExpectedExceptionMatcher(factory.Output),
            new RunCheckGate(factory.Output),
            new RunSegments(factory.FileSystem, factory.Output),
            new PredefinedActionRunner(factory.GitClient, factory.Output),
            factory.FileSystem,
            factory.Output);

    private static HarnessConfig Config() => new()
    {
        Defaults = new HarnessDefaults { StallSeconds = 0 },
    };

    private static RunnerRunRequest Request(TempDirectory temp, RunnerConfig runner) => new()
    {
        RunnerName = "corpus",
        Runner = runner,
        Leg = Leg,
        Layout = new HarnessLayout(temp.Path, temp.Path),
        RunId = RunId,
        SegmentId = "s1",
        TreeRoot = temp.Path,
        ResolvedLegs = [Leg],
    };

    /// <summary>
    /// A phase that runs this assembly as a child in <paramref name="mode"/>, exactly as the process
    /// tests do, so what the step says and what it exits with are what the test decided.
    /// </summary>
    private static RunnerPhase Phase(
        string name,
        string mode,
        IReadOnlyList<string> arguments,
        string? successPattern = null)
        => new()
        {
            Name = name,
            Command = [TestHost.DotnetExecutable, "exec", TestHost.AssemblyPath, .. arguments],
            Env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [TestHost.ChildModeVariable] = mode,
            },
            SuccessPattern = successPattern,
        };

    /// <summary>
    /// A runner carrying an entry for the failure its only step produces, gated on one check.
    /// </summary>
    private static RunnerConfig Excusable() => new()
    {
        Phases = [Phase("measure", "exit", ["7"])],
        ExpectedExceptions =
        [
            new ExpectedException
            {
                ExceptionType = RunnerRunService.StepFailureType,
                Messages = ["exited 7"],
                Success = true,
                Warning = true,
                ResultCode = 0,
                Message = "a known confound",
                EarnedOn = "lin-gcc-release",
                EarnedAt = "2026-09-16",
                Mechanism = "the fixture server refuses the seventh connection of a session",
                Anchor = "D-TEST-RUNNER-GATE",
                RunChecks =
                [
                    new RunCheck
                    {
                        PredefinedRunner = "reproduce",
                        Expects = new RunCheckExpectation { Success = true },
                        MinStepsInFailureWindow = 1,
                        MinStepSeconds = 0,
                    },
                ],
            },
        ],
    };

    private static void WriteAction(TempDirectory temp, string yaml)
        => temp.WriteFile(Path.Combine(".harness-config", "runner", "actions", "corpus.yaml"), yaml);

    private static void WriteSecret(TempDirectory temp)
        => temp.WriteFile(
            Path.Combine(".harness-config", "runner", ".secrets", "ci.env"),
            $"HARNESS_TOKEN={Secret}\n");
}
