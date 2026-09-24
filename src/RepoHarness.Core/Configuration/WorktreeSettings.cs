namespace RepoHarness.Core.Configuration;

/// <summary>Worktree creation policy.</summary>
public sealed class WorktreeSettings
{
    /// <summary>Longest worktree name permitted unless configured otherwise.</summary>
    public const int DefaultMaxNameLength = 10;

    /// <summary>The worktrees root used when config.json names none.</summary>
    public const string DefaultRoot = ".harness-config/worktrees";

    /// <summary>
    /// Where worktrees are created, relative to the main checkout. Configurable because the default
    /// spends 25 characters of the Windows path budget before a worktree's own name, and a repository
    /// whose build paths are long has no name left that fits; a shorter root is what buys those
    /// characters back. The budget is still checked against the real path, so this never hides an
    /// overrun, it only makes one avoidable.
    /// </summary>
    public string Root { get; init; } = DefaultRoot;

    /// <summary>
    /// Directories, relative to a worktree, holding measurements git does not track. Deleting a
    /// worktree is refused while one of them holds anything, naming it, unless the deletion says to
    /// delete the evidence too.
    /// </summary>
    /// <remarks>
    /// Ignored files are otherwise deleted unchecked, as git deletes them. That is right for build
    /// output and wrong for the only copy of a measurement: a worktree's measurements live in an
    /// ignored directory precisely because they are not source, and deleting the worktree silently
    /// is how they are lost. A refusal is preferred to preserving them file by file, which is more
    /// machinery and more ways to be wrong about what it preserved.
    /// </remarks>
    public List<string> EvidenceRoots { get; init; } = [];

    /// <summary>
    /// Longest permitted worktree name. The default keeps a worktree's build tree inside
    /// the Windows path budget for a repository at a typical depth. The budget itself is
    /// still checked against the real path, so this is a naming policy, not the guard.
    /// </summary>
    public int MaxNameLength { get; init; } = DefaultMaxNameLength;

    /// <summary>
    /// Longest path a build system generates below its own build directory,
    /// <c>build/&lt;variant&gt;</c>, used to budget against the Windows path limit. The build
    /// directory's own name is added by the check itself, sized to the longest variant this machine
    /// builds, because the harness knows it and a number that had to include it went stale the day
    /// build directories were keyed by variant. It is a relative path's length, with no leading
    /// separator, as a build measures one: the check counts every separator between the worktree,
    /// the build directory and this path itself. The default is measured against CMake and Ninja,
    /// whose generated dependency files are the longest paths they produce there.
    /// </summary>
    /// <remarks>
    /// Checked by every build, which measures the longest path it actually produced below its build
    /// directory and warns with both numbers when this is lower, so the number cannot go stale
    /// unnoticed.
    /// </remarks>
    public int PathBudgetReserve { get; init; } = 163;

    /// <summary>Headroom kept beyond the reserve.</summary>
    public int PathBudgetMargin { get; init; } = 20;

    /// <summary>
    /// The length every path must stay under, replacing the platform's own limit (260
    /// on Windows, none elsewhere). Leave it unset unless every tool the build runs
    /// handles long paths: the operating system allowing them is not enough, because
    /// compilers and build systems impose the limit independently.
    /// </summary>
    public int? PathLimit { get; init; }

    /// <summary>
    /// Whether a new worktree is checked out detached at the current commit. A
    /// detached worktree cannot be confused with the branch it came from, which is
    /// what makes several worktrees of one repository safe to build in parallel.
    /// </summary>
    public bool Detach { get; init; } = true;
}
