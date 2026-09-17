using System.Buffers;
using System.Diagnostics;
using System.Text.RegularExpressions;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Output;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Runners;

/// <summary>One predefined runner on one leg, with everything the caller has already decided.</summary>
public sealed record RunnerRunRequest
{
    /// <summary>The runner, as <c>predefinedRunners</c> keys it and a run check names it.</summary>
    public required string RunnerName { get; init; }

    /// <summary>The runner's configuration: its steps, its bounds and the failures it is allowed to produce.</summary>
    public required RunnerConfig Runner { get; init; }

    /// <summary>The leg, as the configuration names it and as the ledger shows it.</summary>
    public required string Leg { get; init; }

    /// <summary>Resolved paths for this repository, which the action files and the segment record live under.</summary>
    public required HarnessLayout Layout { get; init; }

    /// <summary>This run's id, which every log path and the segment record are scoped to.</summary>
    public required string RunId { get; init; }

    /// <summary>
    /// This attempt's id, unique within the run. Supplied rather than generated, so that a caller
    /// resuming a run cannot accidentally reopen the segment it is resuming and record its work into
    /// the attempt that was interrupted.
    /// </summary>
    public required string SegmentId { get; init; }

    /// <summary>The tree the runner acts on, which a program named by path must resolve inside.</summary>
    public required string TreeRoot { get; init; }

    /// <summary>The leg's work directory, which a step's own <c>workingDirectory</c> is relative to.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// The leg's variant-keyed build directory, or <see langword="null"/> where this run reaches no
    /// leg and so has none. What a step's <c>watchContention</c> watches.
    /// </summary>
    public string? BuildDirectory { get; init; }

    /// <summary>
    /// The legs this run actually selected, for a runner whose own <c>legs</c> list is empty and
    /// therefore means the default set. Left out, an expected exception's scope is the runner alone,
    /// which is wider than the run intended.
    /// </summary>
    public IReadOnlyList<string> ResolvedLegs { get; init; } = [];

    /// <summary>Whether to pull <c>runTimingRegex</c> out of every step's output.</summary>
    public bool Time { get; init; }

    /// <summary>Whether the leg runs under emulation, which decides what its timings are compared with.</summary>
    public bool Emulated { get; init; }

    /// <summary>
    /// Runs another predefined runner by name, for the checks that confirm an expected exception.
    /// </summary>
    /// <remarks>
    /// Taken from the caller so the gate's re-entry is wired in one place and cannot recurse: a
    /// check names a runner that is never the one carrying it, and the runner it names carries no
    /// checks of its own. Left out, a check cannot be run, so it cannot pass, so the failure it
    /// would have excused stays genuine — which is the bias every doubtful case here takes.
    /// </remarks>
    public Func<string, CancellationToken, Task<RunOutcome>>? InvokeRunner { get; init; }
}

/// <summary>What running one predefined runner on one leg established.</summary>
/// <param name="Verdict">The one verdict the leg reached, and the sentence the ledger shows for it.</param>
/// <param name="Outcome">
/// What the run reported, in the four fields an expected exception declares and a run check
/// compares against, so a declared outcome and a genuine one are read the same way round.
/// </param>
/// <param name="Entry">The leg's ledger line, so the ledger is available as data.</param>
/// <param name="Phases">Every step this attempt ran, in order.</param>
/// <param name="Union">
/// The outcome of every step any segment of this run completed. What a resumed run reports: a suite
/// that ran two thirds of its steps, aborted and finished the rest did the whole suite once.
/// </param>
/// <param name="ExpectedException">The entry that matched the failure, when one did.</param>
/// <param name="Gate">
/// What the checks decided about that entry. Unconfirmed, the failure stays genuine, and this says
/// so rather than leaving an excusal that was never re-measured indistinguishable from one that was.
/// </param>
/// <param name="RequireBuild">
/// Whether this runner needs its leg's tree synced and built first. Reported rather than acted on:
/// syncing and building are the orchestrator's, and a service that did either itself would do it
/// once per leg on a tree that legs share.
/// </param>
/// <param name="PerformedActions">
/// The predefined actions this run performed before its first program started: reading the action
/// file's inputs, and confirming the tree is the commit the file names. Reported so a reader can
/// see that they happened, rather than inferring it from a run that behaved as though they had.
/// </param>
public sealed record RunnerLegResult(
    ReachedVerdict Verdict,
    RunOutcome Outcome,
    LegEntry Entry,
    IReadOnlyList<PhaseResult> Phases,
    IReadOnlyDictionary<string, RunOutcome> Union,
    ExpectedException? ExpectedException,
    RunCheckGateResult? Gate,
    bool RequireBuild,
    IReadOnlyList<string> PerformedActions);

