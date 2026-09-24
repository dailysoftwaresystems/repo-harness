using RepoHarness.Core.Configuration;

namespace RepoHarness.Core.Runners;

/// <summary>
/// One step of an action file: either a predefined action or a <c>run</c> block, plus everything
/// a <see cref="RunnerPhase"/> carries.
/// </summary>
/// <remarks>
/// Every field a phase has is a key on a step. That is not decoration: <c>successPattern</c>,
/// <c>stallSeconds</c> and <c>continueOnError</c> are what decide a leg's verdict, and a step
/// unable to express one of them would make an action file a quieter way to run the same work
/// with no witness, no stall bound and no recorded decision about a failure.
/// </remarks>
public sealed record ActionStep
{
    /// <summary>
    /// The step's name, which names its log file and identifies it in the ledger. Required, because
    /// two unnamed steps would write one log and the second would overwrite the first's evidence.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>The predefined action this step performs, or <see cref="PredefinedAction.None"/>.</summary>
    public PredefinedAction Uses { get; init; }

    /// <summary>
    /// The commit or branch <see cref="PredefinedAction.Checkout"/> puts the tree at. Only that
    /// action reads it.
    /// </summary>
    public string? Reference { get; init; }

    /// <summary>
    /// The program invocations this step's <c>run</c> block declares, one per surviving line.
    /// Empty when <see cref="Uses"/> names a predefined action.
    /// </summary>
    public IReadOnlyList<ActionCommand> Commands { get; init; } = [];

    /// <summary>
    /// Working directory, relative to <see cref="WorkingDirectoryRoot"/>. Absent, the step runs in
    /// that root itself.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// What <see cref="WorkingDirectory"/> is relative to. Defaults to the leg's tree root, which is
    /// where a step with neither key has always run.
    /// </summary>
    public WorkingDirectoryRoot WorkingDirectoryRoot { get; init; }

    /// <summary>
    /// The operating systems this step runs on, from <c>runOn</c>; empty, it runs on every leg. A leg
    /// of any other operating system skips it and says so, and nothing about it is asked of that
    /// leg: not its programs, not the names its lines use.
    /// </summary>
    public IReadOnlyList<string> RunOn { get; init; } = [];

    /// <summary>Whether a leg of <paramref name="os"/> runs this step.</summary>
    /// <param name="os">The leg's operating system.</param>
    /// <remarks>
    /// Compared ignoring case, as a leg's <c>os</c> is everywhere else: a leg may declare
    /// <c>Windows</c>, and read exactly it was refused as running no step, while the host it ran on
    /// ran every one.
    /// </remarks>
    public bool RunsOn(string os) => RunOn.Count == 0 || RunOn.Contains(os, StringComparer.OrdinalIgnoreCase);

