using System.Buffers;
using System.Diagnostics;
using System.Text.RegularExpressions;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Output;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Runners;

/// <summary>One predefined runner on one leg, with everything the caller has already decided.</summary>
public sealed record RunnerRunRequest
{
    /// <inheritdoc cref="Hosts.HostReport.ProgramDirectories"/>
    public IReadOnlyList<string> ProgramDirectories { get; init; } = [];

    /// <summary>
    /// What the host the leg runs on declares under <c>env</c>, beneath the runner's values, its
    /// secrets, its own environment and each step's.
    /// </summary>
    public IReadOnlyDictionary<string, string> HostEnvironment { get; init; } = new Dictionary<string, string>();

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

    /// <summary>
    /// Who this leg is, for a run line that names <c>{leg}</c>, <c>{os}</c> and the rest, or
    /// <see langword="null"/> where this run reaches no leg.
    /// </summary>
    public Execution.LegIdentity? Identity { get; init; }

    /// <summary>The one file this leg's build is declared to produce, when there is exactly one.</summary>
    public string? Product { get; init; }

    /// <summary>Why there is no product, for a refusal that can say which case it is.</summary>
    public string? ProductProblem { get; init; }

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

        // Read before the steps, because an action's declared inputs resolve from these and the
        // resolved values have to be the same ones a run line names and the environment carries. Two
        // resolutions of one declaration is how they come to disagree, which is what happened when
        // the refusal knew a name the expansion could not fill in.
        var values = await _valuesReader
            .ReadAsync(request.Layout.RunnerEnvDirectory, request.Layout.RunnerSecretsDirectory, cancellationToken)
            .ConfigureAwait(false);

        var steps = await StepsAsync(config, request, values, cancellationToken).ConfigureAwait(false);

        var scratch = ScratchFor(request, steps.ActionDirectory);

        if (steps.ActionDirectory is { Length: > 0 } action)
        {
            await RefuseUnignoredScratchAsync(request, action, cancellationToken).ConfigureAwait(false);
        }