/// <summary>Running one predefined runner on one leg.</summary>
public interface IRunnerRunService
{
    /// <summary>Runs one predefined runner on one leg and reports the single verdict it reached.</summary>
    /// <param name="config">The whole configuration.</param>
    /// <param name="request">The runner, the leg, and where this run's state lives.</param>
    /// <param name="cancellationToken">Stops the steps and the children with them.</param>
    Task<RunnerLegResult> RunAsync(
        HarnessConfig config,
        RunnerRunRequest request,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IRunnerRunService"/>
/// <remarks>
/// <para>
/// A predefined runner gets the same isolation, stall bounds, witnesses and reporting every other
/// leg-running command gets, because a procedure that lives in configuration rather than in the tool
/// is still the thing whose green result somebody is going to trust.
/// </para>
/// <para>
/// Two rules decide the order of everything here. Nothing runs until every program the run can start
/// has been vetted, because a file that reaches its fourth step before failing on an undeclared
/// program has already changed the tree. And a failure is genuine until something re-measures it:
/// an entry that matched excuses nothing until its checks pass, since an unconditional excusal hides
/// the regression it was written to explain, on exactly the day that regression appears.
/// </para>
/// </remarks>
public sealed class RunnerRunService(
    PhaseRunner phaseRunner,
    IActionFileParser actionFileParser,
    ActionToolPolicy toolPolicy,
    ActionValuesReader valuesReader,
    ExpectedExceptionMatcher expectedExceptionMatcher,
    RunCheckGate runCheckGate,
    RunSegments runSegments,
    IPredefinedActionRunner predefinedActions,
    InputFingerprint inputFingerprint,
    ProcessSampler processSampler,
    Git.IGitClient gitClient,
    Platform.IHostPlatform platform,
    IFileSystem fileSystem,
    IHarnessOutput output) : IRunnerRunService
{
    private readonly InputFingerprint _inputFingerprint = inputFingerprint;
    private readonly ProcessSampler _processSampler = processSampler;
    private readonly Git.IGitClient _gitClient = gitClient;

    /// <summary>The command this service reports under.</summary>
    public const string CommandName = "run";

    /// <summary>
    /// The type a failure carries when the step that failed named no exception of its own — a step
    /// that merely exited non-zero.
    /// </summary>
    /// <remarks>
    /// Given a name so that an entry can declare it. Left without one, every entry's
    /// <c>exceptionType</c> would be unmatchable against the most common failure a runner produces,
    /// and an author would have to discover that by writing an entry that never fires.
    /// </remarks>
    public const string StepFailureType = "StepFailure";

    /// <summary>
    /// How an exception type is spelled where a child printed one: a dotted name ending in
    /// <c>Exception</c>, which is the form every runtime this tool drives writes it in.
    /// </summary>
    private static readonly Regex ExceptionType = new(
        @"\b([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*Exception)\b",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));


    private readonly PhaseRunner _phaseRunner = phaseRunner;
    private readonly IActionFileParser _actionFileParser = actionFileParser;
    private readonly ActionToolPolicy _toolPolicy = toolPolicy;
    private readonly ActionValuesReader _valuesReader = valuesReader;
    private readonly ExpectedExceptionMatcher _expectedExceptionMatcher = expectedExceptionMatcher;
    private readonly RunCheckGate _runCheckGate = runCheckGate;
    private readonly RunSegments _runSegments = runSegments;
    private readonly IPredefinedActionRunner _predefinedActions = predefinedActions;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly Platform.IHostPlatform _platform = platform;
    private readonly IHarnessOutput _output = output;

    /// <inheritdoc/>
    public async Task<RunnerLegResult> RunAsync(
        HarnessConfig config,
        RunnerRunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(request);

        var steps = await StepsAsync(config, request, cancellationToken).ConfigureAwait(false);
        var values = await _valuesReader
            .ReadAsync(request.Layout.RunnerEnvDirectory, request.Layout.RunnerSecretsDirectory, cancellationToken)
            .ConfigureAwait(false);

        Refuse(request, steps.Phases, values);

        var record = await _runSegments
            .BeginAsync(request.Layout, request.RunId, request.Leg, request.SegmentId, DateTimeOffset.UtcNow, cancellationToken)
            .ConfigureAwait(false);

        var remaining = new HashSet<string>(record.Remaining(steps.Phases.Select(phase => phase.Name)), StringComparer.OrdinalIgnoreCase);
        var elapsed = Stopwatch.StartNew();
        var state = new RunState { Leg = request.Leg };

        // Said before the first one starts, naming every step. A parallel run weaves several legs'
        // lines together, so what a leg set out to do is the one thing its output cannot be read
        // backwards to recover.
        _output.Info(
            CommandName,
            $"{request.Leg}: starting {steps.Phases.Count} step(s): "
            + string.Join(", ", steps.Phases.Select(phase => values.Redact(phase.Name))));

        // One scope around every step, not one per step: an action of five steps has four gaps
        // between them, and a file changed in a gap is the same moving tree as one changed inside a
        // step. A sampler restarted per step loses a contender that spanned the join.
        var watching = steps.Phases.Any(phase => phase.WatchContention);
        var fingerprinting = steps.Phases.Any(phase => phase.RequireInputsUnmoved);

        if (watching && string.IsNullOrEmpty(request.BuildDirectory))
        {
            // Refused rather than watched-as-nothing. A sample of no directory reports a clean one,
            // which is the "nobody looked read as nothing found" this tool refuses everywhere. It
            // cannot be caught when the file is read: an action file is tracked, and reading it
            // cannot know whether the run that uses it will reach a leg.
            throw new HarnessException(
                HarnessExit.UsageError,
                $"a step of '{request.RunnerName}' asks for watchContention, and this run reaches no "
                + "leg, so there is no build directory to watch. Run it for a leg, or take the key "
                + "off the step.");
        }

        var (guarded, unmeasurable) = fingerprinting
            ? await TrackedAsync(request, cancellationToken).ConfigureAwait(false)
            : ((IReadOnlyList<string>?)null, null);

        await using var guards = await LegGuards
            .OpenAsync(
                _inputFingerprint,
                _processSampler,
                new LegGuardRequest
                {
                    Leg = request.Leg,
                    TreeRoot = request.TreeRoot,
                    Inputs = guarded,
                    UnmeasurableInputs = unmeasurable,
                    Contention = watching
                        ? new ContentionRequest
                        {
                            Leg = request.Leg,
                            BuildDirectory = request.BuildDirectory!,
                            BuildTools = config.Contention.BuildTools,
                            SharedResourceTools = config.Contention.SharedResourceTools,
                            SampleSeconds = config.Defaults.ProcessSampleSeconds,
                        }
                        : null,
                },
                cancellationToken)
            .ConfigureAwait(false);

        foreach (var phase in steps.Phases)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!remaining.Contains(phase.Name))
            {
                // Already carried to an outcome by an earlier segment. Skipped rather than repeated,
                // because a suite that aborts near its end otherwise costs its whole duration again.
                _output.Detail(CommandName, $"{request.Leg}: '{values.Redact(phase.Name)}' is already done; skipping it");
                state.Skipped.Add(phase.Name);
                continue;
            }

            _output.Info(CommandName, $"{request.Leg}: '{values.Redact(phase.Name)}' started");

            var result = await RunPhaseAsync(config, request, phase, values, cancellationToken).ConfigureAwait(false);
            Record(state, phase, result, values);

            _output.Info(
                CommandName,
                $"{request.Leg}: '{values.Redact(phase.Name)}' finished "
                + $"{(result.Passed ? "ok" : "failed")} in {LedgerReport.FormatDuration(result.Duration)}");

            await _runSegments
                .RecordCompletedAsync(
                    request.Layout,
                    request.RunId,
                    request.Leg,
                    request.SegmentId,
                    phase.Name,
                    state.Outcomes[^1],
                    state.FinishedAt[^1],
                    cancellationToken)
                .ConfigureAwait(false);

            if (state.Stopped)
            {
                break;
            }
        }

