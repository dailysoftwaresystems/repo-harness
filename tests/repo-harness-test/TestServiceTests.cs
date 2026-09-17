using System.Text.RegularExpressions;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Results;
using RepoHarness.Core.Testing;

namespace RepoHarness.Tests;

/// <summary>
/// One leg, one verdict. Each rule here answers a way a suite was measured passing without having
/// run: a zero exit code with no witness, a tree edited underneath the run, and a snapshot nobody
/// could take being read as "nothing moved".
/// </summary>
public sealed class TestServiceTests
{
    private const string Leg = "win-msvc-release";
    private const string Fixture = "corpus/sample.txt";

    /// <summary>How long a file system watch is given to deliver what it saw, before and after a restore.</summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(250);

    [Fact]
    public async Task ASuiteThatRanAndSaidSo_PassesAndItsCountIsRead()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        temp.WriteFile(Fixture, "fixture");

        var result = await Service(factory).RunAsync(
            Config(),
            Request(temp, Child("All 42 tests passed"), countPattern: @"All (?<total>\d+) tests passed"),
            TestContext.Current.CancellationToken);

        Assert.Equal(LegVerdict.Passed, result.Verdict.Verdict);
        Assert.NotNull(result.Inputs);
        Assert.Equal(InputChange.Unchanged, result.Inputs.Change);

