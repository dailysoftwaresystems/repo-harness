using System.Diagnostics;
using System.Globalization;
using System.IO.Enumeration;
using System.Text.RegularExpressions;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Git;
using RepoHarness.Core.Output;
using RepoHarness.Core.Results;
using RepoHarness.Core.Sync;

namespace RepoHarness.Core.Testing;

/// <summary>One leg's test run, with everything the caller has already decided.</summary>
/// <remarks>
/// Everything a host decides — where the tree is, which build directory this variant uses, how many
/// cores the host offers — arrives here already resolved. A service that measured those itself
/// would measure them again per leg and the legs would stop agreeing about the machine they share.
/// </remarks>
public sealed record TestRequest
{
    /// <summary>The leg, as the configuration names it and as the ledger shows it.</summary>
    public required string Leg { get; init; }

    /// <summary>The tree whose tests run, which every input path is resolved against.</summary>
    public required string TreeRoot { get; init; }

    /// <summary>
    /// This leg's variant-keyed build directory, the one the process table is watched for. Two legs
    /// with their own variant directories legitimately test at the same time; two sharing one do not.
    /// </summary>
    public required string BuildDirectory { get; init; }

    /// <summary>Where this run's logs go, already scoped to the run id.</summary>
    public required string RunDirectory { get; init; }

    /// <summary>The leg's configuration, whose <c>test</c> section replaces the project's.</summary>
    public required LegConfig LegSettings { get; init; }

    /// <summary>The project the leg builds, whose <c>test</c> section applies when the leg declares none.</summary>
    public ProjectConfig? Project { get; init; }

    /// <summary>The operating system the tests run on, which selects the platform section.</summary>
    public required string PlatformKey { get; init; }

    /// <summary>
    /// The host's <c>testCores</c>, when it declares one. A remote host rarely has the same core
    /// count as the machine that wrote the configuration, so its own value outranks the default.
    /// </summary>
    public int? HostTestCores { get; init; }

    /// <summary>A filter the caller asked for, passed through the invocation's <c>filterArg</c>.</summary>
    public string? Filter { get; init; }

    /// <summary>
    /// Exclusions the caller asked for, passed through the invocation's <c>excludeArg</c>. Declared
    /// per leg as well, because a leg reached through a transport legitimately runs a narrower suite
    /// than one running here.
    /// </summary>
    public IReadOnlyList<string> Excludes { get; init; } = [];

    /// <summary>
    /// The inputs to fingerprint, already resolved by the caller, or <see langword="null"/> to take
    /// the leg's <c>test.inputs</c> and, failing that, every file git tracks. Supplied by the caller
    /// because <c>test.inputs</c> may glob, and a glob expanded on the host that runs the tests is
    /// the only expansion that names the files those tests actually read.
    /// </summary>
    public IReadOnlyList<string>? Inputs { get; init; }

    /// <summary>
    /// Whether the leg runs under emulation, which decides what its timings are compared with. An
    /// emulated leg is never compared with a native one: the two have no common scale.
    /// </summary>
    public bool Emulated { get; init; }

    /// <summary>The phase's name, which also names its log file under the leg's run directory.</summary>
    public string PhaseName { get; init; } = TestService.DefaultPhaseName;

    /// <summary>
    /// Whether to pull <c>testTimingRegex</c> out of the phase's output, so a timing comes from what
    /// the suite itself reported rather than from the harness guessing which part of the wall clock
    /// was the testing.
    /// </summary>
    public bool Time { get; init; }
}

/// <summary>What one leg's test run established.</summary>
/// <param name="Verdict">The one verdict the leg reached, and the sentence the ledger shows for it.</param>
/// <param name="Entry">
/// The leg's ledger line, test count included, so the ledger is available as data and a leg that
/// silently skipped its emulated arm is visible beside the sibling that did not.
/// </param>
/// <param name="LogFile">
/// Where the whole of the runner's output was kept. Preserved rather than consumed: an emulator
/// witness the suite wrote itself — a line naming the processor it emulated — is evidence only
/// while a regex can still read it.
/// </param>
/// <param name="Phase">What running the invocation produced.</param>
/// <param name="Inputs">
/// Whether the files the tests read held still, moved, or could not be measured, or
/// <see langword="null"/> where there were none to watch. Null rather than a clean comparison,
/// because a leg with nothing to fingerprint has not established that its tree held still.
/// </param>
/// <param name="Contention">What sampling the process table during the leg found.</param>
/// <param name="Cores">How many cores the run was given, and what decided it.</param>
/// <param name="Command">The invocation as it was started, for a reader and for a refusal that quotes it.</param>
public sealed record TestLegResult(
    ReachedVerdict Verdict,
    LegEntry Entry,
    string LogFile,
    PhaseResult Phase,
    InputComparison? Inputs,
    ContentionReport Contention,
    CoreCount Cores,
    TestCommand Command);