        Refuse(request, steps, values, scratch);

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
                    TreeRoot = request.TreeRoot,
                    Inputs = unmeasurable is not null
                        ? LegInputs.Unmeasured(unmeasurable)
                        : LegInputs.Watch(guarded ?? []),
                    Contention = watching
                        ? Build.ContentionRequests.For(
                            config,
                            request.Leg,
                            request.BuildDirectory!,
                            request.TreeRoot,
                            _platform.PlatformKey)
                        : null,
                },
                cancellationToken)
            .ConfigureAwait(false);

        try
        {
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

                var result = await RunPhaseAsync(config, request, phase, values, steps.Inputs, scratch, cancellationToken)
                    .ConfigureAwait(false);

                result = WithDeclaredOutputs(result, phase, scratch);

            // Kept now, not at the end of the run. A later step — here, or on another host after an
            // artifact sync — reads what an earlier one produced, and what it reads has to be there
            // before the run that produced it is over. A step that did not pass persists nothing:
            // carrying evidence from work that failed is the misattribution this tool exists to
            // refuse.
            if (result.Passed)
            {
                PersistOutputs(phase, scratch);
            }
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
        }
        finally
        {
            // Whatever the verdict, and whatever stopped the run. What it asked to keep is
            // kept and its working space goes: a failed run's intermediate files are the least
            // useful thing on the machine, and a tree that grew one directory per run on every
            // host is a tree nobody prunes. In a finally because cancellation is the case that
            // would otherwise leave one behind on every host at once.
            RemoveWorkingSpace(scratch);
        }

        _output.Info(
            CommandName,
            $"{request.Leg}: finished {state.Outcomes.Count} of {steps.Phases.Count} step(s) in "
            + $"{LedgerReport.FormatDuration(elapsed.Elapsed)}");

        await _runSegments
            .EndAsync(request.Layout, request.RunId, request.Leg, request.SegmentId, DateTimeOffset.UtcNow, cancellationToken)
            .ConfigureAwait(false);

        var seen = await guards.CloseAsync(cancellationToken).ConfigureAwait(false);

        // Said as build and test say it. A runner's steps are sampled with the same rules, and what
        // that found beside them was dropped without a word.
        if (seen.Contention is { } contention)
        {
            ContentionWarnings.Write(_output, CommandName, request.Leg, contention, config.Contention);
        }
        var decided = await DecideAsync(request, state, values, cancellationToken).ConfigureAwait(false);

        // What the guards saw is folded in the same way every other verb folds it, so a step whose
        // tree moved reports the verdict a test leg would and not a sentence of its own.
        //
        // Into the outcome as well as the verdict, because the outcome is what a run check reads:
        // RunCheckGate tests Success and ResultCode and nothing else, so a check run whose own tree
        // moved would otherwise confirm an expected failure on the evidence of a run that measured
        // nothing. Carried() a few lines down already updates both; this is the same rule.
        var folded = seen.Decide(request.Leg, [decided.Verdict]);

        decided = folded.Verdict == decided.Verdict.Verdict
            ? decided with { Verdict = folded }
            : decided with
            {
                Verdict = folded,
                Outcome = RunOutcome.Failed(Verdicts.ExitCodeFor(folded.Verdict), folded.Detail),
            };
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
        ActionValues values,
        CancellationToken cancellationToken)
    {
        var runner = request.Runner;
        IReadOnlyList<RunnerPhase> phases;
        IReadOnlyList<string> performed = [];
        IReadOnlyDictionary<string, string> inputs = new Dictionary<string, string>(StringComparer.Ordinal);
        string? actionDirectory = null;

        if (runner.Action is { Length: > 0 } action)
        {
            var file = await _actionFileParser
                .LoadAsync(request.Layout.RunnerActionsDirectory, action, cancellationToken)
                .ConfigureAwait(false);

            inputs = ResolveInputs(file, values);

            var supplied = Supplied(values, inputs);
            var scratch = ScratchFor(request, file.DirectoryName);

            // Before anything starts, and over the whole file rather than step by step: a file whose
            // last step names an undeclared program is refused with its first step not yet run. Each
            // line is judged by what it will start and where from, worked out exactly as the run
            // works them out: read as written, a directory an input names could put a program
            // outside the repository while the text still read as inside it.
            _toolPolicy.Enforce(
                file,
                config,
                request.TreeRoot,
                (step, command) => Started(
                    request,
                    command.Program,
                    step.WorkingPath(file.DirectoryName),
                    PathsFor(request, scratch, step.Name),
                    supplied,
                    step.Name),
                values.Redact);

            // The names the expansion will actually have, not a subset of them. Handed the declared
            // inputs alone this refused a run line naming a runner value directory's own value — the
            // same check-against-a-different-set defect as before, inverted.
            RefuseUnknownNames(file, supplied.Keys);

            // Performed before the first program starts: one settles what the steps read, the other
            // settles which tree they read it from, and a run that discovered either halfway through
            // would already have written into the wrong one.
            var actions = await _predefinedActions
                .PerformAsync(file, request.TreeRoot, inputs, cancellationToken)
                .ConfigureAwait(false);

            Clean(request);

            performed = actions.Performed;
            actionDirectory = file.DirectoryName;
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

        return new RunnerSteps(phases, performed, inputs, actionDirectory);
    }

    /// <summary>
    /// Refuses a step naming something nothing can fill in, over the whole file and before its
    /// first program starts.
    /// </summary>
    /// <param name="file">The action as it was read.</param>
    /// <param name="fillable">
    /// The names this run can supply beyond the built-in vocabulary: the action's declared inputs,
    /// already resolved, so the check refuses exactly what the expansion would not fill in.
    /// </param>
    /// <exception cref="HarnessException">A step names a placeholder nothing supplies.</exception>
    /// <remarks>
    /// Over the whole file and before the first program starts, so a file whose last step names
    /// something nothing can fill in is refused with its first step not yet run.
    /// <para>
    /// A name nothing supplies is refused rather than passed through. Passed through it reaches the
    /// program as the literal text it was written as and the step exits zero having done something
    /// nobody asked for: a consumer measured a run line naming <c>{greeting}</c> arriving as those
    /// nine characters, with the leg reporting passed. <c>${HOME}</c> is another expander's syntax
    /// and is never touched, and a brace meant literally is written <c>{{</c>.
    /// </para>
    /// </remarks>
    private static void RefuseUnknownNames(ActionFile file, IEnumerable<string> fillable)
    {
        var declared = fillable.ToList();

        foreach (var step in file.Steps)
        {
            foreach (var argument in step.Commands.SelectMany(command => command.Arguments))
            {
                LegPathNames.RefuseUnknown(argument, $"'{step.Name}' run line", extra: declared);
            }

            LegPathNames.RefuseUnknown(
                step.WorkingDirectory,
                $"'{step.Name}' workingDirectory",
                extra: declared);
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

    /// <summary>Runs one step, with the runner's bounds and the values it reads.</summary>
    private async Task<PhaseResult> RunPhaseAsync(
        HarnessConfig config,
        RunnerRunRequest request,
        RunnerPhase phase,
        ActionValues values,
        IReadOnlyDictionary<string, string> inputs,
        ActionScratch? scratch,
        CancellationToken cancellationToken)
    {
        var paths = PathsFor(request, scratch, phase.StepName);

        // Created before the step runs, not lazily by whatever the step happens to do. A program
        // told to write into a directory that is not there fails in its own words, which is a worse
        // message than the one this would have given.
        if (paths.StepBuild is { } stepBuild)
        {
            _fileSystem.CreateDirectory(stepBuild);
        }

        var supplied = Supplied(values, inputs);

        var arguments = phase.Command
            .Skip(1)
            .Select(argument => LegPathNames.Expand(
                argument,
                paths,
                $"'{phase.Name}' run line",
                PlaceholderPolicy.LeaveAsWritten,
                supplied))
            .ToList();

        var (program, working) = Started(request, phase.Command[0], phase.WorkingDirectory, paths, supplied, phase.Name);

        var result = await _phaseRunner
            .RunAsync(
                new PhaseRequest
                {
                    Leg = request.Leg,
                    Phase = phase.Name,
                    FileName = program,
                    Arguments = arguments,
                    LogFile = Path.Combine(
                        request.Layout.RunDirectory(request.RunId),
                        request.Leg,
                        LogNameFor(phase.Name) + ".log"),
                    WorkingDirectory = working,
                    Environment = EnvironmentFor(request, phase, values),
                    AppendToPath = request.ProgramDirectories,
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
    /// The environment a step runs with: the host's own, the values it reads and the secrets among
    /// them over that, then the runner's own environment and the step's over the top.
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
        => PhaseEnvironment.Layered(request.HostEnvironment, values.Values, values.RevealSecrets(), request.Runner.Env, phase.Env);

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
    private static void Refuse(RunnerRunRequest request, RunnerSteps steps, ActionValues values, ActionScratch? scratch)
    {
        var supplied = Supplied(values, steps.Inputs);

        foreach (var phase in steps.Phases)
        {
            if (phase.Command.Count == 0)
            {
                throw new HarnessException(
                    HarnessExit.ConfigInvalid,
                    $"Step '{phase.Name}' of runner '{request.RunnerName}' names no program.");
            }

            // Worked out as the start works it out: a program filled in to nothing - an empty value
            // in the runner's .env - starts nothing, and is refused before the first step rather
            // than reaching the start as a program with no name, which read as a defect in this tool.
            if (string.IsNullOrWhiteSpace(Started(request, phase.Command[0], phase.WorkingDirectory, PathsFor(request, scratch, phase.StepName), supplied, phase.Name).Program))
            {
                throw new HarnessException(
                    HarnessExit.ConfigInvalid,
                    $"Step '{phase.Name}' of runner '{request.RunnerName}' starts nothing once its names are filled in.");
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

        // A copy with one field changed. Written out member by member this dropped
        // WatchContention and RequireInputsUnmoved, so an action that declared inputs ran with both
        // guards off while its own text said they were on.
        return phase with { Env = environment };
    }

    /// <summary>The steps a runner declares, and the predefined actions performed before they ran.</summary>
    /// <summary>
    /// The phase's result, with any output it declared and did not produce named on it.
    /// </summary>
    /// <param name="result">What the phase reported.</param>
    /// <param name="phase">The phase, carrying what it said it would produce.</param>
    /// <param name="scratch">The action's directories, or null outside an action.</param>
    /// <remarks>
    /// Checked only where the phase otherwise passed. A step that failed has already said so, and
    /// adding "and it produced nothing" to a program that crashed names a consequence as though it
    /// were a second cause.
    /// </remarks>
    private PhaseResult WithDeclaredOutputs(PhaseResult result, RunnerPhase phase, ActionScratch? scratch)
    {
        if (phase.Outputs.Count == 0 || scratch is null || !result.Passed)
        {
            return result;
        }

        var directory = Path.Combine(scratch.Build, LogNameFor(phase.StepName));

        var missing = phase.Outputs
            .Where(output => !_fileSystem.FileExists(Path.Combine(directory, output))
                && !_fileSystem.DirectoryExists(Path.Combine(directory, output)))
            .ToList();

        return missing.Count == 0 ? result : result with { MissingOutputs = missing };
    }

    /// <summary>
    /// Refuses a run whose action's working space or kept output git would not ignore in this tree.
    /// </summary>
    /// <param name="request">The run.</param>
    /// <param name="action">The action's own directory, relative to the actions directory.</param>
    /// <param name="cancellationToken">Stops the questions.</param>
    /// <exception cref="HarnessException">
    /// Either directory would not be ignored, a refusal naming the rule; or git could not say whether
    /// it would, which leaves this leg unable to run in this tree. Nothing has run.
    /// </exception>
    /// <remarks>
    /// Asked before anything is written, of the paths this very run would write: a file under each,
    /// because git answers a directory rule for a directory that does not exist yet only when asked
    /// about something inside it. The rules were written by init and nothing checked they were still
    /// in effect, so a repository whose .gitignore predates them wrote a run's kept output where git
    /// status shows it and the next 'git add -A' commits it - output committed beside the thing that
    /// produced it is a measurement nobody can reproduce.
    /// <para>
    /// A sync does not carry these either way: each action's build and artifacts are withheld by name,
    /// whatever .gitignore says. What the rule protects is the repository's own history.
    /// </para>
    /// </remarks>
    private async Task RefuseUnignoredScratchAsync(RunnerRunRequest request, string action, CancellationToken cancellationToken)
    {
        var uncovered = new List<string>();

        foreach (var (kind, relative) in new[]
        {
            (HarnessLayout.ActionBuildDirectoryName, HarnessLayout.ActionBuildRelative(action, request.RunId, request.Leg)),
            (HarnessLayout.ActionArtifactsDirectoryName, HarnessLayout.ActionArtifactsRelative(action, request.RunId, request.Leg)),
        })
        {
            var probe = Path.Combine(relative, "probe").Replace('\\', '/');
            bool ignored;

            try
            {
                ignored = await _gitClient.IsIgnoredAsync(request.TreeRoot, probe, cancellationToken).ConfigureAwait(false);
            }
            catch (HarnessException ex) when (ex.ExitCode == HarnessExit.CommandFailed)
            {
                // A question that could not be asked is no verdict on the code, and not an answer
                // either way. It is about this leg's tree - a worktree whose link to its repository
                // is broken, a copy git can no longer read - so this leg cannot run here, in git's own
                // words, and the legs on other trees still report: raised as a refusal of the run, it
                // ended every leg over one tree's repository.
                throw new HarnessException(
                    HarnessExit.HostUnavailable,
                    $"git could not say whether this action's '{kind}/' is ignored in '{request.TreeRoot}', "
                    + $"so whether this run would write where git commits is unknown. Nothing has run. {ex.Message}");
            }

            if (!ignored)
            {
                uncovered.Add(kind);
            }
        }

        if (uncovered.Count == 0)
        {
            return;
        }

        var directories = string.Join(" and ", uncovered.Select(kind => $"'{kind}/'"));
        var rules = string.Join(", ", uncovered.Select(kind => $"'{HarnessLayout.ActionScratchIgnoreRule(kind)}'"));

        throw new HarnessException(
            HarnessExit.Refused,
            $"git does not ignore this action's {directories} in '{request.TreeRoot}', so what this run "
            + "writes there would show in git status and be committed by the next 'git add -A'. "
            + $"'{Hosts.ToolPackage.Command} init' writes the rule{(uncovered.Count == 1 ? string.Empty : "s")} "
            + $"into the managed block of .gitignore, or add {rules} by hand. Nothing has run.");
    }

    /// <summary>
    /// Copies what a step asked to keep into the action's artifacts, as soon as it has passed.
    /// </summary>
    /// <param name="phase">The step that finished, carrying what it declared and whether it persists.</param>
    /// <param name="scratch">The action's directories, or null outside an action.</param>
    /// <remarks>
    /// Per step rather than per run, because a later step reads what an earlier one produced. On
    /// another host that reading happens after an artifact sync, and a sync cannot carry what the
    /// run has not written yet.
    /// </remarks>
    private void PersistOutputs(RunnerPhase phase, ActionScratch? scratch)
    {
        if (scratch is null || !phase.Persist || phase.Outputs.Count == 0)
        {
            return;
        }

        {
            var from = Path.Combine(scratch.Build, LogNameFor(phase.StepName));
            var into = Path.Combine(scratch.Artifacts, LogNameFor(phase.StepName));

            foreach (var output in phase.Outputs)
            {
                var source = Path.Combine(from, output);
                var destination = Path.Combine(into, output);

                try
                {
                    if (_fileSystem.DirectoryExists(source))
                    {
                        // A directory is as ordinary an output as a file: a step that emits a run of
                        // samples emits a directory of them. Skipped here — which is what this did —
                        // it passed the witness, was never copied, and went with the build directory,
                        // leaving a passed run and no measurements.
                        var unfollowed = KeepDirectory(source, destination);

                        if (unfollowed.Count > 0)
                        {
                            // A kept copy missing what its links led to is an incomplete copy, and one
                            // a later sync carries to every host as though it were the whole output.
                            _output.Warn(
                                CommandName,
                                $"'{phase.StepName}' asked to keep '{output}', which holds {unfollowed.Count} "
                                + "directory link(s) that were not followed, so what they lead to is not in "
                                + $"the kept copy: {string.Join(", ", unfollowed.Select(link => $"'{link}'"))}");
                        }

                        continue;
                    }

                    if (!_fileSystem.FileExists(source))
                    {
                        continue;
                    }

                    _fileSystem.CreateDirectory(Path.GetDirectoryName(destination) ?? into);
                    _fileSystem.CopyFile(source, destination, overwrite: true);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Said rather than swallowed. What a later run will not find is worth a line
                    // now, while somebody can still see which step produced it.
                    _output.Warn(
                        CommandName,
                        $"'{phase.StepName}' asked to keep '{output}', which could not be copied to "
                        + $"'{destination}': {exception.Message}");
                }
            }
        }
    }

    /// <summary>Removes this run's working space, whatever the verdict was.</summary>
    /// <param name="scratch">The action's directories, or null outside an action.</param>
    /// <remarks>
    /// What the run asked to keep has already been copied out, step by step, as each one passed. So
    /// this only removes: a failed run's intermediate files are the least useful thing on the
    /// machine, and a tree that grew one directory per run on every host is a tree nobody prunes.
    /// </remarks>
    private void RemoveWorkingSpace(ActionScratch? scratch)
    {
        if (scratch is null)
        {
            return;
        }

        try
        {
            if (_fileSystem.DirectoryExists(scratch.Build))
            {
                _fileSystem.DeleteDirectory(scratch.Build);
            }

            // And the run's own directory once its last leg has gone. Left behind it is a husk:
            // empty, gitignored, and one per run for ever on every host that ran the action.
            if (Path.GetDirectoryName(scratch.Build) is { Length: > 0 } run
                && _fileSystem.DirectoryExists(run)
                && !_fileSystem.EnumerateDirectories(run).Any()
                && !_fileSystem.EnumerateFiles(run, recursive: false).Any())
            {
                _fileSystem.DeleteDirectory(run);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _output.Warn(
                CommandName,
                $"this run's working directory '{scratch.Build}' could not be removed: {exception.Message}");
        }
    }

    /// <summary>Copies a directory output, with everything under it.</summary>
    /// <param name="source">The directory the step produced.</param>
    /// <param name="destination">Where it is kept.</param>
    /// <returns>
    /// The directory links under it, relative to it, which were not followed: a link can lead out of
    /// the output, or back into it and round again, so what it leads to is not copied - and is named
    /// rather than left out without a word.
    /// </returns>
    private IReadOnlyList<string> KeepDirectory(string source, string destination)
    {
        _fileSystem.CreateDirectory(destination);

        foreach (var file in _fileSystem.EnumerateFiles(source, recursive: true))
        {
            var relative = Path.GetRelativePath(source, file);
            var into = Path.Combine(destination, relative);

            _fileSystem.CreateDirectory(Path.GetDirectoryName(into) ?? destination);
            _fileSystem.CopyFile(file, into, overwrite: true);
        }

        return [.. _fileSystem.EnumerateDirectoryLinks(source).Select(link => Path.GetRelativePath(source, link).Replace('\\', '/'))];
    }

    /// <summary>
    /// The two run-keyed directories an action owns: where its steps write, and what survives.
    /// </summary>
    /// <param name="Build">Where this run's steps write. Emptied when the action finishes.</param>
    /// <param name="Artifacts">Where this run's persisted outputs are kept.</param>
    /// <remarks>
    /// Both are gitignored and neither is written into directly: everything goes under a directory
    /// named for the run, and below that for the step that produced it. An action that wrote into
    /// the roots would have two runs of itself sharing one directory, and the second would measure
    /// what the first left behind.
    /// </remarks>
    private sealed record ActionScratch(string Build, string Artifacts);

    private sealed record RunnerSteps(
        IReadOnlyList<RunnerPhase> Phases,
        IReadOnlyList<string> PerformedActions,
        IReadOnlyDictionary<string, string> Inputs,
        string? ActionDirectory);

    /// <summary>
    /// The action's working space for this run, or <see langword="null"/> for a runner that declares
    /// phases of its own. Keyed by the run, under the action's own directory: two runs of one action
    /// on one machine - two legs, or a retry - would otherwise write over each other's files.
    /// </summary>
    private static ActionScratch? ScratchFor(RunnerRunRequest request, string? actionDirectory)
        => actionDirectory is { Length: > 0 } owned
            ? new ActionScratch(
                Path.Combine(request.TreeRoot, HarnessLayout.ActionBuildRelative(owned, request.RunId, request.Leg)),
                Path.Combine(request.TreeRoot, HarnessLayout.ActionArtifactsRelative(owned, request.RunId, request.Leg)))
            : null;

    /// <summary>
    /// The directories a step's placeholders name: the leg's own, and the action's working space with
    /// the step's own directory in it, through the one expander a project's test invocation uses. A
    /// step that builds out of source has no other way to name where its build went: the directory is
    /// derived per leg and no tracked file can spell it.
    /// </summary>
    /// <param name="request">The leg's run.</param>
    /// <param name="scratch">The action's working space, when the runner uses an action.</param>
    /// <param name="stepName">The step's own name, which names its directory in that space.</param>
    private static LegPaths PathsFor(RunnerRunRequest request, ActionScratch? scratch, string? stepName) => new(request.TreeRoot, request.BuildDirectory)
    {
        Identity = request.Identity,
        Product = request.Product,
        ProductProblem = request.ProductProblem,
        ActionBuild = scratch?.Build,
        ActionArtifacts = scratch?.Artifacts,
        RunArtifacts = scratch is null ? null : Path.GetDirectoryName(scratch.Artifacts),
        StepBuild = scratch is not null && stepName is { Length: > 0 } ? Path.Combine(scratch.Build, LogNameFor(stepName)) : null,
    };

    /// <summary>
    /// The values a step's placeholders are filled from: the runner's own, and the action's declared
    /// inputs over them. One map, because the check that refuses an unfillable name is given this
    /// same set: fed from two places they disagree, and a name the check accepted reached the program
    /// as literal text.
    /// </summary>
    private static IReadOnlyDictionary<string, string> Supplied(ActionValues values, IReadOnlyDictionary<string, string> inputs)
        => inputs.Count == 0 ? values.Supplied : Namable(values.Supplied, inputs);

    /// <summary>
    /// What a line starts, and the directory it starts in, with their placeholders filled in: the
    /// one reading both the start and the policy that allowed it use, so the program a policy judged
    /// is the program that starts, from where it starts.
    /// </summary>
    /// <remarks>
    /// A program named by a relative path is made whole against the directory its step starts in -
    /// './probe.py' in a step that runs in its action's directory is the script beside the action
    /// file. Left to the start, it would be read against wherever this process began, which for a
    /// leg on a worktree is the main checkout.
    /// </remarks>
    /// <param name="request">The leg's run.</param>
    /// <param name="program">The line's program, as written.</param>
    /// <param name="workingDirectory">
    /// The directory its step runs in, as written, relative to the directory the leg's run works in -
    /// its tree root; that directory itself when absent.
    /// </param>
    /// <param name="paths">The directories the placeholders name.</param>
    /// <param name="supplied">The values the placeholders are filled from.</param>
    /// <param name="name">The step, as refusals name it.</param>
    private static (string Program, string WorkingDirectory) Started(
        RunnerRunRequest request,
        string program,
        string? workingDirectory,
        LegPaths paths,
        IReadOnlyDictionary<string, string> supplied,
        string name)
    {
        var directory = workingDirectory is { Length: > 0 } declared
            ? LegPathNames.Expand(declared, paths, $"'{name}' workingDirectory", PlaceholderPolicy.LeaveAsWritten, supplied)
            : ".";

        var filled = LegPathNames.Expand(program, paths, $"'{name}' run line", PlaceholderPolicy.LeaveAsWritten, supplied);

        // Refused naming the step, before anything runs, rather than reaching a path function that
        // cannot read them, which read as a defect in this tool: a directory filled in to nothing -
        // an empty value in the runner's .env - names none, and a NUL is a character no path or
        // program name can hold.
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new HarnessException(
                HarnessExit.ConfigInvalid,
                $"Step '{name}' of runner '{request.RunnerName}' runs in no directory once its names are filled in.");
        }

        if (directory.Contains('\0', StringComparison.Ordinal) || filled.Contains('\0', StringComparison.Ordinal))
        {
            throw new HarnessException(
                HarnessExit.ConfigInvalid,
                $"Step '{name}' of runner '{request.RunnerName}' names a program or a directory holding a NUL character, which no path can hold.");
        }

        var working = Path.GetFullPath(directory, request.WorkingDirectory ?? request.TreeRoot);

        // A line filled in to nothing starts nothing, and is refused before anything runs: by the
        // policy for an action's lines, naming the line, and by Refuse for a runner's own phases.
        return (string.IsNullOrWhiteSpace(filled) ? filled : ProcessRunner.Anchored(filled, working), working);
    }

    /// <summary>What a run line may name: the runner's values, with the action's inputs over them.</summary>
    /// <param name="values">What the runner value directories supply.</param>
    /// <param name="inputs">The action's declared inputs, already resolved.</param>
    private static IReadOnlyDictionary<string, string> Namable(
        IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, string> inputs)
    {
        var namable = new Dictionary<string, string>(values, StringComparer.Ordinal);

        foreach (var (name, value) in inputs)
        {
            namable[name] = value;
        }

        return namable;
    }

    /// <summary>
    /// The value of every input the action declares: what the runner value directories supply under
    /// that name, else the input's own default.
    /// </summary>
    /// <param name="file">The action as it was read.</param>
    /// <param name="values">What the runner value directories hold.</param>
    /// <exception cref="HarnessException">A required input has no value anywhere.</exception>
    /// <remarks>
    /// Resolved once, here, and handed to everything that needs it: the run lines that name an input
    /// and the environment a <c>harness/read-inputs</c> step fills. Resolved twice they drift, which
    /// is exactly what shipped — the load-time check knew the declared names while the expansion was
    /// looking somewhere else entirely, so a step naming a declared input passed the check, reached
    /// the program as the literal text '{name}', and the leg reported passed.
    /// <para>
    /// Secrets are deliberately not a source. A value spliced into a command line reaches the
    /// process table, where anything on the machine can read it; a secret reaches a step through the
    /// environment, which is what <c>.secrets</c> is for.
    /// </para>
    /// </remarks>
    private static IReadOnlyDictionary<string, string> ResolveInputs(ActionFile file, ActionValues values)
    {
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        var missing = new List<string>();

        foreach (var input in file.Inputs)
        {
            if (values.Supplied.TryGetValue(input.Name, out var supplied))
            {
                resolved[input.Name] = supplied;
                continue;
            }

            if (input.Default is { } fallback)
            {
                resolved[input.Name] = fallback;
                continue;
            }

            if (input.Required)
            {
                missing.Add(input.Name);
            }
        }

        if (missing.Count > 0)
        {
            throw new HarnessException(
                HarnessExit.ConfigInvalid,
                $"'{file.Path}' requires input(s) {string.Join(", ", missing)} and neither declares a "
                + "default for them nor finds one in the runner value directories, so its steps would "
                + "run with nothing where a value belongs.");
        }

        return resolved;
    }

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
