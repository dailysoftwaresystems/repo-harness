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

    /// <summary>Environment for this step, applied over the runner's own.</summary>
    public IReadOnlyDictionary<string, string> Env { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Pattern proving the step ran, checked in addition to its exit code. Already known to compile.
    /// </summary>
    public string? SuccessPattern { get; init; }

    /// <summary>Seconds without output after which this step is treated as hung.</summary>
    public int? StallSeconds { get; init; }

    /// <summary>Whether a failure here ends the leg or is recorded and passed over.</summary>
    public bool ContinueOnError { get; init; }

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
        var root = WorkingDirectoryRoots.RelativePath(WorkingDirectoryRoot, actionName);

        // Null where the step declared neither, so a phase that named no directory still names none
        // and the runner's own "tree root" default keeps applying unchanged.
        var working = (root, WorkingDirectory) switch
        {
            (null, var path) => path,
            (var start, null) => start,
            var (start, path) => Path.Combine(start, path),
        };

        return
        [
            .. Commands.Select((command, index) => new RunnerPhase
            {
                Name = total == 1 ? Name : $"{Name} ({index + 1}/{total})",
                Command = [.. command.Arguments],
                WorkingDirectory = working,
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