/// <summary>Running one leg's tests.</summary>
public interface ITestService
{
    /// <summary>Runs one leg's tests and reports the single verdict they reached.</summary>
    /// <param name="config">The whole configuration.</param>
    /// <param name="request">The leg's test run.</param>
    /// <param name="cancellationToken">Stops the runner and the sampling with it.</param>
    Task<TestLegResult> RunAsync(
        HarnessConfig config,
        TestRequest request,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ITestService"/>
/// <remarks>
/// One leg, one verdict. The rules a green suite stops meaning anything without are all applied
/// here at once, because each of them was measured passing a suite that had not run: a zero exit
/// code with no witness, a tree edited underneath the run, and another process in the same build
/// directory. None of them is an escape hatch, and the most fundamental of them decides.
/// </remarks>
public sealed class TestService(
    PhaseRunner phaseRunner,
    InputFingerprint inputFingerprint,
    ProcessSampler processSampler,
    IFileSystem fileSystem,
    IGitClient gitClient,
    IHarnessOutput output) : ITestService
{
    /// <summary>The command this service reports under.</summary>
    public const string CommandName = "test";

    /// <summary>The phase's name when the caller names none, which also names its log file.</summary>
    public const string DefaultPhaseName = "test";

    /// <summary>
    /// How long the count pattern may spend on one match. A count never decides a verdict, so a
    /// pattern that backtracks is stopped and the count is left unknown rather than hanging the leg
    /// it was only ever counting.
    /// </summary>
    private static readonly TimeSpan CountBudget = TimeSpan.FromSeconds(5);

    private readonly PhaseRunner _phaseRunner = phaseRunner;
    private readonly InputFingerprint _inputFingerprint = inputFingerprint;
    private readonly ProcessSampler _processSampler = processSampler;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IGitClient _gitClient = gitClient;
    private readonly IHarnessOutput _output = output;

    /// <inheritdoc/>
    public async Task<TestLegResult> RunAsync(
        HarnessConfig config,
        TestRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(request);

        var settings = TestInvocationResolver.SettingsFor(config, request.LegSettings, request.Project)
            ?? throw new HarnessException(
                HarnessExit.ConfigInvalid,
                $"Leg '{request.Leg}' declares no test settings and neither does its project, so there "
                + "is nothing to run and nothing to report a verdict on.");

        var invocation = TestInvocationResolver.Resolve(settings, request.PlatformKey);
        var cores = CoreCounts.Resolve(invocation.Cores, request.HostTestCores, config.Defaults.TestCores);
        var command = TestInvocationResolver.CommandFor(
            invocation,
            cores.Value,
            request.Filter,
            request.Excludes,
            new LegPaths(request.TreeRoot, request.BuildDirectory));

        // Compiled before anything starts, as the success pattern is: a pattern that is not a regular
        // expression is a mistake in tracked configuration, and finding it after the suite has run
        // would turn a leg that finished into one that reached no verdict.
        var counter = CompileCountPattern(invocation.CountPattern);

        var logFile = Path.Combine(request.RunDirectory, request.Leg, request.PhaseName + ".log");
        var (inputs, unmeasurable) = await ResolveInputsAsync(settings, request, cancellationToken).ConfigureAwait(false);

        var elapsed = Stopwatch.StartNew();

        await using var guards = await LegGuards
            .OpenAsync(
                _inputFingerprint,
                _processSampler,
                new LegGuardRequest
                {
                    TreeRoot = request.TreeRoot,
                    Inputs = unmeasurable is null ? LegInputs.Watch(inputs) : LegInputs.Unmeasured(unmeasurable),
                    Contention = new ContentionRequest
                    {
                        Leg = request.Leg,
                        BuildDirectory = request.BuildDirectory,
                        BuildTools = config.Contention.BuildTools,
                        SharedResourceTools = config.Contention.SharedResourceTools,
                        SampleSeconds = config.Defaults.ProcessSampleSeconds,
                    },
                },
                cancellationToken)
            .ConfigureAwait(false);

        var phase = await _phaseRunner
            .RunAsync(
                new PhaseRequest
                {
                    Leg = request.Leg,
                    Phase = request.PhaseName,
                    FileName = command.Program,
                    Arguments = command.Arguments,
                    LogFile = logFile,

                    // Where the invocation said, and the tree root when it said nothing — which is
                    // what every test phase written before this ran in. A project that builds out
                    // of source has its tests in the build directory, which is derived per leg and
                    // so cannot be written down: started at the tree root, ctest reports that it
                    // found no tests, in a tree holding thousands.
                    WorkingDirectory = command.WorkingDirectory ?? request.TreeRoot,
                    Environment = Environment(command),
                    SuccessPattern = invocation.SuccessPattern,
                    StallSeconds = config.Defaults.StallSeconds,
                    TimingPatterns = request.Time ? config.TestTimingRegex : [],
                    ClockStepToleranceMilliseconds = config.Defaults.ClockStepToleranceMilliseconds,
                },
                cancellationToken)
            .ConfigureAwait(false);

        var seen = await guards.CloseAsync(cancellationToken).ConfigureAwait(false);
        var contention = seen.Contention!;
        var comparison = seen.Inputs;

        Report(request, contention);

        var reached = seen.Decide(request.Leg, [phase.Verdict()]);
        var entry = new LegEntry
        {
            Leg = request.Leg,
            Verdict = reached.Verdict,
            Detail = reached.Detail,
            Duration = elapsed.Elapsed,
            CommandTime = phase.Duration,
            Emulated = request.Emulated,
            TestCount = CountFrom(counter, phase.Output),
            Phases = [new PhaseRecord(phase.Phase, phase.Duration, phase.ClockStepped)],
            TimingNotes = TimingNotes(phase),
            Timings = [.. phase.Timings.Select(timing => new TimingMark(phase.Phase, timing.Text, timing.Value))],
        };

        return new TestLegResult(reached, entry, phase.LogFile, phase, comparison, contention, cores, command);
    }

    /// <summary>
    /// The regular expression <paramref name="countPattern"/> spells, or <see langword="null"/> when
    /// the invocation declares none.
    /// </summary>
    /// <param name="countPattern">The invocation's <c>countPattern</c>.</param>
    /// <exception cref="HarnessException">
    /// The pattern is not a regular expression. Refused when it is read rather than ignored when it
    /// fails to match: a count nobody can extract is indistinguishable from a leg that ran no tests,
    /// and it is precisely a leg running fewer tests than its siblings that this exists to catch.
    /// </exception>
    public static Regex? CompileCountPattern(string? countPattern)
    {
        if (string.IsNullOrWhiteSpace(countPattern))
        {
            return null;
        }

        try
        {
            return new Regex(countPattern, RegexOptions.Multiline | RegexOptions.CultureInvariant, CountBudget);
        }
        catch (ArgumentException ex)
        {
            throw new HarnessException(
                HarnessExit.ConfigInvalid,
                $"'{countPattern}' is not a count pattern: {ex.Message}",
                ex);
        }
    }

    /// <summary>
    /// How many tests <paramref name="output"/> reports, or <see langword="null"/> when nothing said.
    /// </summary>
    /// <remarks>
    /// The named group <c>total</c> where the pattern declares one, its first capturing group where
    /// it does not, and the whole match otherwise. Legs running the same tests are compared by this
    /// number, and one reporting a different count is flagged: a platform that quietly skips a group
    /// of tests passes on less evidence than its siblings and looks exactly as green.
    /// </remarks>
    /// <param name="countPattern">The compiled pattern, or null when the invocation declares none.</param>
    /// <param name="output">The runner's own output, never anything the harness wrote.</param>
    public static int? CountFrom(Regex? countPattern, string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        if (countPattern is null)
        {
            return null;
        }

        try
        {
            var match = countPattern.Match(output);

            if (!match.Success)
            {
                return null;
            }

            var captured = match.Groups["total"] is { Success: true } named
                ? named.Value
                : match.Groups.Count > 1 && match.Groups[1].Success
                    ? match.Groups[1].Value
                    : match.Value;

            return int.TryParse(captured.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var total)
                ? total
                : null;
        }
        catch (RegexMatchTimeoutException)
        {
            // A count never changes a verdict, so a pattern that could not be evaluated leaves the
            // count unknown rather than failing the leg it was only ever measuring.
            return null;
        }
    }

    /// <summary>
    /// The one verdict the leg reached: the most fundamental of the three it could have reached.
    /// </summary>
    /// <remarks>
    /// A leg whose inputs moved is not reported as failed even when its tests failed, because what
    /// failed was a tree that never existed. The order is <see cref="Verdicts.Worst"/>'s, so this
    /// command cannot rank the vocabulary differently from the two that share it.
    /// </remarks>
    /// <summary>
    /// The inputs to fingerprint, and why they could not be established when they could not.
    /// </summary>
    /// <remarks>
    /// The caller's list, else the leg's <c>test.inputs</c>, else every file git tracks. A set that
    /// could not be established is carried as a reason rather than as an empty list: an empty list
    /// fingerprints cleanly, and "nothing moved" is exactly the answer an unmeasured leg must not
    /// give.
    /// </remarks>
    /// <summary>
    /// Declared inputs with their glob patterns replaced by the files they match.
    /// </summary>
    /// <param name="declared">What <c>test.inputs</c> names: paths, patterns, or both.</param>
    /// <param name="treeRoot">The tree the patterns are relative to.</param>
    /// <remarks>
    /// A pattern is not a file. Left unexpanded it is fingerprinted as absent before the suite and
    /// absent after, the two compare equal, and the leg reports that its inputs held still — which
    /// switches the whole inputs-moved contract off for any repository that declares one, silently,
    /// in exactly the configuration its author wrote to switch it on.
    /// A pattern matching nothing makes the leg unmeasured rather than clean: the declaration says
    /// these files are read while the tests run, and if none of them exists the thing that was
    /// supposed to be watched was not watched.
    /// </remarks>
    private (IReadOnlyList<string> Inputs, string? Unmeasurable) Expand(IReadOnlyList<string> declared, string treeRoot)
    {
        var resolved = new List<string>();

        foreach (var input in declared)
        {
            if (!input.Contains('*', StringComparison.Ordinal) && !input.Contains('?', StringComparison.Ordinal))
            {
                resolved.Add(input);
                continue;
            }

            var directory = Path.GetDirectoryName(input)?.Replace('\\', '/') ?? string.Empty;
            var root = Path.Combine(treeRoot, directory);

            if (!_fileSystem.DirectoryExists(root))
            {
                return ([], $"test.inputs names '{input}', and '{directory}' is not a directory of this tree");
            }

            var pattern = Path.GetFileName(input);

            var matched = _fileSystem
                .EnumerateFiles(root, recursive: false)
                .Where(path => FileSystemName.MatchesSimpleExpression(pattern, Path.GetFileName(path)))
                .Select(path => ManifestBuilder.Relative(treeRoot, path))
                .Order(StringComparer.Ordinal)
                .ToList();

            if (matched.Count == 0)
            {
                return ([], $"test.inputs names '{input}', which matches no file in this tree");
            }

            resolved.AddRange(matched);
        }

        return (resolved, null);
    }

    private async Task<(IReadOnlyList<string> Inputs, string? Unmeasurable)> ResolveInputsAsync(
        TestConfig settings,
        TestRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Inputs is { Count: > 0 })
        {
            return (request.Inputs, null);
        }

        if (settings.Inputs.Count > 0)
        {
            return Expand(settings.Inputs, request.TreeRoot);
        }

        try
        {
            var index = await _gitClient.ListIndexAsync(request.TreeRoot, cancellationToken).ConfigureAwait(false);

            return (
                [.. index
                    .Where(entry => entry.IsRegularFile)
                    .Select(entry => entry.Path)
                    .Distinct(StringComparer.Ordinal)],
                null);
        }
        catch (HarnessException ex)
        {
            return ([], $"the files git tracks in '{request.TreeRoot}' could not be listed: {ex.Message}");
        }
    }

    /// <summary>
    /// Says what sampling found besides a contender, and what it could not see. A clean report read
    /// without its limits is read as more than it is.
    /// </summary>
    private void Report(TestRequest request, ContentionReport contention)
    {
        foreach (var shared in contention.SharedResourceUsers)
        {
            _output.Warn(
                CommandName,
                $"{request.Leg}: {shared.Tool} (pid {shared.Process.Id}) ran outside this run, seen "
                + $"{ContentionReport.Describe(shared.Seen)}; it shares state rather than this build directory.");
        }

        foreach (var unreadable in contention.Unreadable)
        {
            // Reported as unknown, never as nothing found: "no contender was running" and "nobody
            // looked" are different facts and only one of them is evidence.
            _output.Warn(CommandName, $"{request.Leg}: the process table was not read for one sample ({unreadable}).");
        }

        foreach (var limit in contention.Limits)
        {
            _output.Detail(CommandName, $"{request.Leg}: sampling cannot see {limit}");
        }
    }

    /// <summary>
    /// Whatever is already known to make this leg's timings meaningless. A timing mark never changes
    /// a verdict, so this is reported beside the verdict and never inside it.
    /// </summary>
    private static IReadOnlyList<string> TimingNotes(PhaseResult phase)
        => phase.ClockStepped
            ? [$"'{phase.Phase}' spanned a clock step or a host sleep (wall and monotonic time "
                + $"disagreed by {phase.ClockDrift}), so its duration and every mtime it stamped are suspect"]
            : [];

    /// <summary>
    /// The invocation's environment as a phase takes it. A <see langword="null"/> value removes a
    /// variable, and a test invocation never asks for that, so every value survives the widening.
    /// </summary>
    private static IReadOnlyDictionary<string, string?> Environment(TestCommand command)
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, value) in command.Environment)
        {
            environment[name] = value;
        }

        return environment;
    }
}