        _output.Info(
            CommandName,
            $"{request.Leg}: finished {state.Outcomes.Count} of {steps.Phases.Count} step(s) in "
            + $"{LedgerReport.FormatDuration(elapsed.Elapsed)}");

        await _runSegments
            .EndAsync(request.Layout, request.RunId, request.Leg, request.SegmentId, DateTimeOffset.UtcNow, cancellationToken)
            .ConfigureAwait(false);

        var seen = await guards.CloseAsync(cancellationToken).ConfigureAwait(false);
        var decided = await DecideAsync(request, state, values, cancellationToken).ConfigureAwait(false);

        // What the guards saw is folded in the same way every other verb folds it, so a step whose
        // tree moved reports the verdict a test leg would and not a sentence of its own.
        decided = decided with { Verdict = seen.Decide(request.Leg, [decided.Verdict]) };
        var union = _runSegments.Load(request.Layout, request.RunId, request.Leg).Union;

        decided = Carried(decided, state, union);

        var entry = new LegEntry
        {
            Leg = request.Leg,
            Verdict = decided.Verdict.Verdict,
            Detail = decided.Verdict.Detail,
            Duration = elapsed.Elapsed,
            CommandTime = state.CommandTime,
            Emulated = request.Emulated,
            Phases = [.. state.Phases.Select(phase => new PhaseRecord(phase.Phase, phase.Duration, phase.ClockStepped))],
            TimingNotes = [.. state.Phases
                .Where(phase => phase.ClockStepped)
                .Select(phase => $"'{values.Redact(phase.Phase)}' spanned a clock step or a host sleep, so its duration is suspect")],

            // Redacted like every other line that leaves a run: a timing pattern matches the phase's
            // own output, and a phase that printed a secret would otherwise have it copied into the
            // ledger and the JSON a script keeps.
            Timings = [.. state.Phases.SelectMany(phase => phase.Timings.Select(timing => new TimingMark(
                values.Redact(phase.Phase),
                values.Redact(timing.Text),
                values.Redact(timing.Value))))],
        };