        // The count is what makes two legs running "the same tests" comparable at all: a platform
        // that quietly skips a group of them passes on less evidence than its siblings.
        Assert.Equal(42, result.Entry.TestCount);
        Assert.Equal(HarnessExit.Success, Verdicts.ExitCodeFor(result.Verdict.Verdict));
    }

    [Fact]
    public async Task ASuiteThatExitedZeroWithNoMatch_IsUnwitnessed()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        temp.WriteFile(Fixture, "fixture");

        // The child prints nothing. A pattern matched against the log rather than against the
        // command's own output would find itself in the header the harness wrote.
        var result = await Service(factory).RunAsync(
            Config(),
            Request(temp, Child(), successPattern: "tests passed"),
            TestContext.Current.CancellationToken);

        Assert.Equal(LegVerdict.Unwitnessed, result.Verdict.Verdict);
        Assert.Equal(LegExit.Unwitnessed, Verdicts.ExitCodeFor(result.Verdict.Verdict));
        Assert.Null(result.Entry.TestCount);
    }

    [Fact]
    public async Task ThePerLegLog_IsKeptWhereARegexCanStillReadIt()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        temp.WriteFile(Fixture, "fixture");

        var result = await Service(factory).RunAsync(
            Config(),
            Request(temp, Child("Emulating processor arm64", "All 3 tests passed")),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            Path.Combine(RunDirectory(temp), Leg, "test.log"),
            result.LogFile);

        var log = await File.ReadAllTextAsync(result.LogFile, TestContext.Current.CancellationToken);

        // The witness a suite writes itself — a line naming the processor it emulated — is evidence
        // only while it is still readable. A leg that silently skipped its emulated arm is exactly
        // what this line's absence would show.
        var witness = Regex.Match(log, @"Emulating processor (?<processor>\S+?)\]", RegexOptions.None, TimeSpan.FromSeconds(5));

        Assert.True(witness.Success, log);
        Assert.Equal("arm64", witness.Groups["processor"].Value);
    }

    [Fact]
    public async Task AnInputEditedMidRun_IsCaughtEvenWhenItIsRestoredBeforeTheEnd()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var path = temp.WriteFile(Fixture, "fixture");

        // Two snapshots cannot see this: the file is byte for byte what it was. Eight failures were
        // measured this way, all passing seconds later on the unchanged tree.
        var runner = new ScriptedRunner(
            async () =>
            {
                await File.WriteAllTextAsync(path, "rewritten while the suite ran", TestContext.Current.CancellationToken);
                await Task.Delay(SettleDelay, TestContext.Current.CancellationToken);
                await File.WriteAllTextAsync(path, "fixture", TestContext.Current.CancellationToken);
                await Task.Delay(SettleDelay, TestContext.Current.CancellationToken);
            },
            exitCode: 0,
            line: "All 42 tests passed");

        var result = await Service(factory, runner).RunAsync(
            Config(),
            Request(temp, Child()),
            TestContext.Current.CancellationToken);

        Assert.Equal(LegVerdict.InputsMoved, result.Verdict.Verdict);
        Assert.NotNull(result.Inputs);
        Assert.Contains(Fixture, result.Inputs.Changed);
        Assert.Equal(LegExit.InputsMoved, Verdicts.ExitCodeFor(result.Verdict.Verdict));
    }

    [Fact]
    public async Task InputsThatMoved_OutrankASuiteThatFailed()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var path = temp.WriteFile(Fixture, "fixture");

        var runner = new ScriptedRunner(
            () => File.WriteAllTextAsync(path, "moved", TestContext.Current.CancellationToken),
            exitCode: 1,
            line: "2 tests failed");

        var result = await Service(factory, runner).RunAsync(
            Config(),
            Request(temp, Child()),
            TestContext.Current.CancellationToken);

        // Not reported as failed even though the suite failed: what failed was a tree that never
        // existed, so the result says nothing about the code and calls for a different remedy.
        Assert.Equal(LegVerdict.InputsMoved, result.Verdict.Verdict);
        Assert.Contains(Fixture, result.Verdict.Detail, StringComparison.Ordinal);
        Assert.Equal(1, result.Phase.ExitCode);
    }

    [Fact]
    public async Task AnInputSetThatCouldNotBeEstablished_IsUnmeasuredAndNeverClean()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();

        // Nothing declares inputs and the tree is not a git repository, so the default set — every
        // file git tracks — cannot be read at all. An empty list would fingerprint cleanly, which is
        // the one answer an unmeasured leg must not give.
        var request = Request(temp, Child("All 1 tests passed")) with { Inputs = null };

        var result = await Service(factory).RunAsync(Config(), request, TestContext.Current.CancellationToken);

        Assert.Equal(LegVerdict.Unmeasured, result.Verdict.Verdict);
        Assert.NotNull(result.Inputs);
        Assert.Equal(InputChange.Unmeasured, result.Inputs.Change);
        Assert.Equal(LegExit.InputsMoved, Verdicts.ExitCodeFor(result.Verdict.Verdict));
        Assert.True(result.Phase.Passed, "the suite itself passed; it is the measurement that did not");
    }

    [Fact]
    public async Task AnInputThatCouldNotBeRead_IsUnmeasuredAndNeverClean()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses a read while another program holds the file open.");

        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var path = temp.WriteFile(Fixture, "held");

        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        var result = await Service(factory).RunAsync(
            Config(),
            Request(temp, Child("All 1 tests passed")),
            TestContext.Current.CancellationToken);

        Assert.Equal(LegVerdict.Unmeasured, result.Verdict.Verdict);
        Assert.NotNull(result.Inputs);
        Assert.Contains(Fixture, result.Inputs.Changed);
    }

    [Fact]
    public async Task ALegsOwnFilterAndExclusions_ReachTheRunner()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        temp.WriteFile(Fixture, "fixture");

        var request = Request(temp, Child("All 1 tests passed"), filterArg: "-R", excludeArg: "-LE") with
        {
            Filter = "parser",
            Excludes = ["slow"],
        };

        var result = await Service(factory).RunAsync(Config(), request, TestContext.Current.CancellationToken);

        Assert.Equal(["-R", "parser", "-LE", "slow"], result.Command.Arguments.TakeLast(4));
        Assert.Equal(LegVerdict.Passed, result.Verdict.Verdict);
    }

    [Fact]
    public async Task ACountPatternThatIsNotARegularExpression_IsRefusedBeforeAnythingRuns()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        temp.WriteFile(Fixture, "fixture");

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => Service(factory).RunAsync(
            Config(),
            Request(temp, Child("All 1 tests passed"), countPattern: "All (?<total>"),
            TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.ConfigInvalid, refusal.ExitCode);
        Assert.False(Directory.Exists(Path.Combine(RunDirectory(temp), Leg)), "the leg ran before its configuration was read");
    }

    [Fact]
    public void ACountPatternWithNoNamedGroup_FallsBackToItsFirstCapture()
    {
        var pattern = TestService.CompileCountPattern(@"Ran (\d+) cases");

        Assert.Equal(17, TestService.CountFrom(pattern, "Ran 17 cases\n"));
        Assert.Null(TestService.CountFrom(pattern, "nothing to count"));
        Assert.Null(TestService.CountFrom(null, "Ran 17 cases"));
    }

    private static TestService Service(HarnessFactory factory, IProcessRunner? phaseRunner = null)
        => new(
            new PhaseRunner(phaseRunner ?? factory.ProcessRunner, factory.FileSystem, factory.Output),
            new InputFingerprint(factory.FileSystem, factory.Platform),

            // A table that answers, rather than the platform's own source: that one is a program on
            // Windows, and these tests are about the verdict rather than about a WMI query. It has
            // to answer, though — a reading that failed is reported as unmeasured, so a double that
            // quietly failed would make every one of these legs unmeasured and hide what they pin.
            new ProcessSampler(new QuietProcessTable(), factory.Platform, factory.Output),
            factory.FileSystem,
            factory.GitClient,
            factory.Output);

    private static HarnessConfig Config() => new()
    {
        Defaults = new HarnessDefaults { StallSeconds = 0, ProcessSampleSeconds = 0, TestCores = 6 },
    };

    private static string RunDirectory(TempDirectory temp)
        => temp.Combine(".harness-config", "runs", "20260916-100000-0a1b2c3d");

    /// <summary>
    /// A child that prints each of <paramref name="printed"/> on its own line, or exits zero having
    /// printed nothing when there is nothing to print. This assembly is the child, exactly as the
    /// process tests use it, so what the runner says is what the test decided it says.
    /// </summary>
    private static TestInvocation Child(params string[] printed)
    {
        var quiet = printed.Length == 0;

        return new TestInvocation
        {
            Runner = TestHost.DotnetExecutable,
            Args = quiet ? ["exec", TestHost.AssemblyPath, "0"] : ["exec", TestHost.AssemblyPath, .. printed],
            Env = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [TestHost.ChildModeVariable] = quiet ? "exit" : "echo-args",
            },
            SuccessPattern = "tests passed",
        };
    }

    private static TestRequest Request(
        TempDirectory temp,
        TestInvocation child,
        string? successPattern = null,
        string? countPattern = null,
        string? filterArg = null,
        string? excludeArg = null)
    {
        var all = new TestInvocation
        {
            Runner = child.Runner,
            Args = child.Args,
            Env = child.Env,
            SuccessPattern = successPattern ?? child.SuccessPattern,
            CountPattern = countPattern,
            FilterArg = filterArg,
            ExcludeArg = excludeArg,
        };

        return new TestRequest
        {
            Leg = Leg,
            TreeRoot = temp.Path,
            BuildDirectory = temp.Combine("build", "x86_64-msvc-release"),
            RunDirectory = RunDirectory(temp),
            LegSettings = new LegConfig
            {
                Os = PlatformNames.Windows,
                Processor = PlatformNames.X64,
                Config = "release",
                Test = new TestConfig { All = all },
            },
            PlatformKey = PlatformNames.Windows,
            Inputs = [Fixture],
        };
    }

    /// <summary>
    /// A runner that does something to the tree while the phase is running, then reports what the
    /// suite said. The edit is what a real suite cannot be made to make at a known moment.
    /// </summary>
    private sealed class ScriptedRunner(Func<Task> duringRun, int exitCode, string line) : IProcessRunner
    {
        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            await duringRun().ConfigureAwait(false);
            request.OnOutputLine?.Invoke(line);

            return new ProcessResult(exitCode, line + "\n", string.Empty, TimeSpan.FromMilliseconds(5), TimedOut: false);
        }

        public string? FindExecutable(string command) => command;
    }

    /// <summary>
    /// A process table that reads, and finds a machine running nothing but this test. It reports no
    /// degradation, which is what makes these tests about the suite's verdict: a table that could
    /// not be read is a verdict of its own.
    /// </summary>
    private sealed class QuietProcessTable : IProcessTable
    {
        public Task<ProcessTableReading> ReadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ProcessTableReading(
                [new SampledProcess(Environment.ProcessId, null, "repo-harness-test", DateTimeOffset.UnixEpoch, "repo-harness-test")],
                null));
    }
}