    /// <summary>Environment for this step, applied over the runner's own.</summary>
    public IReadOnlyDictionary<string, string> Env { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Pattern proving the step ran, checked in addition to its exit code. Already known to compile.
    /// </summary>
    public string? SuccessPattern { get; init; }

    /// <summary>Seconds without output after which this step is treated as hung.</summary>
    public int? StallSeconds { get; init; }

    /// <summary>
    /// What this step produces, by path relative to its own directory under the action's
    /// <c>build</c>.
    /// </summary>
    /// <remarks>
    /// A witness, and the thing a later step consumes. Declared outputs are checked to exist when
    /// the step finishes, so a program that exits zero having written nothing is reported as
    /// unwitnessed rather than passed — which is the failure class an action replacing a hand-written
    /// script most needs to be able to state about itself.
    /// </remarks>
    public IReadOnlyList<string> Outputs { get; init; } = [];

    /// <summary>
    /// Whether this step's outputs survive the run, moved into the action's <c>artifacts</c> when it
    /// finishes.
    /// </summary>
    /// <remarks>
    /// Off by default, because the build directory is emptied when the action ends and anything a
    /// later run needs has to say so. A run that persisted everything by default would grow a tree
    /// nobody pruned, on every host.
    /// </remarks>
    public bool Persist { get; init; }

    /// <summary>Whether a failure here ends the leg or is recorded and passed over.</summary>
    public bool ContinueOnError { get; init; }

    /// <summary>
    /// Whether the process table is watched while this runs, so another run building in the same
    /// directory is reported rather than silently shared with.
    /// </summary>
    /// <remarks>
    /// Needs a build directory to watch, which is the leg's. A run reaching no leg has none, and
    /// that is refused when the action runs rather than passed over: a guard that watched nothing
    /// would report a clean directory without having looked at one.
    /// </remarks>
    public bool WatchContention { get; init; }

    /// <summary>
    /// Whether the tracked files are fingerprinted before, during and after this, so a tree edited
    /// while it ran is reported rather than producing a result that describes no tree that existed.
    /// </summary>
    public bool RequireInputsUnmoved { get; init; }

    /// <summary>
    /// Whether the step runs only where a run names it, from <c>manual</c>: with <c>run --manual-step</c>,
    /// or in the <c>steps</c> a runner declares. A run that names no step runs every step but these.
    /// </summary>
    /// <remarks>
    /// For work that belongs with an action and is not part of what running it means - a benchmark
    /// beside the build and test it shares its modules with. One action per directory, so that work
    /// could otherwise not live beside the modules it reads at all.
    /// </remarks>
    public bool Manual { get; init; }

    /// <summary>
    /// The steps declared before this one that run first whenever it runs, from <c>needs</c>, as they
    /// were written. A run that names only this step runs these too, in the order they are declared.
    /// </summary>
    public IReadOnlyList<string> Needs { get; init; } = [];

    /// <summary>
    /// The values this step alone reads, from its own <c>inputs</c>: beside the action's, which every
    /// step reads. A run that does not run this step reads none of them, so <c>--input</c> naming one
    /// is refused there rather than accepted and never used.
    /// </summary>
    public IReadOnlyList<ActionInput> Inputs { get; init; } = [];


    /// <summary>
    /// The directory this step runs in, relative to the directory the leg's run works in - its tree
    /// root: its root and its own path joined, or <see langword="null"/> where it declared neither and
    /// runs at the tree root.
    /// </summary>
    /// <param name="actionName">
    /// The action's directory name, which <see cref="Runners.WorkingDirectoryRoot.Action"/> resolves
    /// against.
    /// </param>
    /// <remarks>
    /// Where a program the step names by a relative path is read from, by the run and by the policy
    /// that allows it alike: <c>./probe.py</c> in a step that runs in its action's directory is the
    /// script beside the action file. Joined once, here, so the two cannot come to read it from two
    /// different places.
    /// </remarks>
    public string? WorkingPath(string actionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionName);

        var root = WorkingDirectoryRoots.RelativePath(WorkingDirectoryRoot, actionName);

        // Null where the step declared neither, so a phase that named no directory still names none
        // and the runner's own "tree root" default keeps applying unchanged.
        return (root, WorkingDirectory) switch
        {
            (null, var path) => path,
            (var start, null) => start,
            var (start, path) => Path.Combine(start, path),
        };
    }

    /// <summary>
    /// This step as the phases the harness runs: one per line of its <c>run</c> block, each
    /// carrying the step's working directory, environment, witness, stall bound and failure policy.
    /// A predefined action yields none, because it is not a child process.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A step whose block holds several lines yields several phases, each named
    /// <c>&lt;step&gt; (n/total)</c>, so every line gets its own log file and its own verdict line.
    /// Folding them into one phase would put several programs behind a single exit code, and the
    /// one that failed would no longer be identifiable.
    /// </para>
    /// <para>
    /// A phase carries one working directory, relative to the leg's tree root, so the step's root
    /// and its own path are joined here rather than carried separately. That keeps
    /// <see cref="RunnerPhase"/> as the phases declared in <c>config.json</c> already use it: a
    /// runner's own phases have no root to declare and are unaffected by this.
    /// </para>
    /// </remarks>
    /// <param name="actionName">
    /// The action's directory name, which <see cref="Runners.WorkingDirectoryRoot.Action"/> resolves
    /// against.
    /// </param>
    public IReadOnlyList<RunnerPhase> ToPhases(string actionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionName);

        var total = Commands.Count;
        var working = WorkingPath(actionName);

        return
        [
            .. Commands.Select((command, index) => new RunnerPhase
            {
                Name = total == 1 ? Name : $"{Name} ({index + 1}/{total})",

                // The step's own name, not the phase's. A step of three commands is three phases
                // sharing one directory, because it is one step's work: keyed by the phase name
                // each command would write somewhere the next could not find.
                StepName = Name,

                // Checked on the last command, for the reason the success pattern is: that is the
                // one whose finishing means the step did its work.
                Outputs = index == total - 1 ? Outputs : [],
                Persist = Persist,
                Command = [.. command.Arguments],
                WorkingDirectory = working,
                WatchContention = WatchContention,
                RequireInputsUnmoved = RequireInputsUnmoved,
                Env = new Dictionary<string, string>(Env, StringComparer.OrdinalIgnoreCase),

                // The witness belongs to the step, so it is checked against the step's last command:
                // that is the one whose finishing means the step did its work. Copied to every line
                // instead, a step would have to make each of its commands print the same evidence,
                // which no honest sequence does — the first `git --version` would have to say what
                // the last `ctest` says. The earlier commands are still witnessed by their own exit
                // codes, and by the last one having been reached at all.
                SuccessPattern = index == total - 1 ? SuccessPattern : null,
                StallSeconds = StallSeconds,
                ContinueOnError = ContinueOnError,
            }),
        ];
    }
}
