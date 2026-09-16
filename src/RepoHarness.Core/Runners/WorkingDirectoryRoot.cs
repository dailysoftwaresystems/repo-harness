using RepoHarness.Core.Repository;

namespace RepoHarness.Core.Runners;

/// <summary>
/// What a step's <c>workingDirectory</c> is relative to.
/// </summary>
/// <remarks>
/// A closed set, and small. An action file's supporting files live beside it, so a step that runs
/// one wants its own directory; a step that builds or tests the repository wants the tree. Left to
/// a bare path each would have to spell out where it starts from, and the action directory's
/// spelling repeats the action's own name inside its own file.
/// </remarks>
public enum WorkingDirectoryRoot
{
    /// <summary>
    /// The leg's tree root: its worktree, or the repository. The default, and what a step with
    /// neither key has always run in.
    /// </summary>
    Tree = 0,

    /// <summary>The tree's <c>.harness-config</c> directory.</summary>
    Harness,

    /// <summary>The action's own directory, holding its file and everything that file runs.</summary>
    Action,
}

/// <summary>How an action file spells each root, and how a spelling is read back.</summary>
/// <remarks>
/// The spellings live here rather than in the parser so that a refusal can list what is available,
/// as the predefined actions do. An unknown value that merely said "unknown root" would leave the
/// author guessing at a vocabulary the message already knows.
/// </remarks>
public static class WorkingDirectoryRoots
{
    /// <summary>The spelling of <see cref="WorkingDirectoryRoot.Tree"/>.</summary>
    public const string Tree = "tree";

    /// <summary>The spelling of <see cref="WorkingDirectoryRoot.Harness"/>.</summary>
    public const string Harness = "harness";

    /// <summary>The spelling of <see cref="WorkingDirectoryRoot.Action"/>.</summary>
    public const string Action = "action";

    /// <summary>Every spelling, in the order a refusal lists them.</summary>
    /// <remarks>
    /// Derived from the roots themselves, so a root added to the enum cannot go missing from the
    /// refusal that lists what is available.
    /// </remarks>
    public static IReadOnlyList<string> All { get; } = [.. Enum.GetValues<WorkingDirectoryRoot>().Select(Spell)];

    /// <summary>
    /// The root <paramref name="value"/> names, or <see langword="null"/> when nothing does.
    /// Compared exactly, as a predefined action's spelling is: an action file is tracked, so a
    /// spelling that differs only by case is a typo, and accepting it would make two files that
    /// read differently run the same.
    /// </summary>
    /// <param name="value">The value of a step's <c>workingDirectoryRoot</c> key.</param>
    public static WorkingDirectoryRoot? Parse(string value) => value switch
    {
        Tree => WorkingDirectoryRoot.Tree,
        Harness => WorkingDirectoryRoot.Harness,
        Action => WorkingDirectoryRoot.Action,
        _ => null,
    };

    /// <summary>How <paramref name="root"/> is spelled in an action file.</summary>
    /// <param name="root">The root to spell.</param>
    public static string Spell(WorkingDirectoryRoot root) => root switch
    {
        WorkingDirectoryRoot.Tree => Tree,
        WorkingDirectoryRoot.Harness => Harness,
        WorkingDirectoryRoot.Action => Action,
        _ => throw new ArgumentOutOfRangeException(nameof(root), root, "This build has no spelling for that root."),
    };

    /// <summary>
    /// Where <paramref name="root"/> starts from, relative to the leg's tree root, or
    /// <see langword="null"/> for the tree root itself.
    /// </summary>
    /// <remarks>
    /// Relative, so that every root composes against whichever tree the leg actually runs in. A leg
    /// on a worktree has its own checkout of the tracked action files, and an absolute path built
    /// from the invoking tree would send every such leg to the wrong copy.
    /// </remarks>
    /// <param name="root">The root a step declared.</param>
    /// <param name="actionName">The action's directory name, for <see cref="WorkingDirectoryRoot.Action"/>.</param>
    public static string? RelativePath(WorkingDirectoryRoot root, string actionName) => root switch
    {
        WorkingDirectoryRoot.Tree => null,
        WorkingDirectoryRoot.Harness => HarnessLayout.HarnessDirectoryRelative,
        WorkingDirectoryRoot.Action => HarnessLayout.RunnerActionDirectoryRelative(actionName),
        _ => throw new ArgumentOutOfRangeException(nameof(root), root, "This build has no path for that root."),
    };
}