        return new RunnerLegResult(
            decided.Verdict,
            decided.Outcome,
            entry,
            state.Phases,
            union,
            decided.Entry,
            decided.Gate,
            request.Runner.RequireBuild,
            steps.PerformedActions);
    }

    /// <summary>
    /// A step's name as a file name: every character a path cannot carry replaced.
    /// </summary>
    /// <remarks>
    /// A step whose <c>run</c> block holds several lines is named <c>step (1/2)</c>, and that slash
    /// is a directory separator on every platform this tool runs on. Replaced rather than stripped,
    /// so two steps whose names differ only in such a character still name two different files.
    /// </remarks>
    /// <param name="phase">The step's name, as the ledger shows it.</param>
    public static string LogNameFor(string phase) => RunSegments.FileNameFor(phase);

    /// <summary>
    /// The exception type <paramref name="output"/> names, or <see cref="StepFailureType"/> when it
    /// names none.
    /// </summary>
    /// <remarks>
    /// A step is a child process, not a call, so the only type a failure can carry is one the child
    /// printed. Reading it out of the output is what lets an entry earned against a typed failure
    /// keep matching the day the same failure arrives from a wrapper that only prints it.
    /// </remarks>
    /// <param name="output">The failing step's own output.</param>
    public static string FailureTypeIn(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        try
        {
            var match = ExceptionType.Match(output);
            return match.Success ? match.Groups[1].Value : StepFailureType;
        }
        catch (RegexMatchTimeoutException)
        {
            // An answer this expensive is no answer, and the failure keeps the name that matches
            // nothing an author did not deliberately declare.
            return StepFailureType;
        }
    }

    /// <summary>
    /// The runner's steps, with every program it can start already vetted.
    /// </summary>
    /// <exception cref="HarnessException">
    /// The runner declares neither steps nor an action, two steps share a name, or a program is
    /// neither declared under <c>tools</c> nor shipped by the repository. Refused before the first
    /// step runs, because the whole point of the check is that none of them did.
    /// </exception>
    private async Task<RunnerSteps> StepsAsync(
        HarnessConfig config,
        RunnerRunRequest request,
        CancellationToken cancellationToken)
    {
        var runner = request.Runner;
        IReadOnlyList<RunnerPhase> phases;
        IReadOnlyList<string> performed = [];

        if (runner.Action is { Length: > 0 } action)
        {
            var file = await _actionFileParser
                .LoadAsync(request.Layout.RunnerActionsDirectory, action, cancellationToken)
                .ConfigureAwait(false);

            // Before anything starts, and over the whole file rather than step by step: a file whose
            // last step names an undeclared program is refused with its first step not yet run.
            _toolPolicy.Enforce(file, config, request.TreeRoot);
            RefuseUnknownNames(file);

            // Performed before the first program starts: one settles what the steps read, the other
            // settles which tree they read it from, and a run that discovered either halfway through
            // would already have written into the wrong one.
            var actions = await _predefinedActions
                .PerformAsync(file, request.TreeRoot, cancellationToken)
                .ConfigureAwait(false);

            Clean(request);

            performed = actions.Performed;
            phases = [.. file.ToPhases().Select(phase => WithInputs(phase, actions.Environment))];
        }
        else
        {
            Clean(request);
            phases = runner.Phases;
        }

        if (phases.Count == 0 && performed.Count == 0)
        {
            throw new HarnessException(
                HarnessExit.ConfigInvalid,
                $"Runner '{request.RunnerName}' declares no phases and no action, so there is nothing "
                + "to run and nothing to report a verdict on.");
        }

        var repeated = phases
            .GroupBy(phase => phase.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        if (repeated.Count > 0)
        {
            throw new HarnessException(
                HarnessExit.ConfigInvalid,
                $"Runner '{request.RunnerName}' names step(s) {string.Join(", ", repeated)} more than "
                + "once. Two steps sharing a name write one log, and a resumed run cannot tell which "
                + "of them it already did.");
        }

        foreach (var name in performed)
        {
            _output.Detail(CommandName, $"{request.Leg}: performed '{name}'");
        }

        return new RunnerSteps(phases, performed);
    }

    /// <summary>Runs one step, with the runner's bounds and the values it reads.</summary>
    /// <summary>
    /// Refuses a step naming something nothing can fill in, over the whole file and before its
    /// first program starts.
    /// </summary>
    /// <param name="file">The action as it was read.</param>
    /// <exception cref="HarnessException">A step names a placeholder nothing supplies.</exception>
    /// <remarks>
    /// The same vocabulary a project's test invocation uses, refused the same way. Left unchecked a
    /// brace reaches the interpreter as a literal path segment, which a consumer measured: a run
    /// line naming <c>{treeDir}</c> produced a path holding the braces and an Errno 2, from a step
    /// that looked exactly like the configuration that works.
    /// </remarks>
    private static void RefuseUnknownNames(ActionFile file)
    {
        var declared = file.Inputs.Select(input => input.Name).ToList();

        foreach (var step in file.Steps)
        {
            foreach (var argument in step.Commands.SelectMany(command => command.Arguments))
            {
                LegPathNames.RefuseUnknown(argument, $"'{step.Name}' run line", declared);
            }

            LegPathNames.RefuseUnknown(step.WorkingDirectory, $"'{step.Name}' workingDirectory", declared);
        }
    }

    /// <summary>
    /// The tracked files a guarded run watches, and why they could not be listed when they could
    /// not.
    /// </summary>
    /// <param name="request">The run, carrying the tree.</param>
    /// <param name="cancellationToken">Stops the listing.</param>
    private async Task<(IReadOnlyList<string>? Inputs, string? Unmeasurable)> TrackedAsync(
        RunnerRunRequest request,
        CancellationToken cancellationToken)
    {
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
            // Unmeasured rather than clean: a step asked for its inputs to be held still, and
            // nothing here can say whether they were.
            return ([], $"the files git tracks in '{request.TreeRoot}' could not be listed: {ex.Message}");
        }
    }

    private async Task<PhaseResult> RunPhaseAsync(
        HarnessConfig config,
        RunnerRunRequest request,
        RunnerPhase phase,
        ActionValues values,
        CancellationToken cancellationToken)
    {
        // The leg's directories and the action's own values, through the one expander a project's
        // test invocation uses. A step that builds out of source has no other way to name where its
        // build went: the directory is derived per leg and no tracked file can spell it.
        var paths = new LegPaths(request.TreeRoot, request.BuildDirectory ?? request.TreeRoot);
        var supplied = values.Supplied;

        var arguments = phase.Command
            .Skip(1)
            .Select(argument => LegPathNames.Expand(argument, paths, $"'{phase.Name}' run line", supplied))
            .ToList();

        var working = Path.GetFullPath(Path.Combine(
            request.WorkingDirectory ?? request.TreeRoot,
            phase.WorkingDirectory is { Length: > 0 } declared
                ? LegPathNames.Expand(declared, paths, $"'{phase.Name}' workingDirectory", supplied)
                : "."));

        var result = await _phaseRunner
            .RunAsync(
                new PhaseRequest
                {
                    Leg = request.Leg,
                    Phase = phase.Name,
                    FileName = LegPathNames.Expand(phase.Command[0], paths, $"'{phase.Name}' run line", supplied),
                    Arguments = arguments,
                    LogFile = Path.Combine(
                        request.Layout.RunDirectory(request.RunId),
                        request.Leg,
                        LogNameFor(phase.Name) + ".log"),
                    WorkingDirectory = working,
                    Environment = EnvironmentFor(request, phase, values),
                    SuccessPattern = phase.SuccessPattern,

                    // The step's own bound, else the runner's, else the repository's. A stall bound
                    // rather than a budget: output cadence stays stable even when duration does not.
                    StallSeconds = phase.StallSeconds ?? request.Runner.StallSeconds ?? config.Defaults.StallSeconds,
                    TimingPatterns = request.Time ? config.RunTimingRegex : [],
                    ClockStepToleranceMilliseconds = config.Defaults.ClockStepToleranceMilliseconds,

                    // Masked as each line arrives, not once the phase has finished. The verbose echo
                    // reaches the terminal while the child is still writing, so a secret a step
                    // prints would be in front of the reader long before a finished log could be
                    // rewritten.
                    RedactLine = values.Redact,
                },
                cancellationToken)
            .ConfigureAwait(false);

        return result;
    }

    /// <summary>
    /// The environment a step runs with: the values it reads, the secrets among them, then the
    /// runner's own environment and the step's over the top.
    /// </summary>
    /// <remarks>
    /// <see cref="ActionValues.RevealSecrets"/> is called here and in no other place. This is the
    /// one caller that needs the real values, because a child handed a masked credential fails
    /// later and somewhere else, where the failure says nothing about what was wrong.
    /// </remarks>
    private static IReadOnlyDictionary<string, string?> EnvironmentFor(
        RunnerRunRequest request,
        RunnerPhase phase,
        ActionValues values)
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, value) in values.Values)
        {
            environment[name] = value;
        }

        foreach (var (name, value) in values.RevealSecrets())
        {
            environment[name] = value;
        }

        foreach (var (name, value) in request.Runner.Env)
        {
            environment[name] = value;
        }

        foreach (var (name, value) in phase.Env)
        {
            environment[name] = value;
        }

        return environment;
    }

    /// <summary>Records what one step did, and whether the run goes on.</summary>
    private void Record(RunState state, RunnerPhase phase, PhaseResult result, ActionValues values)
    {
        var finishedAt = DateTimeOffset.UtcNow;
        var startedAt = finishedAt - result.Duration;
        var verdict = result.Verdict();

        state.Phases.Add(result);
        state.CommandTime += result.Duration;
        state.FinishedAt.Add(finishedAt);

        // The edges of this step's own window, and the step's lines between them. A run check counts
        // steps inside this window and nowhere else: a once-per-run sample says nothing about what
        // the machine was doing while the unit that failed was running, and was measured excusing
        // and charging the same failure on the same day.
        state.Lines.Add(new RunOutputLine(startedAt, phase.Name, RunOutputKind.Start, $"{phase.Name} started"));

        foreach (var line in result.Output.Split('\n'))
        {
            // The runner does not stamp a child's lines one by one, so they carry the step's own end
            // time. Only the two edges decide the window; these are here so a reader of the window
            // sees what the step said inside it.
            state.Lines.Add(new RunOutputLine(finishedAt, phase.Name, RunOutputKind.Output, values.Redact(line.TrimEnd('\r'))));
        }

        state.Lines.Add(new RunOutputLine(
            finishedAt,
            phase.Name,
            RunOutputKind.Verdict,
            $"{phase.Name} {Verdicts.Display(verdict.Verdict)}"));

        state.Steps.Add(new RunStep(finishedAt, result.Duration, phase.Name));

        if (result.Passed)
        {
            state.Outcomes.Add(RunOutcome.Ok($"'{phase.Name}' passed"));
            return;
        }

        var detail = values.Redact(verdict.Detail);
        state.Outcomes.Add(RunOutcome.Failed(Verdicts.ExitCodeFor(verdict.Verdict), detail));

        if (phase.ContinueOnError)
        {
            // Recorded and passed over, which is what the step asked for. Reported all the same: a
            // failure nobody is told about is a failure nobody fixes.
            _output.Warn(CommandName, $"{state.Leg}: {detail}; the runner declared continueOnError, so the run goes on.");
            state.PassedOver.Add(detail);
            return;
        }

        state.Failure = new FailedStep(phase.Name, verdict, values.Redact(result.Output));
        state.Stopped = true;
    }

    /// <summary>
    /// What this run's passing steps printed, in the order they ran, with every secret masked.
    /// </summary>
    /// <remarks>
    /// Carried on the outcome so a run check can be satisfied by a marker a step emits, which is
    /// what an author asking for one plainly means. Redacted here rather than where it is compared:
    /// the outcome reaches a ledger and a report, and a value that escaped once has escaped.
    /// <para>
    /// Only what passed. A step that failed and was passed over under <c>continueOnError</c> still
    /// printed, and a run check confirming an excused failure would otherwise be satisfiable by
    /// something a dying step said on its way out — which is the opposite of the evidence an
    /// excusal is supposed to rest on.
    /// </para>
    /// </remarks>
    /// <param name="state">The run so far.</param>
    /// <param name="values">Supplies the mask.</param>
    private static string StepOutput(RunState state, ActionValues values)
        => values.Redact(string.Join("\n", state.Phases.Where(phase => phase.Passed).Select(phase => phase.Output)));

    /// <summary>
    /// The verdict and the outcome the leg reached, an expected exception applied only where its
    /// checks confirmed it.
    /// </summary>

    private async Task<Decision> DecideAsync(
        RunnerRunRequest request,
        RunState state,
        ActionValues values,
        CancellationToken cancellationToken)
    {
        if (state.Failure is not { } failed)
        {
            var detail = state.PassedOver.Count == 0
                ? string.Empty
                : $"{state.PassedOver.Count} step(s) failed and were passed over: {string.Join("; ", state.PassedOver)}";

            return new Decision(
                ReachedVerdict.Of(LegVerdict.Passed, detail),
                RunOutcome.Ok(
                    detail.Length > 0 ? detail : $"{state.Phases.Count} step(s) passed",
                    StepOutput(state, values)),
                null,
                null);
        }

        var genuine = new Decision(
            failed.Verdict with { Detail = values.Redact(failed.Verdict.Detail) },
            RunOutcome.Failed(Verdicts.ExitCodeFor(failed.Verdict.Verdict), values.Redact(failed.Verdict.Detail)),
            null,
            null);

        var scope = RunnerScope.From(request.RunnerName, request.Runner, request.ResolvedLegs);
        var entry = _expectedExceptionMatcher.Find(
            scope,
            request.Leg,
            new RunFailure(FailureTypeIn(failed.Output), values.Redact(failed.Verdict.Detail), failed.Output));

        if (entry is null)
        {
            return genuine;
        }

        // The failing step's own execution window, never a once-per-run sample, and the steps
        // recorded inside it. A window that could not be established excuses nothing: an unfinished
        // window is the absence of evidence, and it is honoured as ABSENT.
        var window = FailureWindow.Extract(state.Lines, failed.Phase);

        var gate = await _runCheckGate
            .ConfirmAsync(
                scope,
                entry,
                window,
                state.Steps,
                request.InvokeRunner ?? Unwired,
                cancellationToken)
            .ConfigureAwait(false);

        if (!gate.Confirmed)
        {
            // The entry and the gate are both reported although neither changed the verdict. An
            // unconfirmed excusal that left no trace is indistinguishable from no entry at all, and
            // the author who wrote it would never learn that what it claims stopped being true.
            return genuine with { Entry = entry, Gate = gate };
        }

        var declared = RunOutcome.From(entry);

        return new Decision(
            ReachedVerdict.Of(
                declared.Success ? LegVerdict.Passed : LegVerdict.Failed,
                values.Redact(declared.Message)),
            declared with { Message = values.Redact(declared.Message) },
            entry,
            gate);
    }

    /// <summary>
    /// The decision with what earlier segments already measured folded in.
    /// </summary>
    /// <remarks>
    /// A resumed run reports the union, not the invocation. The steps this attempt skipped are
    /// steps an earlier attempt carried to an outcome, and a resumed run that reported only its own
    /// green steps would report the whole suite green on the strength of the third of it that
    /// happened to run last. Only skipped steps are folded in: a step this attempt ran has a verdict
    /// of its own here, entry and gate included, and that verdict is the later measurement.
    /// </remarks>
    private static Decision Carried(
        Decision decided,
        RunState state,
        IReadOnlyDictionary<string, RunOutcome> union)
    {
        if (decided.Verdict.Verdict != LegVerdict.Passed || state.Skipped.Count == 0)
        {
            return decided;
        }

        var failed = state.Skipped
            .Where(unit => union.TryGetValue(unit, out var outcome) && !outcome.Success)
            .ToList();

        if (failed.Count == 0)
        {
            return decided;
        }

        var detail = $"{failed.Count} step(s) failed in an earlier segment of this run: {string.Join(", ", failed)}";

        return decided with
        {
            Verdict = ReachedVerdict.Of(LegVerdict.Failed, detail),
            Outcome = RunOutcome.Failed(HarnessExit.CommandFailed, detail),
        };
    }

    /// <summary>
    /// What a check does when nothing wired the re-entry: it cannot be run, so it cannot confirm,
    /// so the failure it would have excused stays genuine.
    /// </summary>
    private static Task<RunOutcome> Unwired(string runner, CancellationToken cancellationToken)
        => throw new HarnessException(
            HarnessExit.InternalError,
            $"nothing here can invoke predefined runner '{runner}', so this check confirms nothing");

    /// <summary>
    /// Refuses every step that cannot be run, before the first one starts.
    /// </summary>
    /// <remarks>
    /// Checked over the whole runner rather than step by step, for the reason the tool policy is: a
    /// run that reaches its fourth step and then refuses has already changed the tree, and what it
    /// did has to be understood before it can be repeated.
    /// </remarks>
    /// <exception cref="HarnessException">
    /// A step names no program, or puts a secret in its argument list. The argument list reaches
    /// the log header, the machine's process table and every error that quotes the command; a
    /// secret leaks by being convenient, and the leak that costs a credential is a failed command
    /// echoing what it was asked to run.
    /// </exception>
    private static void Refuse(RunnerRunRequest request, IReadOnlyList<RunnerPhase> phases, ActionValues values)
    {
        foreach (var phase in phases)
        {
            if (phase.Command.Count == 0)
            {
                throw new HarnessException(
                    HarnessExit.ConfigInvalid,
                    $"Step '{phase.Name}' of runner '{request.RunnerName}' names no program.");
            }

            if (Carries(values, phase.Command))
            {
                // Deliberately without quoting the argument: a refusal that named the value would
                // be the leak it exists to prevent.
                throw new HarnessException(
                    HarnessExit.Refused,
                    $"Step '{phase.Name}' of runner '{request.RunnerName}' puts a secret in its "
                    + "argument list, where the log header, the process table and any error quoting "
                    + "the command would all carry it. Hand it over through the environment instead.");
            }
        }
    }

    /// <summary>Whether any element of <paramref name="command"/> carries a secret value.</summary>
    private static bool Carries(ActionValues values, IReadOnlyList<string> command)
        => values.SecretNames.Count > 0
            && command.Any(argument => !string.Equals(values.Redact(argument), argument, StringComparison.Ordinal));

    /// <summary>
    /// Wipes the directories a runner declares as its own, before any step runs.
    /// </summary>
    /// <remarks>
    /// Scratch and run directories are wiped before every leg; build directories are deliberately
    /// not among them, because they stay incremental. A run directory that carries over holds the
    /// previous run's output, and a runner that produces nothing this time then reports the last
    /// run's results as its own. A directory resolving outside the leg's tree is skipped rather than
    /// deleted: the one recursive delete here is bounded by the tree it was given.
    /// </remarks>
    private void Clean(RunnerRunRequest request)
    {
        foreach (var declared in request.Runner.CleanDirectories)
        {
            var path = Path.GetFullPath(Path.Combine(request.TreeRoot, declared));

            // This machine's own comparison, and only it. A path inside under Ordinal is inside
            // under OrdinalIgnoreCase too, so testing both reduces to the looser of the two — and
            // this guard stands in front of a recursive directory deletion.
            if (!PathContainment.IsStrictlyInside(request.TreeRoot, path, _platform.PathComparison))
            {
                _output.Warn(
                    CommandName,
                    $"{request.Leg}: cleanDirectories names '{declared}', which resolves outside "
                    + $"'{request.TreeRoot}'; it was not deleted.");

                continue;
            }

            if (!_fileSystem.DirectoryExists(path))
            {
                continue;
            }

            _fileSystem.DeleteDirectory(path);
            _output.Detail(CommandName, $"{request.Leg}: cleaned '{declared}'");
        }
    }

    /// <summary>
    /// Adds the action file's inputs to a step's environment, without replacing anything the step
    /// declares for itself.
    /// </summary>
    /// <remarks>
    /// The step wins, because a step that names a variable is saying what that variable is for this
    /// step, and a file-wide input silently overriding it would make the file's own text wrong.
    /// </remarks>
    private static RunnerPhase WithInputs(RunnerPhase phase, IReadOnlyDictionary<string, string> inputs)
    {
        if (inputs.Count == 0)
        {
            return phase;
        }

        var environment = new Dictionary<string, string>(inputs, StringComparer.OrdinalIgnoreCase);

        foreach (var (name, value) in phase.Env)
        {
            environment[name] = value;
        }

        return new RunnerPhase
        {
            Name = phase.Name,
            Command = phase.Command,
            WorkingDirectory = phase.WorkingDirectory,
            Env = environment,
            SuccessPattern = phase.SuccessPattern,
            StallSeconds = phase.StallSeconds,
            ContinueOnError = phase.ContinueOnError,
        };
    }

    /// <summary>The steps a runner declares, and the predefined actions performed before they ran.</summary>
    private sealed record RunnerSteps(IReadOnlyList<RunnerPhase> Phases, IReadOnlyList<string> PerformedActions);

    /// <summary>The step whose failure ended the run, with what it reported.</summary>
    private sealed record FailedStep(string Phase, ReachedVerdict Verdict, string Output);

    /// <summary>The verdict and outcome one attempt reached, with the entry and the gate behind it.</summary>
    private sealed record Decision(
        ReachedVerdict Verdict,
        RunOutcome Outcome,
        ExpectedException? Entry,
        RunCheckGateResult? Gate);

    /// <summary>Everything one attempt accumulates while its steps run.</summary>
    private sealed class RunState
    {
        public string Leg { get; init; } = string.Empty;

        public List<PhaseResult> Phases { get; } = [];

        public List<RunOutcome> Outcomes { get; } = [];

        public List<DateTimeOffset> FinishedAt { get; } = [];

        public List<RunOutputLine> Lines { get; } = [];

        public List<RunStep> Steps { get; } = [];

        public List<string> PassedOver { get; } = [];

        public List<string> Skipped { get; } = [];

        public TimeSpan CommandTime { get; set; }

        public FailedStep? Failure { get; set; }

        public bool Stopped { get; set; }
    }
}
