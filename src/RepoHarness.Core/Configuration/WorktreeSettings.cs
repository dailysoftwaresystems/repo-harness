namespace RepoHarness.Core.Configuration;

/// <summary>Worktree creation policy.</summary>
public sealed class WorktreeSettings
{
    /// <summary>Longest worktree name permitted unless configured otherwise.</summary>
    public const int DefaultMaxNameLength = 10;

    /// <summary>
    /// Longest permitted worktree name. The default keeps a worktree's build tree inside
    /// the Windows path budget for a repository at a typical depth. The budget itself is
    /// still checked against the real path, so this is a naming policy, not the guard.
    /// </summary>
    public int MaxNameLength { get; init; } = DefaultMaxNameLength;

    /// <summary>
    /// Longest path a build generates below a tree root, used to budget against the
    /// Windows path limit. The default is measured against CMake and Ninja, whose
    /// generated dependency files are the longest paths they produce; a project
    /// whose build system nests more deeply should raise it.
    /// </summary>
    public int PathBudgetReserve { get; init; } = 163;

    /// <summary>Headroom kept beyond the reserve.</summary>
    public int PathBudgetMargin { get; init; } = 20;

    /// <summary>
    /// Path length to budget against, replacing the platform's own limit (260 on
    /// Windows, none elsewhere). Leave it unset unless every tool the build runs
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
